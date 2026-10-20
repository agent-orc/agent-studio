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

        const string write = IamPermissions.ProjectCardsWrite;
        Assert.Equal(IamRoles.Contributor, PermissionEvaluator.StrongestRole(write, User(), bindings, Target("alpha")));
        Assert.Equal(IamRoles.Developer, PermissionEvaluator.StrongestRole(write, User(), bindings, Target("beta")));
        Assert.Equal(IamRoles.Contributor, PermissionEvaluator.StrongestRole(write, User(), bindings, Multi("alpha", "beta")));
        Assert.Equal(IamRoles.Developer, PermissionEvaluator.StrongestRole(write, User(), bindings, PermissionTarget.Unaddressed));
        Assert.Null(PermissionEvaluator.StrongestRole(write, User(), bindings, Target("gamma")));
        // Only bindings that grant the permission rank: Contributor holds no document write.
        Assert.Null(PermissionEvaluator.StrongestRole(IamPermissions.ProjectDocsWrite, User(), bindings, Target("alpha")));
    }

    [Fact]
    public void An_environment_binding_ranks_as_its_role_only_in_its_own_environment()
    {
        RoleBinding[] bindings =
        [
            Binding(IamPrincipalKind.User, UserId, IamRoles.Contributor, Project("beta")),
            Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Environment("beta", IamEnvironments.Stage)),
        ];

        // Developer@beta/stage grants no project-level write and does not
        // lift the Product role in beta (AGT-3228 review, round 3).
        Assert.Equal(IamRoles.Contributor,
            PermissionEvaluator.StrongestRole(IamPermissions.ProjectCardsWrite, User(), bindings, Target("beta")));
        var write = PermissionEvaluator.Project(IamPermissions.ProjectCardsWrite, User(), bindings, Target("beta"));
        Assert.Equal((StudioRoles.Contributor, IamRoles.Contributor), (write.LegacyRole, write.Role));
        var read = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(), bindings, Target("beta"));
        Assert.Equal(IamRoles.Contributor, read.Role);
        var unaddressed = PermissionEvaluator.Project(IamPermissions.ProjectCardsWrite, User(), bindings, PermissionTarget.Unaddressed);
        Assert.Equal(IamRoles.Contributor, unaddressed.Role);
        // In its own environment the binding ranks as Developer.
        Assert.Equal(IamRoles.Developer, PermissionEvaluator.StrongestRole(IamPermissions.EnvironmentDeploy, User(), bindings,
            Target("beta", IamEnvironments.Stage)));

        // An environment binding alone reads its project as a viewer.
        RoleBinding[] stageOnly = [Binding(IamPrincipalKind.User, UserId, IamRoles.Developer, Environment("beta", IamEnvironments.Stage))];
        var stageRead = PermissionEvaluator.Project(IamPermissions.ProjectRead, User(), stageOnly, Target("beta"));
        Assert.Equal((StudioRoles.Viewer, IamRoles.Viewer), (stageRead.LegacyRole, stageRead.Role));
        Assert.False(Evaluate(IamPermissions.ProjectCardsWrite, User(), stageOnly, Target("beta")).Allowed);
    }

    [Fact]
    public void Environment_scopes_use_the_canonical_environment_kind()
    {
        var normalized = AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", " Production ")));
        Assert.Equal(IamScope.ForEnvironment("alpha", IamEnvironments.Production), normalized.Scope);
        var invalid = Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "prod-eu"))));
        Assert.Equal((400, "invalid-scope"), (invalid.Status, invalid.Code));

        // A route that names an environment id resolves to its declaration's kind.
        AgentStudio.Projects.EnvironmentDeclaration[] declarations =
        [
            new("stage-eu", "Stage", "stage", "host-a", "https://stage.example.test", "container", "8081", "https://stage.example.test/health", "person", [], 8080),
            new("prod-eu", "Production", "production", "host-b", "https://prod.example.test", "container", "8082", "https://prod.example.test/health", "person", [], 8080),
        ];
        Assert.Equal(IamEnvironments.Production, IamEnvironments.ForDeclaration(declarations, "prod-eu"));
        Assert.Equal(IamEnvironments.Stage, IamEnvironments.ForDeclaration(declarations, "stage-eu"));
        Assert.Null(IamEnvironments.ForDeclaration(declarations, "production"));
        Assert.Null(IamEnvironments.ForDeclaration(null, "prod-eu"));

        RoleBinding[] approver = [Binding(IamPrincipalKind.User, UserId, IamRoles.Approver, Environment("alpha", IamEnvironments.Production))];
        Assert.True(Evaluate(IamPermissions.EnvironmentProductionDeployApprove, User(), approver,
            Target("alpha", IamEnvironments.ForDeclaration(declarations, "prod-eu"))).Allowed);
        Assert.False(Evaluate(IamPermissions.EnvironmentProductionDeployApprove, User(), approver, Target("alpha", "prod-eu")).Allowed);
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
