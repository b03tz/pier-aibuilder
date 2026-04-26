namespace AiBuilder.Api.Config;

public sealed record PierEnv(
    string PlexxerAppId,
    string PlexxerApiToken,
    string AppDataDir,
    string? PublicApiBase,
    string? FrontendOrigin,
    string? PierAdminToken,
    string PierAdminBase,
    bool Prod)
{
    // Whether the "Create project on Pier" auto-bootstrap feature is
    // enabled. Driven by presence of an admin token; PIER_ADMIN_BASE has
    // a sensible default so it isn't part of the gate.
    public bool PierAdminConfigured => !string.IsNullOrWhiteSpace(PierAdminToken);

    // Last four chars of the admin token, for display purposes only.
    // Returns null when the token isn't set. Never use the full token in
    // log lines or UI strings — only this masked form.
    public string? PierAdminTokenLastFour
    {
        get
        {
            var t = PierAdminToken;
            if (string.IsNullOrEmpty(t) || t.Length < 4) return null;
            return t[^4..];
        }
    }

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
            // Pier admin-API integration. Both optional — when absent, the
            // "Create project on Pier" feature stays disabled instead of
            // throwing on boot, so an AiBuilder host that doesn't have an
            // admin token can still run (just without auto-create). The
            // base URL must be loopback in prod (Pier's origin gate);
            // dev-on-allowlisted-IP can override to https://admin.<host>.
            PierAdminToken:  Environment.GetEnvironmentVariable("PIER_ADMIN_TOKEN"),
            PierAdminBase:   (Environment.GetEnvironmentVariable("PIER_ADMIN_BASE") ?? "http://127.0.0.1:8080")
                                .TrimEnd('/'),
            Prod:            !string.Equals(aspEnv, "Development", StringComparison.OrdinalIgnoreCase));
    }
}
