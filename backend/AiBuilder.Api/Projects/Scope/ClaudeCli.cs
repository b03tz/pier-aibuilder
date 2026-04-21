using System.Diagnostics;
using System.Text;
using AiBuilder.Api.Config;

namespace AiBuilder.Api.Projects.Scope;

// Thin wrapper over the `claude` CLI as a subprocess. For Phase 3 (scope
// conversations) we invoke in text-output mode and block until completion.
// Phase 4 (builds) extends this with stream-json parsing and SSE forwarding.
public sealed class ClaudeCli
{
    private readonly PierEnv _env;
    private readonly Lazy<string> _writableHome;
    private readonly ILogger<ClaudeCli> _log;

    public ClaudeCli(PierEnv env, ILogger<ClaudeCli> log)
    {
        _env = env;
        _log = log;
        _writableHome = new Lazy<string>(PrepareWritableHome);
    }

    // The `claude` CLI writes session state (session-env, usage cache, etc.)
    // under $HOME/.claude. On Pier, the process's real HOME (/var/lib/pier)
    // is read-only — the only writable path is APP_DATA_DIR. So we relocate
    // HOME into APP_DATA_DIR/claude-home and copy the cached auth from the
    // source HOME on first use.
    //
    // In dev this is a harmless extra hop: HOME ends up at
    // /tmp/aibuilder-appdata/claude-home with our real .claude contents
    // duplicated in.
    private string PrepareWritableHome()
    {
        var target = Path.Combine(_env.AppDataDir, "claude-home");
        var targetClaudeDir = Path.Combine(target, ".claude");
        Directory.CreateDirectory(target);

        if (Directory.Exists(targetClaudeDir))
        {
            _log.LogInformation("claude-home already initialised at {Path}", target);
            return target;
        }

        var srcHome = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(srcHome))
        {
            var srcClaudeDir = Path.Combine(srcHome, ".claude");
            if (Directory.Exists(srcClaudeDir))
            {
                try
                {
                    CopyDirectory(srcClaudeDir, targetClaudeDir);
                    _log.LogInformation("claude-home initialised by copying {Src} → {Dst}", srcClaudeDir, targetClaudeDir);
                    return target;
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Failed to copy cached .claude — falling back to empty");
                }
            }
            else
            {
                _log.LogWarning("Source {Path} does not exist; claude may fail to authenticate", srcClaudeDir);
            }
        }

        Directory.CreateDirectory(targetClaudeDir);
        return target;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var destFile = Path.Combine(dst, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            // .NET's File.Copy preserves mode bits from source. If the source
            // is read-only (Pier's filesystem), the destination ends up
            // read-only too, which breaks claude's own writes. Force writable.
            try { File.SetAttributes(destFile, File.GetAttributes(destFile) & ~FileAttributes.ReadOnly); } catch { /* best effort */ }
        }
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }


    public sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    public sealed record RunOptions(
        string Prompt,
        string? Cwd = null,
        string? AppendSystemPrompt = null,
        bool DangerouslySkipPermissions = false,
        string? DisallowedTools = null,
        TimeSpan? Timeout = null,
        bool StreamJson = false);

    public async Task<RunResult> RunAsync(RunOptions opts, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = await RunStreamingAsync(opts,
            onStdout: line => stdout.AppendLine(line),
            onStderr: line => stderr.AppendLine(line),
            ct);
        return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    // Streaming variant: every stdout / stderr line fires the corresponding
    // callback as soon as it arrives. Used by the build orchestrator to push
    // SSE events and append to an on-disk transcript in real time.
    public async Task<int> RunStreamingAsync(
        RunOptions opts,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken ct)
    {
        var exe = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH") ?? "claude";
        var args = new List<string> { "-p", opts.Prompt };
        if (opts.StreamJson)
        {
            args.Add("--output-format"); args.Add("stream-json");
            // stream-json only emits intermediate events when --verbose is on;
            // without it, only the final message is emitted (same as text).
            args.Add("--verbose");
        }
        else
        {
            args.Add("--output-format"); args.Add("text");
        }
        if (!string.IsNullOrEmpty(opts.AppendSystemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(opts.AppendSystemPrompt);
        }
        if (opts.DangerouslySkipPermissions)
            args.Add("--dangerously-skip-permissions");
        if (!string.IsNullOrEmpty(opts.DisallowedTools))
        {
            args.Add("--disallowedTools");
            args.Add(opts.DisallowedTools);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = opts.Cwd ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Minimal allow-listed env per §7.4 of the spec. HOME is set to our
        // writable mirror under APP_DATA_DIR (see PrepareWritableHome) so the
        // CLI both finds its cached Anthropic login AND can write session-env
        // / cache files. AiBuilder's own secrets never leak into the child.
        psi.Environment.Clear();
        CopyIfSet(psi, "PATH");
        psi.Environment["HOME"] = _writableHome.Value;
        CopyIfSet(psi, "LANG");
        CopyIfSet(psi, "LC_ALL");
        CopyIfSet(psi, "TERM");

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onStderr(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (opts.Timeout.HasValue) cts.CancelAfter(opts.Timeout.Value);

        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        return p.ExitCode;
    }

    private static void CopyIfSet(ProcessStartInfo psi, string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (v is not null) psi.Environment[key] = v;
    }
}
