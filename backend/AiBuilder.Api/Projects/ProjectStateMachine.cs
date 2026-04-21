namespace AiBuilder.Api.Projects;

// One place for every valid transition so controllers never write
// workspaceStatus directly.
public static class ProjectStateMachine
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [WorkspaceStatus.Draft]          = new() { WorkspaceStatus.InConversation },
        [WorkspaceStatus.InConversation] = new() { WorkspaceStatus.ScopeLocked, WorkspaceStatus.InConversation },
        [WorkspaceStatus.ScopeLocked]    = new() { WorkspaceStatus.Building },
        [WorkspaceStatus.Building]       = new() { WorkspaceStatus.DoneBuilding, WorkspaceStatus.ScopeLocked },
        [WorkspaceStatus.DoneBuilding]   = new() { WorkspaceStatus.Deployed },
        [WorkspaceStatus.Deployed]       = new() { WorkspaceStatus.InConversation },
        [WorkspaceStatus.Updating]       = new() { WorkspaceStatus.DoneUpdating, WorkspaceStatus.ScopeLocked },
        [WorkspaceStatus.DoneUpdating]   = new() { WorkspaceStatus.Deployed },
    };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    public static void EnsureTransition(string from, string to)
    {
        if (!CanTransition(from, to))
            throw new InvalidStateTransitionException(from, to);
    }
}

public sealed class InvalidStateTransitionException : Exception
{
    public string From { get; }
    public string To { get; }
    public InvalidStateTransitionException(string from, string to)
        : base($"Cannot transition Project from {from} to {to}.")
    {
        From = from; To = to;
    }
}
