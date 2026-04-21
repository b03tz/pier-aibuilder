namespace AiBuilder.Api.Config;

public sealed record PierEnv(
    string PlexxerAppId,
    string PlexxerApiToken,
    string AppDataDir,
    string? PublicApiBase,
    string? FrontendOrigin,
    bool Prod)
{
    public static PierEnv LoadOrThrow()
    {
        string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
                ?? throw new InvalidOperationException(
                    $"Required env var {key} is not set. " +
                    $"If running locally, export it before `dotnet run`. " +
                    $"If deployed on Pier, set it via PUT /api/aibuilder/env/{key} and restart.");

        var appDataDir = Require("APP_DATA_DIR");
        Directory.CreateDirectory(appDataDir);

        var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        return new PierEnv(
            PlexxerAppId:    Require("PLEXXER_APP_ID"),
            PlexxerApiToken: Require("PLEXXER_API_TOKEN"),
            AppDataDir:      appDataDir,
            PublicApiBase:   Environment.GetEnvironmentVariable("PUBLIC_API_BASE"),
            // Non-secret: where the browser app lives. In dev, Vite proxy
            // makes this irrelevant (same-origin). In prod the backend serves
            // from api-<app>.onpier.tech and the frontend from <app>.onpier.tech
            // so CORS must allow that origin with credentials.
            FrontendOrigin:  Environment.GetEnvironmentVariable("FRONTEND_ORIGIN"),
            Prod:            !string.Equals(aspEnv, "Development", StringComparison.OrdinalIgnoreCase));
    }
}
