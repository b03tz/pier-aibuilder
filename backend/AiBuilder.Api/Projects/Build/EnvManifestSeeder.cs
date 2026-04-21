using System.Text.Json;
using AiBuilder.Api.Projects.Deploy;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Build;

// After a successful build, seed TargetEnvVar records for every env var
// the built app needs at runtime, so the admin sees them pre-populated in
// the Deploy tab instead of having to recall them. Two sources:
//
//  - IMPLICIT: derived from the Project itself (PLEXXER_*) and the
//    workspace shape (PUBLIC_API_BASE when both backend and frontend
//    exist, so the frontend can reach the backend subdomain).
//
//  - EXPLICIT: `.aibuilder/env.manifest.json` written by the claude
//    subprocess with any app-specific extras (third-party API keys,
//    feature flags, etc.).
//
// Seeding uses EnvVarStore.EnsureAsync so admin-overridden values survive
// subsequent builds.
public sealed class EnvManifestSeeder
{
    private readonly EnvVarStore _envs;
    private readonly ILogger<EnvManifestSeeder> _log;

    public EnvManifestSeeder(EnvVarStore envs, ILogger<EnvManifestSeeder> log)
    {
        _envs = envs; _log = log;
    }

    public sealed record SeedResult(int Implicit, int Explicit, int Skipped, List<string> Warnings);

    public async Task<SeedResult> SeedAsync(Project project, string workspacePath, CancellationToken ct)
    {
        var warnings = new List<string>();
        var implicitCount = 0;
        var explicitCount = 0;
        var skipped = 0;
        var backendDir  = Path.Combine(workspacePath, "backend");
        var frontendDir = Path.Combine(workspacePath, "frontend");
        var hasBackend  = Directory.Exists(backendDir)  && Directory.EnumerateFileSystemEntries(backendDir).Any();
        var hasFrontend = Directory.Exists(frontendDir) && Directory.EnumerateFileSystemEntries(frontendDir).Any();

        // --- Implicit seeds derived from the Project record ---
        if (!string.IsNullOrWhiteSpace(project.plexxerAppId) && !string.IsNullOrWhiteSpace(project.plexxerApiToken))
        {
            if (await _envs.EnsureAsync(project.Id!, "PLEXXER_APP_ID",    project.plexxerAppId!,    isSecret: true,  exposeToFrontend: false, ct)) implicitCount++; else skipped++;
            if (await _envs.EnsureAsync(project.Id!, "PLEXXER_API_TOKEN", project.plexxerApiToken!, isSecret: true,  exposeToFrontend: false, ct)) implicitCount++; else skipped++;
        }
        if (hasBackend && hasFrontend)
        {
            var apiBase = $"https://api-{project.pierAppName}.onpier.tech";
            if (await _envs.EnsureAsync(project.Id!, "PUBLIC_API_BASE", apiBase, isSecret: false, exposeToFrontend: true, ct)) implicitCount++; else skipped++;
        }

        // --- Explicit seeds from .aibuilder/env.manifest.json ---
        var manifestPath = Path.Combine(workspacePath, ".aibuilder", "env.manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var doc = await JsonSerializer.DeserializeAsync<EnvManifest>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                }, ct);
                if (doc?.EnvVars is not null)
                {
                    foreach (var entry in doc.EnvVars)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key))
                        {
                            warnings.Add("env manifest entry with empty key — skipped");
                            continue;
                        }
                        if (entry.IsSecret && entry.ExposeToFrontend)
                        {
                            warnings.Add($"{entry.Key}: cannot be both secret and exposed — skipped");
                            continue;
                        }
                        // Don't re-seed any of the three AiBuilder-managed keys.
                        if (entry.Key is "PLEXXER_APP_ID" or "PLEXXER_API_TOKEN" or "PUBLIC_API_BASE")
                        {
                            warnings.Add($"{entry.Key}: managed by AiBuilder — manifest entry ignored");
                            continue;
                        }
                        var value = entry.DefaultValue ?? string.Empty;
                        if (await _envs.EnsureAsync(project.Id!, entry.Key, value, entry.IsSecret, entry.ExposeToFrontend, ct))
                            explicitCount++;
                        else
                            skipped++;
                    }
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "failed to parse env manifest at {Path}", manifestPath);
                warnings.Add($"env manifest parse error: {e.Message}");
            }
        }

        return new SeedResult(implicitCount, explicitCount, skipped, warnings);
    }

    private sealed class EnvManifest
    {
        public List<Entry>? EnvVars { get; set; }

        public sealed class Entry
        {
            public string Key { get; set; } = "";
            public string? Description { get; set; }
            public bool IsSecret { get; set; }
            public bool ExposeToFrontend { get; set; }
            public string? DefaultValue { get; set; }
        }
    }
}
