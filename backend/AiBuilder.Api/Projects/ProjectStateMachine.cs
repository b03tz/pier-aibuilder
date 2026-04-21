namespace AiBuilder.Api.Projects;

// One place for every valid transition so controllers never write
// workspaceStatus directly.
public static class ProjectStateMachine
{
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        [WorkspaceStatus.Draft]          = new() { WorkspaceStatus.InConversation },
        [WorkspaceStatus.InConversation] = new() { WorkspaceStatus.ScopeLocked, WorkspaceStatus.InConversation },
        // ScopeLocked goes to Building on first build or Updating on iteration;
        // caller decides based on build-run history. Also allows an explicit
        // Unlock back to InConversation if the admin wants to edit the scope
        // before kicking off a build.
        [WorkspaceStatus.ScopeLocked]    = new() { WorkspaceStatus.Building, WorkspaceStatus.Updating, WorkspaceStatus.InConversation },
        [WorkspaceStatus.Building]       = new() { WorkspaceStatus.DoneBuilding, WorkspaceStatus.ScopeLocked },
        // DoneBuilding / DoneUpdating: Deploy is the happy path, but Unlock
        // back to InConversation lets the admin add turns without deploying
        // first (iterate on scope before going live).
        [WorkspaceStatus.DoneBuilding]   = new() { WorkspaceStatus.Deployed, WorkspaceStatus.InConversation },
        [WorkspaceStatus.Deployed]       = new() { WorkspaceStatus.InConversation },
        [WorkspaceStatus.Updating]       = new() { WorkspaceStatus.DoneUpdating, WorkspaceStatus.ScopeLocked },
        [WorkspaceStatus.DoneUpdating]   = new() { WorkspaceStatus.Deployed, WorkspaceStatus.InConversation },
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
