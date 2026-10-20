namespace AgentStudio.Security;

/// <summary>The legacy role fields a user record keeps for existing handler checks.</summary>
public sealed record LegacyRoleSummary(string Role, List<string> Projects, List<string> ProductionApprovalProjects);

/// <summary>
/// Pure translation between the legacy access model (one role per user plus a
/// project list and production-approval projects) and role bindings.
/// <para>
/// Migration (Dossier §9.5): owner becomes Owner on the workspace, operator
/// becomes Admin, viewer stays Viewer, and the Product role (contributor)
/// becomes Contributor on each assigned project plus Approver on the
/// production environment of each production-approval project. The
/// <see cref="ProjectAccessAuthorization.AllProjectsGrant"/> entry becomes the
/// workspace scope. An empty project list granted no project before and
/// grants none after.
/// </para>
/// <para>
/// Summary: the reverse projection keeps <see cref="StudioUser.Role"/>,
/// <see cref="StudioUser.Projects"/>, and
/// <see cref="StudioUser.ProductionApprovalProjects"/> for display, for the
/// legacy role PUT, and for an older binary that reads the file after a
/// downgrade. It is downgrade-safe: every listed project is covered by a
/// binding at least as strong as the summary role, so only Admin becomes
/// operator, and Developer or Approver never do. Requests never authorize
/// through it; the middleware projects a per-request principal instead
/// (<see cref="PermissionEvaluator.Project"/>).
/// </para>
/// </summary>
public static class LegacyRoleMapping
{
    public const string ProductionEnvironment = IamEnvironments.Production;

    public static IReadOnlyList<IamGrantRequest> Grants(
        string? role, IReadOnlyList<string>? projects, IReadOnlyList<string>? productionApprovalProjects)
    {
        var assigned = Normalize(projects);
        var allProjects = assigned.Contains(ProjectAccessAuthorization.AllProjectsGrant, StringComparer.Ordinal);
        var named = assigned.Where(project => project != ProjectAccessAuthorization.AllProjectsGrant).ToList();
        return role switch
        {
            StudioRoles.Owner => [new IamGrantRequest(IamRoles.Owner, IamScope.Workspace)],
            StudioRoles.Operator => Scoped(IamRoles.Admin, allProjects, named),
            StudioRoles.Viewer => Scoped(IamRoles.Viewer, allProjects, named),
            StudioRoles.Contributor =>
            [
                .. named.Select(project => new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject(project))),
                .. Normalize(productionApprovalProjects)
                    .Where(project => project != ProjectAccessAuthorization.AllProjectsGrant)
                    .Select(project => new IamGrantRequest(IamRoles.Approver,
                        IamScope.ForEnvironment(project, ProductionEnvironment))),
            ],
            _ => [],
        };
    }

    /// <summary>Bindings that reproduce every user's legacy access in a state without bindings.</summary>
    public static List<RoleBinding> Migrate(IEnumerable<StudioUser> users, DateTime now, Func<string> newId)
        => users.SelectMany(user => Grants(user.Role, user.Projects, user.ProductionApprovalProjects)
                .Select(grant => new RoleBinding
                {
                    Id = newId(),
                    PrincipalKind = IamPrincipalKind.User,
                    PrincipalId = user.Id,
                    Role = grant.Role,
                    Scope = grant.Scope,
                    Source = IamBindingSources.Migration,
                    CreatedAt = now,
                    CreatedBy = IamBindingSources.Migration,
                }))
            .ToList();

    public static LegacyRoleSummary Summarize(IEnumerable<RoleBinding> effectiveBindings)
    {
        var bindings = effectiveBindings.Where(binding => IamRoles.Find(binding.Role) is not null).ToList();
        if (bindings.Any(binding => binding.Role == IamRoles.Owner && binding.Scope.Kind == IamScopeKind.Workspace))
            return new LegacyRoleSummary(StudioRoles.Owner, [], []);
        if (bindings.Count == 0) return new LegacyRoleSummary(StudioRoles.Viewer, [], []);

        var admins = bindings.Where(binding => binding.Role == IamRoles.Admin).ToList();
        if (admins.Count > 0) return new LegacyRoleSummary(StudioRoles.Operator, ProjectsOf(admins), []);

        var contributors = bindings.Where(binding => binding.Role == IamRoles.Contributor).ToList();
        if (contributors.Count > 0)
        {
            var projects = ProjectsOf(contributors);
            var approvals = Normalize(bindings
                .Where(binding => binding.Role == IamRoles.Approver && binding.Scope.Project is not null
                    && (binding.Scope.Kind == IamScopeKind.Project
                        || string.Equals(binding.Scope.Environment, ProductionEnvironment, StringComparison.OrdinalIgnoreCase)))
                .Select(binding => binding.Scope.Project!).ToList())
                .Where(project => projects.Contains(project, StringComparer.OrdinalIgnoreCase)).ToList();
            return new LegacyRoleSummary(StudioRoles.Contributor, projects, approvals);
        }

        // Developer, Approver, and Viewer read their scopes; an older binary
        // must not let them write, so they summarize as viewer.
        return new LegacyRoleSummary(StudioRoles.Viewer, ProjectsOf(bindings), []);
    }

    /// <summary>
    /// True when the direct bindings are exactly what a legacy role PUT would
    /// write for the summary, so that PUT can replace them without loss.
    /// </summary>
    public static bool IsLegacyRepresentable(IReadOnlyList<RoleBinding> directBindings, LegacyRoleSummary summary)
    {
        var expected = Grants(summary.Role, summary.Projects, summary.ProductionApprovalProjects)
            .Select(grant => Key(grant.Role, grant.Scope)).Order(StringComparer.Ordinal).ToList();
        var actual = directBindings.Select(binding => Key(binding.Role, binding.Scope))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    private static string Key(string role, IamScope scope)
        => $"{role}|{scope.Kind}|{scope.Project?.ToLowerInvariant()}|{scope.Environment?.ToLowerInvariant()}";

    private static List<string> ProjectsOf(IReadOnlyList<RoleBinding> bindings)
        => bindings.Any(binding => binding.Scope.Kind == IamScopeKind.Workspace)
            ? [ProjectAccessAuthorization.AllProjectsGrant]
            : Normalize(bindings.Select(binding => binding.Scope.Project).OfType<string>().ToList());

    private static IReadOnlyList<IamGrantRequest> Scoped(string role, bool allProjects, IReadOnlyList<string> projects)
        => allProjects
            ? [new IamGrantRequest(role, IamScope.Workspace)]
            : projects.Select(project => new IamGrantRequest(role, IamScope.ForProject(project))).ToList();

    private static List<string> Normalize(IReadOnlyList<string>? projects)
        => projects?.Where(project => !string.IsNullOrWhiteSpace(project)).Select(project => project.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
}
