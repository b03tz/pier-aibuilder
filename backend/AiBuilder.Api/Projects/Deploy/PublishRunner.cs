using System.Diagnostics;
using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Deploy;

// Runs `dotnet publish -c Release` and `npm ci && npm run build` inside a
// project workspace. Returns the path to the publish/dist directory plus
// captured stdout/stderr for the DeployRun log. Both are executed with the
// same stripped env as the claude subprocess so AiBuilder's secrets don't
// leak into the generated tooling.
public sealed class PublishRunner
{
    private readonly PierEnv _env;
    private readonly Lazy<string> _buildHome;

    public PublishRunner(PierEnv env)
    {
        _env = env;
        _buildHome = new Lazy<string>(() =>
        {
            // HOME for dotnet + npm subprocesses. Both tools want to write
            // caches to $HOME (npm → .npm, nuget → .nuget/packages) and on
            // Pier the real HOME is read-only. This path lives under
            // APP_DATA_DIR which is the one place the app can write.
            // Distinct from ClaudeCli's claude-home so we don't conflate
            // the two caches.
            var p = Path.Combine(env.AppDataDir, "build-home");
            Directory.CreateDirectory(p);
            return p;
        });
    }

    public sealed record RunOutput(int ExitCode, string Stdout, string Stderr);

    public async Task<RunOutput> RunAsync(string exe, string[] args, string cwd, CancellationToken ct)
    {
        // The systemd service for pier-aibuilder runs with a minimal PATH
        // (~/usr/bin:/bin). Tools like npm may live under fnm/nvm/volta,
        // whose entries only get added by the user's login profile (.bashrc
        // via /etc/profile etc.). Running through a login shell picks those
        // up so `npm` (and `dotnet`, if it's in /opt) resolve correctly.
        //
        // `exec "$0" "$@"` replaces the shell with the target process so we
        // don't leave a stray bash hanging around; `$@` preserves each
        // argument exactly, no escaping needed on our side.
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("exec \"$0\" \"$@\"");
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment.Clear();
        foreach (var key in new[] { "PATH", "LANG", "LC_ALL", "TERM", "USER", "LOGNAME" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (v is not null) psi.Environment[key] = v;
        }
        // HOME → writable mirror under APP_DATA_DIR. Pier's real HOME is
        // read-only and npm/nuget both want to write caches under it.
        var home = _buildHome.Value;
        psi.Environment["HOME"] = home;
        // Belt-and-suspenders: tell each tool its cache dir explicitly in
        // case something else tries to override HOME.
        psi.Environment["NPM_CONFIG_CACHE"] = Path.Combine(home, ".npm");
        psi.Environment["NUGET_PACKAGES"]   = Path.Combine(home, ".nuget", "packages");

        psi.Environment["DOTNET_ROOT"]                 = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"]               = "1";
        psi.Environment["NPM_CONFIG_UPDATE_NOTIFIER"]  = "false";

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
