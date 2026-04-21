using System.Diagnostics;
using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Build;

// One place that decides "where does this project's files live on disk" and
// handles the git init + commit dance for per-iteration rollback. Every
// workspace path flows through ResolvePath so no caller interpolates
// pierAppName into a path without validation.
public sealed class WorkspaceManager
{
    private readonly PierEnv _env;
    public WorkspaceManager(PierEnv env) => _env = env;

    public string Root => Path.Combine(_env.AppDataDir, "projects");

    public string ResolvePath(string pierAppName)
    {
        if (!ProjectsEndpoints.PierAppNameRegex.IsMatch(pierAppName))
            throw new ArgumentException($"pierAppName '{pierAppName}' fails the regex — refusing to interpolate into a path.", nameof(pierAppName));
        return Path.Combine(Root, pierAppName);
    }

    public string EnsureExists(string pierAppName)
    {
        var path = ResolvePath(pierAppName);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".aibuilder"));
        return path;
    }

    public async Task EnsureGitInitAsync(string pierAppName, CancellationToken ct)
    {
        var path = EnsureExists(pierAppName);
        if (Directory.Exists(Path.Combine(path, ".git"))) return;
        await RunGitAsync(path, ct, "init", "-q", "-b", "main");
        // Minimal identity so commits don't fail on the system defaults. Real
        // rewriting can happen later once we add git remotes per project.
        await RunGitAsync(path, ct, "config", "user.email", "aibuilder@localhost");
        await RunGitAsync(path, ct, "config", "user.name",  "AiBuilder");
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"),
            "bin/\nobj/\nnode_modules/\ndist/\npublish/\n.env\n.aibuilder/\n", ct);
        await RunGitAsync(path, ct, "add", "-A");
        await RunGitAsync(path, ct, "commit", "-q", "--allow-empty", "-m", "aibuilder: initial workspace");
    }

    public async Task CommitBuildAsync(string pierAppName, string message, CancellationToken ct)
    {
        var path = ResolvePath(pierAppName);
        await RunGitAsync(path, ct, "add", "-A");
        // Use --allow-empty so a "no-op" build still stamps a commit with the run id.
        await RunGitAsync(path, ct, "commit", "-q", "--allow-empty", "-m", message);
    }

    private static async Task RunGitAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        }
    }
}
