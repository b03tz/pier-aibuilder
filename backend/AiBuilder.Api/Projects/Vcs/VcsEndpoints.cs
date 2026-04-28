using System.Text;
using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Import;

namespace AiBuilder.Api.Projects.Vcs;

public static class VcsEndpoints
{
    public sealed record VcsSettingsRequest(string? RemoteUrl, string? Branch);

    public sealed record VcsStateDto(
        string? RemoteUrl,
        string? Branch,
        string? CurrentBranch,
        string? HeadSha,
        string? HeadShortSha,
        string? HeadSubject,
        DateTime? HeadAt,
        string? LastPushSha,
        DateTime? LastPushAt,
        bool IsImported,
        bool WorkspaceHasGit);

    public sealed record CloneResponse(bool Ok, string? Error, int? EnvMirroredCount, string? EnvMirrorError, bool? IntrospectionOk, string? IntrospectionError);

    public sealed record PullRequest(bool DiscardLocalChanges);

    public sealed record PullResponse(
        bool Ok,
        string? ErrorCode,
        string? ErrorMessage,
        string? PreviousSha,
        string? NewSha,
        int FilesChanged,
        IReadOnlyList<string> UncommittedFiles,
        string Output,
        VcsStateDto? State);

    public static void MapVcs(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}/vcs").RequireAuthorization().WithTags("vcs");

        group.MapGet("", async (string id, ProjectStore projects, WorkspaceManager ws, CancellationToken ct) =>
        {
            var p = await projects.GetSafeAsync(id, ct);
            if (p is null) return Results.NotFound();
            var head = await ws.GetHeadInfoAsync(p.pierAppName, ct);
            return Results.Ok(new VcsStateDto(
                p.gitRemoteUrl,
                p.gitRemoteBranch,
                head.CurrentBranch,
                head.Sha,
                head.ShortSha,
                head.Subject,
                head.CommittedAt,
                p.lastPushSha,
                p.lastPushAt,
                p.isImported ?? false,
                ws.HasGitWorkspace(p.pierAppName)));
        });

        group.MapPut("", async (string id, VcsSettingsRequest req, ProjectStore projects, WorkspaceManager ws, CancellationToken ct) =>
        {
            var p = await projects.GetSafeAsync(id, ct);
            if (p is null) return Results.NotFound();

            if (!GitRemoteUrl.TryNormalize(req.RemoteUrl, out var url, out var urlErr))
                return Results.BadRequest(new { error = "invalid-remote-url", reason = urlErr });
            var branchInput = string.IsNullOrWhiteSpace(req.Branch) ? "master" : req.Branch;
            if (!GitRemoteUrl.TryNormalizeBranch(branchInput, out var branch, out var brErr))
                return Results.BadRequest(new { error = "invalid-branch", reason = brErr });

            await projects.UpdateFieldsAsync(id, new Dictionary<string, object?>
            {
                ["gitRemoteUrl"]    = url,
                ["gitRemoteBranch"] = branch,
            }, ct);

            var after = await projects.GetSafeAsync(id, ct);
            var head  = await ws.GetHeadInfoAsync(p.pierAppName, ct);
            return Results.Ok(new VcsStateDto(
                after?.gitRemoteUrl, after?.gitRemoteBranch,
                head.CurrentBranch, head.Sha, head.ShortSha, head.Subject, head.CommittedAt,
                after?.lastPushSha, after?.lastPushAt,
                after?.isImported ?? false,
                ws.HasGitWorkspace(p.pierAppName)));
        });

        group.MapDelete("", async (string id, ProjectStore projects, CancellationToken ct) =>
        {
            var p = await projects.GetSafeAsync(id, ct);
            if (p is null) return Results.NotFound();
            await projects.UpdateFieldsAsync(id, new Dictionary<string, object?>
            {
                ["gitRemoteUrl"]    = null,
                ["gitRemoteBranch"] = null,
            }, ct);
            return Results.NoContent();
        });

        // Recovery action for imports whose initial clone failed (auth,
        // unreachable host, wrong branch, etc.). The Project record was
        // created anyway so the admin can fix the URL/branch via PUT and
        // call this endpoint to retry the clone. Refuses if the workspace
        // already has a git repo — re-cloning over a populated workspace
        // would be destructive.
        group.MapPost("/clone", async (
            string id,
            ProjectStore projects,
            WorkspaceManager ws,
            ImportPierEnvMirror envMirror,
            ImportIntrospector introspector,
            ProjectLockManager locks,
            CancellationToken ct) =>
        {
            var p = await projects.GetWithSecretsAsync(id, ct);
            if (p is null) return Results.NotFound();

            if (!(p.isImported ?? false))
                return Results.Conflict(new { error = "not-an-import" });
            if (string.IsNullOrWhiteSpace(p.gitRemoteUrl))
                return Results.BadRequest(new { error = "no-remote-configured" });
            if (ws.HasGitWorkspace(p.pierAppName))
                return Results.Conflict(new { error = "workspace-already-has-git",
                    message = "Workspace is already initialised. Reset the project first if you want to re-clone." });

            var sem = locks.Get(id);
            if (!await sem.WaitAsync(TimeSpan.Zero, ct))
                return Results.Conflict(new { error = "workspace-busy" });

            try
            {
                var branch = string.IsNullOrWhiteSpace(p.gitRemoteBranch) ? "master" : p.gitRemoteBranch!;
                var clone = await ws.CloneAsync(p.pierAppName, p.gitRemoteUrl!, branch, ct);
                if (!clone.Ok)
                    return Results.Ok(new CloneResponse(false, clone.ErrorMessage, null, null, null, null));

                var mirror = await envMirror.MirrorAsync(p.Id!, p.pierAppName, p.pierApiToken, ct);
                var intro  = await introspector.RunAsync(p.Id!, p.pierAppName, ct);
                return Results.Ok(new CloneResponse(true, null, mirror.Mirrored, mirror.Error, intro.Ok, intro.Error));
            }
            finally { sem.Release(); }
        });

        // Sync workspace ← remote. Refuses by default if the workspace has
        // uncommitted changes; pass DiscardLocalChanges=true to hard-reset to
        // origin/<branch> and clean untracked files.
        group.MapPost("/pull", async (
            string id,
            PullRequest? body,
            ProjectStore projects,
            WorkspaceManager ws,
            ProjectLockManager locks,
            CancellationToken ct) =>
        {
            var p = await projects.GetSafeAsync(id, ct);
            if (p is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(p.gitRemoteUrl))
                return Results.Conflict(new { error = "no-remote-configured" });
            if (!ws.HasGitWorkspace(p.pierAppName))
                return Results.Conflict(new { error = "not-a-repo",
                    message = "Workspace has no .git — clone first." });

            // Don't pull while the project is mid-iteration; claude is
            // actively writing to the same workspace.
            if (p.workspaceStatus == WorkspaceStatus.Building ||
                p.workspaceStatus == WorkspaceStatus.Updating)
                return Results.Conflict(new { error = "busy",
                    message = $"Project is {p.workspaceStatus} — pull is unsafe right now." });

            var sem = locks.Get(id);
            if (!await sem.WaitAsync(TimeSpan.Zero, ct))
                return Results.Conflict(new { error = "workspace-busy" });

            try
            {
                var branch = string.IsNullOrWhiteSpace(p.gitRemoteBranch) ? "master" : p.gitRemoteBranch!;
                var discard = body?.DiscardLocalChanges ?? false;
                var result = await ws.PullAsync(p.pierAppName, branch, discard, ct);

                var head  = await ws.GetHeadInfoAsync(p.pierAppName, ct);
                var stateDto = new VcsStateDto(
                    p.gitRemoteUrl, p.gitRemoteBranch,
                    head.CurrentBranch, head.Sha, head.ShortSha, head.Subject, head.CommittedAt,
                    p.lastPushSha, p.lastPushAt,
                    p.isImported ?? false,
                    ws.HasGitWorkspace(p.pierAppName));

                var response = new PullResponse(
                    result.Ok, result.ErrorCode, result.ErrorMessage,
                    result.PreviousSha, result.NewSha, result.FilesChanged,
                    result.UncommittedFiles, result.CombinedOutput, stateDto);

                // Workspace-dirty is a 409 so the frontend can route it to
                // the override-prompt path; everything else (success or
                // genuine git failure) returns 200 with ok=false so the UI
                // can render the combined git output.
                if (!result.Ok && result.ErrorCode == "workspace-dirty")
                    return Results.Json(response, statusCode: 409);

                return Results.Ok(response);
            }
            finally { sem.Release(); }
        });

        group.MapPost("/push", async (string id, PushOrchestrator orch, CancellationToken ct) =>
        {
            try
            {
                var r = await orch.StartAsync(id, ct);
                return Results.Ok(new { runId = r.RunId });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });

        group.MapGet("/push/{runId}/stream", async (
            string id, string runId, BuildStreamHub hub, PushOrchestrator orch,
            HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);

            var stream = hub.Get(runId);
            if (stream is null)
            {
                // Stream evicted / never started in this process. Try the
                // transcript file if we know where it lives.
                var path = orch.GetTranscriptPath(runId);
                if (path is not null && File.Exists(path))
                {
                    foreach (var line in await File.ReadAllLinesAsync(path, ct))
                        await WriteEventAsync(ctx, "line", line, ct);
                }
                await WriteEventAsync(ctx, "end", "unknown", ct);
                return;
            }

            var (backlog, live, completed, termStatus, _) = stream.Subscribe();
            foreach (var line in backlog)
                await WriteEventAsync(ctx, "line", line, ct);
            if (completed)
            {
                await WriteEventAsync(ctx, "end", termStatus ?? "completed", ct);
                return;
            }
            await foreach (var line in live.ReadAllAsync(ct))
                await WriteEventAsync(ctx, "line", line, ct);
            await WriteEventAsync(ctx, "end", stream.TerminalStatus ?? "completed", ct);
        });
    }

    private static async Task WriteEventAsync(HttpContext ctx, string eventName, string data, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await ctx.Response.WriteAsync(sb.ToString(), ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
