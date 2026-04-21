// Round-trips one Admin + one Project record through Plexxer via the generated
// client. Deletes both on exit (success or failure). Throwaway — will be
// removed once Phase 2's integration tests cover the same ground.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
