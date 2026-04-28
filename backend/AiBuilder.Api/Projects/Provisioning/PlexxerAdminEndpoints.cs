using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Provisioning;

// HTTP surface for AiBuilder's "Plexxer integration" feature. Mirrors
// `PierAdminEndpoints`: a status read so the UI can render the Settings
// card without ever seeing the account token. The token itself is never
// returned — only the last four characters, which are not sensitive on
// their own and let the admin spot-check that the right token is loaded.
public static class PlexxerAdminEndpoints
{
    public sealed record AdminStatusDto(
        bool   Configured,
        string Base,
        string? TokenLastFour);

    public static void MapPlexxerAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/_plexxer-admin")
                       .RequireAuthorization()
                       .WithTags("plexxer-admin");

        group.MapGet("/status", (PierEnv env) =>
            Results.Ok(new AdminStatusDto(
                Configured:     env.PlexxerAccountConfigured,
                Base:           env.PlexxerAccountBase,
                TokenLastFour:  env.PlexxerAccountTokenLastFour)));
    }
}
