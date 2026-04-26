using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Provisioning;

// HTTP surface for AiBuilder's "Pier integration" feature. Right now this
// is just a status read — does the host have a `padm_…` admin token
// configured? — so the UI can render the Settings page card without ever
// seeing the token itself. Future expansion (e.g. listing AiBuilder-owned
// apps from the admin surface) lives here too.
public static class PierAdminEndpoints
{
    // Safe to expose: only describes WHETHER the feature is configured and
    // which Pier base it points at. The token itself is never returned;
    // only the last four characters, which are not sensitive on their own
    // and let the admin spot-check that the right token is loaded.
    public sealed record AdminStatusDto(
        bool   Configured,
        string Base,
        string? TokenLastFour);

    public static void MapPierAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/_pier-admin")
                       .RequireAuthorization()
                       .WithTags("pier-admin");

        group.MapGet("/status", (PierEnv env) =>
            Results.Ok(new AdminStatusDto(
                Configured:     env.PierAdminConfigured,
                Base:           env.PierAdminBase,
                TokenLastFour:  env.PierAdminTokenLastFour)));
    }
}
