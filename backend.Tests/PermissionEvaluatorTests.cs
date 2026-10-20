using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Permission evaluation table (IP-8): the effective permission of a
/// principal is the union of its role bindings, and a binding grants only
/// what its role holds, at the scope kinds the permission's level allows,
/// for the projects and environments its scope covers.
/// </summary>
public sealed class PermissionEvaluatorTests
{
    private const string UserId = "usr_subject";
    private const string GroupId = "grp_team";
    private const string AccountId = "sa_deployer";

    private static readonly IamScope Workspace = IamScope.Workspace;
    private static IamScope Project(string project) => IamScope.ForProject(project);
    private static IamScope Environment(string project, string environment) => IamScope.ForEnvironment(project, environment);

    public static TheoryData<string, string, IamScope, string, string, string?, bool> Matrix => new()
    {
        // case, role, binding scope, permission, target ("-" unaddressed, "*" workspace-wide, else project), environment, expected
        { "owner holds access management", IamRoles.Owner, Workspace, IamPermissions.AccessManage, "*", null, true },
        { "owner holds workspace administration", IamRoles.Owner, Workspace, IamPermissions.WorkspaceAdminister, "-", null, true },
        { "owner resolves secrets as break-glass", IamRoles.Owner, Workspace, IamPermissions.SecretsResolve, "alpha", "production", true },
        { "admin does not read access (Owner only, as before)", IamRoles.Admin, Workspace, IamPermissions.AccessRead, "*", null, false },
        { "workspace admin writes workspace settings", IamRoles.Admin, Workspace, IamPermissions.WorkspaceSettingsWrite, "*", null, true },
        { "workspace admin operates the installation", IamRoles.Admin, Workspace, IamPermissions.WorkspaceOperate, "*", null, true },
        { "project admin no longer writes workspace settings", IamRoles.Admin, Project("alpha"), IamPermissions.WorkspaceSettingsWrite, "-", null, false },
        { "project admin no longer operates the installation", IamRoles.Admin, Project("alpha"), IamPermissions.WorkspaceOperate, "-", null, false },
        { "project admin cannot write settings even addressed", IamRoles.Admin, Project("alpha"), IamPermissions.WorkspaceSettingsWrite, "alpha", null, false },
        { "admin cannot manage access", IamRoles.Admin, Workspace, IamPermissions.AccessManage, "*", null, false },
        { "admin cannot administer the installation", IamRoles.Admin, Workspace, IamPermissions.WorkspaceAdminister, "-", null, false },
        { "admin approves production", IamRoles.Admin, Workspace, IamPermissions.EnvironmentProductionDeployApprove, "alpha", "production", true },
        { "human admin cannot resolve secrets", IamRoles.Admin, Workspace, IamPermissions.SecretsResolve, "alpha", "production", false },
        { "project admin writes its settings", IamRoles.Admin, Project("alpha"), IamPermissions.ProjectSettingsWrite, "alpha", null, true },
        { "project admin cannot write another project", IamRoles.Admin, Project("alpha"), IamPermissions.ProjectSettingsWrite, "beta", null, false },
        { "project admin gets no workspace-level access read", IamRoles.Admin, Project("alpha"), IamPermissions.AccessRead, "*", null, false },
        { "project binding does not cover a workspace-wide route", IamRoles.Admin, Project("alpha"), IamPermissions.WorkspaceRead, "*", null, false },
        { "project binding covers an unaddressed collection", IamRoles.Viewer, Project("alpha"), IamPermissions.ProjectRead, "-", null, true },
        { "developer writes cards", IamRoles.Developer, Project("alpha"), IamPermissions.ProjectCardsWrite, "alpha", null, true },
        { "developer operates runs", IamRoles.Developer, Project("alpha"), IamPermissions.ProjectRunsOperate, "alpha", null, true },
        { "developer cannot write project settings", IamRoles.Developer, Project("alpha"), IamPermissions.ProjectSettingsWrite, "alpha", null, false },
        { "developer deploys stage", IamRoles.Developer, Project("alpha"), IamPermissions.EnvironmentDeploy, "alpha", "stage", true },
        { "developer cannot deploy production", IamRoles.Developer, Project("alpha"), IamPermissions.EnvironmentProductionDeploy, "alpha", "production", false },
        { "developer cannot approve production", IamRoles.Developer, Project("alpha"), IamPermissions.EnvironmentProductionDeployApprove, "alpha", "production", false },
        { "developer cannot operate the workspace", IamRoles.Developer, Workspace, IamPermissions.WorkspaceOperate, "-", null, false },
        { "contributor writes cards", IamRoles.Contributor, Project("alpha"), IamPermissions.ProjectCardsWrite, "alpha", null, true },
        { "contributor chats", IamRoles.Contributor, Project("alpha"), IamPermissions.ProjectChatWrite, "alpha", null, true },
        { "contributor cannot start runs", IamRoles.Contributor, Project("alpha"), IamPermissions.ProjectRunsOperate, "alpha", null, false },
        { "viewer reads", IamRoles.Viewer, Project("alpha"), IamPermissions.ProjectRead, "alpha", null, true },
        { "viewer sees secret metadata", IamRoles.Viewer, Project("alpha"), IamPermissions.SecretsRead, "alpha", "production", true },
        { "viewer cannot write cards", IamRoles.Viewer, Workspace, IamPermissions.ProjectCardsWrite, "alpha", null, false },
        { "viewer of alpha cannot read beta", IamRoles.Viewer, Project("alpha"), IamPermissions.ProjectRead, "beta", null, false },
        { "approver approves its environment", IamRoles.Approver, Environment("alpha", "production"), IamPermissions.EnvironmentProductionDeployApprove, "alpha", "production", true },
        { "approver of production cannot approve stage", IamRoles.Approver, Environment("alpha", "production"), IamPermissions.EnvironmentProductionDeployApprove, "alpha", "stage", false },
        { "approver of alpha cannot approve beta", IamRoles.Approver, Environment("alpha", "production"), IamPermissions.EnvironmentProductionDeployApprove, "beta", "production", false },
        { "environment binding reads its project", IamRoles.Approver, Environment("alpha", "production"), IamPermissions.ProjectRead, "alpha", null, true },
        { "environment binding writes no cards", IamRoles.Developer, Environment("alpha", "stage"), IamPermissions.ProjectCardsWrite, "alpha", null, false },
        { "environment developer deploys its environment", IamRoles.Developer, Environment("alpha", "stage"), IamPermissions.EnvironmentDeploy, "alpha", "stage", true },
        { "environment developer cannot deploy another", IamRoles.Developer, Environment("alpha", "stage"), IamPermissions.EnvironmentDeploy, "alpha", "preview", false },
        { "nobody passes a runner route", IamRoles.Owner, Workspace, IamPermissions.RunnerService, "-", null, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Role_scope_and_permission_matrix(
        string scenario, string role, IamScope scope, string permission, string target, string? environment, bool expected)
    {
        var decision = Evaluate(permission, User(), [Binding(IamPrincipalKind.User, UserId, role, scope)], Target(target, environment));

        Assert.True(expected == decision.Allowed, $"{scenario}: {decision.Reason}");
    }

    [Theory]
    [InlineData(IamPermissions.Anonymous)]
    [InlineData(IamPermissions.SelfSession)]
    public void Unbound_permissions_need_no_binding(string permission)
        => Assert.True(Evaluate(permission, User(), [], PermissionTarget.WorkspaceWide).Allowed);

    [Fact]
    public void Unknown_permissions_and_principals_without_bindings_are_denied()
    {
        Assert.False(Evaluate("project.anything", User(), [Binding(IamPrincipalKind.User, UserId, IamRoles.Owner, Workspace)],
            PermissionTarget.WorkspaceWide).Allowed);
        Assert.False(Evaluate(IamPermissions.WorkspaceRead, User(), [], PermissionTarget.Unaddressed).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectRead, User(),
            [Binding(IamPrincipalKind.User, "usr_other", IamRoles.Owner, Workspace)], Target("alpha")).Allowed);
    }

    [Fact]
    public void Effective_permission_is_the_union_of_direct_and_group_bindings()
    {
        RoleBinding[] bindings =
        [
            Binding(IamPrincipalKind.User, UserId, IamRoles.Viewer, Project("alpha")),
            Binding(IamPrincipalKind.Group, GroupId, IamRoles.Developer, Project("beta")),
        ];

        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(), bindings, Target("alpha")).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(), bindings, Target("beta")).Allowed);
        Assert.True(Evaluate(IamPermissions.ProjectCardsWrite, User(GroupId), bindings, Target("beta")).Allowed);
        Assert.True(Evaluate(IamPermissions.ProjectRead, User(GroupId), bindings, Multi("alpha", "beta")).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(GroupId), bindings, Multi("alpha", "beta")).Allowed);
    }

    [Fact]
    public void Addressed_routes_fail_closed_and_a_project_query_only_narrows()
    {
        RoleBinding[] bindings = [Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Project("alpha"))];

        Assert.False(Evaluate(IamPermissions.ProjectRead, User(), bindings, Multi("")).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectRead, User(), bindings, new PermissionTarget(ProjectRouteScope.Addressed, [])).Allowed);
        Assert.True(Evaluate(IamPermissions.ProjectCardsWrite, User(), bindings, PermissionTarget.Unaddressed with { QueryProject = "alpha" }).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(), bindings, PermissionTarget.Unaddressed with { QueryProject = "beta" }).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(), bindings, Target("alpha") with { QueryProject = "beta" }).Allowed);
    }

    [Fact]
    public void Service_accounts_use_their_own_bindings_and_need_the_workspace_for_collections()
    {
        var account = IamSubject.ForServiceAccount(AccountId);
        RoleBinding[] bindings = [Binding(IamPrincipalKind.ServiceAccount, AccountId, IamRoles.Developer, Project("alpha"))];

        Assert.True(Evaluate(IamPermissions.ProjectRead, account, bindings, Target("alpha")).Allowed);
        Assert.True(Evaluate(IamPermissions.SecretsResolve, account, bindings, Target("alpha", "production")).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectRead, account, bindings, PermissionTarget.Unaddressed).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectRead, User(), bindings, Target("alpha")).Allowed);
        Assert.True(Evaluate(IamPermissions.ProjectRead, account,
            [Binding(IamPrincipalKind.ServiceAccount, AccountId, IamRoles.Viewer, Workspace)], PermissionTarget.Unaddressed).Allowed);
    }

    [Fact]
    public void Service_accounts_never_approve_production()
    {
        var account = IamSubject.ForServiceAccount(AccountId);
        RoleBinding[] bindings = [Binding(IamPrincipalKind.ServiceAccount, AccountId, IamRoles.Admin, Workspace)];

        Assert.False(Evaluate(IamPermissions.EnvironmentProductionDeployApprove, account, bindings, Target("alpha", "production")).Allowed);
        Assert.True(Evaluate(IamPermissions.EnvironmentProductionDeploy, account, bindings, Target("alpha", "production")).Allowed);
        Assert.True(Evaluate(IamPermissions.EnvironmentProductionDeployApprove, User(),
            [Binding(IamPrincipalKind.User, UserId, IamRoles.Admin, Workspace)], Target("alpha", "production")).Allowed);
    }

    [Fact]
    public void Body_addressed_routes_pass_with_a_project_binding_for_the_handler_to_check()
    {
        var deferred = new PermissionTarget(ProjectRouteScope.DeferredToHandler, []);
        RoleBinding[] human = [Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Project("alpha"))];
        RoleBinding[] account = [Binding(IamPrincipalKind.ServiceAccount, AccountId, IamRoles.Developer, Project("alpha"))];

        Assert.True(Evaluate(IamPermissions.ProjectCardsWrite, User(), human, deferred).Allowed);
        Assert.True(Evaluate(IamPermissions.ProjectCardsWrite, IamSubject.ForServiceAccount(AccountId), account, deferred).Allowed);
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(),
            [Binding(IamPrincipalKind.User, UserId, IamRoles.Viewer, Project("alpha"))], deferred).Allowed);
    }

    [Fact]
    public void The_projected_principal_holds_the_route_permission_projects_and_the_covering_role()
    {
        RoleBinding[] bindings =
        [
            Binding(IamPrincipalKind.User, UserId, IamRoles.Contributor, Project("beta")),
            Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Project("alpha")),
            Binding(IamPrincipalKind.User, UserId, IamRoles.Viewer, Project("gamma")),
        ];

        var beta = PermissionEvaluator.Project(IamPermissions.ProjectCardsWrite, User(), bindings, Target("beta"));
        Assert.Equal((StudioRoles.Contributor, IamRoles.Contributor), (beta.LegacyRole, beta.Role));
        Assert.Equal(["beta", "alpha"], beta.Projects);
        var alpha = PermissionEvaluator.Project(IamPermissions.ProjectCardsWrite, User(), bindings, Target("alpha"));
        Assert.Equal(StudioRoles.Operator, alpha.LegacyRole);
        var runs = PermissionEvaluator.Project(IamPermissions.ProjectRunsOperate, User(), bindings, PermissionTarget.Unaddressed);
        Assert.Equal(["alpha"], runs.Projects);
        var reads = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(), bindings, PermissionTarget.Unaddressed);
        Assert.Equal(["beta", "alpha", "gamma"], reads.Projects);
        var gamma = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(), bindings, Target("gamma"));
        Assert.Equal(StudioRoles.Viewer, gamma.LegacyRole);

        var owner = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(),
            [Binding(IamPrincipalKind.User, UserId, IamRoles.Owner, Workspace)], Target("gamma"));
        Assert.Equal(StudioRoles.Owner, owner.LegacyRole);
        var workspaceViewer = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(),
            [Binding(IamPrincipalKind.User, UserId, IamRoles.Viewer, Workspace)], PermissionTarget.Unaddressed);
        Assert.Equal(["*"], workspaceViewer.Projects);
    }

    [Fact]
    public void Project_aliases_match_through_the_supplied_comparer()
    {
        RoleBinding[] bindings = [Binding(IamPrincipalKind.User, UserId, IamRoles.Viewer, Project("PROJ-001"))];

        var decision = PermissionEvaluator.Evaluate(IamPermissions.ProjectRead, User(), bindings, Target("Website"),
            (bound, requested) => bound == "PROJ-001" && requested == "Website");

        Assert.True(decision.Allowed);
        Assert.Single(decision.GrantingBindings);
    }

    [Fact]
    public void Strongest_role_is_decided_per_addressed_project()
    {
        RoleBinding[] bindings =
        [
            Binding(IamPrincipalKind.User, UserId, IamRoles.Contributor, Project("alpha")),
            Binding(IamPrincipalKind.User, UserId, IamRoles.Approver, Environment("alpha", "production")),
            Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Project("beta")),
        ];

        Assert.Equal(IamRoles.Contributor, PermissionEvaluator.StrongestRole(User(), bindings, Target("alpha")));
        Assert.Equal(IamRoles.Developer, PermissionEvaluator.StrongestRole(User(), bindings, Target("beta")));
        Assert.Equal(IamRoles.Contributor, PermissionEvaluator.StrongestRole(User(), bindings, Multi("alpha", "beta")));
        Assert.Equal(IamRoles.Developer, PermissionEvaluator.StrongestRole(User(), bindings, PermissionTarget.Unaddressed));
        Assert.Null(PermissionEvaluator.StrongestRole(User(), bindings, Target("gamma")));
    }

    [Fact]
    public void Every_role_grants_only_known_permissions_and_owner_holds_every_human_permission()
    {
        foreach (var role in IamRoles.All)
            Assert.All(role.Permissions, permission => Assert.True(IamPermissions.IsKnown(permission), $"{role.Id}: {permission}"));
        var owner = IamRoles.Find(IamRoles.Owner)!;
        Assert.All(IamPermissions.All.Where(permission => !IamPermissions.IsUnbound(permission.Name)
                && permission.Name != IamPermissions.RunnerService),
            permission => Assert.Contains(permission.Name, owner.Permissions));
        Assert.All(IamRoles.All.Where(role => role.Id != IamRoles.Owner),
            role => Assert.DoesNotContain(IamPermissions.AccessManage, role.Permissions));
    }

    private static PermissionDecision Evaluate(string permission, IamSubject subject, IEnumerable<RoleBinding> bindings, PermissionTarget target)
        => PermissionEvaluator.Evaluate(permission, subject, bindings, target);

    private static IamSubject User(params string[] groups)
        => new(IamPrincipalKind.User, UserId, new HashSet<string>(groups, StringComparer.Ordinal));

    private static PermissionTarget Target(string target, string? environment = null) => target switch
    {
        "-" => PermissionTarget.Unaddressed with { Environment = environment },
        "*" => PermissionTarget.WorkspaceWide with { Environment = environment },
        _ => PermissionTarget.ForProject(target, environment),
    };

    private static PermissionTarget Multi(params string[] projects) => new(ProjectRouteScope.Addressed, projects);

    private static RoleBinding Binding(IamPrincipalKind kind, string principal, string role, IamScope scope) => new()
    {
        Id = $"rb_{kind}_{principal}_{role}_{scope.Kind}_{scope.Project}_{scope.Environment}",
        PrincipalKind = kind, PrincipalId = principal, Role = role, Scope = scope, Source = IamBindingSources.Manual,
    };
}
