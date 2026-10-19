using System.Text.Json.Serialization;

namespace AgentStudio.Security;

/// <summary>The narrowest scope kind whose bindings may grant a permission.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionLevel>))]
public enum PermissionLevel
{
    /// <summary>Only a workspace binding grants it.</summary>
    [JsonStringEnumMemberName("workspace")] Workspace,
    /// <summary>A workspace or project binding grants it.</summary>
    [JsonStringEnumMemberName("project")] Project,
    /// <summary>A workspace, project, or matching environment binding grants it.</summary>
    [JsonStringEnumMemberName("environment")] Environment,
}

public sealed record PermissionDefinition(string Name, PermissionLevel Level, string Description);

/// <summary>
/// Named permissions of the Studio rights model (Dossier integrierte-plattform
/// §9.5, IP-8). Every route declares exactly one of them; the route inventory
/// test fails for a route without one. <see cref="Anonymous"/> and
/// <see cref="SelfSession"/> need no role binding.
/// </summary>
public static class IamPermissions
{
    public const string Anonymous = "public";
    public const string SelfSession = "self.session";
    public const string RunnerService = "runner.service";

    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceSettingsWrite = "workspace.settings.write";
    public const string WorkspaceOperate = "workspace.operate";
    public const string WorkspaceAdminister = "workspace.administer";
    public const string AccessRead = "access.read";
    public const string AccessManage = "access.manage";

    public const string ProjectRead = "project.read";
    public const string ProjectCardsWrite = "project.cards.write";
    public const string ProjectRunsOperate = "project.runs.operate";
    public const string ProjectChatWrite = "project.chat.write";
    public const string ProjectDocsWrite = "project.docs.write";
    public const string ProjectSettingsWrite = "project.settings.write";

    public const string EnvironmentDeploy = "environment.deploy";
    public const string EnvironmentProductionDeploy = "environment.production.deploy";
    public const string EnvironmentProductionDeployApprove = "environment.production.deploy.approve";

    public const string SecretsRead = "secrets.read";
    public const string SecretsWrite = "secrets.write";
    public const string SecretsResolve = "secrets.resolve";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Anonymous, PermissionLevel.Workspace, "Open to anonymous callers: health, sign-in, invitation acceptance, enrollment."),
        new(SelfSession, PermissionLevel.Workspace, "The caller's own session: read it, change the password, sign out."),
        new(RunnerService, PermissionLevel.Workspace, "Runner and engine service routes; Runner credential scopes decide."),
        new(WorkspaceRead, PermissionLevel.Project, "Read workspace surfaces: system, models, usage, search, runner status."),
        new(WorkspaceSettingsWrite, PermissionLevel.Project, "Change workspace, model, quota, prompt, and tag settings; create projects."),
        new(WorkspaceOperate, PermissionLevel.Project, "Operate the installation: Task Server, hosts, supervisor, sweeps, recovery."),
        new(WorkspaceAdminister, PermissionLevel.Workspace, "Installation-wide changes reserved to the Owner, such as path import."),
        new(AccessRead, PermissionLevel.Workspace, "See users, groups, service accounts, and role bindings."),
        new(AccessManage, PermissionLevel.Workspace, "Invite users and change groups, service accounts, and role bindings."),
        new(ProjectRead, PermissionLevel.Project, "Read cards, runs, chats, documents, and project reports."),
        new(ProjectCardsWrite, PermissionLevel.Project, "Create, edit, order, and move cards."),
        new(ProjectRunsOperate, PermissionLevel.Project, "Start, stop, and continue runs, pipeline steps, merges, and integrations."),
        new(ProjectChatWrite, PermissionLevel.Project, "Send project, card, and Dossier chat turns."),
        new(ProjectDocsWrite, PermissionLevel.Project, "Edit the wiki, Dossiers, steering, and security documents."),
        new(ProjectSettingsWrite, PermissionLevel.Project, "Change project settings, pipeline, checkout, and URLs."),
        new(EnvironmentDeploy, PermissionLevel.Environment, "Release an artifact to a non-production environment."),
        new(EnvironmentProductionDeploy, PermissionLevel.Environment, "Deploy to or roll back an environment, production included."),
        new(EnvironmentProductionDeployApprove, PermissionLevel.Environment, "Approve or reject a production release."),
        new(SecretsRead, PermissionLevel.Environment, "See secret metadata: name, scope, version, expiry, last use."),
        new(SecretsWrite, PermissionLevel.Environment, "Set, rotate, and revoke secret values without reading them."),
        new(SecretsResolve, PermissionLevel.Environment, "Resolve a secret value; workload identities only, Owner as break-glass."),
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByName =
        All.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    public static bool IsKnown(string? name) => name is not null && ByName.ContainsKey(name);

    public static PermissionLevel Level(string name)
        => ByName.TryGetValue(name, out var definition) ? definition.Level : PermissionLevel.Workspace;

    /// <summary>Permissions that need no role binding at all.</summary>
    public static bool IsUnbound(string name) => name is Anonymous or SelfSession;

    /// <summary>
    /// Permissions an environment binding contributes outside its own
    /// environment, so an Approver of production can open the project's board.
    /// </summary>
    public static bool IsEnvironmentBindingRead(string name) => name is WorkspaceRead or ProjectRead;
}

public sealed record RoleDefinition(
    string Id,
    string DisplayName,
    int Rank,
    string Description,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<IamScopeKind> BindableScopes,
    bool ServiceAccountBindable);

/// <summary>
/// The Studio roles. A role always applies in a scope: a binding is a user,
/// group, or service account times a role times a scope. Owner is the
/// break-glass role and holds every human permission.
/// </summary>
public static class IamRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Developer = "developer";
    public const string Contributor = "contributor";
    public const string Viewer = "viewer";
    public const string Approver = "approver";

    private static readonly string[] ReadSet =
        [IamPermissions.WorkspaceRead, IamPermissions.ProjectRead, IamPermissions.SecretsRead];

    private static readonly string[] DeveloperSet =
    [
        .. ReadSet,
        IamPermissions.ProjectCardsWrite, IamPermissions.ProjectRunsOperate, IamPermissions.ProjectChatWrite,
        IamPermissions.ProjectDocsWrite, IamPermissions.EnvironmentDeploy, IamPermissions.SecretsResolve,
    ];

    private static readonly string[] AdminSet =
    [
        .. DeveloperSet,
        IamPermissions.WorkspaceSettingsWrite, IamPermissions.WorkspaceOperate, IamPermissions.AccessRead,
        IamPermissions.ProjectSettingsWrite, IamPermissions.EnvironmentProductionDeploy,
        IamPermissions.EnvironmentProductionDeployApprove, IamPermissions.SecretsWrite,
    ];

    public static readonly IReadOnlyList<RoleDefinition> All =
    [
        Role(Owner, "Owner", 50, "Break-glass role of the installation. Holds every permission.",
            IamPermissions.All.Select(permission => permission.Name)
                .Where(name => !IamPermissions.IsUnbound(name) && name != IamPermissions.RunnerService),
            [IamScopeKind.Workspace], serviceAccounts: false),
        Role(Admin, "Admin", 40, "Runs the workspace or a project: settings, operation, production deploys, secrets.",
            AdminSet, [IamScopeKind.Workspace, IamScopeKind.Project], serviceAccounts: true),
        Role(Developer, "Developer", 30, "Works on cards, runs, chats, and documents; deploys outside production.",
            DeveloperSet, [IamScopeKind.Workspace, IamScopeKind.Project, IamScopeKind.Environment], serviceAccounts: true),
        Role(Contributor, StudioRoles.ContributorDisplayName, 20, "Product role: writes and accepts cards in assigned projects.",
            [IamPermissions.WorkspaceRead, IamPermissions.ProjectRead, IamPermissions.ProjectCardsWrite, IamPermissions.ProjectChatWrite],
            [IamScopeKind.Project], serviceAccounts: false),
        Role(Viewer, "Viewer", 10, "Reads everything in its scope, secret values excluded.",
            ReadSet, [IamScopeKind.Workspace, IamScopeKind.Project, IamScopeKind.Environment], serviceAccounts: true),
        Role(Approver, "Approver", 15, "Approves or rejects production releases in its scope.",
            [IamPermissions.WorkspaceRead, IamPermissions.ProjectRead, IamPermissions.EnvironmentProductionDeployApprove],
            [IamScopeKind.Workspace, IamScopeKind.Project, IamScopeKind.Environment], serviceAccounts: false),
    ];

    private static readonly Dictionary<string, RoleDefinition> ById =
        All.ToDictionary(role => role.Id, StringComparer.Ordinal);

    public static RoleDefinition? Find(string? id) => id is not null && ById.TryGetValue(id, out var role) ? role : null;

    public static bool Grants(string role, string permission)
        => Find(role)?.Permissions.Contains(permission) == true;

    private static RoleDefinition Role(
        string id, string displayName, int rank, string description,
        IEnumerable<string> permissions, IamScopeKind[] scopes, bool serviceAccounts)
        => new(id, displayName, rank, description,
            new HashSet<string>(permissions, StringComparer.Ordinal), new HashSet<IamScopeKind>(scopes), serviceAccounts);
}
