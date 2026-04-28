using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

namespace AiBuilder.Api.Auth;

public static class AuthEndpoints
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    // Per-IP fixed window covering BOTH /auth/login and /auth/login/totp. Same
    // bucket key, so a brute-forcer can't burn budget on the password step and
    // still get fresh TOTP attempts. Set on the login endpoints only — admin
    // self-service routes (totp setup/confirm/disable) sit behind the cookie.
    public const string LoginRateLimiterPolicy = "auth-login";

    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginTotpRequest(string PendingId, string Code);
    public sealed record BootstrapRequest(string Username, string Password);
    public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
    public sealed record TotpConfirmRequest(string Code);
    public sealed record TotpDisableRequest(string Password);

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

        // Step 1 of the 2-step flow. If the admin has TOTP enabled we don't
        // sign them in — we issue a short-lived pendingId that's only good for
        // a single /auth/login/totp submission within PendingTtl. The pendingId
        // grants no access on its own.
        group.MapPost("/login", async (
            LoginRequest req, AdminStore store, TotpPendingStore pending,
            HttpContext ctx, CancellationToken ct) =>
        {
            var admin = await store.FindByUsernameForAuthAsync(req.Username, ct);
            if (admin is null || !PasswordHasher.Verify(req.Password, admin.passwordHash))
                return Results.Unauthorized();

            if (admin.totpEnabled)
            {
                var pendingId = pending.IssuePendingLogin(admin.Id!);
                return Results.Ok(new { requiresTotp = true, pendingId });
            }

            await SignInCookieAsync(ctx, admin);
            return Results.Ok(new { requiresTotp = false, username = admin.username });
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimiterPolicy);

        // Step 2 of the 2-step flow. Anonymous (uses pendingId, not the cookie).
        // Wrong code keeps the pending entry alive so the user can retype;
        // expiry/missing pendingId is a 401 with no detail.
        group.MapPost("/login/totp", async (
            LoginTotpRequest req, AdminStore store, TotpPendingStore pending,
            HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.PendingId) || string.IsNullOrEmpty(req.Code))
                return Results.Unauthorized();

            var adminId = pending.PeekPendingLogin(req.PendingId);
            if (adminId is null) return Results.Unauthorized();

            var admin = await store.FindByIdForAuthAsync(adminId, ct);
            if (admin is null || !admin.totpEnabled || string.IsNullOrEmpty(admin.totpSecret))
            {
                pending.DropPendingLogin(req.PendingId);
                return Results.Unauthorized();
            }

            if (!Totp.Verify(admin.totpSecret, req.Code))
                return Results.Unauthorized();

            pending.DropPendingLogin(req.PendingId);
            await SignInCookieAsync(ctx, admin);
            return Results.Ok(new { username = admin.username });
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimiterPolicy);

        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/me", async (HttpContext ctx, AdminStore store, CancellationToken ct) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var username = ctx.User.Identity.Name!;
            // We re-fetch totpEnabled rather than caching it in the cookie
            // claims, so toggling it from Settings takes effect on the next
            // /auth/me without forcing a re-login.
            var admin = await store.FindByUsernameForAuthAsync(username, ct);
            return Results.Ok(new
            {
                username,
                totpEnabled = admin?.totpEnabled ?? false,
            });
        }).RequireAuthorization();

        // Password rotation. Requires the cookie AND the current password so
        // a stolen session can't silently swap credentials.
        group.MapPost("/change-password", async (ChangePasswordRequest req, AdminStore store, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.OldPassword) || req.NewPassword.Length < 8)
                return Results.BadRequest(new { error = "new-password-min-8" });
            if (req.OldPassword == req.NewPassword)
                return Results.BadRequest(new { error = "new-password-must-differ" });

            var username = ctx.User.Identity?.Name;
            if (string.IsNullOrEmpty(username)) return Results.Unauthorized();

            var admin = await store.FindByUsernameForAuthAsync(username, ct);
            if (admin is null || !PasswordHasher.Verify(req.OldPassword, admin.passwordHash))
                return Results.Unauthorized();

            await store.UpdatePasswordAsync(admin.Id!, req.NewPassword, ct);
            // Log the current session out so the next request re-authenticates
            // with the new password. Prevents a leftover cookie from lingering
            // past a credential rotation.
            await ctx.SignOutAsync(CookieScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        // Enrolment step 1: mint a fresh secret, stash it in the pending bucket
        // keyed on adminId, and return the QR + manual-entry payload. The
        // secret does NOT land on the Admin row until /confirm. Re-calling this
        // overwrites any previous pending secret, which is fine — the QR and
        // the manual code in the response always reflect what /confirm will
        // verify against.
        group.MapPost("/totp/setup", (HttpContext ctx, TotpPendingStore pending) =>
        {
            var username = ctx.User.Identity?.Name;
            var adminId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(adminId))
                return Results.Unauthorized();

            var secret = Totp.GenerateSecretBase32();
            pending.StagePendingEnrolment(adminId, secret);
            var uri = Totp.OtpAuthUri(username, secret);
            return Results.Ok(new
            {
                secretBase32 = secret,
                otpAuthUri = uri,
                qrPngDataUri = Totp.QrPngDataUri(uri),
            });
        }).RequireAuthorization();

        // Enrolment step 2: verify a code against the pending secret. On a bad
        // code we leave the pending entry in place so the user can retype
        // without re-scanning. On a good code we persist + clear pending +
        // flip the gate.
        group.MapPost("/totp/confirm", async (
            TotpConfirmRequest req, AdminStore store, TotpPendingStore pending,
            HttpContext ctx, CancellationToken ct) =>
        {
            var adminId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminId)) return Results.Unauthorized();

            var pendingSecret = pending.GetPendingEnrolment(adminId);
            if (pendingSecret is null)
                return Results.BadRequest(new { error = "no-pending-enrolment" });

            if (!Totp.Verify(pendingSecret, req.Code))
                return Results.BadRequest(new { error = "invalid-code" });

            await store.UpdateTotpAsync(adminId, pendingSecret, enabled: true, ct);
            pending.ClearPendingEnrolment(adminId);
            return Results.Ok(new { totpEnabled = true });
        }).RequireAuthorization();

        // Disable: requires the current password (NOT a TOTP code). Defends
        // against a hijacked cookie silently flipping 2FA off.
        group.MapPost("/totp/disable", async (
            TotpDisableRequest req, AdminStore store, TotpPendingStore pending,
            HttpContext ctx, CancellationToken ct) =>
        {
            var adminId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var username = ctx.User.Identity?.Name;
            if (string.IsNullOrEmpty(adminId) || string.IsNullOrEmpty(username))
                return Results.Unauthorized();

            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { error = "password-required" });

            var admin = await store.FindByUsernameForAuthAsync(username, ct);
            if (admin is null || !PasswordHasher.Verify(req.Password, admin.passwordHash))
                return Results.Unauthorized();

            await store.UpdateTotpAsync(adminId, secretBase32: null, enabled: false, ct);
            pending.ClearPendingEnrolment(adminId);
            return Results.Ok(new { totpEnabled = false });
        }).RequireAuthorization();
    }

    private static Task SignInCookieAsync(HttpContext ctx, Plexxer.Client.AiBuilder.Admin admin)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id!),
            new Claim(ClaimTypes.Name, admin.username),
        }, CookieScheme);

        return ctx.SignInAsync(CookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14),
        });
    }
}
