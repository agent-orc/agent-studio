using AgentStudio.Registry;
using AgentStudio.Shared;

namespace AgentStudio.Security;

/// <summary>
/// Shared project-membership checks for routes that return workspace-wide
/// collections. Route middleware protects project-addressed URLs; collection
/// handlers must also filter their payloads so a scoped account cannot learn
/// about projects it was not assigned. Operator and viewer are scoped roles:
/// an empty membership list grants no project, and only the explicit
/// <see cref="AllProjectsGrant"/> entry grants every project.
/// </summary>
public static class ProjectAccessAuthorization
{
    /// <summary>Membership entry that grants a scoped role every project.</summary>
    public const string AllProjectsGrant = "*";

    /// <summary>
    /// True for an owner and for a scoped account holding the explicit
    /// <see cref="AllProjectsGrant"/>. An empty membership list never widens a
    /// scoped role.
    /// </summary>
    public static bool HasUnrestrictedProjectAccess(StudioUser user)
        => user.Role == StudioRoles.Owner
           || user.Projects.Contains(AllProjectsGrant, StringComparer.Ordinal);

    public static bool Allows(StudioUser user, string? projectHandle, ProjectRegistry? registry = null)
    {
        if (HasUnrestrictedProjectAccess(user)) return true;
        if (string.IsNullOrWhiteSpace(projectHandle)) return false;
        if (user.Projects.Contains(projectHandle, StringComparer.OrdinalIgnoreCase)) return true;

        var project = Resolve(projectHandle, registry);
        return project is not null && user.Projects.Any(allowed => Matches(project, allowed));
    }

    /// <summary>
    /// True when a granted project entry (id, display name, short code, or
    /// storage location) names the requested project handle. Role binding
    /// scopes use the same matching as legacy membership lists.
    /// </summary>
    public static bool SameProject(string allowed, string projectHandle, ProjectRegistry? registry = null)
    {
        if (string.Equals(allowed, projectHandle, StringComparison.OrdinalIgnoreCase)) return true;
        var project = Resolve(projectHandle, registry);
        return project is not null && Matches(project, allowed);
    }

    private static ProjectRecord? Resolve(string projectHandle, ProjectRegistry? registry)
        => registry?.FindByIdOrDisplayName(projectHandle)
           ?? registry?.FindByShortCode(projectHandle)
           ?? registry?.FindByStorageLocation(projectHandle);

    public static IEnumerable<TaskInfo> FilterTasks(
        HttpContext context,
        IEnumerable<TaskInfo> tasks,
        ProjectRegistry registry)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
            return tasks;
        return tasks.Where(task => Allows(human.User, task.ProjectName, registry));
    }

    /// <summary>
    /// The per-project form of <see cref="FilterTasks"/>, for collections
    /// keyed by project name rather than holding <see cref="TaskInfo"/>
    /// (the persisted board).
    /// </summary>
    public static bool ShowsProject(HttpContext context, string? projectName, ProjectRegistry registry)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
           || Allows(human.User, projectName, registry);

    /// <summary>
    /// Membership guard for body-addressed task-set mutations (reorder,
    /// batch-move). The networked middleware cannot infer a project from routes
    /// whose target set travels in the request body, so those handlers resolve
    /// each affected task's project and call this to enforce that a scoped
    /// non-owner human is a member of every one of them. An owner or an account
    /// holding the all-projects grant is always allowed. A null/blank resolved
    /// project for any item fails closed for a scoped account, so an unresolvable
    /// id can never smuggle a cross-project mutation through. Returns true when no
    /// human principal is present (local profile / Runner routes handled upstream).
    /// </summary>
    public static bool AllowsTasks(
        HttpContext context,
        IEnumerable<string?> taskProjects,
        ProjectRegistry? registry = null)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
            return true;
        var user = human.User;
        if (HasUnrestrictedProjectAccess(user)) return true;
        foreach (var project in taskProjects)
        {
            if (string.IsNullOrWhiteSpace(project)) return false;
            if (!Allows(user, project, registry)) return false;
        }
        return true;
    }

    /// <summary>
    /// Permission-aware form of the body-addressed guard: every affected
    /// project must grant <paramref name="permission"/> through the principal's
    /// role bindings (review item 1). Without a captured
    /// <see cref="RequestAccess"/> (local profile) it falls back to the
    /// projected membership list.
    /// </summary>
    public static bool AllowsTasks(
        HttpContext context,
        IEnumerable<string?> taskProjects,
        string permission,
        ProjectRegistry? registry = null)
    {
        if (context.Items[AccessSecurityMiddleware.RequestAccessItem] is not RequestAccess access)
            return AllowsTasks(context, taskProjects, registry);
        foreach (var project in taskProjects)
            if (!access.AllowsProject(permission, project)) return false;
        return true;
    }

    /// <summary>
    /// True when the Product role's limits apply to the caller in
    /// <paramref name="project"/>: its strongest role there is Contributor.
    /// </summary>
    public static bool IsProductRoleIn(HttpContext context, string? project)
        => context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
            ? access.IsProductRoleIn(project)
            : context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal { User.Role: StudioRoles.Contributor };

    /// <summary>
    /// True when the caller holds operator authority for this request (an
    /// operator override completion, the operator role on a decision card):
    /// Admin or Owner through its role bindings. Developer acts as legacy
    /// operator for other handler checks but holds no operator authority.
    /// Without a captured <see cref="RequestAccess"/> the legacy role decides,
    /// and the local profile without a principal is the local operator.
    /// </summary>
    public static bool HasOperatorAuthority(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
            ? access.HasOperatorAuthority
            : context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
              || human.User.Role is StudioRoles.Owner or StudioRoles.Operator;

    /// <summary>
    /// The role a decision card's <c>role:</c> or <c>owner</c> decider is
    /// matched against: owner for the Owner, operator only for Admin, and the
    /// role's own id otherwise, so Developer never decides as operator.
    /// </summary>
    public static string? DecisionRole(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess { ProjectedRole: { } role }
            ? role switch
            {
                IamRoles.Owner => StudioRoles.Owner,
                IamRoles.Admin => StudioRoles.Operator,
                _ => role,
            }
            : (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Role;

    private static bool Matches(ProjectRecord project, string allowed)
        => string.Equals(project.Id, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.DisplayName, allowed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(project.ShortCode, allowed, StringComparison.OrdinalIgnoreCase)
           || WatchPathComparison.PathsEqual(project.StorageLocation, allowed);
}
