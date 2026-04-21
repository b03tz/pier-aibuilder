using System.Text;
using Plexxer.Client.AiBuilder;

namespace AiBuilder.Api.Projects.Scope;

public static class TranscriptRenderer
{
    // Stateless: each turn ships the full transcript to `claude -p`.
    public static string RenderScopePrompt(Project project, IReadOnlyList<ConversationTurn> priorTurns, string newUserMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {project.name} (pierAppName: {project.pierAppName})");
        sb.AppendLine();
        sb.AppendLine("Initial scope brief:");
        sb.AppendLine(project.scopeBrief);
        sb.AppendLine();
        if (priorTurns.Count > 0)
        {
            sb.AppendLine("Conversation so far:");
            foreach (var t in priorTurns.OrderBy(t => t.turnIndex))
            {
                sb.Append('[').Append(t.role.ToUpperInvariant()).Append("]: ");
                sb.AppendLine(t.content);
            }
            sb.AppendLine();
        }
        sb.AppendLine("New user message:");
        sb.AppendLine(newUserMessage);
        sb.AppendLine();
        sb.AppendLine("Respond with a concise reply (<200 words). Ask one or two focused clarifying questions per turn. When you have enough detail across 3–5 turns to confidently scope the app, say so and invite the user to lock the scope.");
        return sb.ToString();
    }

    public static string ScopeSystemPrompt =>
        """
        You are the scope-clarification agent for AiBuilder, helping a single admin user scope a new application they want built. Your job across 3–5 short turns is to clarify:
          - what the app does (the one-line purpose)
          - who uses it (admin, customers, internal staff, etc.)
          - what data it stores (rough list of record types)
          - what the UI looks like (pages, flows — rough sketch is fine)
          - any non-obvious constraints (integrations, deadlines, compliance)
        Be concise, one or two focused questions per turn. Do not propose an architecture — that comes later. Do not write code. When you have enough, tell the user they can lock the scope and move to build.
        """;
}
