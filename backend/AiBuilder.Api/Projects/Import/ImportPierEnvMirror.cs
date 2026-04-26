using AiBuilder.Api.Projects.Deploy;

namespace AiBuilder.Api.Projects.Import;

// Pulls the env vars that already live on the imported app's Pier target
// and copies them into our local TargetEnvVar store. The point is so the
// admin sees, in our UI, the exact env vars the running app is using —
// without us having to ask Pier on every page render.
//
// Important property: we only mirror entries Pier returns a non-null
// VALUE for. Pier redacts secret values on read; mirroring an empty
// secret would create a TargetEnvVar that, on the next deploy, would
// PUT an empty string back to Pier and wipe the real secret. So we
// skip secrets-with-no-readable-value, and the deploy pipeline simply
// leaves them alone (it only PUTs entries that exist in our store).
public sealed class ImportPierEnvMirror
{
    private readonly IHttpClientFactory _http;
    private readonly EnvVarStore _envs;
    private readonly ILogger<ImportPierEnvMirror> _log;

    public ImportPierEnvMirror(IHttpClientFactory http, EnvVarStore envs, ILogger<ImportPierEnvMirror> log)
    {
        _http = http; _envs = envs; _log = log;
    }

    public sealed record MirrorResult(int Mirrored, int SkippedRedacted, string? Error);

    public async Task<MirrorResult> MirrorAsync(
        string projectId, string pierAppName, string pierApiToken, CancellationToken ct)
    {
        try
        {
            var pier = new PierClient(_http.CreateClient(), pierAppName, pierApiToken);
            var entries = await pier.ListEnvAsync(ct);
            var mirrored = 0;
            var skipped = 0;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                if (e.Value is null)
                {
                    // Pier hides secret values; nothing useful to mirror.
                    skipped++;
                    continue;
                }
                if (e.IsSecret && e.ExposeToFrontend)
                {
                    // Defensive — TargetEnvVar enforces the same invariant
                    // server-side via UpsertAsync. Skip rather than throw.
                    _log.LogWarning("Pier env {Key} flagged secret AND frontend-exposed; skipping mirror", e.Key);
                    skipped++;
                    continue;
                }
                await _envs.UpsertAsync(projectId, e.Key, e.Value, e.IsSecret, e.ExposeToFrontend, ct);
                mirrored++;
            }
            return new MirrorResult(mirrored, skipped, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to mirror Pier env vars for project {Id}", projectId);
            return new MirrorResult(0, 0, ex.Message);
        }
    }
}
