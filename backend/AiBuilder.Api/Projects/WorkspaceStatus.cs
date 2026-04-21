namespace AiBuilder.Api.Projects;

// Stored as string on Project.workspaceStatus. Parsed/serialised at the edge;
// the state machine below is the only place transitions are allowed.
public static class WorkspaceStatus
{
    public const string Draft          = "Draft";
    public const string InConversation = "InConversation";
    public const string ScopeLocked    = "ScopeLocked";
    public const string Building       = "Building";
    public const string DoneBuilding   = "DoneBuilding";
    public const string Updating       = "Updating";
    public const string DoneUpdating   = "DoneUpdating";
    public const string Deployed       = "Deployed";

    public static readonly HashSet<string> All = new()
    {
        Draft, InConversation, ScopeLocked, Building,
        DoneBuilding, Updating, DoneUpdating, Deployed,
    };
}
