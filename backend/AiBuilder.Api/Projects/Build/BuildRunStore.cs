using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Build;

public sealed class BuildRunStore
{
    private readonly PlexxerClient _plexxer;
    public BuildRunStore(PlexxerClient plexxer) => _plexxer = plexxer;

    public async Task<BuildRun> CreateAsync(string projectId, string kind, string transcriptPath, CancellationToken ct) =>
        await _plexxer.CreateAsync(new BuildRun
        {
            project        = new RelationRef<Project>(projectId),
            kind           = kind,
            status         = "running",
            transcriptPath = transcriptPath,
            startedAt      = DateTime.UtcNow,
        }, ct);

    public async Task MarkFinishedAsync(string runId, string status, string? failureReason, CancellationToken ct)
    {
        var set = new Dictionary<string, object?>
        {
            ["status"]     = status,
            ["finishedAt"] = DateTime.UtcNow,
        };
        if (failureReason is not null) set["failureReason"] = failureReason;
        await _plexxer.UpdateAsync<BuildRun>(
            new Dictionary<string, object?> { ["_id:eq"] = runId },
            new Dictionary<string, object?> { [":set"] = set },
            ct);
    }

    public async Task<BuildRun?> GetAsync(string runId, CancellationToken ct)
    {
        var list = await _plexxer.ReadAsync<BuildRun>(new Dictionary<string, object?>
        {
            ["_id:eq"] = runId,
            ["query"]  = new Dictionary<string, object?> { ["limit"] = 1 },
        }, ct);
        return list.FirstOrDefault();
    }

    public async Task<List<BuildRun>> ListForProjectAsync(string projectId, CancellationToken ct)
    {
        return await _plexxer.ReadAsync<BuildRun>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]  = new Dictionary<string, object?> { ["startedAt"] = -1 },
                ["limit"] = 50,
            },
        }, ct);
    }

    public async Task<bool> HasSucceededBefore(string projectId, CancellationToken ct)
    {
        var n = await _plexxer.CountAsync<BuildRun>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["status:eq"]  = "succeeded",
        }, ct);
        return n > 0;
    }

    public async Task<List<BuildRun>> ListRunningAsync(CancellationToken ct) =>
        await _plexxer.ReadAsync<BuildRun>(new Dictionary<string, object?>
        {
            ["status:eq"] = "running",
            ["query"]     = new Dictionary<string, object?> { ["limit"] = 200 },
        }, ct);
}
