using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Provisioning;

// Typed wrapper over Plexxer's account-level control plane (`POST /apps`,
// polymorphic `POST /account/tokens` with `appKey` set, `DELETE /apps/{appKey}`).
// AiBuilder uses these to bootstrap a brand-new Plexxer app + per-app
// token for a freshly-created project, without the user pasting any
// credential.
//
// Strict invariants (mirror PierAdminClient):
//   * The account token (`plx_…` with scope=account) is read once from
//     PierEnv and lives only inside this singleton. Never logged, never
//     returned in an error message, never copied into any DTO.
//   * Errors are translated into `PlexxerAdminError` with a stable code
//     that the controller maps to a user-facing 4xx/5xx without leaking
//     sensitive context.
//   * Only the small subset of endpoints the auto-bootstrap path needs is
//     exposed. List/get/patch app, mint account-scoped tokens, etc. stay
//     out of scope until we have a concrete use case.
public sealed class PlexxerAdminClient
{
    private readonly IHttpClientFactory _http;
    private readonly PierEnv _env;
    private readonly ILogger<PlexxerAdminClient> _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PlexxerAdminClient(IHttpClientFactory http, PierEnv env, ILogger<PlexxerAdminClient> log)
    {
        _http = http;
        _env  = env;
        _log  = log;
    }

    public bool Configured => _env.PlexxerAccountConfigured;

    public sealed record CreateAppRequest(string Name, string? Description = null);

    public sealed record CreateAppResult(string AppKey, string Name);

    public sealed record MintTokenRequest(
        string Label,
        IDictionary<string, string> Permissions);

    public sealed record MintTokenResult(string PlaintextToken, string TokenId);

    public sealed class PlexxerAdminError : Exception
    {
        public string Code { get; }
        public int    Status { get; }
        public string? Detail { get; }

        public PlexxerAdminError(string code, int status, string? detail)
            : base($"{code} ({status}){(detail is null ? "" : ": " + detail)}")
        {
            Code = code; Status = status; Detail = detail;
        }
    }

    // POST /apps — create a brand-new Plexxer app under the calling
    // account. Returns the server-assigned `appKey`, the only safe
    // identifier to feed into the rollback DELETE later.
    public async Task<CreateAppResult> CreateAppAsync(CreateAppRequest req, CancellationToken ct)
    {
        EnsureConfigured();
        var http = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, _env.PlexxerAccountBase + "/apps")
        {
            Content = JsonContent.Create(new
            {
                name        = req.Name,
                description = req.Description,
            }, options: Json),
        };
        ApplyAuth(msg);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Plexxer admin-API create-app transport failure");
            throw new PlexxerAdminError("plexxer-account-unreachable", 0, e.Message);
        }
        using var _ = resp;
        var status = (int)resp.StatusCode;

        if (status is (int)HttpStatusCode.Created or (int)HttpStatusCode.OK)
        {
            CreateAppDto? dto;
            try { dto = await resp.Content.ReadFromJsonAsync<CreateAppDto>(Json, ct); }
            catch (Exception e)
            {
                _log.LogWarning(e, "Plexxer admin-API create-app returned {Status} but body was unparseable", status);
                throw new PlexxerAdminError("plexxer-malformed-response", status, "could not parse create-app response");
            }
            if (dto is null || string.IsNullOrEmpty(dto.AppKey))
                throw new PlexxerAdminError("plexxer-malformed-response", status, "missing appKey");
            return new CreateAppResult(AppKey: dto.AppKey, Name: dto.Name ?? req.Name);
        }

        throw await TranslateErrorAsync(resp, status, "create-app", ct);
    }

    // POST /account/tokens with `appKey` set — polymorphic mint that
    // produces a per-app token under the targeted app. The account
    // token itself stays untouched. Plaintext is returned exactly once;
    // we surface it to the caller and never persist it server-side.
    public async Task<MintTokenResult> MintAppTokenAsync(string appKey, MintTokenRequest req, CancellationToken ct)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("appKey required", nameof(appKey));

        var http = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, _env.PlexxerAccountBase + "/account/tokens")
        {
            Content = JsonContent.Create(new
            {
                label       = req.Label,
                permissions = req.Permissions,
                appKey      = appKey,
            }, options: Json),
        };
        ApplyAuth(msg);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Plexxer admin-API mint-token transport failure");
            throw new PlexxerAdminError("plexxer-account-unreachable", 0, e.Message);
        }
        using var _ = resp;
        var status = (int)resp.StatusCode;

        if (status is (int)HttpStatusCode.Created or (int)HttpStatusCode.OK)
        {
            MintTokenDto? dto;
            try { dto = await resp.Content.ReadFromJsonAsync<MintTokenDto>(Json, ct); }
            catch (Exception e)
            {
                _log.LogWarning(e, "Plexxer admin-API mint-token returned {Status} but body was unparseable", status);
                throw new PlexxerAdminError("plexxer-malformed-response", status, "could not parse mint-token response");
            }
            if (dto is null || string.IsNullOrEmpty(dto.PlaintextToken) || dto.Token is null || string.IsNullOrEmpty(dto.Token.Id))
                throw new PlexxerAdminError("plexxer-malformed-response", status, "missing token fields");
            return new MintTokenResult(PlaintextToken: dto.PlaintextToken, TokenId: dto.Token.Id);
        }

        throw await TranslateErrorAsync(resp, status, "mint-token", ct);
    }

    // DELETE /apps/{appKey} — the rollback path. Idempotent on 404 so
    // a retry after a partial cleanup doesn't explode. The caller MUST
    // pass an `appKey` returned from a recent CreateAppAsync — never a
    // value derived from user input or an existing DB record. Plexxer
    // soft-deletes (returns 202) and runs the actual cleanup async.
    public async Task DeleteAppAsync(string appKey, CancellationToken ct)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("appKey required", nameof(appKey));

        var http = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Delete,
            _env.PlexxerAccountBase + $"/apps/{Uri.EscapeDataString(appKey)}");
        ApplyAuth(msg);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Plexxer admin-API delete-app transport failure (appKey={LastFour})", LastFour(appKey));
            throw new PlexxerAdminError("plexxer-account-unreachable", 0, e.Message);
        }
        using var _ = resp;
        var status = (int)resp.StatusCode;
        if (status is 200 or 202 or 204 or 404) return;

        throw await TranslateErrorAsync(resp, status, "delete-app", ct);
    }

    // GET /apps/{appKey} — best-effort verification that a freshly-
    // created app is reachable. Returns true on 200, false on 404,
    // throws on anything else (we don't want to silently treat a
    // 401/403/500 as "missing").
    public async Task<bool> AppExistsAsync(string appKey, CancellationToken ct)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("appKey required", nameof(appKey));

        var http = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get,
            _env.PlexxerAccountBase + $"/apps/{Uri.EscapeDataString(appKey)}");
        ApplyAuth(msg);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Plexxer admin-API app-exists transport failure");
            throw new PlexxerAdminError("plexxer-account-unreachable", 0, e.Message);
        }
        using var _ = resp;
        var status = (int)resp.StatusCode;
        if (status == 200) return true;
        if (status == 404) return false;

        throw await TranslateErrorAsync(resp, status, "app-exists", ct);
    }

    private void ApplyAuth(HttpRequestMessage msg)
    {
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _env.PlexxerAccountToken!);
    }

    private void EnsureConfigured()
    {
        if (!_env.PlexxerAccountConfigured)
            throw new PlexxerAdminError("plexxer-account-not-configured", 0,
                "PLEXXER_ACCOUNT_TOKEN is not set on this AiBuilder host");
    }

    private async Task<PlexxerAdminError> TranslateErrorAsync(HttpResponseMessage resp, int status, string op, CancellationToken ct)
    {
        string? detail = null;
        try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { /* tolerate */ }
        detail = TruncateDetail(detail);

        // Plexxer's own error envelope sometimes has a JSON `error` field
        // — surface it for distinguishing ip-not-allowed (token-side
        // allowlist) from control-plane-forbidden (grants-insufficient)
        // since both come back as 403.
        var inner = ExtractErrorCode(detail);

        var code = (status, inner) switch
        {
            (400, _)                          => "plexxer-validation-failed",
            (401, _)                          => "plexxer-account-token-invalid",
            (403, "ip-not-allowed")           => "plexxer-account-ip-not-allowed",
            (403, _)                          => "plexxer-account-grants-insufficient",
            (404, _)                          => "plexxer-not-found",
            (409, _)                          => "plexxer-name-conflict",
            (429, _)                          => "plexxer-rate-limited",
            (>= 500 and < 600, _)             => "plexxer-server-error",
            _                                 => "plexxer-unexpected",
        };
        _log.LogWarning("Plexxer admin-API {Op} failed: status={Status} code={Code}", op, status, code);
        return new PlexxerAdminError(code, status, detail);
    }

    private static string? ExtractErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String)
            {
                return err.GetString();
            }
        }
        catch { /* tolerate non-JSON bodies */ }
        return null;
    }

    private static string? TruncateDetail(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        if (s.Length == 0) return null;
        const int max = 400;
        return s.Length <= max ? s : s[..max] + "…";
    }

    // For log lines that need to reference the appKey without echoing
    // it. The appKey isn't a secret per se but excessive logging makes
    // it easier to correlate across systems — keep it short.
    private static string LastFour(string s) => s.Length < 4 ? s : s[^4..];

    private sealed record CreateAppDto(string AppKey, string? Name);

    private sealed record MintTokenDto(TokenMetaDto? Token, string PlaintextToken);

    private sealed record TokenMetaDto(string Id);
}
