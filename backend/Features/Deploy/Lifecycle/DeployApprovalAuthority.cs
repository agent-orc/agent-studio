namespace AgentStudio.Deploy;

/// <summary>
/// Maps today's Studio accounts onto the P1 approval roles. Owner is the
/// Studio owner role. Maintainer is an account holding the per-project
/// production approval grant (<see cref="StudioUser.ProductionApprovalProjects"/>);
/// the Studio has no separate Maintainer role yet. The local profile without a
/// session is the single local operator, who owns the installation.
/// </summary>
public static class DeployApprovalAuthority
{
    public const string LocalOperator = "local-operator";

    public static DeployActor Resolve(StudioUser? user, string project, bool localProfile)
    {
        if (user is null) return new(LocalOperator, localProfile);
        var approver = !user.Disabled && (user.Role == StudioRoles.Owner
            || user.ProductionApprovalProjects.Any(grant => string.Equals(grant, project, StringComparison.OrdinalIgnoreCase)));
        return new(user.Id, approver);
    }
}
