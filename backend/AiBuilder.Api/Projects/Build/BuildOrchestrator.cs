using System.Collections.Concurrent;
using AiBuilder.Api.Projects.Scope;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Build;

// Coordinates everything that happens for a single `POST /build` call:
// workspace prep, state-machine transitions, claude subprocess, streaming,
// transcript persistence, final git commit, BuildRun status update.
public sealed class BuildOrchestrator
{
    private readonly ProjectStore _projects;
    private readonly ConversationStore _turns;
    private readonly BuildRunStore _runs;
    private readonly WorkspaceManager _ws;
    private readonly BuildStreamHub _hub;
    private readonly ClaudeCli _cli;
    private readonly ILogger<BuildOrchestrator> _log;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new();

    public BuildOrchestrator(
        ProjectStore projects, ConversationStore turns, BuildRunStore runs,
        WorkspaceManager ws, BuildStreamHub hub, ClaudeCli cli,
        ILogger<BuildOrchestrator> log)
    {
        _projects = projects; _turns = turns; _runs = runs;
        _ws = ws; _hub = hub; _cli = cli; _log = log;
    }

    public sealed record StartResult(string RunId, string Kind, string WorkspacePath);

    public async Task<StartResult> StartAsync(string projectId, CancellationToken ct)
    {
        var project = await _projects.GetWithSecretsAsync(projectId, ct)
            ?? throw new KeyNotFoundException($"project {projectId} not found");

        var currentStatus = project.workspaceStatus;
        var isIteration = await _runs.HasSucceededBefore(projectId, ct);
        var targetStatus = isIteration ? WorkspaceStatus.Updating : WorkspaceStatus.Building;
        var kind = isIteration ? "update" : "build";
        var finalSuccessStatus = isIteration ? WorkspaceStatus.DoneUpdating : WorkspaceStatus.DoneBuilding;

        if (!ProjectStateMachine.CanTransition(currentStatus, targetStatus))
            throw new InvalidStateTransitionException(currentStatus, targetStatus);

        // Serialise builds per project so we don't have two claude subprocesses
        // thrashing the same workspace.
        var sem = ProjectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException($"build already running for project {projectId}");

        string workspacePath;
        BuildRun run;
        string transcriptPath;
        try
        {
            workspacePath = _ws.EnsureExists(project.pierAppName);
            await _ws.EnsureGitInitAsync(project.pierAppName, ct);

            transcriptPath = Path.Combine(workspacePath, ".aibuilder", $"build-{Guid.NewGuid():N}.log");
            run = await _runs.CreateAsync(projectId, kind, transcriptPath, ct);
            await _projects.SetStatusAsync(projectId, currentStatus, targetStatus, ct);
        }
        catch
        {
            sem.Release();
            throw;
        }

        // Fire-and-forget the actual build. Errors are captured into the run
        // record + stream; they never propagate into the HTTP response of
        // `POST /build` (which only reports "run started").
        _ = Task.Run(async () =>
        {
            var stream = _hub.Create(run.Id!, transcriptPath);
            try
            {
                await RunBuildAsync(project, run.Id!, isIteration, workspacePath, stream, finalSuccessStatus, targetStatus);
            }
            catch (Exception e)
            {
                _log.LogError(e, "build {RunId} crashed", run.Id);
                stream.Write($"[aibuilder] build failed: {e.Message}");
                stream.Complete("failed", e.Message);
                try
                {
                    await _runs.MarkFinishedAsync(run.Id!, "failed", e.Message, CancellationToken.None);
                    await _projects.SetStatusAsync(projectId, targetStatus, WorkspaceStatus.ScopeLocked, CancellationToken.None);
                }
                catch { /* best effort */ }
            }
            finally { sem.Release(); }
        }, CancellationToken.None);

        return new StartResult(run.Id!, kind, workspacePath);
    }

    private async Task RunBuildAsync(
        Project project, string runId, bool isIteration, string workspacePath,
        BuildStream stream, string finalSuccessStatus, string inProgressStatus)
    {
        var scopeTurns = await _turns.ListAsync(project.Id!, CancellationToken.None);

        stream.Write($"[aibuilder] starting {(isIteration ? "iteration" : "first build")} for {project.pierAppName}");
        stream.Write($"[aibuilder] workspace: {workspacePath}");

        var systemPrompt = BuildPromptAssembler.BuildSystemPrompt(project);
        var userPrompt = BuildPromptAssembler.BuildUserPrompt(project, scopeTurns, isIteration);

        var exitCode = await _cli.RunStreamingAsync(
            new ClaudeCli.RunOptions(
                Prompt: userPrompt,
                Cwd: workspacePath,
                AppendSystemPrompt: systemPrompt,
                DangerouslySkipPermissions: true,
                Timeout: TimeSpan.FromMinutes(30),
                StreamJson: true),
            onStdout: line => { foreach (var formatted in StreamJsonFormatter.Format(line)) stream.Write(formatted); },
            onStderr: line => stream.Write("[stderr] " + line),
            CancellationToken.None);

        if (exitCode != 0)
        {
            var detail = $"claude exited with code {exitCode}";
            stream.Write($"[aibuilder] {detail}");
            stream.Complete("failed", detail);
            await _runs.MarkFinishedAsync(runId, "failed", detail, CancellationToken.None);
            await _projects.SetStatusAsync(project.Id!, inProgressStatus, WorkspaceStatus.ScopeLocked, CancellationToken.None);
            return;
        }

        try
        {
            await _ws.CommitBuildAsync(project.pierAppName,
                $"aibuilder build {runId} ({(isIteration ? "update" : "first")})",
                CancellationToken.None);
            stream.Write("[aibuilder] git: committed workspace snapshot");
        }
        catch (Exception e)
        {
            // A commit failure is a soft warning — the build ran, files are on
            // disk, we just couldn't snapshot. Still mark the run succeeded.
            stream.Write($"[aibuilder] git commit warning: {e.Message}");
        }

        stream.Write("[aibuilder] build succeeded");
        stream.Complete("succeeded", "OK");
        await _runs.MarkFinishedAsync(runId, "succeeded", null, CancellationToken.None);
        await _projects.SetStatusAsync(project.Id!, inProgressStatus, finalSuccessStatus, CancellationToken.None);
    }
}
