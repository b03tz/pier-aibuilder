using AiBuilder.Api.Projects;

namespace AiBuilder.Api.Projects.Deploy;

public static class DeployEndpoints
{
    public sealed record EnvVarDto(string Key, string? Value, bool IsSecret, bool ExposeToFrontend, DateTime UpdatedAt);
    public sealed record EnvVarUpsertRequest(string Value, bool IsSecret, bool ExposeToFrontend);
    public sealed record DeployRunDto(
        string Id, string Status, string? FailureReason, double? PierDeployVersion,
        double? PierFrontendDeployVersion, string? DeployNotes,
        DateTime StartedAt, DateTime? FinishedAt);

    public static void MapDeploy(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}").RequireAuthorization().WithTags("deploy");

        // --- Env vars (local mirror; deploy pushes them to Pier) ---

        group.MapGet("/env", async (string id, EnvVarStore envs, CancellationToken ct) =>
        {
            var list = await envs.ListForProjectAsync(id, includeSecretValues: false, ct);
            return Results.Ok(list.Select(v => new EnvVarDto(
                v.key,
                v.isSecret ? null : v.value,
                v.isSecret, v.exposeToFrontend, v.updatedAt)));
        });

        group.MapPut("/env/{key}", async (string id, string key, EnvVarUpsertRequest req, EnvVarStore envs, CancellationToken ct) =>
        {
            try
            {
                await envs.UpsertAsync(id, key, req.Value, req.IsSecret, req.ExposeToFrontend, ct);
                return Results.NoContent();
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = "invalid-env-var", message = e.Message });
            }
        });

        group.MapDelete("/env/{key}", async (string id, string key, EnvVarStore envs, CancellationToken ct) =>
        {
            await envs.DeleteAsync(id, key, ct);
            return Results.NoContent();
        });

        // --- Deploy runs ---

        group.MapPost("/deploy", async (string id, DeployOrchestrator orch, ILogger<DeployOrchestrator> log, CancellationToken ct) =>
        {
            try
            {
                var r = await orch.DeployAsync(id, ct);
                return Results.Ok(new { deployRunId = r.DeployRunId, status = r.Status,
                    backendVersion = r.BackendVersion, frontendVersion = r.FrontendVersion });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidStateTransitionException e)
            {
                return Results.Conflict(new { error = "invalid-transition", from = e.From, to = e.To });
            }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
            catch (Exception e)
            {
                // Orchestrator crashed mid-flight (e.g. npm missing, Pier
                // returned an error the orchestrator didn't expect). The
                // DeployRun itself was already marked failed inside the
                // orchestrator's own try/catch; we surface a structured
                // 502 here so the UI shows a real error instead of a
                // generic 500 stack dump.
                log.LogError(e, "deploy crashed for project {ProjectId}", id);
                return Results.Json(
                    new { error = "deploy-crashed", message = e.Message,
                          hint = "Check the most recent DeployRun's notes for details." },
                    statusCode: 502);
            }
        });

        group.MapGet("/deploys", async (string id, DeployRunStore runs, CancellationToken ct) =>
        {
            var list = await runs.ListForProjectAsync(id, ct);
            return Results.Ok(list.Select(ToDto));
        });

        group.MapGet("/deploys/{runId}", async (string id, string runId, DeployRunStore runs, CancellationToken ct) =>
        {
            var r = await runs.GetAsync(runId, ct);
            return r is null ? Results.NotFound() : Results.Ok(ToDto(r));
        });
    }

    private static DeployRunDto ToDto(Plexxer.Client.AiBuilder.DeployRun r) =>
        new(r.Id!, r.status, r.failureReason, r.pierDeployVersion, r.pierFrontendDeployVersion,
            r.deployNotes, r.startedAt, r.finishedAt);
}
