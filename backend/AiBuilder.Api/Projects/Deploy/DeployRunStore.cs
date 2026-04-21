using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Deploy;

public sealed class DeployRunStore
{
    private readonly PlexxerClient _plexxer;
    public DeployRunStore(PlexxerClient plexxer) => _plexxer = plexxer;

    public async Task<DeployRun> CreateAsync(string projectId, string buildRunId, CancellationToken ct) =>
        await _plexxer.CreateAsync(new DeployRun
        {
            project   = new RelationRef<Project>(projectId),
            buildRun  = new RelationRef<BuildRun>(buildRunId),
            status    = "running",
            startedAt = DateTime.UtcNow,
        }, ct);

    public async Task MarkFinishedAsync(
        string runId, string status, string? failureReason, string? notes,
        double? backendVersion, double? frontendVersion, CancellationToken ct)
    {
        var set = new Dictionary<string, object?>
        {
            ["status"]     = status,
            ["finishedAt"] = DateTime.UtcNow,
        };
        if (failureReason is not null) set["failureReason"] = failureReason;
        if (notes is not null)         set["deployNotes"]   = notes;
        if (backendVersion is not null)  set["pierDeployVersion"]         = backendVersion.Value;
        if (frontendVersion is not null) set["pierFrontendDeployVersion"] = frontendVersion.Value;
        await _plexxer.UpdateAsync<DeployRun>(
            new Dictionary<string, object?> { ["_id:eq"] = runId },
            new Dictionary<string, object?> { [":set"] = set },
            ct);
    }

    public async Task<DeployRun?> GetAsync(string runId, CancellationToken ct)
    {
        var list = await _plexxer.ReadAsync<DeployRun>(new Dictionary<string, object?>
        {
            ["_id:eq"] = runId,
            ["query"]  = new Dictionary<string, object?> { ["limit"] = 1 },
        }, ct);
        return list.FirstOrDefault();
    }

    public async Task<List<DeployRun>> ListForProjectAsync(string projectId, CancellationToken ct) =>
        await _plexxer.ReadAsync<DeployRun>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]  = new Dictionary<string, object?> { ["startedAt"] = -1 },
                ["limit"] = 50,
            },
        }, ct);
}
