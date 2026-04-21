using System.Diagnostics;
using System.Text;

namespace AiBuilder.Api.Projects.Scope;

// Thin wrapper over the `claude` CLI as a subprocess. For Phase 3 (scope
// conversations) we invoke in text-output mode and block until completion.
// Phase 4 (builds) extends this with stream-json parsing and SSE forwarding.
public sealed class ClaudeCli
{
    public sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    public sealed record RunOptions(
        string Prompt,
        string? Cwd = null,
        string? AppendSystemPrompt = null,
        bool DangerouslySkipPermissions = false,
        string? DisallowedTools = null,
        TimeSpan? Timeout = null);

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
        var args = new List<string> { "-p", opts.Prompt, "--output-format", "text" };
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

        // Minimal allow-listed env per §7.4 of the spec. HOME is deliberately
        // preserved (even when Cwd is overridden) so the CLI finds its cached
        // Anthropic login.
        psi.Environment.Clear();
        CopyIfSet(psi, "PATH");
        CopyIfSet(psi, "HOME");
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
