using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The networked middleware enforces the permission each routed endpoint
/// declares, after the legacy role checks, for humans and service accounts.
/// </summary>
public sealed class IamAccessMiddlewareTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iam-middleware-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly AccessSecurityStore _store;
    private readonly string _ownerId;

    public IamAccessMiddlewareTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build();
        _store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        _ownerId = _store.Bootstrap(new BootstrapRequest("first.owner", Password, null)).User.Id;
    }

    [Fact]
    public async Task A_developer_binding_grants_its_permissions_and_nothing_else()
    {
        var developer = Invite("project.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));

        Assert.True((await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/alpha/snapshot", developer)).Called);
        Assert.True((await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", developer)).Called);

        var settings = await Invoke("PUT", "/api/projects/{projectName}/auto-commit", "/api/projects/alpha/auto-commit", developer);
        Assert.Equal((403, "permission-denied", false), (settings.Status, settings.Error, settings.Called));
        var production = await Invoke("POST", "/api/projects/{projectName}/environments/promotions/approve",
            "/api/projects/alpha/environments/promotions/approve", developer);
        Assert.Equal((403, false), (production.Status, production.Called));
        var foreign = await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/beta/snapshot", developer);
        Assert.Equal((403, "project-scope-denied", false), (foreign.Status, foreign.Error, foreign.Called));
        var workspace = await Invoke("PUT", "/api/workspaces/{id}/autonomy", "/api/workspaces/main/autonomy", developer);
        Assert.Equal((403, "permission-denied"), (workspace.Status, workspace.Error));
    }

    [Fact]
    public async Task An_environment_approver_approves_production_and_cannot_release_or_edit()
    {
        var approver = Invite("release.approver",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha")),
            new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "production")));

        Assert.True((await Invoke("POST", "/api/projects/{projectName}/environments/promotions/approve",
            "/api/projects/alpha/environments/promotions/approve", approver)).Called);
        // Outside approvals an Approver is read-only, like a viewer.
        var release = await Invoke("POST", "/api/projects/{projectName}/environments/releases",
            "/api/projects/alpha/environments/releases", approver);
        Assert.Equal((403, "role-denied", false), (release.Status, release.Error, release.Called));
        var wiki = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", approver);
        Assert.Equal((403, "role-denied", false), (wiki.Status, wiki.Error, wiki.Called));
    }

    [Fact]
    public async Task The_product_allowlist_applies_where_contributor_is_the_strongest_role()
    {
        var mixed = Invite("mixed.user",
            new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("alpha")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("beta")));

        var productSettings = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", mixed);
        Assert.Equal((403, "role-denied"), (productSettings.Status, productSettings.Error));
        Assert.True((await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/alpha/snapshot", mixed)).Called);
        Assert.True((await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/beta/wiki/pages", mixed)).Called);
    }

    [Fact]
    public async Task Group_bindings_open_routes_for_members()
    {
        var member = Invite("group.member", new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha")));
        var denied = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", member);
        Assert.Equal(403, denied.Status);

        var group = _store.CreateGroup(new CreateGroupRequest("Writers", null, [member.UserId]), _ownerId);
        _store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Developer, IamScope.ForProject("alpha")), _ownerId);

        Assert.True((await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", member)).Called);
    }

    [Fact]
    public async Task A_route_without_a_declared_permission_is_refused_even_for_the_owner()
    {
        var owner = Login("first.owner", Password);

        var result = await Invoke("GET", "/api/brand-new-area", "/api/brand-new-area", owner);

        Assert.Equal((403, "permission-undeclared", false), (result.Status, result.Error, result.Called));
    }

    [Fact]
    public async Task Service_accounts_act_through_their_own_bindings_without_csrf()
    {
        var account = _store.CreateServiceAccount(new CreateServiceAccountRequest("docs-bot", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), _ownerId);
        var token = _store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)).Secret;

        var write = await InvokeBearer("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", token);
        Assert.True(write.Called);
        Assert.Equal("service-account:" + account.Id, write.Context.Items["ClientId"]);
        Assert.Equal("service-account:" + account.Id, AccessSecurityMiddleware.ActorId(write.Context, "local-operator"));
        Assert.IsType<ServiceAccountPrincipal>(write.Context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem]);
        var synthetic = Assert.IsType<HumanPrincipal>(write.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]);
        Assert.Equal(("service-account:" + account.Id, StudioRoles.Operator), (synthetic.User.Id, synthetic.User.Role));
        Assert.Equal(["alpha"], synthetic.User.Projects);
        // A service account is attributed as a non-human actor.
        var actor = AgentStudio.Tasks.TaskCrudEndpoints.OperatorActor(write.Context);
        Assert.Equal("service-account:" + account.Id, actor);
        Assert.False(TimelineActors.IsHuman(actor));

        // The task collection filters per task for the projected principal;
        // another unaddressed read needs a workspace binding.
        var tasks = await InvokeBearer("GET", "/api/tasks/", "/api/tasks", token);
        Assert.True(tasks.Called);
        var collection = await InvokeBearer("GET", "/api/workspace/screenshots", "/api/workspace/screenshots", token);
        Assert.Equal((403, "project-scope-denied"), (collection.Status, collection.Error));
        var audit = File.ReadAllLines(Path.Combine(_store.SecurityFolder, AccessSecurityStore.ServiceAccountAuditFileName));
        Assert.Contains(audit, row => row.Contains($"\"actor\":\"service-account:{account.Id}\"", StringComparison.Ordinal)
            && row.Contains("\"status\":403", StringComparison.Ordinal));
        Assert.All(audit, row => Assert.DoesNotContain(token, row, StringComparison.Ordinal));
        var session = await InvokeBearer("POST", "/api/auth/logout", "/api/auth/logout", token);
        Assert.Equal((403, "token-route-denied"), (session.Status, session.Error));
        var runnerRoute = await InvokeBearer("POST", "/api/runner/claim", "/api/runner/claim", token);
        Assert.Equal((401, "runner-authentication-required"), (runnerRoute.Status, runnerRoute.Error));
        var settings = await InvokeBearer("PUT", "/api/projects/{projectName}/auto-commit", "/api/projects/alpha/auto-commit", token);
        Assert.Equal((403, "permission-denied"), (settings.Status, settings.Error));

        _store.DisableServiceAccount(account.Id);
        var disabled = await InvokeBearer("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", token);
        Assert.Equal((401, false), (disabled.Status, disabled.Called));
    }

    [Fact]
    public async Task Event_streams_need_a_role_binding()
    {
        var created = _store.CreateUser(new CreateUserRequest("unbound.ops", "Unbound", StudioRoles.Operator, null, Password));
        var login = _store.Login(created.User.Username, created.TemporaryPassword, "unbound|127.0.0.1");
        _store.ChangePassword(new HumanPrincipal(login.User, _store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(created.TemporaryPassword, "unbound operator phrase!"));
        var unbound = new SignedIn(login.User.Id, login.SessionToken, login.CsrfToken);
        var viewer = Invite("hub.viewer", new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha")));

        var denied = await Invoke("POST", "/hubs/jobs/negotiate", "/hubs/jobs/negotiate", unbound);
        Assert.Equal((403, "permission-denied"), (denied.Status, denied.Error));
        Assert.True((await Invoke("POST", "/hubs/jobs/negotiate", "/hubs/jobs/negotiate", viewer)).Called);
    }

    [Fact]
    public async Task Unaddressed_writes_need_a_workspace_binding_unless_the_handler_checks_each_project()
    {
        var scoped = Invite("scoped.dev",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")),
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta")));
        var workspace = Invite("workspace.dev", new IamGrantRequest(IamRoles.Developer, IamScope.Workspace));

        var supervisor = await Invoke("POST", "/api/supervisor/pause", "/api/supervisor/pause", scoped);
        Assert.Equal((403, "project-scope-denied", false), (supervisor.Status, supervisor.Error, supervisor.Called));
        Assert.True((await Invoke("POST", "/api/supervisor/pause", "/api/supervisor/pause", workspace)).Called);

        // Body-addressed routes reach their handler, which checks every
        // affected project against the captured access (see IamHandlerAccessTests).
        var create = await Invoke("POST", "/api/tasks", "/api/tasks", scoped);
        Assert.True(create.Called);
        var access = Assert.IsType<RequestAccess>(create.Context.Items[AccessSecurityMiddleware.RequestAccessItem]);
        Assert.True(access.AllowsProject(IamPermissions.ProjectCardsWrite, "alpha"));
        Assert.False(access.AllowsProject(IamPermissions.ProjectCardsWrite, "beta"));
        Assert.True((await Invoke("POST", "/api/tasks/batch-move", "/api/tasks/batch-move", scoped)).Called);
    }

    [Fact]
    public async Task Report_and_supervisor_routes_address_the_project_they_name()
    {
        var scoped = Invite("reports.dev",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")),
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta")));

        Assert.True((await Invoke("POST", "/api/supervisor/{project}/intervene/pause-pickup",
            "/api/supervisor/alpha/intervene/pause-pickup", scoped)).Called);
        Assert.True((await Invoke("POST", "/api/drift/{project}/actions/adr-code-drift",
            "/api/drift/alpha/actions/adr-code-drift", scoped)).Called);
        Assert.True((await Invoke("PUT", "/api/analysis/{project}/schedule", "/api/analysis/alpha/schedule", scoped)).Called);
        var foreign = await Invoke("POST", "/api/supervisor/{project}/intervene/pause-pickup",
            "/api/supervisor/beta/intervene/pause-pickup", scoped);
        Assert.Equal((403, false), (foreign.Status, foreign.Called));
        // A literal route of the area names no project: its write needs the workspace.
        var literal = await Invoke("POST", "/api/drift/actions/code-pattern-drift", "/api/drift/actions/code-pattern-drift", scoped);
        Assert.Equal((403, "project-scope-denied", false), (literal.Status, literal.Error, literal.Called));
    }

    [Fact]
    public async Task A_stage_developer_releases_through_the_canonical_stage_key()
    {
        var stage = Invite("stage.dev",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForEnvironment("alpha", "Stage")));

        var release = await Invoke("POST", "/api/projects/{projectName}/environments/releases",
            "/api/projects/alpha/environments/releases", stage);
        Assert.True(release.Called);
        var approve = await Invoke("POST", "/api/projects/{projectName}/environments/promotions/approve",
            "/api/projects/alpha/environments/promotions/approve", stage);
        Assert.Equal((403, false), (approve.Status, approve.Called));
        // Outside its environment the binding reads only: a project write stays refused.
        var wiki = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", stage);
        Assert.Equal((403, false), (wiki.Status, wiki.Called));
    }

    [Fact]
    public async Task A_contributor_with_a_developer_environment_binding_keeps_the_product_role_in_that_project()
    {
        var mixed = Invite("stage.product",
            new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("beta")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForEnvironment("beta", IamEnvironments.Stage)));

        var create = await Invoke("POST", "/api/tasks", "/api/tasks", mixed);
        Assert.True(create.Called);
        var access = Assert.IsType<RequestAccess>(create.Context.Items[AccessSecurityMiddleware.RequestAccessItem]);
        Assert.True(access.IsProductRoleIn("beta"));
        Assert.Equal(StudioRoles.Contributor,
            Assert.IsType<HumanPrincipal>(create.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User.Role);
        var snapshot = await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/beta/snapshot", mixed);
        Assert.Equal(StudioRoles.Contributor,
            Assert.IsType<HumanPrincipal>(snapshot.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User.Role);
    }

    [Fact]
    public async Task Operator_authority_and_the_operator_decision_role_belong_to_admin_and_owner()
    {
        var developer = Invite("authority.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));
        var admin = Invite("authority.admin", new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha")));
        var owner = Login("first.owner", Password);

        var expectations = new (SignedIn User, string LegacyRole, bool Authority, string DecisionRole)[]
        {
            (developer, StudioRoles.Operator, false, IamRoles.Developer),
            (admin, StudioRoles.Operator, true, StudioRoles.Operator),
            (owner, StudioRoles.Owner, true, StudioRoles.Owner),
        };
        foreach (var (user, legacyRole, authority, decisionRole) in expectations)
        {
            var result = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", user);
            Assert.True(result.Called);
            Assert.Equal(legacyRole, Assert.IsType<HumanPrincipal>(result.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User.Role);
            Assert.Equal(authority, ProjectAccessAuthorization.HasOperatorAuthority(result.Context));
            Assert.Equal(decisionRole, ProjectAccessAuthorization.DecisionRole(result.Context));
        }
        var operatorDecision = new DecisionContent { Decider = "role:operator" };
        Assert.False(DecisionCardPolicy.MayDecide(operatorDecision, developer.UserId, IamRoles.Developer));
        Assert.True(DecisionCardPolicy.MayDecide(operatorDecision, admin.UserId, StudioRoles.Operator));
    }

    [Fact]
    public async Task Handlers_see_a_principal_projected_for_the_addressed_project()
    {
        var mixed = Invite("projected.user",
            new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("beta")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));

        var beta = await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/beta/snapshot", mixed);
        var betaUser = Assert.IsType<HumanPrincipal>(beta.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User;
        Assert.Equal(StudioRoles.Contributor, betaUser.Role);
        Assert.Equal(["beta", "alpha"], betaUser.Projects);
        var alpha = await Invoke("GET", "/api/projects/{projectName}/snapshot", "/api/projects/alpha/snapshot", mixed);
        Assert.Equal(StudioRoles.Operator,
            Assert.IsType<HumanPrincipal>(alpha.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User.Role);
        var runs = await Invoke("POST", "/api/projects/{projectName}/queue-health/refresh", "/api/projects/alpha/queue-health/refresh", mixed);
        Assert.Equal(["alpha"], Assert.IsType<HumanPrincipal>(runs.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]).User.Projects);

        // The persisted record keeps its downgrade-safe summary.
        Assert.Equal(StudioRoles.Contributor, _store.ListUsers().Single(user => user.Id == mixed.UserId).Role);
    }

    [Fact]
    public async Task The_task_server_proxy_declares_read_and_operate_and_forwards_a_binding_derived_role()
    {
        var developer = Invite("proxy.dev", new IamGrantRequest(IamRoles.Developer, IamScope.Workspace));
        var admin = Invite("proxy.admin", new IamGrantRequest(IamRoles.Admin, IamScope.Workspace));
        var owner = Login("first.owner", Password);

        var read = await InvokeProxy("GET", developer);
        Assert.True(read.Called);
        Assert.Equal(StudioRoles.Viewer, AgentStudio.Host.TaskServerPlaneProxy.ActingRole(read.Context, _config));
        var write = await InvokeProxy("POST", developer);
        Assert.Equal((403, "permission-denied", false), (write.Status, write.Error, write.Called));
        var operate = await InvokeProxy("POST", admin);
        Assert.True(operate.Called);
        Assert.Equal(StudioRoles.Operator, AgentStudio.Host.TaskServerPlaneProxy.ActingRole(operate.Context, _config));
        Assert.Equal(StudioRoles.Owner, AgentStudio.Host.TaskServerPlaneProxy.ActingRole((await InvokeProxy("POST", owner)).Context, _config));
    }

    [Fact]
    public async Task Studio_service_account_bearers_never_take_the_proxied_plane_skip()
    {
        var proxied = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
            ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071",
        }).Build();
        var account = _store.CreateServiceAccount(new CreateServiceAccountRequest("proxy-bot", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), _ownerId);
        var token = _store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)).Secret;
        var foreign = "tsk." + Guid.NewGuid().ToString("N");

        var studio = await Run("GET", "/api/v1/{**path}", "/api/v1/management/status", Bearer(token), proxied, ProxyMetadata());
        Assert.Equal((403, "token-route-denied", false), (studio.Status, studio.Error, studio.Called));
        var upstream = await Run("GET", "/api/v1/{**path}", "/api/v1/management/status", Bearer(foreign), proxied, ProxyMetadata());
        Assert.True(upstream.Called);
    }

    [Fact]
    public async Task A_method_mismatch_keeps_its_405_endpoint()
    {
        var owner = Login("first.owner", Password);
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; }, _config, _store, clock: _clock);
        var context = new DefaultHttpContext();
        context.Request.Method = "PATCH";
        context.Request.Path = "/api/projects/alpha/snapshot";
        context.Request.Scheme = "https";
        context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}");
        context.Request.Headers["X-CSRF-Token"] = new StringValues(owner.CsrfToken);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "405 HTTP Method Not Supported"));

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.True(RoutePermissionCatalog.IsMethodRejection(context.GetEndpoint()!));
    }

    private sealed record SignedIn(string UserId, string SessionToken, string CsrfToken);

    private sealed record Outcome(HttpContext Context, int Status, string? Error, bool Called);

    private SignedIn Invite(string username, params IamGrantRequest[] grants)
    {
        var invite = _store.InviteUser(new InviteUserRequest(username, username, "", null, grants));
        var accepted = _store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "accepted pass phrase!"));
        return new SignedIn(accepted.User.Id, accepted.SessionToken, accepted.CsrfToken);
    }

    private SignedIn Login(string username, string password)
    {
        var login = _store.Login(username, password, $"{username}|127.0.0.1");
        return new SignedIn(login.User.Id, login.SessionToken, login.CsrfToken);
    }

    private Task<Outcome> Invoke(string method, string pattern, string path, SignedIn user)
        => Run(method, pattern, path, context =>
        {
            context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={user.SessionToken}");
            context.Request.Headers["X-CSRF-Token"] = new StringValues(user.CsrfToken);
        });

    private Task<Outcome> InvokeBearer(string method, string pattern, string path, string token)
        => Run(method, pattern, path, Bearer(token));

    private Task<Outcome> InvokeProxy(string method, SignedIn user)
        => Run(method, "/api/v1/{**path}", "/api/v1/runners", context =>
        {
            context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={user.SessionToken}");
            context.Request.Headers["X-CSRF-Token"] = new StringValues(user.CsrfToken);
        }, metadata: ProxyMetadata());

    private static object[] ProxyMetadata()
        => [AgentStudio.Host.TaskServerPlaneProxyEndpoint.Instance,
            new MethodPermissionMetadata(IamPermissions.WorkspaceRead, IamPermissions.WorkspaceOperate)];

    private static Action<HttpContext> Bearer(string token)
        => context => context.Request.Headers.Authorization = new StringValues("Bearer " + token);

    private async Task<Outcome> Run(
        string method, string pattern, string path, Action<HttpContext> authenticate,
        IConfiguration? configuration = null, object[]? metadata = null)
    {
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; },
            configuration ?? _config, _store, clock: _clock);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        authenticate(context);
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0,
            new EndpointMetadataCollection([new HttpMethodMetadata([method]), .. metadata ?? []]), pattern));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return new Outcome(context, context.Response.StatusCode, ErrorCode(context), called);
    }

    private static string? ErrorCode(HttpContext context)
    {
        if (context.Response.Body.Length == 0) return null;
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
