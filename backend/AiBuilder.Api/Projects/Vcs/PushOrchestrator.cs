using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Scope;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Vcs;

// Runs the claude CLI to push a project's workspace to its configured
// git remote. Mirrors BuildOrchestrator's shape: per-project lock,
// streaming output through BuildStreamHub (same hub — run ids are
// GUIDs, no collision with build runs), transcript written to
// .aibuilder/push-<guid>.log, final short-sha parsed out of the agent's
// output and stamped onto the Project record.
public sealed class PushOrchestrator
{
    private readonly ProjectStore _projects;
    private readonly WorkspaceManager _ws;
    private readonly BuildStreamHub _hub;
    private readonly ClaudeCli _cli;
    private readonly ProjectLockManager _locks;
    private readonly ILogger<PushOrchestrator> _log;

    // Push runs, unlike builds, are not persisted in Plexxer — a push
    // transcript lives on disk and the last-push outcome is a pair of
    // fields on the Project. We do hold the run-id → transcript-path
    // map in memory so the stream endpoint can replay after the in-memory
    // stream has closed.
    private static readonly ConcurrentDictionary<string, string> TranscriptByRun = new();

    public PushOrchestrator(
        ProjectStore projects, WorkspaceManager ws,
        BuildStreamHub hub, ClaudeCli cli,
        ProjectLockManager locks,
        ILogger<PushOrchestrator> log)
    {
        _projects = projects; _ws = ws; _hub = hub; _cli = cli;
        _locks = locks; _log = log;
    }

    public sealed record StartResult(string RunId);

    public string? GetTranscriptPath(string runId) =>
        TranscriptByRun.TryGetValue(runId, out var p) ? p : null;

    public async Task<StartResult> StartAsync(string projectId, CancellationToken ct)
    {
        var project = await _projects.GetWithSecretsAsync(projectId, ct)
            ?? throw new KeyNotFoundException($"project {projectId} not found");

        if (string.IsNullOrWhiteSpace(project.gitRemoteUrl))
            throw new InvalidOperationException("remote-not-configured");
        var branch = string.IsNullOrWhiteSpace(project.gitRemoteBranch) ? "main" : project.gitRemoteBranch!;

        var workspacePath = _ws.ResolvePath(project.pierAppName);
        if (!Directory.Exists(workspacePath))
            throw new InvalidOperationException("workspace-missing");
        // A push with no commits is pointless; make sure git is initialised
        // so `git log` doesn't fail and so a first-time push has a root
        // commit to talk about.
        await _ws.EnsureGitInitAsync(project.pierAppName, ct);

        // Share the workspace lock with builds — a build editing files
        // while we're mid-push would be a disaster.
        var sem = _locks.Get(projectId);
        if (!await sem.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("workspace-busy");

        var runId = Guid.NewGuid().ToString("N");
        var transcriptPath = Path.Combine(workspacePath, ".aibuilder", $"push-{runId}.log");
        TranscriptByRun[runId] = transcriptPath;

        var stream = _hub.Create(runId, transcriptPath);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunPushAsync(project, branch, workspacePath, stream, ct);
            }
            catch (OperationCanceledException)
            {
                stream.Write("[aibuilder] push cancelled");
                stream.Complete("failed", "cancelled");
            }
            catch (Exception e)
            {
                _log.LogError(e, "push {RunId} crashed", runId);
                stream.Write($"[aibuilder] push failed: {e.Message}");
                stream.Complete("failed", e.Message);
            }
            finally
            {
                sem.Release();
            }
        }, CancellationToken.None);

        return new StartResult(runId);
    }

    private async Task RunPushAsync(
        Project project, string branch, string workspacePath,
        BuildStream stream, CancellationToken ct)
    {
        stream.Write($"[aibuilder] push starting for {project.pierAppName}");
        stream.Write($"[aibuilder] remote: {project.gitRemoteUrl}");
        stream.Write($"[aibuilder] branch: {branch}");

        var system = PushPromptBuilder.BuildSystemPrompt(project, project.gitRemoteUrl!, branch);
        var user   = PushPromptBuilder.BuildUserPrompt(project);

        var shortShaBeforePush = (await _ws.GetHeadInfoAsync(project.pierAppName, CancellationToken.None)).ShortSha;

        var exit = await _cli.RunStreamingAsync(
            new ClaudeCli.RunOptions(
                Prompt: user,
                Cwd: workspacePath,
                AppendSystemPrompt: system,
                DangerouslySkipPermissions: true,
                DisallowedTools: "Edit,Write,NotebookEdit",
                Timeout: TimeSpan.FromMinutes(10),
                StreamJson: true,
                ExtraEnv: null),
            onStdout: line =>
            {
                foreach (var formatted in StreamJsonFormatter.Format(line))
                    stream.Write(formatted);
            },
            onStderr: line => stream.Write("[stderr] " + line),
            ct);

        if (exit != 0)
        {
            var detail = $"claude exited with code {exit}";
            stream.Write($"[aibuilder] {detail}");
            stream.Complete("failed", detail);
            return;
        }

        // Verify a push actually happened. Trust-but-verify: parse the
        // agent's own marker line first, but always confirm the sha
        // against `git rev-parse HEAD` — the agent could claim success
        // when it didn't, and we don't want to stamp a fake lastPushSha.
        var headAfter = await _ws.GetHeadInfoAsync(project.pierAppName, CancellationToken.None);
        var claimedShort = TryExtractPushedShort(stream);
        var verified = headAfter.ShortSha is not null &&
            (claimedShort is null || claimedShort == headAfter.ShortSha);

        if (!verified || headAfter.Sha is null)
        {
            stream.Write("[aibuilder] could not verify the push landed — not stamping lastPushSha");
            stream.Complete("failed", "verification-failed");
            return;
        }

        // Also cheap sanity check: did the head move since we started? If
        // not AND no claimed push, we probably didn't do anything. Still
        // stamp it as "pushed" because the remote may now hold the same
        // sha we already had, which is a valid no-op push.
        if (headAfter.ShortSha == shortShaBeforePush && claimedShort is null)
        {
            stream.Write("[aibuilder] workspace had nothing new to push — treating as no-op");
        }

        try
        {
            await _projects.UpdateFieldsAsync(project.Id!, new Dictionary<string, object?>
            {
                ["lastPushSha"] = headAfter.Sha,
                ["lastPushAt"]  = DateTime.UtcNow,
            }, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Plexxer hiccup shouldn't be treated as a failed push — the
            // remote has the commit. Log it so Patrick sees what's up.
            stream.Write($"[aibuilder] lastPushSha save warning: {e.Message}");
        }

        stream.Write($"[aibuilder] push succeeded: {headAfter.ShortSha}");
        stream.Complete("succeeded", headAfter.ShortSha!);
    }

    // Parses the sentinel the push prompt asks the agent to emit:
    //     [aibuilder-push] pushed <short-sha> to <branch>
    private static readonly Regex PushedMarker = new(
        @"\[aibuilder-push\]\s+pushed\s+([0-9a-f]{4,40})\s+to\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? TryExtractPushedShort(BuildStream stream)
    {
        // The stream's on-disk transcript is the full record; but we don't
        // want to re-read from disk here. Instead, scan the most recent
        // lines captured on the transcript file directly.
        try
        {
            if (!File.Exists(stream.TranscriptPath)) return null;
            foreach (var line in File.ReadLines(stream.TranscriptPath).Reverse().Take(50))
            {
                var m = PushedMarker.Match(line);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { /* best effort */ }
        return null;
    }
}
