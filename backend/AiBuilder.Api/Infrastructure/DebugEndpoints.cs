using AiBuilder.Api.Config;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AiBuilder.Api.Infrastructure;

public static class DebugEndpoints
{
    public static void MapDebug(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/_debug").RequireAuthorization().WithTags("debug");

        // Proxies Plexxer's /_meta/self so I can confirm the token wired into DI
        // is the one the server recognises. Backend-side only — the plx_ token
        // never leaves this process.
        group.MapGet("/plexxer-self", async (PierEnv env, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            using var http = httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.plexxer.com/d/{env.PlexxerAppId}/_meta/self");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", env.PlexxerApiToken);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadFromJsonAsync<object>(cancellationToken: ct);
            return Results.Json(new { status = (int)resp.StatusCode, body });
        });
    }
}
