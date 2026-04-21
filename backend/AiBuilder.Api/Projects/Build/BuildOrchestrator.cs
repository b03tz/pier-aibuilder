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

    // Per-run cancellation handles. Populated when a build starts, removed
    // when it finishes. POST /builds/{runId}/cancel flips the matching CTS.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> RunCts = new();

    public bool Cancel(string runId)
    {
        if (!RunCts.TryGetValue(runId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    // Call once at boot. Any BuildRun with status=running predates this
    // process, so its orchestrator is gone; mark those failed and roll the
    // owning project back from Building/Updating to ScopeLocked.
    public async Task ReconcileOrphansOnStartupAsync(CancellationToken ct)
    {
        List<BuildRun> orphans;
        try { orphans = await _runs.ListRunningAsync(ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "orphan reconciliation failed at startup — skipping");
            return;
        }
        if (orphans.Count == 0) return;
        _log.LogWarning("reconciling {N} orphaned BuildRun(s) left over from a previous process", orphans.Count);
        foreach (var r in orphans)
        {
            try
            {
                await _runs.MarkFinishedAsync(r.Id!, "failed",
                    "orphaned: AiBuilder restarted or crashed before the subprocess could finish", ct);

                var projectId = r.project.Id;
                if (!string.IsNullOrEmpty(projectId))
                {
                    var p = await _projects.GetSafeAsync(projectId, ct);
                    if (p is not null &&
                        (p.workspaceStatus == WorkspaceStatus.Building || p.workspaceStatus == WorkspaceStatus.Updating))
                    {
                        await _projects.SetStatusAsync(projectId, p.workspaceStatus, WorkspaceStatus.ScopeLocked, ct);
                    }
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "failed to reconcile orphan run {RunId}", r.Id);
            }
        }
    }

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
        var runCts = new CancellationTokenSource();
        RunCts[run.Id!] = runCts;

        _ = Task.Run(async () =>
        {
            var stream = _hub.Create(run.Id!, transcriptPath);
            try
            {
                await RunBuildAsync(project, run.Id!, isIteration, workspacePath, stream, finalSuccessStatus, targetStatus, runCts.Token);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("build {RunId} cancelled by admin", run.Id);
                stream.Write("[aibuilder] build cancelled by admin");
                stream.Complete("failed", "cancelled");
                try
                {
                    await _runs.MarkFinishedAsync(run.Id!, "failed", "cancelled", CancellationToken.None);
                    await _projects.SetStatusAsync(projectId, targetStatus, WorkspaceStatus.ScopeLocked, CancellationToken.None);
                }
                catch { /* best effort */ }
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
            finally
            {
                RunCts.TryRemove(run.Id!, out _);
                runCts.Dispose();
                sem.Release();
            }
        }, CancellationToken.None);

        return new StartResult(run.Id!, kind, workspacePath);
    }

    private async Task RunBuildAsync(
        Project project, string runId, bool isIteration, string workspacePath,
        BuildStream stream, string finalSuccessStatus, string inProgressStatus,
        CancellationToken ct)
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
            ct);

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
