using System.Collections.Concurrent;
using System.Net;
using AiBuilder.Api.Projects.Build;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Deploy;

// Runs the full deploy sequence for one project. Steps are ordered to match
// spec §3.5: publish → zip → push env vars → upload backend → upload
// frontend (if present) → restart → poll for Running → tail logs. Any step
// failing keeps the project state at DoneBuilding / DoneUpdating so the
// admin retries just the deploy (not the whole build).
public sealed class DeployOrchestrator
{
    private readonly ProjectStore _projects;
    private readonly BuildRunStore _builds;
    private readonly DeployRunStore _deploys;
    private readonly EnvVarStore _envs;
    private readonly WorkspaceManager _ws;
    private readonly PublishRunner _publish;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeployOrchestrator> _log;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new();

    public DeployOrchestrator(
        ProjectStore projects, BuildRunStore builds, DeployRunStore deploys,
        EnvVarStore envs, WorkspaceManager ws, PublishRunner publish,
        IHttpClientFactory httpFactory, ILogger<DeployOrchestrator> log)
    {
        _projects = projects; _builds = builds; _deploys = deploys;
        _envs = envs; _ws = ws; _publish = publish;
        _httpFactory = httpFactory; _log = log;
    }

    public sealed record Result(string DeployRunId, string Status, double? BackendVersion, double? FrontendVersion, string Notes);

    public async Task<Result> DeployAsync(string projectId, CancellationToken ct)
    {
        var project = await _projects.GetWithSecretsAsync(projectId, ct)
            ?? throw new KeyNotFoundException($"project {projectId} not found");

        var from = project.workspaceStatus;
        if (from != WorkspaceStatus.DoneBuilding && from != WorkspaceStatus.DoneUpdating)
            throw new InvalidStateTransitionException(from, WorkspaceStatus.Deployed);

        // The freshest succeeded build run drives the deploy; we link it on
        // the DeployRun for traceability. If there's none, we refuse.
        var runs = await _builds.ListForProjectAsync(projectId, ct);
        var latestSucceeded = runs.FirstOrDefault(r => r.status == "succeeded")
            ?? throw new InvalidOperationException("no succeeded build to deploy");

        var sem = ProjectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("deploy already running for this project");

        var deployRun = await _deploys.CreateAsync(projectId, latestSucceeded.Id!, ct);
        var notes = new System.Text.StringBuilder();
        try
        {
            var result = await RunStepsAsync(project, notes, ct);

            await _deploys.MarkFinishedAsync(deployRun.Id!, result.Status,
                result.Status == "succeeded" ? null : result.Notes,
                notes.ToString(),
                result.BackendVersion, result.FrontendVersion, ct);

            if (result.Status == "succeeded")
                await _projects.SetStatusAsync(projectId, from, WorkspaceStatus.Deployed, ct);

            return result with { DeployRunId = deployRun.Id! };
        }
        catch (Exception e)
        {
            notes.AppendLine($"ORCHESTRATOR CRASH: {e}");
            await _deploys.MarkFinishedAsync(deployRun.Id!, "failed", e.Message, notes.ToString(), null, null, ct);
            throw;
        }
        finally { sem.Release(); }
    }

    private async Task<Result> RunStepsAsync(Project project, System.Text.StringBuilder notes, CancellationToken ct)
    {
        var workspace = _ws.ResolvePath(project.pierAppName);
        var backendDir  = Path.Combine(workspace, "backend");
        var frontendDir = Path.Combine(workspace, "frontend");
        var hasBackend  = Directory.Exists(backendDir)  && Directory.EnumerateFileSystemEntries(backendDir).Any();
        var hasFrontend = Directory.Exists(frontendDir) && Directory.EnumerateFileSystemEntries(frontendDir).Any();

        if (!hasBackend && !hasFrontend)
            return new Result("", "failed", null, null,
                $"neither backend/ nor frontend/ present at {workspace} — nothing to deploy");

        // 1. dotnet publish → zip (if backend present)
        string? backendZip = null;
        if (hasBackend)
        {
            notes.AppendLine("[step] dotnet publish");
            var publishOut = Path.Combine(workspace, ".aibuilder", "publish");
            if (Directory.Exists(publishOut)) Directory.Delete(publishOut, recursive: true);
            // cwd = backend/; dotnet auto-discovers the csproj/sln there.
            var pub = await _publish.DotnetPublishAsync(backendDir, publishOut, ct);
            notes.AppendLine(TrimForNotes(pub.Stdout, 2000));
            if (pub.ExitCode != 0)
            {
                notes.AppendLine($"STDERR:\n{TrimForNotes(pub.Stderr, 2000)}");
                return new Result("", "failed", null, null, "dotnet publish failed");
            }
            backendZip = Path.Combine(workspace, ".aibuilder", "backend.zip");
            ZipBuilder.CreateFromDirectory(publishOut, backendZip);
            notes.AppendLine($"[step] backend.zip = {new FileInfo(backendZip).Length:N0} bytes");
        }
        else
        {
            notes.AppendLine("[step] no backend/ — skipping dotnet publish");
        }

        // 2. npm build → zip (if frontend present)
        string? frontendZip = null;
        if (hasFrontend)
        {
            notes.AppendLine("[step] npm install && npm run build");
            var fe = await _publish.NpmInstallAndBuildAsync(frontendDir, ct);
            notes.AppendLine(TrimForNotes(fe.Stdout, 2000));
            if (fe.ExitCode != 0)
            {
                notes.AppendLine($"STDERR:\n{TrimForNotes(fe.Stderr, 2000)}");
                return new Result("", "failed", null, null, "npm build failed");
            }
            var distDir = Path.Combine(frontendDir, "dist");
            if (!Directory.Exists(distDir))
                return new Result("", "failed", null, null, "frontend/dist/ missing after npm build");
            frontendZip = Path.Combine(workspace, ".aibuilder", "frontend.zip");
            ZipBuilder.CreateFromDirectory(distDir, frontendZip);
            notes.AppendLine($"[step] frontend.zip = {new FileInfo(frontendZip).Length:N0} bytes");
        }
        else
        {
            notes.AppendLine("[step] no frontend/ — skipping npm build");
        }

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        var pier = new PierClient(http, project.pierAppName, project.pierApiToken);
        var limiter = new PierRateLimiter();

        // 3. Push env vars
        var envVars = await _envs.ListForProjectAsync(project.Id!, includeSecretValues: true, ct);
        notes.AppendLine($"[step] sync {envVars.Count} env var(s) to Pier");
        foreach (var v in envVars)
        {
            if (v.isSecret && v.exposeToFrontend)
            {
                notes.AppendLine($"  SKIP {v.key}: invalid (secret + exposed)");
                continue;
            }
            await limiter.WaitAsync(ct);
            var resp = await pier.PutEnvAsync(v.key, new PierClient.PutEnvBody(v.value, v.isSecret, v.exposeToFrontend), ct);
            notes.AppendLine($"  PUT /env/{v.key} -> {(int)resp.StatusCode}");
            resp.Dispose();
        }

        // 4. Upload backend zip (if we built one)
        double? backendVersion = null;
        if (backendZip is not null)
        {
            await limiter.WaitAsync(ct);
            var backendUpload = await pier.DeployBackendAsync(backendZip, $"aibuilder build {project.Id}", ct);
            notes.AppendLine($"[step] POST /deploy -> {backendUpload.Status}");
            notes.AppendLine(TrimForNotes(backendUpload.Body, 1500));
            if (backendUpload.Status >= 400)
                return new Result("", "failed", null, null, $"backend deploy returned {backendUpload.Status}");
            backendVersion = TryParseVersion(backendUpload.Body);
        }

        // 5. Upload frontend zip (if we built one)
        double? frontendVersion = null;
        if (frontendZip is not null)
        {
            await limiter.WaitAsync(ct);
            var fe = await pier.DeployFrontendAsync(frontendZip, $"aibuilder build {project.Id}", ct);
            notes.AppendLine($"[step] POST /frontend/deploy -> {fe.Status}");
            notes.AppendLine(TrimForNotes(fe.Body, 1500));
            if (fe.Status >= 400)
                return new Result("", "failed", backendVersion, null, $"frontend deploy returned {fe.Status}");
            frontendVersion = TryParseVersion(fe.Body);
        }

        // 6. Restart
        await limiter.WaitAsync(ct);
        using (var rr = await pier.RestartAsync(ct))
        {
            notes.AppendLine($"[step] POST /restart -> {(int)rr.StatusCode}");
            if (!rr.IsSuccessStatusCode)
                return new Result("", "failed", backendVersion, frontendVersion, $"restart returned {(int)rr.StatusCode}");
        }

        // 7. Poll for Running (max ~45s)
        for (int attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(3000, ct);
            await limiter.WaitAsync(ct);
            var state = await pier.GetStateAsync(ct);
            if (state?.Status == "Running")
            {
                notes.AppendLine("[step] app is Running");
                break;
            }
            if (attempt == 14)
                notes.AppendLine("[step] app did not reach Running in 45s — check logs");
        }

        // 8. Log tail (best effort)
        try
        {
            await limiter.WaitAsync(ct);
            var tail = await pier.GetLogsAsync(200, ct);
            notes.AppendLine("[step] /logs?lines=200:");
            notes.AppendLine(TrimForNotes(tail, 3000));
        }
        catch (Exception e) { notes.AppendLine($"[step] log tail failed: {e.Message}"); }

        return new Result("", "succeeded", backendVersion, frontendVersion, notes.ToString());
    }

    private static double? TryParseVersion(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            foreach (var key in new[] { "versionNumber", "version", "Version", "newVersion", "deployVersion" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                    return v.GetDouble();
        }
        catch { /* best effort only */ }
        return null;
    }

    private static string TrimForNotes(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…(truncated)";
}
