using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiBuilder.Api.Projects.Deploy;

// Typed wrapper over the Pier admin API for a single target. Base URL is
// https://admin.onpier.tech/api/{pierAppName}/. Auth is a per-target pier_
// token. Rate limit 60 req/min per token — callers that push many envs at
// once should honour the PierRateLimiter.
public sealed class PierClient
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _base;

    public PierClient(HttpClient http, string pierAppName, string pierApiToken)
    {
        _http = http;
        _token = pierApiToken;
        _base = $"https://admin.onpier.tech/api/{pierAppName}";
    }

    public sealed record MetaResponse(
        string App,
        string BaseUrl,
        PierStateDto? State);
    public sealed record PierStateDto(
        string? Name,
        string? Status,
        string? DesiredState,
        int CurrentVersion,
        bool HasFrontend,
        int CurrentFrontendVersion,
        DateTime? ApiTokenLastUsedAt);

    public async Task<MetaResponse?> GetMetaAsync(CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, $"{_base}/meta"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<MetaResponse>(cancellationToken: ct);
    }

    public async Task<PierStateDto?> GetStateAsync(CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, $"{_base}"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<PierStateDto>(cancellationToken: ct);
    }

    public sealed record PutEnvBody(string value, bool isSecret, bool exposeToFrontend);
    public async Task<HttpResponseMessage> PutEnvAsync(string key, PutEnvBody body, CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Put, $"{_base}/env/{Uri.EscapeDataString(key)}")
        {
            Content = JsonContent.Create(body),
        });
        return await _http.SendAsync(req, ct);
    }

    public async Task<HttpResponseMessage> DeleteEnvAsync(string key, CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Delete, $"{_base}/env/{Uri.EscapeDataString(key)}"));
        return await _http.SendAsync(req, ct);
    }

    public sealed record PierEnvEntry(string Key, string? Value, bool IsSecret, bool ExposeToFrontend);
    public async Task<List<PierEnvEntry>> ListEnvAsync(CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, $"{_base}/env"));
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<PierEnvEntry>>(cancellationToken: ct) ?? new();
    }

    public async Task<(int Status, string Body)> DeployBackendAsync(string zipPath, string? notes, CancellationToken ct)
        => await UploadZipAsync($"{_base}/deploy", zipPath, notes, ct);

    public async Task<(int Status, string Body)> DeployFrontendAsync(string zipPath, string? notes, CancellationToken ct)
        => await UploadZipAsync($"{_base}/frontend/deploy", zipPath, notes, ct);

    private async Task<(int Status, string Body)> UploadZipAsync(string url, string zipPath, string? notes, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var stream = File.OpenRead(zipPath);
        var zipContent = new StreamContent(stream);
        zipContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(zipContent, "zip", Path.GetFileName(zipPath));
        if (!string.IsNullOrEmpty(notes)) form.Add(new StringContent(notes), "notes");

        using var req = Auth(new HttpRequestMessage(HttpMethod.Post, url) { Content = form });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, body);
    }

    public async Task<HttpResponseMessage> RestartAsync(CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Post, $"{_base}/restart"));
        return await _http.SendAsync(req, ct);
    }

    public async Task<string> GetLogsAsync(int lines, CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, $"{_base}/logs?lines={lines}"));
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private HttpRequestMessage Auth(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }
}

// Per-token rate limiter. Pier is 60 req/min per token — easiest path is to
// ensure at least 1.05s between outbound calls sharing a token.
public sealed class PierRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _nextAllowed = DateTime.MinValue;
    private readonly TimeSpan _minGap = TimeSpan.FromMilliseconds(1050);

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (now < _nextAllowed)
                await Task.Delay(_nextAllowed - now, ct);
            _nextAllowed = DateTime.UtcNow + _minGap;
        }
        finally { _gate.Release(); }
    }
}
