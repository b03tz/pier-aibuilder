using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Deploy;

// TargetEnvVar mirror: AiBuilder's local view of what it has pushed to the
// target's Pier, so the admin can edit env vars in the UI without
// round-tripping through Pier's admin. Each record gets PUT to Pier on the
// next deploy. For now there's no dirty flag — every deploy syncs every
// local record; Phase 8 polish can add diffing.
public sealed class EnvVarStore
{
    private readonly PlexxerClient _plexxer;
    public EnvVarStore(PlexxerClient plexxer) => _plexxer = plexxer;

    // Always returns the full record including the `value` field. Callers
    // that expose results to the browser (GET /env) must nullify values
    // for isSecret entries at the DTO boundary — don't strip here, because
    // that would also hide non-secret values which the UI should display.
    public async Task<List<TargetEnvVar>> ListForProjectAsync(string projectId, CancellationToken ct) =>
        await _plexxer.ReadAsync<TargetEnvVar>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]  = new Dictionary<string, object?> { ["key"] = 1 },
                ["limit"] = 500,
            },
        }, ct);

    public async Task<TargetEnvVar?> GetAsync(string projectId, string key, CancellationToken ct)
    {
        var list = await _plexxer.ReadAsync<TargetEnvVar>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["key:eq"]     = key,
            ["query"]      = new Dictionary<string, object?> { ["limit"] = 1 },
        }, ct);
        return list.FirstOrDefault();
    }

    public async Task UpsertAsync(string projectId, string key, string value, bool isSecret, bool exposeToFrontend, CancellationToken ct)
    {
        if (isSecret && exposeToFrontend)
            throw new ArgumentException("A variable cannot be both secret and exposed to the frontend.");

        var existing = await GetAsync(projectId, key, ct);
        if (existing is null)
        {
            await _plexxer.CreateAsync(new TargetEnvVar
            {
                project          = new RelationRef<Project>(projectId),
                key              = key,
                value            = value,
                isSecret         = isSecret,
                exposeToFrontend = exposeToFrontend,
                updatedAt        = DateTime.UtcNow,
            }, ct);
        }
        else
        {
            await _plexxer.UpdateAsync<TargetEnvVar>(
                new Dictionary<string, object?> { ["_id:eq"] = existing.Id },
                new Dictionary<string, object?>
                {
                    [":set"] = new Dictionary<string, object?>
                    {
                        ["value"]            = value,
                        ["isSecret"]         = isSecret,
                        ["exposeToFrontend"] = exposeToFrontend,
                        ["updatedAt"]        = DateTime.UtcNow,
                    },
                },
                ct);
        }
    }

    // Create only if missing. Used by the post-build env-var seeder: we
    // want to surface every var the app needs in the UI, but if the admin
    // has already customised a value (e.g. rotated a secret) we must not
    // clobber it on the next build.
    public async Task<bool> EnsureAsync(string projectId, string key, string value, bool isSecret, bool exposeToFrontend, CancellationToken ct)
    {
        if (isSecret && exposeToFrontend)
            throw new ArgumentException("A variable cannot be both secret and exposed to the frontend.");

        var existing = await GetAsync(projectId, key, ct);
        if (existing is not null) return false;

        await _plexxer.CreateAsync(new TargetEnvVar
        {
            project          = new RelationRef<Project>(projectId),
            key              = key,
            value            = value,
            isSecret         = isSecret,
            exposeToFrontend = exposeToFrontend,
            updatedAt        = DateTime.UtcNow,
        }, ct);
        return true;
    }

    public async Task DeleteAsync(string projectId, string key, CancellationToken ct)
    {
        await _plexxer.DeleteAsync<TargetEnvVar>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["key:eq"]     = key,
        }, ct);
    }
}
