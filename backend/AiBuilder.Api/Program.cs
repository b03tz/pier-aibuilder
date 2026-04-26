using AiBuilder.Api.Auth;
using AiBuilder.Api.Config;
using AiBuilder.Api.Infrastructure;
using AiBuilder.Api.Projects;
using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Deploy;
using AiBuilder.Api.Projects.Import;
using AiBuilder.Api.Projects.Provisioning;
using AiBuilder.Api.Projects.Scope;
using AiBuilder.Api.Projects.Vcs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Plexxer.Client.AiBuilder;

var env = PierEnv.LoadOrThrow();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(env);

// Cookie signing + anti-forgery keys persist on disk so restarts don't
// invalidate sessions. APP_DATA_DIR is required; subdir `keys/` is created
// on boot by PierEnv.LoadOrThrow.
var keyDir = Path.Combine(env.AppDataDir, "keys");
Directory.CreateDirectory(keyDir);
builder.Services.AddDataProtection()
    .SetApplicationName("AiBuilder")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDir));

// PlexxerClient is a typed wrapper over HttpClient; safe as singleton. It
// owns its HttpClient with a pinned baseUrl and will be reused across requests.
builder.Services.AddSingleton(_ => new PlexxerClient(env.PlexxerApiToken, env.PlexxerAppId));
builder.Services.AddSingleton<AdminStore>();
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<TokenVerifier>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<ClaudeCli>();
builder.Services.AddSingleton<BuildRunStore>();
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<BuildStreamHub>();
builder.Services.AddSingleton<EnvManifestSeeder>();
builder.Services.AddSingleton<ProjectLockManager>();
builder.Services.AddSingleton<BuildOrchestrator>();
builder.Services.AddSingleton<EnvVarStore>();
builder.Services.AddSingleton<DeployRunStore>();
builder.Services.AddSingleton<PublishRunner>();
builder.Services.AddSingleton<DeployOrchestrator>();
builder.Services.AddSingleton<PushOrchestrator>();
builder.Services.AddSingleton<ImportPierEnvMirror>();
builder.Services.AddSingleton<ImportIntrospector>();
builder.Services.AddSingleton<PierAdminClient>();
builder.Services.AddHttpClient();

builder.Services.AddAuthentication(AuthEndpoints.CookieScheme)
    .AddCookie(AuthEndpoints.CookieScheme, opts =>
    {
        opts.Cookie.Name = "AiBuilderAuth";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SameSite = SameSiteMode.Lax;
        // In prod we only accept the cookie over HTTPS; in dev over either.
        opts.Cookie.SecurePolicy = env.Prod ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        opts.ExpireTimeSpan = TimeSpan.FromDays(14);
        opts.SlidingExpiration = true;
        // API returns 401/403 JSON instead of redirecting to a login page.
        opts.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        opts.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// CORS: in dev the Vite dev server on :5173 calls the backend on :5218.
// In prod on Pier the browser app runs on <app>.onpier.tech and the API on
// api-<app>.onpier.tech — same-site but different origin, credentials
// required. Origins come from env so we don't hardcode.
var devOrigins = new[] { "http://localhost:5173", "http://127.0.0.1:5173" };
var prodOrigins = env.FrontendOrigin is null ? Array.Empty<string>() : new[] { env.FrontendOrigin };
builder.Services.AddCors(o => o.AddPolicy("default", p =>
{
    p.WithOrigins(devOrigins.Concat(prodOrigins).ToArray())
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();

app.UseCors("default");

app.UseAuthentication();
app.UseAuthorization();

// Startup reconciliation: flip any stuck "running" BuildRun from a previous
// process to "failed", and roll affected projects back to ScopeLocked.
// Fire-and-forget — we don't want a flaky Plexxer to block boot.
_ = Task.Run(async () =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await app.Services.GetRequiredService<BuildOrchestrator>().ReconcileOrphansOnStartupAsync(cts.Token); }
    catch (Exception e) { app.Logger.LogWarning(e, "orphan reconciliation crashed"); }
});

app.MapHealth();
app.MapPublicEnv();
app.MapAuth();
app.MapDebug();
app.MapProjects();
app.MapScope();
app.MapBuilds();
app.MapWorkspace();
app.MapDeploy();
app.MapVcs();
app.MapPierAdmin();

// Static frontend (Vue/Vite build output). Served at the app root.
var frontendDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "frontend", "dist"));
if (Directory.Exists(frontendDist))
{
    app.UseDefaultFiles(new Microsoft.AspNetCore.Builder.DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendDist),
    });
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendDist),
    });
    // SPA fallback: unknown non-API routes return index.html so client-side
    // routing can take over.
    app.MapFallback(ctx =>
    {
        var indexPath = Path.Combine(frontendDist, "index.html");
        if (!File.Exists(indexPath))
        {
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        }
        ctx.Response.ContentType = "text/html";
        return ctx.Response.SendFileAsync(indexPath);
    });
}

app.Run();
