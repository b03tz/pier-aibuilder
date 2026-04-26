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

    public sealed record CloneResult(bool Ok, string? ErrorMessage);

    // Clones an existing remote into the workspace dir. Used by the import
    // flow at project-create time and by the VCS-tab "re-clone" recovery
    // action when an earlier import failed. Refuses to overwrite an existing
    // non-empty workspace — caller is responsible for clearing it first if
    // they want to re-clone.
    public async Task<CloneResult> CloneAsync(string pierAppName, string remoteUrl, string branch, CancellationToken ct)
    {
        var path = ResolvePath(pierAppName);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            return new CloneResult(false, $"workspace at '{path}' is not empty — refusing to clone over it");

        try
        {
            // Hand --branch to git clone so we land on the requested branch
            // even if the remote's default differs.
            var (ok, stderr) = await TryRunGitAsync(_env.AppDataDir, ct,
                "clone", "--branch", branch, "--", remoteUrl, path);
            if (!ok)
                return new CloneResult(false, TruncateStderr(stderr));

            await RunGitAsync(path, ct, "config", "user.email", "aibuilder@localhost");
            await RunGitAsync(path, ct, "config", "user.name",  "AiBuilder");
            Directory.CreateDirectory(Path.Combine(path, ".aibuilder"));
            return new CloneResult(true, null);
        }
        catch (Exception e)
        {
            return new CloneResult(false, e.Message);
        }
    }

    // Whether a workspace looks "ready to build" (cloned or initialised).
    // The VCS tab uses this to decide whether to show a "Clone from remote"
    // recovery action for a failed import.
    public bool HasGitWorkspace(string pierAppName)
    {
        var path = ResolvePath(pierAppName);
        return Directory.Exists(Path.Combine(path, ".git"));
    }

    private static string TruncateStderr(string s)
    {
        s = (s ?? string.Empty).Trim();
        const int max = 800;
        return s.Length <= max ? s : s[..max] + "…";
    }

    public async Task CommitBuildAsync(string pierAppName, string message, CancellationToken ct)
    {
        var path = ResolvePath(pierAppName);
        await RunGitAsync(path, ct, "add", "-A");
        // Use --allow-empty so a "no-op" build still stamps a commit with the run id.
        await RunGitAsync(path, ct, "commit", "-q", "--allow-empty", "-m", message);
    }

    public sealed record HeadInfo(string? CurrentBranch, string? Sha, string? ShortSha, string? Subject, DateTime? CommittedAt);

    // Reads current branch + HEAD commit without mutating anything. Returns
    // a fully-null record if there is no git repo yet (workspace never
    // built) or no commits yet.
    public async Task<HeadInfo> GetHeadInfoAsync(string pierAppName, CancellationToken ct)
    {
        var path = ResolvePath(pierAppName);
        if (!Directory.Exists(Path.Combine(path, ".git")))
            return new HeadInfo(null, null, null, null, null);

        string? branch = null, sha = null, shortSha = null, subject = null;
        DateTime? committedAt = null;
        try
        {
            var br = await TryReadGitAsync(path, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (!string.IsNullOrWhiteSpace(br)) branch = br.Trim();
            var log = await TryReadGitAsync(path, ct, "log", "-1", "--format=%H%x09%h%x09%ct%x09%s");
            if (!string.IsNullOrWhiteSpace(log))
            {
                var parts = log.Trim().Split('\t', 4);
                if (parts.Length == 4)
                {
                    sha = parts[0]; shortSha = parts[1];
                    if (long.TryParse(parts[2], out var unix))
                        committedAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    subject = parts[3];
                }
            }
        }
        catch
        {
            // A freshly-init'd repo with no commits yet will throw on log.
            // That's fine — we just report nulls.
        }
        return new HeadInfo(branch, sha, shortSha, subject, committedAt);
    }

    // Like RunGitAsync but returns stdout and never throws on non-zero exit —
    // used for read-only probes where an empty result is a valid answer.
    private static async Task<string?> TryReadGitAsync(string cwd, CancellationToken ct, params string[] args)
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
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? stdout : null;
    }

    // Like RunGitAsync but never throws — returns ok + stderr so callers can
    // surface git's own error message to the user rather than swallowing it
    // inside an InvalidOperationException.
    private static async Task<(bool Ok, string Stderr)> TryRunGitAsync(string cwd, CancellationToken ct, params string[] args)
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
        // GIT_TERMINAL_PROMPT=0 prevents git from blocking on a tty prompt
        // for HTTPS basic auth; we want it to fail fast and let us surface
        // the error rather than hang the request.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        return (p.ExitCode == 0, stderr);
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
