using System.Collections.Concurrent;
using AiBuilder.Api.Projects.Deploy;
using AiBuilder.Api.Projects.Import;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Scope;

public static class ScopeEndpoints
{
    // Serialise /turns calls per project so turnIndex assignment can't race.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new();

    public sealed record TurnRequest(string Message);
    public sealed record TurnDto(string Id, string Role, string Content, int TurnIndex, DateTime CreatedAt);

    public static void MapScope(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}").RequireAuthorization().WithTags("scope");

        group.MapGet("/turns", async (string id, ConversationStore conv, CancellationToken ct) =>
        {
            var turns = await conv.ListAsync(id, ct);
            return Results.Ok(turns.Select(ToDto));
        });

        group.MapPost("/turns", async (
            string id,
            TurnRequest req,
            ProjectStore projects,
            ConversationStore conv,
            ClaudeCli cli,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "message-required" });

            var project = await projects.GetWithSecretsAsync(id, ct);
            if (project is null) return Results.NotFound();

            // Only states that allow scope conversation: Draft (first turn),
            // InConversation (subsequent), Deployed (iteration kickoff).
            var s = project.workspaceStatus;
            if (s != WorkspaceStatus.Draft && s != WorkspaceStatus.InConversation && s != WorkspaceStatus.Deployed)
                return Results.Conflict(new { error = "scope-closed", currentStatus = s });

            var sem = ProjectLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                // Flip Draft/Deployed → InConversation on first turn of a scope segment.
                if (s != WorkspaceStatus.InConversation)
                {
                    await projects.SetStatusAsync(id, s, WorkspaceStatus.InConversation, ct);
                    project.workspaceStatus = WorkspaceStatus.InConversation;
                }

                var prior = await conv.ListAsync(id, ct);
                var baseIdx = prior.Count == 0 ? 0 : (int)prior[^1].turnIndex + 1;

                var userTurn = await conv.AppendAsync(id, "user", req.Message, baseIdx, ct);

                var prompt = TranscriptRenderer.RenderScopePrompt(project, prior, req.Message);
                var run = await cli.RunAsync(new ClaudeCli.RunOptions(
                    Prompt: prompt,
                    AppendSystemPrompt: TranscriptRenderer.ScopeSystemPrompt,
                    DangerouslySkipPermissions: true,
                    DisallowedTools: "Edit,Write,Bash",
                    Timeout: TimeSpan.FromMinutes(2)), ct);

                if (run.ExitCode != 0)
                    return Results.Problem(
                        title: "claude-cli-failed",
                        detail: $"exit {run.ExitCode}; stderr: {run.Stderr.TrimEnd()}",
                        statusCode: 502);

                var assistantText = run.Stdout.TrimEnd();
                var assistantTurn = await conv.AppendAsync(id, "assistant", assistantText, baseIdx + 1, ct);

                return Results.Ok(new
                {
                    user      = ToDto(userTurn),
                    assistant = ToDto(assistantTurn),
                });
            }
            finally
            {
                sem.Release();
            }
        });

        group.MapPost("/lock-scope", async (string id, ProjectStore projects, ConversationStore conv, CancellationToken ct) =>
        {
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) return Results.NotFound();

            if (!ProjectStateMachine.CanTransition(project.workspaceStatus, WorkspaceStatus.ScopeLocked))
                return Results.Conflict(new { error = "invalid-transition",
                    from = project.workspaceStatus, to = WorkspaceStatus.ScopeLocked });

            var turns = await conv.ListAsync(id, ct);
            if (turns.Count == 0)
                return Results.BadRequest(new { error = "no-turns", message = "Have at least one turn before locking." });

            await projects.SetStatusAsync(id, project.workspaceStatus, WorkspaceStatus.ScopeLocked, ct);
            return Results.Ok(new { id, workspaceStatus = WorkspaceStatus.ScopeLocked });
        });

        // Wipe the scope conversation for a project. Distinct from Reset
        // (which also nukes workspace files, builds, deploys, env vars).
        // Clear-scope leaves the deployed app alone — it's the "I shipped
        // a chunk, now starting a fresh conversation for the next idea"
        // affordance. For imported projects we re-run introspection so
        // the next conversation has a current codebase summary that
        // catches edits made to the import from outside AiBuilder.
        group.MapPost("/clear-scope", async (
            string id,
            ProjectStore projects,
            ConversationStore conv,
            DeployRunStore deploys,
            ImportIntrospector introspector,
            CancellationToken ct) =>
        {
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) return Results.NotFound();

            var s = project.workspaceStatus;
            if (s != WorkspaceStatus.Draft && s != WorkspaceStatus.InConversation && s != WorkspaceStatus.Deployed)
                return Results.Conflict(new { error = "scope-busy", currentStatus = s });

            // Use the same per-project semaphore /turns uses, so a clear
            // can't race a turn that's mid-flight in claude.
            var sem = ProjectLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                var deleted = await conv.ClearAsync(id, ct);

                bool? reintrospected = null;
                if (project.isImported == true)
                {
                    var r = await introspector.RunAsync(id, project.pierAppName, ct);
                    reintrospected = r.Ok;
                }

                // Decide post-clear status. "Shipped, idle" = Deployed if
                // any successful deploy exists; otherwise stay Draft.
                // Bypass the state machine — InConversation→Deployed and
                // Deployed→Draft aren't valid transitions, this is an
                // explicit admin override (mirrors how Reset bypasses).
                var deployRuns = await deploys.ListForProjectAsync(id, ct);
                var hasSuccessfulDeploy = deployRuns.Any(r => r.status == "succeeded");
                var newStatus = hasSuccessfulDeploy ? WorkspaceStatus.Deployed : WorkspaceStatus.Draft;

                if (newStatus != s)
                {
                    await projects.UpdateFieldsAsync(id, new Dictionary<string, object?>
                    {
                        ["workspaceStatus"] = newStatus,
                    }, ct);
                }

                return Results.Ok(new
                {
                    id,
                    workspaceStatus = newStatus,
                    deleted,
                    reintrospected,
                });
            }
            finally
            {
                sem.Release();
            }
        });

        group.MapPost("/unlock-scope", async (string id, ProjectStore projects, CancellationToken ct) =>
        {
            // Flip a locked-ish status back to InConversation so the admin
            // can append more turns and then re-lock for another build.
            // Workspace files are preserved — this is state-only.
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) return Results.NotFound();

            var from = project.workspaceStatus;
            if (from == WorkspaceStatus.InConversation || from == WorkspaceStatus.Draft)
                return Results.Ok(new { id, workspaceStatus = from, noop = true });

            if (!ProjectStateMachine.CanTransition(from, WorkspaceStatus.InConversation))
                return Results.Conflict(new { error = "invalid-transition",
                    from, to = WorkspaceStatus.InConversation });

            await projects.SetStatusAsync(id, from, WorkspaceStatus.InConversation, ct);
            return Results.Ok(new { id, workspaceStatus = WorkspaceStatus.InConversation });
        });
    }

    private static TurnDto ToDto(ConversationTurn t) =>
        new(t.Id!, t.role, t.content, (int)t.turnIndex, t.createdAt);
}
