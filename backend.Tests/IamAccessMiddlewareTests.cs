using System.Text.Json;
using AgentStudio.Security;
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
        Assert.Equal((403, false), (foreign.Status, foreign.Called));
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
        var release = await Invoke("POST", "/api/projects/{projectName}/environments/releases",
            "/api/projects/alpha/environments/releases", approver);
        Assert.Equal((403, "permission-denied"), (release.Status, release.Error));
        var wiki = await Invoke("POST", "/api/projects/{projectName}/wiki/pages", "/api/projects/alpha/wiki/pages", approver);
        Assert.Equal((403, "permission-denied"), (wiki.Status, wiki.Error));
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
        Assert.Equal(account.Id, write.Context.Items["ClientId"]);
        Assert.IsType<ServiceAccountPrincipal>(write.Context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem]);
        Assert.Null(write.Context.Items[AccessSecurityMiddleware.HumanPrincipalItem]);

        var collection = await InvokeBearer("GET", "/api/tasks/", "/api/tasks", token);
        Assert.Equal((403, "permission-denied"), (collection.Status, collection.Error));
        var session = await InvokeBearer("POST", "/api/auth/logout", "/api/auth/logout", token);
        Assert.Equal((403, "human-session-required"), (session.Status, session.Error));
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
        => Run(method, pattern, path, context => context.Request.Headers.Authorization = new StringValues("Bearer " + token));

    private async Task<Outcome> Run(string method, string pattern, string path, Action<HttpContext> authenticate)
    {
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; }, _config, _store, clock: _clock);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        authenticate(context);
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0,
            new EndpointMetadataCollection(new HttpMethodMetadata([method])), pattern));
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
