using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AiBuilder.Api.Projects.Provisioning;

// Destroys every entity schema in a per-project Plexxer app.
//
// Used as part of the "delete project" flow when the admin opts to
// also wipe the deployed app's data. The data lives in entities (rows)
// gated by their schemas — deleting the schema deletes the rows. Once
// Plexxer exposes an admin-level "delete app" verb we can swap this
// out; for now schema-by-schema deletion is the closest we have.
//
// Authentication is the *project's own* Plexxer token (read from the
// AiBuilder Project record), not AiBuilder's token. The project token
// must carry `app:schemas:w` — every AiBuilder-built app's token is
// granted that during the build prompt's token-permission step, but
// manually-bound projects may not. A 403 here is a hard stop; the
// caller surfaces it cleanly so the admin can fix the token before
// retrying.
public sealed class PlexxerSchemaWiper
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PlexxerSchemaWiper> _log;
    private const string Base = "https://api.plexxer.com";

    public PlexxerSchemaWiper(IHttpClientFactory http, ILogger<PlexxerSchemaWiper> log)
    {
        _http = http; _log = log;
    }

    public sealed record WipeResult(int Deleted, IReadOnlyList<string> SkippedAlreadyGone, IReadOnlyList<string> Entities);

    public async Task<WipeResult> WipeAsync(string appKey, string apiToken, CancellationToken ct)
    {
        var http = _http.CreateClient();

        // ---- List entities ----
        using var listMsg = new HttpRequestMessage(HttpMethod.Get, $"{Base}/apps/{Uri.EscapeDataString(appKey)}/schemas");
        listMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        using var listResp = await http.SendAsync(listMsg, ct);
        if (!listResp.IsSuccessStatusCode)
            throw new PlexxerSchemaWipeError(MapStatus(listResp.StatusCode, "list"),
                (int)listResp.StatusCode, await SafeBodyAsync(listResp, ct));

        // Plexxer returns a bare JSON array of {entityName, currentVersion, …}.
        var listed = await listResp.Content.ReadFromJsonAsync<List<SchemaSummary>>(cancellationToken: ct);
        var entities = (listed ?? new List<SchemaSummary>())
            .Select(s => s.EntityName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (entities.Count == 0)
            return new WipeResult(0, Array.Empty<string>(), Array.Empty<string>());

        // ---- Delete each ----
        var alreadyGone = new List<string>();
        var deleted = 0;
        foreach (var entity in entities)
        {
            // confirm query param must echo the entity name exactly per
            // Plexxer's API contract; without it the DELETE is rejected.
            var url = $"{Base}/apps/{Uri.EscapeDataString(appKey)}/schemas/{Uri.EscapeDataString(entity)}?confirm={Uri.EscapeDataString(entity)}";
            using var del = new HttpRequestMessage(HttpMethod.Delete, url);
            del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(del, ct); }
            catch (Exception e)
            {
                _log.LogWarning(e, "Plexxer schema delete transport failure (entity={Entity})", entity);
                throw new PlexxerSchemaWipeError("plexxer-unreachable", 0, e.Message);
            }
            using var _ = resp;
            var status = (int)resp.StatusCode;
            if (status is 200 or 204) { deleted++; continue; }
            if (status == 404)        { alreadyGone.Add(entity); continue; }

            var detail = await SafeBodyAsync(resp, ct);
            _log.LogWarning("Plexxer schema delete failed entity={Entity} status={Status} detail={Detail}",
                entity, status, detail);
            throw new PlexxerSchemaWipeError(MapStatus(resp.StatusCode, "delete"), status, detail, entity);
        }
        return new WipeResult(deleted, alreadyGone, entities);
    }

    private static string MapStatus(HttpStatusCode s, string verb) => s switch
    {
        HttpStatusCode.Unauthorized        => "plexxer-token-invalid",
        HttpStatusCode.Forbidden           => "plexxer-permission-denied",
        HttpStatusCode.NotFound            => $"plexxer-{verb}-not-found",
        HttpStatusCode.Conflict            => "plexxer-conflict",
        HttpStatusCode.TooManyRequests     => "plexxer-rate-limited",
        _ when (int)s >= 500               => "plexxer-server-error",
        _                                  => "plexxer-unexpected",
    };

    private static async Task<string?> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(s)) return null;
            const int max = 400;
            return s.Length <= max ? s : s[..max] + "…";
        }
        catch { return null; }
    }

    public sealed class PlexxerSchemaWipeError : Exception
    {
        public string Code { get; }
        public int    Status { get; }
        public string? Detail { get; }
        public string? Entity { get; }
        public PlexxerSchemaWipeError(string code, int status, string? detail, string? entity = null)
            : base($"{code} ({status}){(entity is null ? "" : $" entity={entity}")}{(detail is null ? "" : $": {detail}")}")
        {
            Code = code; Status = status; Detail = detail; Entity = entity;
        }
    }

    private sealed class SchemaSummary
    {
        [JsonPropertyName("entityName")]
        public string EntityName { get; set; } = "";
    }
}
