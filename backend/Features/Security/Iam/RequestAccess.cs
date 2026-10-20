namespace AgentStudio.Security;

/// <summary>
/// The rights of the principal behind one networked request, captured by the
/// access middleware with its permission decision. Handlers whose projects
/// travel in the body ask it per project instead of reading the legacy summary
/// (see <see cref="ProjectAccessAuthorization.AllowsTasks(HttpContext, IEnumerable{string?}, string, AgentStudio.Registry.ProjectRegistry?)"/>).
/// </summary>
public sealed class RequestAccess(
    IamSubject subject,
    IReadOnlyList<RoleBinding> bindings,
    string permission,
    Func<string, string, bool> sameProject)
{
    public IamSubject Subject => subject;

    /// <summary>The permission the route declared.</summary>
    public string Permission => permission;

    public bool Allows(string required, PermissionTarget target)
        => PermissionEvaluator.Evaluate(required, subject, bindings, target, sameProject).Allowed;

    /// <summary>False for a blank project: an unresolvable target fails closed.</summary>
    public bool AllowsProject(string required, string? project)
        => !string.IsNullOrWhiteSpace(project) && Allows(required, PermissionTarget.ForProject(project));

    public string? StrongestRole(string? project)
        => string.IsNullOrWhiteSpace(project)
            ? null
            : PermissionEvaluator.StrongestRole(subject, bindings, PermissionTarget.ForProject(project), sameProject);

    /// <summary>True where the Product role's lane and route limits apply.</summary>
    public bool IsProductRoleIn(string? project) => StrongestRole(project) == IamRoles.Contributor;

    /// <summary>
    /// The role forwarded to a standalone Task Server: owner for the workspace
    /// Owner, operator only with <see cref="IamPermissions.WorkspaceOperate"/>
    /// on the workspace, viewer otherwise.
    /// </summary>
    public string TaskServerRole()
        => bindings.Any(binding => binding.Role == IamRoles.Owner && binding.Scope.Kind == IamScopeKind.Workspace
                                   && PermissionEvaluator.AppliesTo(binding, subject))
            ? StudioRoles.Owner
            : Allows(IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide)
                ? StudioRoles.Operator
                : StudioRoles.Viewer;
}
