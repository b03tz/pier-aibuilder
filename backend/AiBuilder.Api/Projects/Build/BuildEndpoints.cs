using System.Text;

namespace AiBuilder.Api.Projects.Build;

public static class BuildEndpoints
{
    public sealed record BuildStartedResponse(string RunId, string Kind, string WorkspacePath);
    public sealed record BuildRunDto(
        string Id, string Kind, string Status, string? FailureReason,
        string TranscriptPath, DateTime StartedAt, DateTime? FinishedAt);

    public static void MapBuilds(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}").RequireAuthorization().WithTags("builds");

        group.MapPost("/build", async (string id, BuildOrchestrator orch, CancellationToken ct) =>
        {
            try
            {
                var r = await orch.StartAsync(id, ct);
                return Results.Ok(new BuildStartedResponse(r.RunId, r.Kind, r.WorkspacePath));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidStateTransitionException e)
            {
                return Results.Conflict(new { error = "invalid-transition", from = e.From, to = e.To });
            }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });

        group.MapGet("/builds", async (string id, BuildRunStore runs, CancellationToken ct) =>
        {
            var list = await runs.ListForProjectAsync(id, ct);
            return Results.Ok(list.Select(ToDto));
        });

        group.MapGet("/builds/{runId}", async (string id, string runId, BuildRunStore runs, CancellationToken ct) =>
        {
            var r = await runs.GetAsync(runId, ct);
            return r is null ? Results.NotFound() : Results.Ok(ToDto(r));
        });

        group.MapGet("/builds/{runId}/transcript", async (string id, string runId, BuildRunStore runs, CancellationToken ct) =>
        {
            var r = await runs.GetAsync(runId, ct);
            if (r is null) return Results.NotFound();
            if (!File.Exists(r.transcriptPath)) return Results.NotFound(new { error = "transcript-missing" });
            return Results.Text(await File.ReadAllTextAsync(r.transcriptPath, ct), "text/plain");
        });

        group.MapGet("/builds/{runId}/stream", async (string id, string runId, BuildRunStore runs, BuildStreamHub hub, HttpContext ctx, CancellationToken ct) =>
        {
            // SSE: backlog first, then live tail. Works for in-flight runs; for
            // completed runs we fall through to the transcript on disk.
            var r = await runs.GetAsync(runId, ct);
            if (r is null) { ctx.Response.StatusCode = 404; return; }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: disable proxy buffering
            await ctx.Response.Body.FlushAsync(ct);

            var stream = hub.Get(runId);
            if (stream is null)
            {
                // The run lived through a process restart OR was never started
                // in this process. Replay transcript file and emit terminal.
                if (File.Exists(r.transcriptPath))
                    foreach (var line in await File.ReadAllLinesAsync(r.transcriptPath, ct))
                        await WriteEventAsync(ctx, "line", line, ct);
                await WriteEventAsync(ctx, "end", r.status, ct);
                return;
            }

            var (backlog, live, completed, termStatus, termDetail) = stream.Subscribe();
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

    private static BuildRunDto ToDto(Plexxer.Client.AiBuilder.BuildRun r) =>
        new(r.Id!, r.kind, r.status, r.failureReason, r.transcriptPath, r.startedAt, r.finishedAt);

    private static async Task WriteEventAsync(HttpContext ctx, string eventName, string data, CancellationToken ct)
    {
        // SSE requires each line of a multi-line `data:` block to be prefixed.
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await ctx.Response.WriteAsync(sb.ToString(), ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
