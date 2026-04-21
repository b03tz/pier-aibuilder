using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Scope;

public sealed class ConversationStore
{
    private readonly PlexxerClient _plexxer;
    public ConversationStore(PlexxerClient plexxer) => _plexxer = plexxer;

    public async Task<List<ConversationTurn>> ListAsync(string projectId, CancellationToken ct = default) =>
        await _plexxer.ReadAsync<ConversationTurn>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]  = new Dictionary<string, object?> { ["turnIndex"] = 1 },
                ["limit"] = 500,
            },
        }, ct);

    public async Task<int> NextIndexAsync(string projectId, CancellationToken ct = default)
    {
        var existing = await _plexxer.ReadAsync<ConversationTurn>(new Dictionary<string, object?>
        {
            ["project:eq"] = projectId,
            ["query"] = new Dictionary<string, object?>
            {
                ["sort"]   = new Dictionary<string, object?> { ["turnIndex"] = -1 },
                ["limit"]  = 1,
                ["fields"] = new[] { "_id", "turnIndex" },
            },
        }, ct);
        return existing.Count == 0 ? 0 : (int)existing[0].turnIndex + 1;
    }

    public async Task<ConversationTurn> AppendAsync(string projectId, string role, string content, int turnIndex, CancellationToken ct = default)
    {
        return await _plexxer.CreateAsync(new ConversationTurn
        {
            project   = new RelationRef<Project>(projectId),
            role      = role,
            content   = content,
            turnIndex = turnIndex,
            createdAt = DateTime.UtcNow,
        }, ct);
    }
}
