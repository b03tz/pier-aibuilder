using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects;

public sealed class ProjectStore
{
    private readonly PlexxerClient _plexxer;
    public ProjectStore(PlexxerClient plexxer) => _plexxer = plexxer;

    // Fields that never leave the backend unless explicitly requested.
    private static readonly string[] SecretFields = { "pierApiToken", "plexxerApiToken" };

    public async Task<Project> CreateAsync(Project p, CancellationToken ct = default) =>
        await _plexxer.CreateAsync(p, ct);

    public async Task<List<Project>> ListSafeAsync(CancellationToken ct = default) =>
        await _plexxer.ReadAsync<Project>(new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]          = new Dictionary<string, object?> { ["createdAt"] = -1 },
                ["excludeFields"] = SecretFields,
            },
        }, ct);

    public async Task<Project?> GetSafeAsync(string id, CancellationToken ct = default)
    {
        var list = await _plexxer.ReadAsync<Project>(new Dictionary<string, object?>
        {
            ["_id:eq"]       = id,
            ["query"] = new Dictionary<string, object?>
            {
                ["limit"]         = 1,
                ["excludeFields"] = SecretFields,
            },
        }, ct);
        return list.FirstOrDefault();
    }

    // Full record including tokens — used by deploy, build prompt assembly, etc.
    public async Task<Project?> GetWithSecretsAsync(string id, CancellationToken ct = default)
    {
        var list = await _plexxer.ReadAsync<Project>(new Dictionary<string, object?>
        {
            ["_id:eq"] = id,
            ["query"]  = new Dictionary<string, object?> { ["limit"] = 1 },
        }, ct);
        return list.FirstOrDefault();
    }

    public async Task<bool> PierAppNameExistsAsync(string pierAppName, CancellationToken ct = default)
    {
        var count = await _plexxer.CountAsync<Project>(
            new Dictionary<string, object?> { ["pierAppName:eq"] = pierAppName }, ct);
        return count > 0;
    }

    public async Task UpdateFieldsAsync(string id, Dictionary<string, object?> patch, CancellationToken ct = default)
    {
        patch["updatedAt"] = DateTime.UtcNow;
        await _plexxer.UpdateAsync<Project>(
            new Dictionary<string, object?> { ["_id:eq"] = id },
            new Dictionary<string, object?> { [":set"] = patch },
            ct);
    }

    public async Task SetStatusAsync(string id, string currentStatus, string newStatus, CancellationToken ct = default)
    {
        ProjectStateMachine.EnsureTransition(currentStatus, newStatus);
        await UpdateFieldsAsync(id, new Dictionary<string, object?> { ["workspaceStatus"] = newStatus }, ct);
    }
}
