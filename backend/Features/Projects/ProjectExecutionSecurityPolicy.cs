namespace AgentStudio.Projects;

/// <summary>Authority for changing a remotely placed project to local execution.</summary>
public static class ProjectExecutionSecurityPolicy
{
    public static bool MaySelectLocation(bool networked, string? role,
        string currentLocation, string targetLocation) =>
        !networked
        || targetLocation != ExecutionLocations.Local
        || currentLocation == ExecutionLocations.Local
        || role == StudioRoles.Owner;
}
