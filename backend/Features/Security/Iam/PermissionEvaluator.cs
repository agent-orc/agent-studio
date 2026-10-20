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

/// <summary>
/// The legacy role and project list a handler sees for one request, and the
/// role of this model it was derived from (<c>null</c> without a covering
/// binding).
/// </summary>
public sealed record ProjectedAccess(string LegacyRole, List<string> Projects, string? Role);

public sealed record PermissionDecision(bool Allowed, IReadOnlyList<RoleBinding> GrantingBindings, string? Reason)
{
    /// <summary>
    /// True when a binding grants the permission, but not in the scope the
    /// request addresses (another project, or a project instead of the
    /// workspace). The middleware then answers <c>project-scope-denied</c>.
    /// </summary>
    public bool ScopeDenied { get; init; }

    public static PermissionDecision Allow(IReadOnlyList<RoleBinding> granting) => new(true, granting, null);
    public static PermissionDecision Deny(string reason) => new(false, [], reason);
    public static PermissionDecision DenyScope(string reason) => new(false, [], reason) { ScopeDenied = true };
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
                if (covering.Count == 0) return PermissionDecision.DenyScope($"'{permission}' is needed on the workspace for this route.");
                break;
            case ProjectRouteScope.Addressed:
                if (target.Projects.Count == 0) return PermissionDecision.DenyScope("The route addresses no resolvable project.");
                covering = [];
                foreach (var project in target.Projects)
                {
                    if (string.IsNullOrWhiteSpace(project)) return PermissionDecision.DenyScope("The route addresses no resolvable project.");
                    var forProject = granting.Where(binding => Covers(binding, project, matches)).ToList();
                    if (forProject.Count == 0) return PermissionDecision.DenyScope($"No role binding grants '{permission}' in project '{project}'.");
                    covering.AddRange(forProject);
                }
                break;
            case ProjectRouteScope.DeferredToHandler:
                // The handler checks every affected project against the
                // projected principal (see ProjectAccessAuthorization.AllowsTasks).
                covering = granting;
                break;
            default:
                // Collection handlers filter their payload for humans. A
                // service account keeps the API-token rule: an unaddressed
                // route needs the workspace scope.
                covering = subject.Kind == IamPrincipalKind.ServiceAccount ? granting.Where(IsWorkspace).ToList() : granting;
                if (covering.Count == 0) return PermissionDecision.DenyScope($"A service account needs '{permission}' on the workspace for this route.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(target.QueryProject))
        {
            covering = covering.Where(binding => Covers(binding, target.QueryProject, matches)).ToList();
            if (covering.Count == 0)
                return PermissionDecision.DenyScope($"No role binding grants '{permission}' in project '{target.QueryProject}'.");
        }
        return PermissionDecision.Allow(covering.Distinct().ToList());
    }

    /// <summary>
    /// The strongest role whose bindings grant <paramref name="permission"/>
    /// on every project the target addresses, or <c>null</c> without one. Only
    /// workspace and project bindings rank as their role; an environment
    /// binding ranks as its role only for a target in its own environment and
    /// as <see cref="IamRoles.Viewer"/> elsewhere, because it grants no
    /// project-level write. The Product role's lane and route allowlist
    /// applies when this is <see cref="IamRoles.Contributor"/>.
    /// </summary>
    public static string? StrongestRole(
        string permission,
        IamSubject subject,
        IEnumerable<RoleBinding> bindings,
        PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        var matches = sameProject ?? DefaultSameProject;
        var granting = GrantingBindings(permission, subject, bindings, target.Environment)
            .Where(binding => IamRoles.Find(binding.Role) is not null).ToList();
        return StrongestCovering(granting, target, matches);
    }

    public static bool AppliesTo(RoleBinding binding, IamSubject subject) => binding.PrincipalKind switch
    {
        IamPrincipalKind.User => subject.Kind == IamPrincipalKind.User && binding.PrincipalId == subject.Id,
        IamPrincipalKind.Group => subject.Kind == IamPrincipalKind.User && subject.GroupIds.Contains(binding.PrincipalId),
        IamPrincipalKind.ServiceAccount => subject.Kind == IamPrincipalKind.ServiceAccount && binding.PrincipalId == subject.Id,
        _ => false,
    };

    /// <summary>
    /// The subject's bindings that grant <paramref name="permission"/>
    /// somewhere, before the route's address narrows them.
    /// </summary>
    public static IReadOnlyList<RoleBinding> GrantingBindings(
        string permission, IamSubject subject, IEnumerable<RoleBinding> bindings, string? environment)
        => bindings.Where(binding => AppliesTo(binding, subject) && Grants(binding, permission, subject, environment)).ToList();

    /// <summary>
    /// The per-request legacy view of an allowed request (review item 2): the
    /// role is the strongest role among the bindings that grant the route's
    /// permission on the addressed project(s) (see <see cref="StrongestRole"/>),
    /// or for an unaddressed route among all bindings that grant it; the
    /// projects are those where the route's permission holds. Handler checks that still read <see cref="StudioUser.Role"/> and
    /// <see cref="StudioUser.Projects"/> then never see more than the bindings
    /// grant for this route.
    /// </summary>
    public static ProjectedAccess Project(
        string permission,
        IamSubject subject,
        IEnumerable<RoleBinding> bindings,
        PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        var matches = sameProject ?? DefaultSameProject;
        var own = bindings.Where(binding => AppliesTo(binding, subject) && IamRoles.Find(binding.Role) is not null).ToList();
        if (own.Any(binding => binding.Role == IamRoles.Owner && IsWorkspace(binding)))
            return new ProjectedAccess(StudioRoles.Owner, [], IamRoles.Owner);

        var granting = GrantingBindings(permission, subject, own, target.Environment);
        var role = target.Scope switch
        {
            ProjectRouteScope.WorkspaceWide => Strongest(granting.Where(IsWorkspace), target.Environment),
            _ => StrongestCovering(granting, target, matches),
        };
        // Only a denied request has no granting role. It still gets the
        // strongest role it holds on the address, ranked the same way, so the
        // refusal names the Product or read-only limit rather than none.
        role ??= StrongestCovering(own, target, matches);
        List<string> projects = granting.Any(IsWorkspace)
            ? [ProjectAccessAuthorization.AllProjectsGrant]
            : granting.Select(binding => binding.Scope.Project).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ProjectedAccess(IamRoles.LegacyRole(role), projects, role);
    }

    /// <summary>
    /// The strongest role among <paramref name="bindings"/> that covers every
    /// project an addressed target names (the weakest of the per-project
    /// maxima), or among all of them for an unaddressed target.
    /// </summary>
    private static string? StrongestCovering(
        IReadOnlyList<RoleBinding> bindings, PermissionTarget target, Func<string, string, bool> matches)
    {
        var projects = target.Scope == ProjectRouteScope.Addressed
            ? target.Projects.Where(project => !string.IsNullOrWhiteSpace(project)).Select(project => project!).ToList()
            : [];
        if (projects.Count == 0)
            return target.Scope == ProjectRouteScope.Addressed ? null : Strongest(bindings, target.Environment);
        RoleDefinition? weakest = null;
        foreach (var project in projects)
        {
            var best = Strongest(bindings.Where(binding => Covers(binding, project, matches)), target.Environment);
            if (best is null) return null;
            var role = IamRoles.Find(best)!;
            if (weakest is null || role.Rank < weakest.Rank) weakest = role;
        }
        return weakest?.Id;
    }

    private static bool Grants(RoleBinding binding, string permission, IamSubject subject, string? environment)
    {
        if (!IamRoles.Grants(binding.Role, permission)) return false;
        // A production approval is a human decision; no workload identity
        // holds it, whatever role it is bound to (review item 4).
        if (permission == IamPermissions.EnvironmentProductionDeployApprove
            && subject.Kind == IamPrincipalKind.ServiceAccount) return false;
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

    private static string? Strongest(IEnumerable<RoleBinding> bindings, string? environment)
        => bindings.Select(binding => IamRoles.Find(RankedRole(binding, environment))!)
            .OrderByDescending(role => role.Rank).FirstOrDefault()?.Id;

    /// <summary>
    /// The role a binding ranks as for a target: an environment binding
    /// outside its own environment contributes reads only (see
    /// <see cref="IamPermissions.IsEnvironmentBindingRead"/>).
    /// </summary>
    private static string RankedRole(RoleBinding binding, string? environment)
        => binding.Scope.Kind == IamScopeKind.Environment
           && !string.Equals(binding.Scope.Environment, environment, StringComparison.OrdinalIgnoreCase)
            ? IamRoles.Viewer
            : binding.Role;

    private static bool DefaultSameProject(string bound, string requested)
        => string.Equals(bound, requested, StringComparison.OrdinalIgnoreCase);
}
