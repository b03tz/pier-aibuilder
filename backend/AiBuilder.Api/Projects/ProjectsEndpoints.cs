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
        string? PierAppName,        // dual-purpose: manual-flow paste field, OR slug override on auto-create
        string? PierApiToken,       // required only on the manual + import + Pier=existing paths
        string? PlexxerAppId,       // required when AutoCreateOnPlexxer=false on a new project + always on import
        string? PlexxerApiToken,
        string ScopeBrief,
        string? GitRemoteUrl,
        string? GitRemoteBranch,
        bool   IsImport,
        bool?  AutoCreateOnPier,    // null defaults to true on new projects (when admin configured)
        bool?  AutoCreateOnPlexxer, // null defaults to true on new projects (when account configured)
        bool?  HasFrontend);        // null defaults to true; only honoured on Pier auto-create

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

    // Surfaced on the auto-create branch (Pier and/or Plexxer). Reports
    // what each side produced so the UI can render a friendly "Created X"
    // confirmation and a green/yellow indicator on the post-create env-
    // var seed call. Each pair of fields is null when the corresponding
    // side wasn't auto-created (the user pasted existing creds).
    public sealed record ProvisioningReportDto(
        // Pier side
        bool    PierAutoCreated,
        string? PierAppName,
        string? ApiBaseUrl,
        string? ApiDomain,
        string? FrontendDomain,
        bool?   EnvSeedOk,
        string? EnvSeedError,
        // Plexxer side
        bool    PlexxerAutoCreated,
        string? PlexxerAppKey);

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
            PlexxerAdminClient plexxerAdmin,
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
            // when the host is configured for it. Both flags default true on
            // new projects so the friction-free path is the default.
            var autoCreatePier    = !req.IsImport
                                 && pierAdmin.Configured
                                 && (req.AutoCreateOnPier ?? true);
            var autoCreatePlexxer = !req.IsImport
                                 && plexxerAdmin.Configured
                                 && (req.AutoCreateOnPlexxer ?? true);

            // Mutual-exclusion checks. Auto-create and pasted creds for the
            // same provider would be ambiguous — fail loudly. We don't
            // silently prefer one over the other because that would mask
            // user error.
            if (autoCreatePlexxer && hasPlexxerAppId)
                return Results.BadRequest(new { error = "plexxer-creds-with-auto-create",
                    message = "Plexxer auto-create is on but plexxerAppId/plexxerApiToken were also supplied. Choose one." });
            if (autoCreatePier && !string.IsNullOrWhiteSpace(req.PierApiToken))
                return Results.BadRequest(new { error = "pier-token-with-auto-create",
                    message = "Pier auto-create is on but pierApiToken was also supplied. Choose one." });

            // Plexxer creds (when present) are verified once up-front so we
            // fail fast before touching anything. Same shape on every branch.
            if (hasPlexxerAppId)
            {
                var plex = await verifier.VerifyPlexxerAsync(req.PlexxerAppId!, req.PlexxerApiToken!, ct);
                if (!plex.Ok)
                    return Results.BadRequest(new { error = "plexxer-token-rejected", message = plex.Message });
            }

            if (autoCreatePier || autoCreatePlexxer)
            {
                return await CreateWithAutoBootstrapAsync(
                    req, store, verifier, pierAdmin, plexxerAdmin, pierEnv, httpFactory, log,
                    autoCreatePier, autoCreatePlexxer,
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
            bool? deletePlexxerApp,
            bool? forcePlexxerError,
            ProjectStore projects,
            ConversationStore turns,
            PlexxerClient plexxer,
            WorkspaceManager ws,
            PierAdminClient pierAdmin,
            PlexxerAdminClient plexxerAdmin,
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

            var doPier    = deletePierApp     ?? false;
            var doPlexxer = deletePlexxerApp  ?? false;
            var force     = forcePlexxerError ?? false;

            // Per-step report so the UI can show "Pier app deleted, Plexxer
            // app deleted, AiBuilder record removed". Each step appends.
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

            // ---- 2. Delete the Plexxer app (account-API). Fail-fast unless
            // `force` is set — the override is for cases like "the user
            // already deleted the app in Plexxer's dashboard" or "the
            // account token doesn't have grants for this particular app".
            // 404-already-gone is silently treated as success by
            // PlexxerAdminClient.DeleteAppAsync, so the simple manual-delete
            // case doesn't need force. ----
            if (doPlexxer)
            {
                if (string.IsNullOrWhiteSpace(project.plexxerAppId))
                    return Results.BadRequest(new
                    {
                        error = "plexxer-not-configured",
                        message = "This project has no Plexxer app id; un-tick 'Delete the Plexxer app' to proceed."
                    });
                if (!plexxerAdmin.Configured)
                    return Results.BadRequest(new
                    {
                        error = "plexxer-account-not-configured",
                        message = "Plexxer account token is not set on this AiBuilder host; un-tick 'Delete the Plexxer app' or configure PLEXXER_ACCOUNT_TOKEN first."
                    });
                try
                {
                    await plexxerAdmin.DeleteAppAsync(project.plexxerAppId!, ct);
                    plexxerResult = "deleted";
                }
                catch (PlexxerAdminClient.PlexxerAdminError e)
                {
                    log.LogWarning("Delete project: Plexxer admin DELETE failed for {AppKey}: {Code} (force={Force})",
                        project.plexxerAppId, e.Code, force);
                    if (!force) return MapPlexxerAdminErrorToResponse(e);
                    plexxerResult = $"force-skipped: {e.Code}";
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
    // Combined Pier + Plexxer auto-create flow. Handles four cases:
    //   * Pier=auto, Plexxer=auto      (the new default for new projects)
    //   * Pier=auto, Plexxer=existing  (use the supplied plexxerAppId/Token)
    //   * Pier=existing, Plexxer=auto  (use the supplied pierAppName/Token)
    //   * (Pier=existing, Plexxer=existing falls through to the manual path
    //     above and never reaches this method.)
    //
    // Order is **Plexxer first, Pier second** by Patrick's call. Plexxer's
    // DELETE keys on the server-assigned appKey we just received from
    // POST /apps; Pier's DELETE keys on a name we minted in this same call.
    // By creating Plexxer first, a Plexxer failure means we never touched
    // Pier at all. Hard invariant: every rollback uses an identifier from
    // the immediately-preceding successful create, never a user-supplied
    // or DB-read value. The tokens are powerful — we never want to touch
    // an unrelated app by accident.
    private static async Task<IResult> CreateWithAutoBootstrapAsync(
        CreateRequest req,
        ProjectStore store,
        TokenVerifier verifier,
        PierAdminClient pierAdmin,
        PlexxerAdminClient plexxerAdmin,
        PierEnv pierEnv,
        IHttpClientFactory httpFactory,
        ILogger<Marker> log,
        bool autoCreatePier,
        bool autoCreatePlexxer,
        bool hasPlexxerAppId,
        bool hasPlexxerToken,
        string? normalizedRemoteUrl,
        string? normalizedBranch,
        CancellationToken ct)
    {
        var hasFrontend = req.HasFrontend ?? true;

        // ---- Resolve the shared slug (Pier name + Plexxer display name) ---
        string baseSlug;
        if (!string.IsNullOrWhiteSpace(req.PierAppName))
        {
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

        // ---- If Pier=existing, the user supplied creds — verify NOW so we
        // don't waste a Plexxer create on a bad Pier token. ----
        string? existingPierAppName = null;
        string? existingPierApiToken = null;
        if (!autoCreatePier)
        {
            if (string.IsNullOrWhiteSpace(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-required" });
            if (!PierAppNameRegex.IsMatch(req.PierAppName))
                return Results.BadRequest(new { error = "pier-app-name-invalid",
                    message = "Must match ^[a-z][a-z0-9-]{1,30}$" });
            if (string.IsNullOrWhiteSpace(req.PierApiToken))
                return Results.BadRequest(new { error = "pier-token-required" });
            if (await store.PierAppNameExistsAsync(req.PierAppName, ct))
                return Results.Conflict(new { error = "pier-app-name-already-in-use" });
            var pier = await verifier.VerifyPierAsync(req.PierAppName, req.PierApiToken, ct);
            if (!pier.Ok)
                return Results.BadRequest(new { error = "pier-token-rejected", message = pier.Message });
            existingPierAppName  = req.PierAppName.Trim();
            existingPierApiToken = req.PierApiToken;
        }

        // ---- If Plexxer=existing, pre-flight already verified above ----

        // ===============================================================
        // STEP 1: Plexxer auto-create (if requested). On any failure no
        // rollback is needed — Pier hasn't been touched.
        // ===============================================================
        string? plexxerAppKey      = null;   // server-assigned, used for rollback DELETE
        string? plexxerAppToken    = null;   // plaintext minted token, persisted on the project
        if (autoCreatePlexxer)
        {
            try
            {
                var created = await plexxerAdmin.CreateAppAsync(
                    new PlexxerAdminClient.CreateAppRequest(Name: baseSlug),
                    ct);
                plexxerAppKey = created.AppKey;
            }
            catch (PlexxerAdminClient.PlexxerAdminError e)
            {
                return MapPlexxerAdminErrorToResponse(e);
            }

            try
            {
                var minted = await plexxerAdmin.MintAppTokenAsync(
                    plexxerAppKey,
                    new PlexxerAdminClient.MintTokenRequest(
                        Label: $"aibuilder-{baseSlug}",
                        // Plexxer's app-scoped mint validator requires at
                        // least one key that isn't `app:*` or `account:*`
                        // (i.e. a per-entity grant). Brand-new apps have
                        // no entities yet — chicken-and-egg — so we mint
                        // with the sentinel `_init: rw`. Claude widens
                        // the token via `app:tokens:rw` PATCH after
                        // publishing schemas; the sentinel can be left in
                        // or removed at that point. The leading
                        // underscore guarantees no collision with a
                        // legit user-defined entity name.
                        Permissions: new Dictionary<string, string>
                        {
                            ["_init"]            = "rw",
                            ["app:schemas"]      = "rw",
                            ["app:tokens"]       = "rw",
                            ["app:backups"]      = "rw",
                            ["app:meta-samples"] = "y",
                            ["app:client"]       = "y",
                        }),
                    ct);
                plexxerAppToken = minted.PlaintextToken;
            }
            catch (PlexxerAdminClient.PlexxerAdminError e)
            {
                // Token mint failed; roll back the Plexxer app we just
                // created. appKey came from the line above — never from
                // user input.
                await TryRollbackPlexxerAsync(plexxerAdmin, plexxerAppKey, log, "mint-token-failed", ct);
                return MapPlexxerAdminErrorToResponse(e);
            }
        }

        // ===============================================================
        // STEP 2: Pier auto-create (if requested). On any failure, roll
        // back the Plexxer app from step 1 (if it ran).
        // ===============================================================
        PierAdminClient.BootstrapResult? pierResult = null;
        if (autoCreatePier)
        {
            const int maxAttempts = 5;
            var originator = $"aibuilder/{baseSlug}";

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
                    await TryRollbackPlexxerAsync(plexxerAdmin, plexxerAppKey, log, "pier-existence-check-failed", ct);
                    return MapAdminErrorToResponse(e);
                }

                slug = candidate;
                break;
            }
            if (slug is null)
            {
                await TryRollbackPlexxerAsync(plexxerAdmin, plexxerAppKey, log, "pier-slug-exhausted", ct);
                return Results.Conflict(new { error = "pier-app-slug-no-free-name",
                    message = $"Could not find an unused Pier app name after {maxAttempts} attempts. Try a different project name or set an explicit override." });
            }

            try
            {
                pierResult = await pierAdmin.CreateAppAsync(
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
                await TryRollbackPlexxerAsync(plexxerAdmin, plexxerAppKey, log, "pier-create-failed", ct);
                return MapAdminErrorToResponse(e);
            }
        }

        // ===============================================================
        // STEP 3: Persist the AiBuilder Project record. On failure, roll
        // back BOTH external creates (whichever ran).
        // ===============================================================
        var resolvedPierAppName  = pierResult?.AppName  ?? existingPierAppName!;
        var resolvedPierApiToken = pierResult?.ApiToken ?? existingPierApiToken!;
        var resolvedPlexxerAppId    = autoCreatePlexxer ? plexxerAppKey   : (hasPlexxerAppId ? req.PlexxerAppId!.Trim() : null);
        var resolvedPlexxerApiToken = autoCreatePlexxer ? plexxerAppToken : (hasPlexxerToken ? req.PlexxerApiToken    : null);

        Project created2;
        try
        {
            var now = DateTime.UtcNow;
            created2 = await store.CreateAsync(new Project
            {
                name            = req.Name.Trim(),
                pierAppName     = resolvedPierAppName,
                pierApiToken    = resolvedPierApiToken,
                plexxerAppId    = resolvedPlexxerAppId,
                plexxerApiToken = resolvedPlexxerApiToken,
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
            // Both rollbacks use identifiers from the immediately-preceding
            // successful create. Never user input, never DB lookup.
            log.LogError(e,
                "Plexxer Project create failed after auto-bootstrap (pierAuto={PierAuto} plexxerAuto={PlexxerAuto}); rolling back",
                autoCreatePier, autoCreatePlexxer);
            await TryRollbackPierAsync(pierAdmin, pierResult?.AppName, log, "project-persist-failed", ct);
            await TryRollbackPlexxerAsync(plexxerAdmin, plexxerAppKey, log, "project-persist-failed", ct);
            return Results.Problem(
                title: "project-persist-failed",
                detail: "AiBuilder project record could not be persisted. Any newly-created Pier or Plexxer apps have been rolled back. Try again.",
                statusCode: 500);
        }

        // ===============================================================
        // STEP 4: Best-effort post-create env seed on the new Pier app
        // (only if Pier was auto-created — env seed is for the brand-new
        // app's runtime only).
        // ===============================================================
        bool? envSeedOk = null;
        string? envSeedErr = null;
        if (pierResult is not null)
        {
            (envSeedOk, envSeedErr) = await SeedNewAppEnvAsync(httpFactory, pierResult, log, ct);
        }

        var dto = ToDto(created2);
        var report = new ProvisioningReportDto(
            PierAutoCreated:    pierResult is not null,
            PierAppName:        pierResult?.AppName,
            ApiBaseUrl:         pierResult?.ApiBaseUrl,
            ApiDomain:          pierResult?.ApiDomain,
            FrontendDomain:     pierResult?.FrontendDomain,
            EnvSeedOk:          envSeedOk,
            EnvSeedError:       envSeedErr,
            PlexxerAutoCreated: plexxerAppKey is not null,
            PlexxerAppKey:      plexxerAppKey);

        return Results.Created($"/api/projects/{created2.Id}",
            new CreateResponse(dto, null, report));
    }

    // Best-effort Pier rollback. Logs (does not throw) on failure so the
    // caller can still surface the *original* error to the client. Skips
    // when there's nothing to delete. Uses ONLY the appName from a
    // successful CreateAppAsync — never a user-supplied or DB-read value.
    private static async Task TryRollbackPierAsync(PierAdminClient pierAdmin, string? appName, ILogger log, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appName)) return;
        try
        {
            await pierAdmin.DeleteAppAsync(appName, $"aibuilder/{appName}", ct);
            log.LogInformation("Rolled back Pier app '{App}' after {Reason}", appName, reason);
        }
        catch (Exception e)
        {
            log.LogError(e,
                "Pier rollback FAILED for app '{App}' after {Reason}. Manual cleanup required via Pier's admin UI.",
                appName, reason);
        }
    }

    // Best-effort Plexxer rollback. Same invariant: appKey comes from a
    // successful CreateAppAsync, never user input.
    private static async Task TryRollbackPlexxerAsync(PlexxerAdminClient plexxerAdmin, string? appKey, ILogger log, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appKey)) return;
        try
        {
            await plexxerAdmin.DeleteAppAsync(appKey, ct);
            log.LogInformation("Rolled back Plexxer app (appKey ends in {LastFour}) after {Reason}",
                appKey.Length >= 4 ? appKey[^4..] : appKey, reason);
        }
        catch (Exception e)
        {
            log.LogError(e,
                "Plexxer rollback FAILED for appKey ending in {LastFour} after {Reason}. Manual cleanup required via Plexxer's UI.",
                appKey.Length >= 4 ? appKey[^4..] : appKey, reason);
        }
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

    // Plexxer-side equivalent of MapAdminErrorToResponse. Stable code +
    // scrubbed detail; never echoes the account token. The
    // `plexxer-account-ip-not-allowed` and `-grants-insufficient` codes
    // come from PlexxerAdminClient pulling the inner `error` field of
    // Plexxer's 403 envelope so we can disambiguate the two common
    // misconfigurations.
    private static IResult MapPlexxerAdminErrorToResponse(PlexxerAdminClient.PlexxerAdminError e) => e.Code switch
    {
        "plexxer-account-not-configured" =>
            Results.Problem(title: e.Code,
                detail: "PLEXXER_ACCOUNT_TOKEN is not set on this AiBuilder host; configure it in Pier env vars to enable Plexxer auto-create.",
                statusCode: 503),
        "plexxer-account-token-invalid" =>
            Results.Problem(title: e.Code,
                detail: "Plexxer rejected the account token. Mint a fresh one in Plexxer's dashboard and update PLEXXER_ACCOUNT_TOKEN.",
                statusCode: 502),
        "plexxer-account-ip-not-allowed" =>
            Results.Problem(title: e.Code,
                detail: "Plexxer rejected the request because this AiBuilder host's IP isn't on the account token's allowlist. Widen the allowlist or remint the token without one.",
                statusCode: 502),
        "plexxer-account-grants-insufficient" =>
            Results.Problem(title: e.Code,
                detail: "Plexxer rejected the request because the account token lacks required grants. Need account:apps:w + account:tokens:w.",
                statusCode: 502),
        "plexxer-name-conflict" =>
            Results.Conflict(new { error = e.Code, message = "Plexxer reports the requested name is already taken." }),
        "plexxer-validation-failed" =>
            Results.BadRequest(new { error = e.Code, message = e.Detail }),
        "plexxer-rate-limited" =>
            Results.Problem(title: e.Code, detail: "Plexxer rate limit reached; try again shortly.", statusCode: 503),
        "plexxer-account-unreachable" =>
            Results.Problem(title: e.Code, detail: "Plexxer is unreachable from this host. Verify PLEXXER_ACCOUNT_BASE and network.", statusCode: 502),
        _ =>
            Results.Problem(title: e.Code, detail: $"Unexpected Plexxer admin-API failure (HTTP {e.Status}).", statusCode: 502),
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
