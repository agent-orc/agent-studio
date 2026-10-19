namespace AgentStudio.Security;

/// <summary>
/// What a request addresses: the route's project scope (decided by the route,
/// see <see cref="ProjectScopePolicy"/>), an optional environment, and an
/// optional <c>?project=</c> value that may only narrow the decision.
/// </summary>
public sealed record PermissionTarget(
    ProjectRouteScope Scope,
    IReadOnlyList<string?> Projects,
    string? Environment = null,
    string? QueryProject = null)
{
    public static readonly PermissionTarget Unaddressed = new(ProjectRouteScope.Unaddressed, []);
    public static readonly PermissionTarget WorkspaceWide = new(ProjectRouteScope.WorkspaceWide, []);

    public static PermissionTarget ForProject(string? project, string? environment = null)
        => new(ProjectRouteScope.Addressed, [project], environment);
}

public sealed record PermissionDecision(bool Allowed, IReadOnlyList<RoleBinding> GrantingBindings, string? Reason)
{
    public static PermissionDecision Allow(IReadOnlyList<RoleBinding> granting) => new(true, granting, null);
    public static PermissionDecision Deny(string reason) => new(false, [], reason);
}

/// <summary>
/// Pure permission evaluation. The effective permissions of a principal are
/// the union of its role bindings, direct and through its groups. A binding
/// grants a permission when its role holds it, its scope kind is allowed for
/// the permission's level, and its scope covers what the request addresses.
/// </summary>
public static class PermissionEvaluator
{
    public static PermissionDecision Evaluate(
        string permission,
        IamSubject subject,
        IEnumerable<RoleBinding> bindings,
        PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        if (!IamPermissions.IsKnown(permission)) return PermissionDecision.Deny($"Unknown permission '{permission}'.");
        if (IamPermissions.IsUnbound(permission)) return PermissionDecision.Allow([]);
        if (permission == IamPermissions.RunnerService)
            return PermissionDecision.Deny("This route serves Runner credentials only.");

        var matches = sameProject ?? DefaultSameProject;
        var granting = bindings
            .Where(binding => AppliesTo(binding, subject) && Grants(binding, permission, subject, target.Environment))
            .ToList();
        if (granting.Count == 0) return PermissionDecision.Deny($"No role binding grants '{permission}'.");

        List<RoleBinding> covering;
        switch (target.Scope)
        {
            case ProjectRouteScope.WorkspaceWide:
                covering = granting.Where(IsWorkspace).ToList();
                if (covering.Count == 0) return PermissionDecision.Deny($"'{permission}' is needed on the workspace for this route.");
                break;
            case ProjectRouteScope.Addressed:
                if (target.Projects.Count == 0) return PermissionDecision.Deny("The route addresses no resolvable project.");
                covering = [];
                foreach (var project in target.Projects)
                {
                    if (string.IsNullOrWhiteSpace(project)) return PermissionDecision.Deny("The route addresses no resolvable project.");
                    var forProject = granting.Where(binding => Covers(binding, project, matches)).ToList();
                    if (forProject.Count == 0) return PermissionDecision.Deny($"No role binding grants '{permission}' in project '{project}'.");
                    covering.AddRange(forProject);
                }
                break;
            default:
                // Collection and body-addressed handlers filter or check per item
                // for human principals only. A service account therefore needs
                // the workspace scope on such routes.
                covering = subject.Kind == IamPrincipalKind.ServiceAccount ? granting.Where(IsWorkspace).ToList() : granting;
                if (covering.Count == 0) return PermissionDecision.Deny($"A service account needs '{permission}' on the workspace for this route.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(target.QueryProject))
        {
            covering = covering.Where(binding => Covers(binding, target.QueryProject, matches)).ToList();
            if (covering.Count == 0)
                return PermissionDecision.Deny($"No role binding grants '{permission}' in project '{target.QueryProject}'.");
        }
        return PermissionDecision.Allow(covering.Distinct().ToList());
    }

    /// <summary>
    /// The strongest role whose bindings cover every project the target
    /// addresses, or <c>null</c> without one. The Product role's lane and
    /// route allowlist applies when this is <see cref="IamRoles.Contributor"/>.
    /// </summary>
    public static string? StrongestRole(
        IamSubject subject,
        IEnumerable<RoleBinding> bindings,
        PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        var matches = sameProject ?? DefaultSameProject;
        var own = bindings.Where(binding => AppliesTo(binding, subject) && IamRoles.Find(binding.Role) is not null).ToList();
        var projects = target.Scope == ProjectRouteScope.Addressed
            ? target.Projects.Where(project => !string.IsNullOrWhiteSpace(project)).Select(project => project!).ToList()
            : [];
        if (projects.Count == 0) return Strongest(own);
        RoleDefinition? weakest = null;
        foreach (var project in projects)
        {
            var best = Strongest(own.Where(binding => Covers(binding, project, matches)));
            if (best is null) return null;
            var role = IamRoles.Find(best)!;
            if (weakest is null || role.Rank < weakest.Rank) weakest = role;
        }
        return weakest?.Id;
    }

    public static bool AppliesTo(RoleBinding binding, IamSubject subject) => binding.PrincipalKind switch
    {
        IamPrincipalKind.User => subject.Kind == IamPrincipalKind.User && binding.PrincipalId == subject.Id,
        IamPrincipalKind.Group => subject.Kind == IamPrincipalKind.User && subject.GroupIds.Contains(binding.PrincipalId),
        IamPrincipalKind.ServiceAccount => subject.Kind == IamPrincipalKind.ServiceAccount && binding.PrincipalId == subject.Id,
        _ => false,
    };

    private static bool Grants(RoleBinding binding, string permission, IamSubject subject, string? environment)
    {
        if (!IamRoles.Grants(binding.Role, permission)) return false;
        // Write without read (Dossier IP-D8 A): a human resolves secret values
        // only through the Owner break-glass role.
        if (permission == IamPermissions.SecretsResolve && subject.Kind != IamPrincipalKind.ServiceAccount
            && binding.Role != IamRoles.Owner) return false;
        var level = IamPermissions.Level(permission);
        return binding.Scope.Kind switch
        {
            IamScopeKind.Workspace => true,
            IamScopeKind.Project => level != PermissionLevel.Workspace,
            IamScopeKind.Environment => level == PermissionLevel.Environment
                ? environment is not null && string.Equals(binding.Scope.Environment, environment, StringComparison.OrdinalIgnoreCase)
                : IamPermissions.IsEnvironmentBindingRead(permission),
            _ => false,
        };
    }

    private static bool Covers(RoleBinding binding, string project, Func<string, string, bool> sameProject)
        => binding.Scope.Kind == IamScopeKind.Workspace
           || (binding.Scope.Project is { } bound && sameProject(bound, project));

    private static bool IsWorkspace(RoleBinding binding) => binding.Scope.Kind == IamScopeKind.Workspace;

    private static string? Strongest(IEnumerable<RoleBinding> bindings)
        => bindings.Select(binding => IamRoles.Find(binding.Role)!)
            .OrderByDescending(role => role.Rank).FirstOrDefault()?.Id;

    private static bool DefaultSameProject(string bound, string requested)
        => string.Equals(bound, requested, StringComparison.OrdinalIgnoreCase);
}
