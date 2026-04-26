using System.Text.RegularExpressions;
using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Import;
using AiBuilder.Api.Projects.Scope;
using AiBuilder.Api.Projects.Vcs;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects;

public static class ProjectsEndpoints
{
    // Must match Pier's subdomain regex. Rejects path traversal, uppercase,
    // leading dash. Same regex is used before any path interpolation on disk.
    public static readonly Regex PierAppNameRegex = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    public sealed record CreateRequest(
        string Name,
        string PierAppName,
        string PierApiToken,
        string? PlexxerAppId,
        string? PlexxerApiToken,
        string ScopeBrief,
        string? GitRemoteUrl,
        string? GitRemoteBranch,
        bool   IsImport);

    public sealed record CreateResponse(
        ProjectDto Project,
        ImportReportDto? Import);

    // Per-mode side-effect report. Surfaces clone outcome, env-var mirror
    // outcome, and introspection outcome so the UI can show a meaningful
    // message when one of the best-effort steps failed (e.g. clone failed
    // because the SSH key isn't on the remote).
    public sealed record ImportReportDto(
        string Mode,                    // "new" | "import"
        bool? CloneOk,
        string? CloneError,
        int?    EnvMirroredCount,
        int?    EnvSkippedCount,
        string? EnvMirrorError,
        bool?   IntrospectionOk,
        string? IntrospectionError);

    public sealed record UpdateRequest(
        string? Name,
        string? PierApiToken,
        string? PlexxerAppId,
        string? PlexxerApiToken,
        string? ScopeBrief);

    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization().WithTags("projects");

        group.MapPost("", async (
            CreateRequest req,
            ProjectStore store,
            TokenVerifier verifier,
            WorkspaceManager ws,
            ImportPierEnvMirror envMirror,
            ImportIntrospector introspector,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name-required" });
            if (!PierAppNameRegex.IsMatch(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-invalid",
                    message = "Must match ^[a-z][a-z0-9-]{0,39}$" });
            if (string.IsNullOrWhiteSpace(req.PierApiToken) || string.IsNullOrWhiteSpace(req.ScopeBrief))
                return Results.BadRequest(new { error = "required-fields-missing" });

            // Plexxer creds are optional overall, but must be provided as a
            // pair — appId alone or token alone is a misconfiguration.
            var hasPlexxerAppId = !string.IsNullOrWhiteSpace(req.PlexxerAppId);
            var hasPlexxerToken = !string.IsNullOrWhiteSpace(req.PlexxerApiToken);
            if (hasPlexxerAppId != hasPlexxerToken)
                return Results.BadRequest(new { error = "plexxer-both-or-neither",
                    message = "Plexxer app id and API token must be provided together, or both omitted." });

            // Git remote URL: required for imports, optional for new
            // projects. When provided, must pass the same strict validator
            // we use for the VCS-tab Save action (no embedded credentials,
            // no shell metacharacters, etc.).
            string? normalizedRemoteUrl = null;
            string? normalizedBranch    = null;
            if (req.IsImport && string.IsNullOrWhiteSpace(req.GitRemoteUrl))
                return Results.BadRequest(new { error = "git-remote-url-required-for-import" });
            if (!string.IsNullOrWhiteSpace(req.GitRemoteUrl))
            {
                if (!GitRemoteUrl.TryNormalize(req.GitRemoteUrl, out var url, out var urlErr))
                    return Results.BadRequest(new { error = "invalid-remote-url", reason = urlErr });
                normalizedRemoteUrl = url;
                var branchInput = string.IsNullOrWhiteSpace(req.GitRemoteBranch) ? "master" : req.GitRemoteBranch;
                if (!GitRemoteUrl.TryNormalizeBranch(branchInput, out var branch, out var brErr))
                    return Results.BadRequest(new { error = "invalid-branch", reason = brErr });
                normalizedBranch = branch;
            }

            if (await store.PierAppNameExistsAsync(req.PierAppName, ct))
                return Results.Conflict(new { error = "pier-app-name-already-in-use" });

            // Inline token verification — fail loudly now rather than at deploy.
            var pier = await verifier.VerifyPierAsync(req.PierAppName, req.PierApiToken, ct);
            if (!pier.Ok)
                return Results.BadRequest(new { error = "pier-token-rejected", message = pier.Message });
            if (hasPlexxerAppId)
            {
                var plex = await verifier.VerifyPlexxerAsync(req.PlexxerAppId!, req.PlexxerApiToken!, ct);
                if (!plex.Ok)
                    return Results.BadRequest(new { error = "plexxer-token-rejected", message = plex.Message });
            }

            var now = DateTime.UtcNow;
            var created = await store.CreateAsync(new Project
            {
                name            = req.Name.Trim(),
                pierAppName     = req.PierAppName.Trim(),
                pierApiToken    = req.PierApiToken,
                plexxerAppId    = hasPlexxerAppId ? req.PlexxerAppId!.Trim() : null,
                plexxerApiToken = hasPlexxerToken ? req.PlexxerApiToken    : null,
                scopeBrief      = req.ScopeBrief,
                workspaceStatus = WorkspaceStatus.Draft,
                gitRemoteUrl    = normalizedRemoteUrl,
                gitRemoteBranch = normalizedBranch,
                isImported      = req.IsImport,
                createdAt       = now,
                updatedAt       = now,
            }, ct);

            // For imports: best-effort clone + env-var mirror + introspection.
            // Failures don't roll back the project — the VCS tab can recover.
            ImportReportDto? import = null;
            if (req.IsImport)
            {
                var clone = await ws.CloneAsync(created.pierAppName, normalizedRemoteUrl!, normalizedBranch!, ct);
                ImportPierEnvMirror.MirrorResult? mirror = null;
                ImportIntrospector.IntrospectResult? intro = null;
                if (clone.Ok)
                {
                    mirror = await envMirror.MirrorAsync(created.Id!, created.pierAppName, req.PierApiToken, ct);
                    intro  = await introspector.RunAsync(created.Id!, created.pierAppName, ct);
                }
                import = new ImportReportDto(
                    Mode:               "import",
                    CloneOk:            clone.Ok,
                    CloneError:         clone.Ok ? null : clone.ErrorMessage,
                    EnvMirroredCount:   mirror?.Mirrored,
                    EnvSkippedCount:    mirror?.SkippedRedacted,
                    EnvMirrorError:     mirror?.Error,
                    IntrospectionOk:    intro?.Ok,
                    IntrospectionError: intro?.Error);
            }

            return Results.Created($"/api/projects/{created.Id}",
                new CreateResponse(ToDto(created), import));
        });

        group.MapGet("", async (ProjectStore store, CancellationToken ct) =>
        {
            var all = await store.ListSafeAsync(ct);
            return Results.Ok(all.Select(ToDto));
        });

        group.MapGet("/{id}", async (string id, ProjectStore store, CancellationToken ct) =>
        {
            var p = await store.GetSafeAsync(id, ct);
            return p is null ? Results.NotFound() : Results.Ok(ToDto(p));
        });

        group.MapPost("/{id}/reset", async (
            string id,
            string? confirm,
            ProjectStore projects,
            ConversationStore turns,
            Plexxer.Client.AiBuilder.PlexxerClient plexxer,
            WorkspaceManager ws,
            CancellationToken ct) =>
        {
            var p = await projects.GetSafeAsync(id, ct);
            if (p is null) return Results.NotFound();

            // Require the caller to echo the pierAppName so a misclick on
            // the Reset button can't silently nuke the project's history.
            if (!string.Equals(confirm, p.pierAppName, StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    error = "confirm-required",
                    message = $"Pass ?confirm={p.pierAppName} to reset this project.",
                });

            // Plexxer-side deletes. Do entities first (child records) so a
            // partial failure doesn't orphan them under a missing project.
            await plexxer.DeleteAsync<TargetEnvVar>   (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<DeployRun>      (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<BuildRun>       (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<ConversationTurn>(new Dictionary<string, object?> { ["project:eq"] = id }, ct);

            // Filesystem-side wipe — nuke the workspace dir (source, git
            // history, .aibuilder logs, shared caches stay untouched since
            // they live under build-home not projects/<appname>).
            try
            {
                var workspace = ws.ResolvePath(p.pierAppName);
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (Exception e)
            {
                // Workspace wipe failures are recoverable (admin can delete
                // on disk); report but still flip status so the project is
                // usable from the UI.
                return Results.Ok(new { status = "partial", workspaceWipeError = e.Message });
            }

            // Flip state back to Draft. Bypass the state machine because the
            // valid transitions don't include 'anywhere -> Draft'; this is
            // an explicit admin override.
            await projects.UpdateFieldsAsync(id, new Dictionary<string, object?>
            {
                ["workspaceStatus"] = WorkspaceStatus.Draft,
            }, ct);

            return Results.Ok(new { status = "ok", workspaceStatus = WorkspaceStatus.Draft });
        });

        group.MapPatch("/{id}", async (string id, UpdateRequest req, ProjectStore store, TokenVerifier verifier, CancellationToken ct) =>
        {
            var existing = await store.GetWithSecretsAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var patch = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(req.Name)) patch["name"] = req.Name.Trim();
            if (req.ScopeBrief is not null)           patch["scopeBrief"] = req.ScopeBrief;

            if (!string.IsNullOrWhiteSpace(req.PierApiToken))
            {
                var pier = await verifier.VerifyPierAsync(existing.pierAppName, req.PierApiToken, ct);
                if (!pier.Ok) return Results.BadRequest(new { error = "pier-token-rejected", message = pier.Message });
                patch["pierApiToken"] = req.PierApiToken;
            }
            // Allow setting both Plexxer fields in one PATCH (e.g. configuring
            // Plexxer on a project that was created without it). Clear both
            // if the admin sends empty strings for both.
            var newAppId = req.PlexxerAppId;
            var newToken = req.PlexxerApiToken;
            var effectiveAppId = newAppId ?? existing.plexxerAppId;
            var effectiveToken = newToken ?? existing.plexxerApiToken;
            if (newAppId is not null || newToken is not null)
            {
                var hasEff = !string.IsNullOrWhiteSpace(effectiveAppId) || !string.IsNullOrWhiteSpace(effectiveToken);
                var bothEff = !string.IsNullOrWhiteSpace(effectiveAppId) && !string.IsNullOrWhiteSpace(effectiveToken);
                if (hasEff && !bothEff)
                    return Results.BadRequest(new { error = "plexxer-both-or-neither" });
                if (bothEff)
                {
                    var plex = await verifier.VerifyPlexxerAsync(effectiveAppId!, effectiveToken!, ct);
                    if (!plex.Ok) return Results.BadRequest(new { error = "plexxer-token-rejected", message = plex.Message });
                }
                if (newAppId is not null) patch["plexxerAppId"]    = string.IsNullOrWhiteSpace(newAppId) ? null : newAppId.Trim();
                if (newToken is not null) patch["plexxerApiToken"] = string.IsNullOrWhiteSpace(newToken) ? null : newToken;
            }

            if (patch.Count == 0) return Results.Ok(ToDto(await SafeAfterPatchAsync(store, id, ct) ?? existing));

            await store.UpdateFieldsAsync(id, patch, ct);
            var after = await store.GetSafeAsync(id, ct);
            return Results.Ok(ToDto(after ?? existing));
        });
    }

    private static async Task<Project?> SafeAfterPatchAsync(ProjectStore store, string id, CancellationToken ct) =>
        await store.GetSafeAsync(id, ct);

    // Safe DTO — never includes *ApiToken. Stamped with the fields we actually
    // want the browser to see.
    public sealed record ProjectDto(
        string Id,
        string Name,
        string PierAppName,
        string? PlexxerAppId,
        string ScopeBrief,
        string WorkspaceStatus,
        bool IsImported,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    public static ProjectDto ToDto(Project p) => new(
        p.Id!, p.name, p.pierAppName, p.plexxerAppId, p.scopeBrief,
        p.workspaceStatus, p.isImported ?? false, p.createdAt, p.updatedAt);
}
