// Round-trips one Admin + one Project record through Plexxer via the generated
// client. Deletes both on exit (success or failure). Throwaway — will be
// removed once Phase 2's integration tests cover the same ground.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiBuilder.Api.Auth;
using Plexxer.Client.AiBuilder;

var token = Environment.GetEnvironmentVariable("PLEXXER_API_TOKEN")
    ?? throw new InvalidOperationException("PLEXXER_API_TOKEN must be set");
var appKey = Environment.GetEnvironmentVariable("PLEXXER_APP_ID")
    ?? "3ovxi6nl1wgdoggvdic64yutd79pdvu8";

using var client = new PlexxerClient(token, appKey);

string? adminId = null;
string? projectId = null;
try
{
    Console.WriteLine("[1/6] Create Admin ...");
    var admin = await client.CreateAsync(new Admin
    {
        username = $"smoketest-{Guid.NewGuid():N}",
        passwordHash = "PLACEHOLDER-not-a-real-hash",
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
    });
    adminId = admin.Id;
    Console.WriteLine($"       _id={adminId}, username={admin.username}");

    Console.WriteLine("[2/6] Read Admin back ...");
    var readBack = await client.ReadAsync<Admin>(new Dictionary<string, object?>
    {
        ["_id:eq"] = adminId,
    });
    if (readBack.Count != 1)
        throw new Exception($"Expected 1 Admin read, got {readBack.Count}");
    Console.WriteLine($"       username={readBack[0].username}, passwordHash=<{readBack[0].passwordHash}> (note: we never project this to the browser)");

    Console.WriteLine("[3/6] Update Admin passwordHash ...");
    await client.UpdateAsync<Admin>(
        new Dictionary<string, object?> { ["_id:eq"] = adminId },
        new Dictionary<string, object?> { [":set"] = new Dictionary<string, object?>
        {
            ["passwordHash"] = "PLACEHOLDER-rotated",
            ["updatedAt"] = DateTime.UtcNow,
        }});
    Console.WriteLine("       updated.");

    Console.WriteLine("[4/6] Create Project ...");
    var project = await client.CreateAsync(new Project
    {
        name = "SmokeTest Project",
        pierAppName = "smoketest-" + Guid.NewGuid().ToString("N").Substring(0, 8),
        pierApiToken = "pier_FAKE-smoketest-token",
        plexxerAppId = "smoketest-fake-app-id",
        plexxerApiToken = "plx_FAKE-smoketest-token",
        scopeBrief = "(smoke test — will be deleted)",
        workspaceStatus = "Draft",
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
    });
    projectId = project.Id;
    Console.WriteLine($"       _id={projectId}, pierAppName={project.pierAppName}");

    Console.WriteLine("[5/6] Read Project with excludeFields to hide tokens ...");
    var safeRead = await client.ReadAsync<Project>(new Dictionary<string, object?>
    {
        ["_id:eq"] = projectId,
        ["query"] = new Dictionary<string, object?>
        {
            ["excludeFields"] = new[] { "pierApiToken", "plexxerApiToken" },
        },
    });
    if (safeRead.Count != 1)
        throw new Exception($"Expected 1 Project read, got {safeRead.Count}");
    var p = safeRead[0];
    Console.WriteLine($"       pierAppName={p.pierAppName}  pierApiToken={(string.IsNullOrEmpty(p.pierApiToken) ? "<hidden>" : p.pierApiToken)}  plexxerApiToken={(string.IsNullOrEmpty(p.plexxerApiToken) ? "<hidden>" : p.plexxerApiToken)}");

    Console.WriteLine("[6/6] Everything round-tripped. Cleaning up.");

    // ---- TOTP block ------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("[TOTP-1] Generated secret round-trips via Plexxer.");
    var totpSecret = Totp.GenerateSecretBase32();
    if (string.IsNullOrEmpty(totpSecret) || totpSecret.Length < 24)
        throw new Exception("Generated secret looks too short");
    await client.UpdateAsync<Admin>(
        new Dictionary<string, object?> { ["_id:eq"] = adminId },
        new Dictionary<string, object?> { [":set"] = new Dictionary<string, object?>
        {
            ["totpSecret"]  = totpSecret,
            ["totpEnabled"] = true,
            ["updatedAt"]   = DateTime.UtcNow,
        }});
    var totpRead = await client.ReadAsync<Admin>(new Dictionary<string, object?>
    {
        ["_id:eq"] = adminId,
    });
    if (totpRead.Count != 1) throw new Exception("Admin not found after TOTP update");
    if (totpRead[0].totpSecret  != totpSecret) throw new Exception("totpSecret didn't round-trip");
    if (totpRead[0].totpEnabled != true)       throw new Exception("totpEnabled didn't round-trip");
    Console.WriteLine($"         totpEnabled={totpRead[0].totpEnabled}, totpSecret length={totpRead[0].totpSecret!.Length}");

    Console.WriteLine("[TOTP-2] Boundary verification (now-30s, now, now+30s pass; ±60s fail).");
    var now    = DateTime.UtcNow;
    var atNow  = Totp.ComputeAt(totpSecret, now);
    var atPrev = Totp.ComputeAt(totpSecret, now.AddSeconds(-30));
    var atNext = Totp.ComputeAt(totpSecret, now.AddSeconds( 30));
    if (!Totp.Verify(totpSecret, atNow))  throw new Exception("now code rejected");
    if (!Totp.Verify(totpSecret, atPrev)) throw new Exception("now-30 code rejected (should pass within ±1 window)");
    if (!Totp.Verify(totpSecret, atNext)) throw new Exception("now+30 code rejected (should pass within ±1 window)");
    var atFar = Totp.ComputeAt(totpSecret, now.AddSeconds(-90));
    if (Totp.Verify(totpSecret, atFar))   throw new Exception("now-90 code accepted (should be outside window)");
    Console.WriteLine("         ±30s pass, -90s fail.");

    Console.WriteLine("[TOTP-3] Wrong-length code rejected.");
    if (Totp.Verify(totpSecret, ""))       throw new Exception("empty code accepted");
    if (Totp.Verify(totpSecret, "12345"))  throw new Exception("5-digit code accepted");
    if (Totp.Verify(totpSecret, "1234567"))throw new Exception("7-digit code accepted");
    if (Totp.Verify(totpSecret, "abcdef")) throw new Exception("non-numeric code accepted");
    Console.WriteLine("         empty/5/7/non-numeric all rejected.");

    Console.WriteLine("[TOTP-4] Malformed Base32 secret returns false (no throw).");
    if (Totp.Verify("!!!not-base32!!!", "123456")) throw new Exception("malformed secret accepted");
    Console.WriteLine("         clean reject.");

    Console.WriteLine("[TOTP-5] otpauth URI shape + QR data URI.");
    var uri = Totp.OtpAuthUri("smoketest", totpSecret);
    if (!uri.StartsWith("otpauth://totp/")) throw new Exception("otpauth scheme wrong");
    if (!uri.Contains("issuer=AiBuilder"))  throw new Exception("issuer missing");
    if (!uri.Contains("digits=6"))          throw new Exception("digits missing");
    if (!uri.Contains("period=30"))         throw new Exception("period missing");
    var dataUri = Totp.QrPngDataUri(uri);
    if (!dataUri.StartsWith("data:image/png;base64,")) throw new Exception("QR data URI shape wrong");
    if (dataUri.Length < 200) throw new Exception("QR data URI suspiciously short");
    Console.WriteLine($"         uri ok, qrPng length={dataUri.Length}");

    Console.WriteLine("[TOTP-6] Disable clears the secret.");
    await client.UpdateAsync<Admin>(
        new Dictionary<string, object?> { ["_id:eq"] = adminId },
        new Dictionary<string, object?> { [":set"] = new Dictionary<string, object?>
        {
            ["totpSecret"]  = (string?)null,
            ["totpEnabled"] = false,
            ["updatedAt"]   = DateTime.UtcNow,
        }});
    var disabled = await client.ReadAsync<Admin>(new Dictionary<string, object?> { ["_id:eq"] = adminId });
    if (disabled[0].totpEnabled) throw new Exception("totpEnabled didn't flip off");
    if (!string.IsNullOrEmpty(disabled[0].totpSecret)) throw new Exception("totpSecret didn't clear");
    Console.WriteLine("         off + secret cleared.");
}
finally
{
    if (projectId is not null)
    {
        await client.DeleteAsync<Project>(new Dictionary<string, object?> { ["_id:eq"] = projectId });
        Console.WriteLine($"       deleted Project {projectId}");
    }
    if (adminId is not null)
    {
        await client.DeleteAsync<Admin>(new Dictionary<string, object?> { ["_id:eq"] = adminId });
        Console.WriteLine($"       deleted Admin {adminId}");
    }
}

Console.WriteLine("OK");
