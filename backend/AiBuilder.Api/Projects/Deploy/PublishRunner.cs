using System.Diagnostics;

namespace AiBuilder.Api.Projects.Deploy;

// Runs `dotnet publish -c Release` and `npm ci && npm run build` inside a
// project workspace. Returns the path to the publish/dist directory plus
// captured stdout/stderr for the DeployRun log. Both are executed with the
// same stripped env as the claude subprocess so AiBuilder's secrets don't
// leak into the generated tooling.
public sealed class PublishRunner
{
    public sealed record RunOutput(int ExitCode, string Stdout, string Stderr);

    public async Task<RunOutput> RunAsync(string exe, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "LANG", "LC_ALL", "TERM" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (v is not null) psi.Environment[key] = v;
        }
        // Tooling needs these on .NET and Node installs.
        psi.Environment["DOTNET_ROOT"]         = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";

        using var p = new Process { StartInfo = psi };
        var outBuf = new System.Text.StringBuilder();
        var errBuf = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuf.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) errBuf.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return new RunOutput(p.ExitCode, outBuf.ToString(), errBuf.ToString());
    }

    public Task<RunOutput> DotnetPublishAsync(string backendDir, string outputDir, CancellationToken ct) =>
        RunAsync("dotnet", new[] { "publish", "-c", "Release", "-o", outputDir, "--nologo" }, backendDir, ct);

    public async Task<RunOutput> NpmInstallAndBuildAsync(string frontendDir, CancellationToken ct)
    {
        var install = await RunAsync(NpmExecutable(), new[] { "install", "--no-audit", "--no-fund" }, frontendDir, ct);
        if (install.ExitCode != 0) return install;
        return await RunAsync(NpmExecutable(), new[] { "run", "build" }, frontendDir, ct);
    }

    private static string NpmExecutable() =>
        OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
}
