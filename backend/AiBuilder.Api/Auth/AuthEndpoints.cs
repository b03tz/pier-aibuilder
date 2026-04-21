using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AiBuilder.Api.Auth;

public static class AuthEndpoints
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public sealed record LoginRequest(string Username, string Password);
    public sealed record BootstrapRequest(string Username, string Password);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("auth");

        // One-shot seed: creates the very first Admin row. Refuses if any admin
        // already exists, so a stolen/leaked call can't overwrite credentials.
        group.MapPost("/bootstrap", async (BootstrapRequest req, AdminStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 8)
                return Results.BadRequest(new { error = "username-required-and-password-min-8" });

            if (await store.AnyExistsAsync(ct))
                return Results.Conflict(new { error = "admin-already-exists" });

            var created = await store.CreateAsync(req.Username.Trim(), req.Password, ct);
            return Results.Ok(new { id = created.Id, username = created.username });
        }).AllowAnonymous();

        group.MapPost("/login", async (LoginRequest req, AdminStore store, HttpContext ctx, CancellationToken ct) =>
        {
            var admin = await store.FindByUsernameForAuthAsync(req.Username, ct);
            if (admin is null || !PasswordHasher.Verify(req.Password, admin.passwordHash))
                return Results.Unauthorized();

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id!),
                new Claim(ClaimTypes.Name, admin.username),
            }, CookieScheme);

            await ctx.SignInAsync(CookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14),
            });
            return Results.Ok(new { username = admin.username });
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/me", (HttpContext ctx) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            return Results.Ok(new { username = ctx.User.Identity.Name });
        }).RequireAuthorization();
    }
}
