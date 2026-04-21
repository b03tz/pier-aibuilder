using AiBuilder.Api.Auth;
using AiBuilder.Api.Config;
using AiBuilder.Api.Infrastructure;
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
builder.Services.AddHttpClient();

builder.Services.AddAuthentication(AuthEndpoints.CookieScheme)
    .AddCookie(AuthEndpoints.CookieScheme, opts =>
    {
        opts.Cookie.Name = "AiBuilderAuth";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SameSite = SameSiteMode.Lax;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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

// Dev-only CORS for the Vite dev server. Production serves the built frontend
// from this process's static files, so no CORS needed there.
builder.Services.AddCors(o => o.AddPolicy("dev", p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseCors("dev");

app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapPublicEnv();
app.MapAuth();
app.MapDebug();

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
