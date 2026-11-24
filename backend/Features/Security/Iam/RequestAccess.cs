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
    Func<string, string, bool> sameProject,
    string? projectedRole = null,
    IReadOnlySet<string>? approvedPermissions = null)
{
    private readonly IReadOnlySet<string> _approved = approvedPermissions ?? new HashSet<string>(StringComparer.Ordinal);

    public IamSubject Subject => subject;

    /// <summary>
    /// Permissions an Owner approval of exactly this request added (ES-2):
    /// an operator session request admitted under an approval of
    /// <c>card.accept-override</c> or <c>card.archive-override</c> carries
    /// <see cref="IamPermissions.ProjectCardsOverride"/>, for this request only.
    /// </summary>
    public IReadOnlySet<string> ApprovedPermissions => _approved;

    /// <summary>The permission the route declared.</summary>
    public string Permission => permission;

    public bool Allows(string required, PermissionTarget target)
        => PermissionEvaluator.Evaluate(required, subject, bindings, target, sameProject).Allowed;

    /// <summary>False for a blank project: an unresolvable target fails closed.</summary>
    public bool AllowsProject(string required, string? project)
        => !string.IsNullOrWhiteSpace(project) && Allows(required, PermissionTarget.ForProject(project));

    /// <summary>
    /// True when the principal holds <paramref name="required"/> in
    /// <paramref name="project"/> through its role bindings, or when an
    /// approval admitted this request with that permission.
    /// </summary>
    public bool HoldsInProject(string required, string? project)
        => _approved.Contains(required) || AllowsProject(required, project);

    /// <summary>
    /// The strongest role that grants the route's permission in
    /// <paramref name="project"/>; an environment binding never ranks above
    /// Viewer there (see <see cref="PermissionEvaluator.StrongestRole"/>).
    /// </summary>
    public string? StrongestRole(string? project)
        => string.IsNullOrWhiteSpace(project)
            ? null
            : PermissionEvaluator.StrongestRole(permission, subject, bindings, PermissionTarget.ForProject(project), sameProject);

    /// <summary>True where the Product role's lane and route limits apply.</summary>
    public bool IsProductRoleIn(string? project) => StrongestRole(project) == IamRoles.Contributor;

    /// <summary>
    /// The role of this model projected for the request (see
    /// <see cref="ProjectedAccess.Role"/>), <c>null</c> without one.
    /// </summary>
    public string? ProjectedRole => projectedRole;

    /// <summary>
    /// True where operator authority applies to the request: an operator
    /// override completion or the operator role on a decision card. Admin and
    /// Owner hold it; Developer acts as legacy operator for handler checks but
    /// does not hold it.
    /// </summary>
    public bool HasOperatorAuthority => projectedRole is IamRoles.Owner or IamRoles.Admin;

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
