using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Auth;

// Thin wrapper around PlexxerClient for the Admin entity. Centralises the
// "don't project passwordHash unless you need it" rule.
public sealed class AdminStore
{
    private readonly PlexxerClient _plexxer;

    public AdminStore(PlexxerClient plexxer) => _plexxer = plexxer;

    public async Task<bool> AnyExistsAsync(CancellationToken ct = default)
    {
        var count = await _plexxer.CountAsync<Admin>(new Dictionary<string, object?>(), ct);
        return count > 0;
    }

    // Full fetch — includes passwordHash. Only call from auth verification.
    public async Task<Admin?> FindByUsernameForAuthAsync(string username, CancellationToken ct = default)
    {
        var results = await _plexxer.ReadAsync<Admin>(new Dictionary<string, object?>
        {
            ["username:eq"] = username,
            ["query"] = new Dictionary<string, object?> { ["limit"] = 1 },
        }, ct);
        return results.Count == 0 ? null : results[0];
    }

    public async Task<Admin> CreateAsync(string username, string plainPassword, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _plexxer.CreateAsync(new Admin
        {
            username = username,
            passwordHash = PasswordHasher.Hash(plainPassword),
            createdAt = now,
            updatedAt = now,
        }, ct);
    }
}
