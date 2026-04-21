using System.Collections.Concurrent;
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
