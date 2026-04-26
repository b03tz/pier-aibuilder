using AiBuilder.Api.Projects.Build;
using AiBuilder.Api.Projects.Scope;

namespace AiBuilder.Api.Projects.Import;

// Runs once, immediately after a successful import-clone. Spawns a
// read-only claude subprocess inside the freshly cloned workspace whose
// only job is to look around and write a short summary of the codebase.
// The summary is persisted as the first ASSISTANT turn (turnIndex 0) of
// the project's scope conversation, so when the admin opens the Scope
// tab they see claude's own orientation as the opening message — and
// claude itself, on the *next* turn, has already primed itself on what
// the codebase looks like.
//
// Best-effort: if claude fails or times out, we skip persistence and
// the conversation simply starts blank. The import is not blocked.
public sealed class ImportIntrospector
{
    private readonly ClaudeCli _cli;
    private readonly ConversationStore _conv;
    private readonly WorkspaceManager _ws;
    private readonly ILogger<ImportIntrospector> _log;

    public ImportIntrospector(ClaudeCli cli, ConversationStore conv, WorkspaceManager ws, ILogger<ImportIntrospector> log)
    {
        _cli = cli; _conv = conv; _ws = ws; _log = log;
    }

    public sealed record IntrospectResult(bool Ok, string? Summary, string? Error);

    public async Task<IntrospectResult> RunAsync(string projectId, string pierAppName, CancellationToken ct)
    {
        var workspace = _ws.ResolvePath(pierAppName);
        if (!Directory.Exists(workspace))
            return new IntrospectResult(false, null, "workspace-missing");

        const string systemPrompt =
            "You were just spawned inside a freshly imported codebase to give the human a fast " +
            "orientation. Don't change anything. Walk the top-level layout, peek at README / " +
            "package.json / *.csproj / *.sln if present, and infer language, frameworks, build " +
            "tooling, and notable directories. Then output a 6 to 12 line summary aimed at " +
            "another engineer who is about to make changes — what's there, how it's organised, " +
            "anything that looks unusual. No preamble, no greeting, no headers. Just the summary.";

        const string userPrompt =
            "Summarise this codebase. Use Read/Glob/Grep and Bash for ls/cat/find as needed.";

        try
        {
            var run = await _cli.RunAsync(new ClaudeCli.RunOptions(
                Prompt: userPrompt,
                Cwd: workspace,
                AppendSystemPrompt: systemPrompt,
                DangerouslySkipPermissions: true,
                DisallowedTools: "Edit,Write,NotebookEdit",
                Timeout: TimeSpan.FromMinutes(3)), ct);

            if (run.ExitCode != 0)
            {
                _log.LogWarning("Introspection claude run exited {Code}: {Err}", run.ExitCode, run.Stderr.TrimEnd());
                return new IntrospectResult(false, null, $"claude exit {run.ExitCode}");
            }

            var summary = run.Stdout.Trim();
            if (string.IsNullOrWhiteSpace(summary))
                return new IntrospectResult(false, null, "empty-summary");

            await _conv.AppendAsync(projectId, "assistant", summary, turnIndex: 0, ct);
            return new IntrospectResult(true, summary, null);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Introspection failed for project {Id}", projectId);
            return new IntrospectResult(false, null, e.Message);
        }
    }
}
