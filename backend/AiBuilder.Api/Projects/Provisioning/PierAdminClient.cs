using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Provisioning;

// Typed wrapper over Pier's `/admin-api/*` surface — the privileged
// loopback-only API that AiBuilder uses to bootstrap a brand-new Pier
// app for a freshly-created project.
//
// Strict invariants:
//   * The admin token (`padm_…`) is read once from PierEnv and lives
//     only inside this singleton. It is never logged, never returned
//     in an error message, never copied into any DTO.
//   * Every outbound call carries `X-Pier-Originator: aibuilder/<slug>`
//     so Pier's activity feed attributes the action correctly.
//   * Errors are translated into `PierAdminError` with a stable code
//     that the controller can map to a user-facing 4xx/5xx without
//     leaking sensitive context.
//   * Only the small subset of endpoints AiBuilder actually needs is
//     exposed here. Adding more (delete, regenerate-token, list) is
//     out of scope until we have a concrete use case.
public sealed class PierAdminClient
{
    private readonly IHttpClientFactory _http;
    private readonly PierEnv _env;
    private readonly ILogger<PierAdminClient> _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PierAdminClient(IHttpClientFactory http, PierEnv env, ILogger<PierAdminClient> log)
    {
        _http = http;
        _env  = env;
        _log  = log;
    }

    public bool Configured => _env.PierAdminConfigured;

    public sealed record CreateAppRequest(
        string Name,
        bool   HasFrontend,
        string Category,
        bool   MintApiToken = true,
        string? Subdomain = null,
        string? ApiSubdomain = null);

    // Mirror of the live `AppBootstrapDto`. Only the fields AiBuilder
    // actually consumes are surfaced — `app.id`, `port`, etc. are
    // ignored because we don't store them.
    public sealed record BootstrapResult(
        string AppName,
        string Subdomain,
        bool   HasFrontend,
        string ApiToken,        // pier_… plaintext, returned exactly once
        string ApiBaseUrl,      // https://admin.<host>/api/<name>
        string ApiDomain,       // api-<name>.<host> | <name>.<host>
        string? FrontendDomain);

    // Stable error envelope. The HTTP layer translates each Code into a
    // controller-friendly 4xx/5xx without ever copying the admin token
    // into the response.
    public sealed class PierAdminError : Exception
    {
        public string Code { get; }
        public int    Status { get; }
        public string? Detail { get; }

        public PierAdminError(string code, int status, string? detail)
            : base($"{code} ({status}){(detail is null ? "" : ": " + detail)}")
        {
            Code = code; Status = status; Detail = detail;
        }
    }

    // Best-effort existence check used by the slug-collision pre-flight.
    // Returns true if Pier already owns the name, false if it doesn't, and
    // throws PierAdminError for anything that isn't a clean 200/404 — we
    // don't want to silently treat a 401/403/500 as "name is free" and
    // then race into a confusing CreateAppAsync error.
    public async Task<bool> AppExistsAsync(string name, string originator, CancellationToken ct)
    {
        EnsureConfigured();
        var http = _http.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Get, _env.PierAdminBase + $"/admin-api/apps/{Uri.EscapeDataString(name)}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _env.PierAdminToken!);
        msg.Headers.Add("X-Pier-Originator", originator);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(msg, ct); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Pier admin-API app-exists transport failure (originator={Originator})", originator);
            throw new PierAdminError("pier-admin-unreachable", 0, e.Message);
        }
        using var _ = resp;
        var status = (int)resp.StatusCode;
        if (status == 200) return true;
        if (status == 404) return false;

        var code = status switch
        {
            401 => "pier-admin-token-invalid",
            403 => "pier-admin-origin-rejected",
            429 => "pier-admin-rate-limited",
            >= 500 and < 600 => "pier-admin-server-error",
            _   => "pier-admin-unexpected",
        };
        throw new PierAdminError(code, status, null);
    }

    public async Task<BootstrapResult> CreateAppAsync(CreateAppRequest req, string originator, CancellationToken ct)
    {
        EnsureConfigured();
        var http = _http.CreateClient();
        // Build the request manually so we can attach the bearer + originator
        // headers without ever dropping the token into a serialised payload.
        using var msg = new HttpRequestMessage(HttpMethod.Post, _env.PierAdminBase + "/admin-api/apps")
        {
            Content = JsonContent.Create(new
            {
                name           = req.Name,
                subdomain      = req.Subdomain,
                hasFrontend    = req.HasFrontend,
                category       = req.Category,
                mintApiToken   = req.MintApiToken,
                apiSubdomain   = req.ApiSubdomain,
            }, options: Json),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _env.PierAdminToken!);
        msg.Headers.Add("X-Pier-Originator", originator);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(msg, ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Pier admin-API create-app transport failure (originator={Originator})", originator);
            throw new PierAdminError("pier-admin-unreachable", 0, e.Message);
        }

        using var _ = resp;
        var status = (int)resp.StatusCode;

        if (status == (int)HttpStatusCode.Created || status == (int)HttpStatusCode.OK)
        {
            BootstrapDto? dto;
            try { dto = await resp.Content.ReadFromJsonAsync<BootstrapDto>(Json, ct); }
            catch (Exception e)
            {
                _log.LogWarning(e, "Pier admin-API create-app returned {Status} but body was unparseable", status);
                throw new PierAdminError("pier-admin-malformed-response", status, "could not parse AppBootstrapDto");
            }
            if (dto is null || dto.App is null || string.IsNullOrEmpty(dto.ApiToken))
                throw new PierAdminError("pier-admin-malformed-response", status, "missing fields");
            return new BootstrapResult(
                AppName:        dto.App.Name,
                Subdomain:      dto.App.Subdomain ?? dto.App.Name,
                HasFrontend:    dto.App.HasFrontend,
                ApiToken:       dto.ApiToken,
                ApiBaseUrl:     dto.ApiBaseUrl,
                ApiDomain:      dto.ApiDomain,
                FrontendDomain: dto.FrontendDomain);
        }

        // Read the body for the error path — Pier sometimes returns a
        // JSON-encoded string, sometimes a structured envelope. Either is
        // fine to surface as a Detail string; we never copy the request
        // body or the auth header into the log.
        string? detail = null;
        try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { /* tolerate */ }
        detail = TruncateDetail(detail);

        var code = status switch
        {
            400 => "pier-validation-failed",
            401 => "pier-admin-token-invalid",
            403 => "pier-admin-origin-rejected",
            404 => "pier-admin-not-found",
            409 => "pier-admin-name-conflict",
            429 => "pier-admin-rate-limited",
            >= 500 and < 600 => "pier-admin-server-error",
            _   => "pier-admin-unexpected",
        };
        _log.LogWarning("Pier admin-API create-app failed: status={Status} code={Code} originator={Originator}",
            status, code, originator);
        throw new PierAdminError(code, status, detail);
    }

    private void EnsureConfigured()
    {
        if (!_env.PierAdminConfigured)
            throw new PierAdminError("pier-admin-not-configured", 0,
                "PIER_ADMIN_TOKEN is not set on this AiBuilder host");
    }

    private static string? TruncateDetail(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        if (s.Length == 0) return null;
        // Pier's 400 messages are short JSON strings; cap at 400 chars
        // so a runaway 500-page HTML error doesn't pollute our logs.
        const int max = 400;
        return s.Length <= max ? s : s[..max] + "…";
    }

    // Wire-shape for AppBootstrapDto. Internal — callers only see
    // BootstrapResult.
    private sealed record BootstrapDto(
        AppDto App,
        string ApiToken,
        string ApiBaseUrl,
        string ApiDomain,
        string? FrontendDomain);

    private sealed record AppDto(
        string Name,
        string? Subdomain,
        bool HasFrontend,
        string? Category);
}
