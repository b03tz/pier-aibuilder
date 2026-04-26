using System.Text.RegularExpressions;
using AiBuilder.Api.Config;
using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Deploy;
using AiBuilder.Api.Projects.Import;
using AiBuilder.Api.Projects.Provisioning;
using AiBuilder.Api.Projects.Scope;
using AiBuilder.Api.Projects.Vcs;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects;

public static class ProjectsEndpoints
{
    // Must match Pier's subdomain regex (`^[a-z][a-z0-9-]{1,30}$`, total
    // length 2..31). Rejects path traversal, uppercase, leading dash, and
    // anything Pier itself would 400 on. Same regex is used before any
    // path interpolation on disk.
    public static readonly Regex PierAppNameRegex = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.Compiled);

    public sealed record CreateRequest(
        string Name,
        string? PierAppName,        // optional when AutoCreateOnPier=true (we derive a slug)
        string? PierApiToken,       // required only on the manual + import paths
        string? PlexxerAppId,
        string? PlexxerApiToken,
        string ScopeBrief,
        string? GitRemoteUrl,
        string? GitRemoteBranch,
        bool   IsImport,
        bool?  AutoCreateOnPier,    // null defaults to true on new projects (when admin configured)
        bool?  HasFrontend);        // null defaults to true; only honoured on auto-create

    public sealed record CreateResponse(
        ProjectDto Project,
        ImportReportDto?     Import,
        ProvisioningReportDto? Provisioning);

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

    // Surfaced only on the auto-create branch. Reports what got created
    // on Pier and how the post-create env-seed call went; lets the UI
    // show "Created Pier app `foo`" + a green/yellow indicator.
    public sealed record ProvisioningReportDto(
        string  PierAppName,
        string  ApiBaseUrl,
        string  ApiDomain,
        string? FrontendDomain,
        bool    EnvSeedOk,
        string? EnvSeedError);

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
            PierAdminClient pierAdmin,
            PierEnv pierEnv,
            IHttpClientFactory httpFactory,
            ILogger<Marker> log,
            CancellationToken ct) =>
        {
            // ---- Field validation that applies to every flow ----------------
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name-required" });
            if (string.IsNullOrWhiteSpace(req.ScopeBrief))
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

            // ---- Branch selection ------------------------------------------
            // Auto-create only applies to new projects (not imports) and only
            // when the host is configured for it. The flag defaults true on
            // new projects so the friction-free path is the default.
            var autoCreate = !req.IsImport
                          && pierAdmin.Configured
                          && (req.AutoCreateOnPier ?? true);

            // Plexxer creds (when present) are verified once up-front so we
            // fail fast before touching Pier. Same shape on every branch.
            if (hasPlexxerAppId)
            {
                var plex = await verifier.VerifyPlexxerAsync(req.PlexxerAppId!, req.PlexxerApiToken!, ct);
                if (!plex.Ok)
                    return Results.BadRequest(new { error = "plexxer-token-rejected", message = plex.Message });
            }

            if (autoCreate)
            {
                return await CreateWithPierAutoBootstrapAsync(
                    req, store, pierAdmin, pierEnv, httpFactory, log,
                    hasPlexxerAppId, hasPlexxerToken,
                    normalizedRemoteUrl, normalizedBranch,
                    ct);
            }

            // ---- Manual / import flow (existing behaviour) ------------------
            // pierAppName + pierApiToken are required: either the user
            // provided them at creation time, or it's an import that points
            // at an existing Pier app.
            if (string.IsNullOrWhiteSpace(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-required" });
            if (!PierAppNameRegex.IsMatch(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-invalid",
                    message = "Must match ^[a-z][a-z0-9-]{1,30}$ (2..31 lowercase chars, digits or hyphens, starting with a letter)" });
            if (string.IsNullOrWhiteSpace(req.PierApiToken))
                return Results.BadRequest(new { error = "pier-token-required" });

            if (await store.PierAppNameExistsAsync(req.PierAppName, ct))
                return Results.Conflict(new { error = "pier-app-name-already-in-use" });

            // Inline token verification — fail loudly now rather than at deploy.
            var pier = await verifier.VerifyPierAsync(req.PierAppName, req.PierApiToken, ct);
            if (!pier.Ok)
                return Results.BadRequest(new { error = "pier-token-rejected", message = pier.Message });

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
                    mirror = await envMirror.MirrorAsync(created.Id!, created.pierAppName, req.PierApiToken!, ct);
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
                new CreateResponse(ToDto(created), import, null));
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

        // ----------------------------------------------------------------
        // Hard delete. Wipes the project from every system the admin opted
        // into in the dialog, then removes the AiBuilder record itself.
        // External-system actions run first so a partial failure (Pier
        // unreachable, Plexxer 403, etc.) leaves the AiBuilder record
        // intact and the admin can retry the same call.
        //
        // The `confirm` query param must be the literal string
        // "delete <pierAppName>" — both the verb and the name. Reset
        // already protects against single-name typos; delete bumps the
        // bar a notch higher because it's irreversible.
        // ----------------------------------------------------------------
        group.MapDelete("/{id}", async (
            string id,
            string? confirm,
            bool? deletePierApp,
            bool? deletePlexxerSchemas,
            ProjectStore projects,
            ConversationStore turns,
            PlexxerClient plexxer,
            WorkspaceManager ws,
            PierAdminClient pierAdmin,
            PlexxerSchemaWiper schemaWiper,
            ILogger<Marker> log,
            CancellationToken ct) =>
        {
            var project = await projects.GetWithSecretsAsync(id, ct);
            if (project is null) return Results.NotFound();

            // Confirm phrase: literal "delete <pierAppName>" — case-sensitive.
            // Catches both "I clicked the wrong row" and "I forgot which
            // app I was looking at" because the admin must type both the
            // verb and the name.
            var expectedPhrase = $"delete {project.pierAppName}";
            if (!string.Equals(confirm, expectedPhrase, StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    error = "confirm-required",
                    message = $"Pass ?confirm={Uri.EscapeDataString(expectedPhrase)} to delete this project.",
                });

            var doPier    = deletePierApp        ?? false;
            var doPlexxer = deletePlexxerSchemas ?? false;

            // Per-step report so the UI can show "Pier app deleted, 3 Plexxer
            // schemas wiped, AiBuilder record removed". Each step appends.
            var pierResult    = "skipped";
            var plexxerResult = "skipped";

            // ---- 1. Delete Pier app (admin-API). Fail-fast. ----
            if (doPier)
            {
                if (!pierAdmin.Configured)
                    return Results.BadRequest(new
                    {
                        error = "pier-admin-not-configured",
                        message = "Pier admin token is not set on this AiBuilder host; un-tick 'Delete the Pier app' or configure PIER_ADMIN_TOKEN first."
                    });
                try
                {
                    await pierAdmin.DeleteAppAsync(project.pierAppName, $"aibuilder/{project.pierAppName}", ct);
                    pierResult = "deleted";
                }
                catch (PierAdminClient.PierAdminError e)
                {
                    log.LogWarning("Delete project: Pier admin DELETE failed for {App}: {Code}", project.pierAppName, e.Code);
                    return MapAdminErrorToResponse(e);
                }
            }

            // ---- 2. Wipe Plexxer schemas. Fail-fast on permission errors. ----
            if (doPlexxer)
            {
                if (string.IsNullOrWhiteSpace(project.plexxerAppId) || string.IsNullOrWhiteSpace(project.plexxerApiToken))
                    return Results.BadRequest(new
                    {
                        error = "plexxer-not-configured",
                        message = "This project has no Plexxer credentials; un-tick 'Wipe the Plexxer schemas' to proceed."
                    });
                try
                {
                    var wipe = await schemaWiper.WipeAsync(project.plexxerAppId!, project.plexxerApiToken!, ct);
                    plexxerResult = $"wiped {wipe.Deleted} schema(s); {wipe.SkippedAlreadyGone.Count} already gone";
                }
                catch (PlexxerSchemaWiper.PlexxerSchemaWipeError e)
                {
                    log.LogWarning("Delete project: Plexxer schema wipe failed for {AppKey} entity={Entity}: {Code}",
                        project.plexxerAppId, e.Entity, e.Code);
                    return Results.Problem(
                        title: e.Code,
                        detail: e.Entity is null
                            ? $"Plexxer schema wipe failed (HTTP {e.Status}): {e.Detail}"
                            : $"Plexxer schema wipe failed on entity '{e.Entity}' (HTTP {e.Status}): {e.Detail}",
                        statusCode: e.Code is "plexxer-permission-denied" or "plexxer-token-invalid" ? 400 : 502);
                }
            }

            // ---- 3. Delete child Plexxer rows in AiBuilder's own store. ----
            // Children before parent so a transient failure doesn't orphan
            // them under a missing Project.
            await plexxer.DeleteAsync<TargetEnvVar>     (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<DeployRun>        (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<BuildRun>         (new Dictionary<string, object?> { ["project:eq"] = id }, ct);
            await plexxer.DeleteAsync<ConversationTurn> (new Dictionary<string, object?> { ["project:eq"] = id }, ct);

            // ---- 4. Wipe the on-disk workspace. ----
            string? workspaceWarning = null;
            try
            {
                var workspace = ws.ResolvePath(project.pierAppName);
                if (Directory.Exists(workspace))
                    Directory.Delete(workspace, recursive: true);
            }
            catch (Exception e)
            {
                // Same posture as Reset: workspace wipe is best-effort.
                // The admin can rm -rf the dir manually if a file lock
                // briefly prevents the recursive delete.
                workspaceWarning = e.Message;
            }

            // ---- 5. Delete the AiBuilder Project record itself. ----
            await projects.DeleteAsync(id, ct);

            return Results.Ok(new
            {
                status         = "ok",
                pier           = pierResult,
                plexxer        = plexxerResult,
                workspace      = workspaceWarning is null ? "wiped" : $"warning: {workspaceWarning}",
                projectRecord  = "deleted",
            });
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

    // Marker type so we can request a categorised ILogger without
    // declaring a static class as the category. Keeps log lines neatly
    // tagged "AiBuilder.Api.Projects.ProjectsEndpoints" instead of a
    // generic "Default".
    public sealed class Marker { }

    // Auto-create branch — derives a Pier-valid slug from the project
    // name, reserves it on Pier via the admin-API, and persists the
    // resulting Project record. Designed to leave the system tidy on
    // every failure path:
    //
    //   * If we can't find a free slug after 5 attempts, nothing is
    //     created and the user gets a clear conflict error.
    //   * If the Pier admin-API call fails, no Pier app exists (Pier
    //     either rejected the request or never received it) and we
    //     never created our Plexxer Project, so there's nothing to
    //     clean up.
    //   * If the post-create env-var seed fails, the Pier app + our
    //     Project record are both healthy — we surface a warning in
    //     the response but don't roll back; the user can re-seed via
    //     the Deploy tab manually.
    private static async Task<IResult> CreateWithPierAutoBootstrapAsync(
        CreateRequest req,
        ProjectStore store,
        PierAdminClient pierAdmin,
        PierEnv pierEnv,
        IHttpClientFactory httpFactory,
        ILogger<Marker> log,
        bool hasPlexxerAppId,
        bool hasPlexxerToken,
        string? normalizedRemoteUrl,
        string? normalizedBranch,
        CancellationToken ct)
    {
        var hasFrontend = req.HasFrontend ?? true;

        // The user can supply their own slug as an override (free-form
        // text in the form's "advanced" section). When they don't, derive
        // one from the project name.
        string baseSlug;
        if (!string.IsNullOrWhiteSpace(req.PierAppName))
        {
            // The override must already be Pier-valid; we don't try to
            // re-normalise it because that would silently rewrite the
            // user's intent. Reject loudly instead.
            if (!PierAppNameRegex.IsMatch(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-invalid",
                    message = "Override must match ^[a-z][a-z0-9-]{1,30}$" });
            baseSlug = req.PierAppName.Trim();
        }
        else
        {
            try { baseSlug = PierAppSlug.Derive(req.Name); }
            catch (Exception e)
            {
                log.LogWarning(e, "PierAppSlug.Derive threw on input '{Name}'", req.Name);
                return Results.BadRequest(new { error = "pier-app-slug-derivation-failed",
                    message = "Could not derive a valid Pier app name from the project name." });
            }
        }

        const int maxAttempts = 5;
        var originator = $"aibuilder/{baseSlug}";

        // Find the first slug that's free both locally and on Pier. Both
        // sides because (a) Pier may already own a name we don't track and
        // (b) we don't want two Plexxer Projects sharing a pierAppName.
        string? slug = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string candidate;
            try
            {
                candidate = attempt == 1 ? baseSlug : PierAppSlug.WithCollisionSuffix(baseSlug, attempt);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Could not generate slug suffix for base '{Base}' attempt {Attempt}", baseSlug, attempt);
                break;
            }

            if (await store.PierAppNameExistsAsync(candidate, ct))
                continue;

            try
            {
                if (await pierAdmin.AppExistsAsync(candidate, originator, ct))
                    continue;
            }
            catch (PierAdminClient.PierAdminError e)
            {
                // Surface admin-API errors immediately — a 401/403/500 here
                // means the create call would also fail, and we'd rather
                // tell the user "your admin token is invalid" than silently
                // race past it.
                return MapAdminErrorToResponse(e);
            }

            slug = candidate;
            break;
        }
        if (slug is null)
            return Results.Conflict(new { error = "pier-app-slug-no-free-name",
                message = $"Could not find an unused Pier app name after {maxAttempts} attempts. Try a different project name or set an explicit override." });

        // ---- Reserve on Pier (the only step that could leave external state) ----
        PierAdminClient.BootstrapResult result;
        try
        {
            result = await pierAdmin.CreateAppAsync(
                new PierAdminClient.CreateAppRequest(
                    Name:         slug,
                    HasFrontend:  hasFrontend,
                    Category:     "AiBuilder",
                    MintApiToken: true),
                originator,
                ct);
        }
        catch (PierAdminClient.PierAdminError e)
        {
            // No Pier app exists at this point (Pier either rejected with
            // a 4xx pre-create or the network call never landed). Nothing
            // to roll back.
            return MapAdminErrorToResponse(e);
        }

        // ---- Persist our Plexxer Project with the minted token ----
        Project created;
        try
        {
            var now = DateTime.UtcNow;
            created = await store.CreateAsync(new Project
            {
                name            = req.Name.Trim(),
                pierAppName     = result.AppName,
                pierApiToken    = result.ApiToken,
                plexxerAppId    = hasPlexxerAppId ? req.PlexxerAppId!.Trim() : null,
                plexxerApiToken = hasPlexxerToken ? req.PlexxerApiToken    : null,
                scopeBrief      = req.ScopeBrief,
                workspaceStatus = WorkspaceStatus.Draft,
                gitRemoteUrl    = normalizedRemoteUrl,
                gitRemoteBranch = normalizedBranch,
                isImported      = false,
                createdAt       = now,
                updatedAt       = now,
            }, ct);
        }
        catch (Exception e)
        {
            // We have a Pier app but no Plexxer record. Leaving an orphan
            // is worse than a noisy error, but admin DELETE is out of
            // scope for v1 — log loudly and surface a clear message. The
            // user can clean up via Pier's UI; once `/admin-api/apps/{x}
            // DELETE` is allowed for AiBuilder we'll close this gap.
            log.LogError(e,
                "Plexxer Project create failed AFTER Pier admin-API created app '{Slug}'. " +
                "Pier app is orphaned and must be deleted manually via Pier's UI.",
                result.AppName);
            return Results.Problem(
                title: "pier-app-orphaned",
                detail: $"Pier app '{result.AppName}' was created, but persisting the AiBuilder project failed. " +
                        $"Delete '{result.AppName}' in Pier's admin UI to retry, then try again.",
                statusCode: 500);
        }

        // ---- Best-effort post-create env seed (PIER_API_TOKEN, PIER_API_BASE) ----
        // Per the spec these live as env vars on the new Pier app so the
        // running app can call its own /api/{name}/* surface if it ever
        // needs to. They use the per-app token, not the admin token.
        // Defer the restart per the locked decisions — first /deploy will
        // restart organically.
        var (envOk, envErr) = await SeedNewAppEnvAsync(httpFactory, result, log, ct);

        var dto = ToDto(created);
        var report = new ProvisioningReportDto(
            PierAppName:    result.AppName,
            ApiBaseUrl:     result.ApiBaseUrl,
            ApiDomain:      result.ApiDomain,
            FrontendDomain: result.FrontendDomain,
            EnvSeedOk:      envOk,
            EnvSeedError:   envErr);

        return Results.Created($"/api/projects/{created.Id}",
            new CreateResponse(dto, null, report));
    }

    private static async Task<(bool ok, string? error)> SeedNewAppEnvAsync(
        IHttpClientFactory httpFactory,
        PierAdminClient.BootstrapResult result,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            var pier = new PierClient(httpFactory.CreateClient(), result.AppName, result.ApiToken);
            // PIER_API_TOKEN is a secret (it's the live deploy token);
            // PIER_API_BASE is plain config. Neither is exposeToFrontend.
            var t = await pier.PutEnvAsync("PIER_API_TOKEN",
                new PierClient.PutEnvBody(result.ApiToken, isSecret: true,  exposeToFrontend: false), ct);
            if (!t.IsSuccessStatusCode)
                return (false, $"PIER_API_TOKEN PUT returned {(int)t.StatusCode}");
            var b = await pier.PutEnvAsync("PIER_API_BASE",
                new PierClient.PutEnvBody(result.ApiBaseUrl, isSecret: false, exposeToFrontend: false), ct);
            if (!b.IsSuccessStatusCode)
                return (false, $"PIER_API_BASE PUT returned {(int)b.StatusCode}");
            return (true, null);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Post-create env seed failed for new Pier app {Name}", result.AppName);
            return (false, e.Message);
        }
    }

    // Stable mapping from PierAdminClient.PierAdminError to a controller
    // response. Never includes the admin token or the request body —
    // only the structured code + a short, scrubbed detail.
    private static IResult MapAdminErrorToResponse(PierAdminClient.PierAdminError e) => e.Code switch
    {
        "pier-admin-not-configured" =>
            Results.Problem(title: e.Code,
                detail: "PIER_ADMIN_TOKEN is not set on this AiBuilder host; configure it in Pier env vars to enable auto-create.",
                statusCode: 503),
        "pier-admin-token-invalid" =>
            Results.Problem(title: e.Code,
                detail: "Pier rejected the admin token. Regenerate it in Pier's Settings page and update PIER_ADMIN_TOKEN.",
                statusCode: 502),
        "pier-admin-origin-rejected" =>
            Results.Problem(title: e.Code,
                detail: "Pier rejected the origin. AiBuilder must reach Pier over loopback (or from an allowlisted IP). Check PIER_ADMIN_BASE.",
                statusCode: 502),
        "pier-admin-name-conflict" =>
            Results.Conflict(new { error = e.Code, message = "Pier reports the requested name is already taken." }),
        "pier-validation-failed" =>
            Results.BadRequest(new { error = e.Code, message = e.Detail }),
        "pier-admin-rate-limited" =>
            Results.Problem(title: e.Code, detail: "Pier admin-API rate limit reached; try again shortly.", statusCode: 503),
        "pier-admin-unreachable" =>
            Results.Problem(title: e.Code, detail: "Pier admin-API is unreachable from this host. Verify PIER_ADMIN_BASE and network.", statusCode: 502),
        _ =>
            Results.Problem(title: e.Code, detail: $"Unexpected admin-API failure (HTTP {e.Status}).", statusCode: 502),
    };

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
