using System.Collections;

namespace AiBuilder.Api.Config;

// Every AiBuilder-generated app must expose /_pier/env.json — and AiBuilder
// dogfoods the same convention. Unauthenticated; returns only env vars whose
// key starts with PUBLIC_. Secret env vars (everything else) never come near
// this endpoint.
public static class PublicEnvEndpoint
{
    public static void MapPublicEnv(this IEndpointRouteBuilder app)
    {
        app.MapGet("/_pier/env.json", () =>
        {
            var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                if (e.Key is string k && k.StartsWith("PUBLIC_", StringComparison.Ordinal))
                    dict[k] = e.Value?.ToString() ?? "";
            }
            return Results.Json(dict);
        }).AllowAnonymous();
    }
}
