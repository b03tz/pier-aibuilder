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
        if (project.isImported == true)
        {
            sb.AppendLine($"This project was IMPORTED from an existing codebase (remote: {project.gitRemoteUrl}, branch: {project.gitRemoteBranch ?? "master"}). The repo has been cloned into the workspace and a separate introspection pass already produced the assistant's first message summarising what's there.");
        }
        sb.AppendLine();
        sb.AppendLine(project.isImported == true ? "Initial change request:" : "Initial scope brief:");
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

        IF the conversation transcript indicates this project was IMPORTED from an existing codebase, your job is different: you are clarifying a CHANGE REQUEST against code that already exists, not designing a greenfield app. The first assistant turn (already in the transcript) is your own summary of the cloned codebase. Build on it. Ask focused questions about WHAT the user wants to change or add and WHERE in the existing code, not about who uses the app or what it stores — that's already decided. When you have enough, invite the user to lock the scope and move to build.
        """;
}
