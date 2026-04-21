namespace AiBuilder.Api.Infrastructure;

public static class HealthEndpoint
{
    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/_health", () => Results.Ok(new
        {
            status = "ok",
            version = typeof(HealthEndpoint).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            time = DateTime.UtcNow,
        })).AllowAnonymous();
    }
}
