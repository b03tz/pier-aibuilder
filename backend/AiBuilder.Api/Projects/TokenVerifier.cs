using System.Net.Http.Headers;

namespace AiBuilder.Api.Projects;

// Verifies the tokens the user provides at project-creation time. Hits the
// live Pier + Plexxer introspection endpoints so we fail loudly on a typo
// instead of months later when the admin clicks Deploy.
public sealed class TokenVerifier
{
    private readonly IHttpClientFactory _http;
    public TokenVerifier(IHttpClientFactory http) => _http = http;

    public sealed record Result(bool Ok, string Message);

    public async Task<Result> VerifyPierAsync(string pierAppName, string pierApiToken, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://admin.onpier.tech/api/{pierAppName}/meta");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pierApiToken);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? new Result(true, $"Pier /meta OK ({(int)resp.StatusCode})")
                : new Result(false, $"Pier /meta returned {(int)resp.StatusCode}");
        }
        catch (Exception e)
        {
            return new Result(false, $"Pier /meta request failed: {e.Message}");
        }
    }

    public async Task<Result> VerifyPlexxerAsync(string plexxerAppId, string plexxerApiToken, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.plexxer.com/d/{plexxerAppId}/_meta/self");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plexxerApiToken);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? new Result(true, $"Plexxer /_meta/self OK ({(int)resp.StatusCode})")
                : new Result(false, $"Plexxer /_meta/self returned {(int)resp.StatusCode}");
        }
        catch (Exception e)
        {
            return new Result(false, $"Plexxer /_meta/self request failed: {e.Message}");
        }
    }
}
