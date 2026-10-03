using System.Security.Claims;
using System.Text.Json;
using AgentStudio.Registry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Scoped-operator authorization gaps (AGT-2988): the route or addressed task
/// decides the project and <c>?project=</c> may only narrow it; the
/// <c>/api/v1</c> bypass needs the real proxy endpoint and a well-formed
/// bearer; an empty membership never widens a scoped role; hubs honour the
/// forced password change and the project scope.
/// </summary>
public sealed class ScopedOperatorAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-scoped-auth-" + Guid.NewGuid().ToString("N"));

    // --- Pure route-scope policy -------------------------------------------

    public static TheoryData<string, string[], string?, bool> PolicyMatrix => new()
    {
        { "addressed-member", ["mine"], null, true },
        { "addressed-foreign", ["foreign"], null, false },
        { "addressed-foreign-query-cannot-replace", ["foreign"], "mine", false },
        { "addressed-member-query-narrows-to-foreign", ["mine"], "foreign", false },
        { "addressed-member-query-same", ["mine"], "mine", true },
        { "addressed-mixed-task-resolutions", ["mine", "foreign"], null, false },
        { "addressed-unresolved", [""], "mine", false },
        { "addressed-none", [], "mine", false },
        { "unaddressed", [], null, true },
        { "unaddressed-query-member", [], "mine", true },
        { "unaddressed-query-foreign", [], "foreign", false },
        { "deferred", [], null, true },
        { "deferred-query-foreign", [], "foreign", false },
        { "workspace-wide", [], null, false },
        { "workspace-wide-query-member", [], "mine", false },
    };

    [Theory]
    [MemberData(nameof(PolicyMatrix))]
    public void Route_scope_policy_decides_first_and_query_only_narrows(
        string scenario, string[] routeProjects, string? query, bool expected)
    {
        var route = scenario switch
        {
            _ when scenario.StartsWith("addressed", StringComparison.Ordinal) => ProjectRouteAddress.Addressed(routeProjects),
            _ when scenario.StartsWith("deferred", StringComparison.Ordinal) => ProjectRouteAddress.DeferredToHandler,
            _ when scenario.StartsWith("workspace-wide", StringComparison.Ordinal) => ProjectRouteAddress.WorkspaceWide,
            _ => ProjectRouteAddress.Unaddressed,
        };

        Assert.Equal(expected, ProjectScopePolicy.Allows(route, query, project => project == "mine"));
    }

    // --- 1. Route or task first; ?project= never replaces ------------------

    [Fact]
    public async Task Change_project_on_a_foreign_task_is_refused_even_with_a_member_project_query()
    {
        var fixture = NewTaskFixture();
        var login = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id]);

        var viaProject = await fixture.Invoke("POST", $"/api/tasks/{ForeignTask}/change-project",
            $"?project={fixture.Mine.Id}", login);
        Assert.Equal(StatusCodes.Status403Forbidden, viaProject.Context.Response.StatusCode);
        Assert.Equal("project-scope-denied", ErrorCode(viaProject.Context));
        Assert.False(viaProject.Called.Value);

        var viaDisplayName = await fixture.Invoke("POST", $"/api/tasks/{ForeignTask}/change-project",
            "?project=Mine", login);
        Assert.False(viaDisplayName.Called.Value);

        var viaWatchPath = await fixture.Invoke("POST", $"/api/tasks/{ForeignTask}/change-project",
            $"?watchPath={Uri.EscapeDataString(fixture.MinePath)}", login);
        Assert.False(viaWatchPath.Called.Value);
    }

    [Fact]
    public async Task Own_task_is_allowed_and_a_foreign_project_query_narrows_it_to_denied()
    {
        var fixture = NewTaskFixture();
        var login = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id]);

        var own = await fixture.Invoke("POST", $"/api/tasks/{MineTask}/change-project", $"?project={fixture.Mine.Id}", login);
        Assert.True(own.Called.Value);
        var ownNoQuery = await fixture.Invoke("POST", $"/api/tasks/{MineTask}/move-to-top", "", login);
        Assert.True(ownNoQuery.Called.Value);

        var narrowed = await fixture.Invoke("POST", $"/api/tasks/{MineTask}/change-project", $"?project={fixture.Foreign.Id}", login);
        Assert.Equal(StatusCodes.Status403Forbidden, narrowed.Context.Response.StatusCode);
        Assert.False(narrowed.Called.Value);
    }

    [Theory]
    [InlineData("GET", "/api/projects/{foreign}/security", "?project={mine}")]
    [InlineData("GET", "/api/runner/{foreign}/orchestrator-log", "?project={mine}")]
    [InlineData("GET", "/api/orchestrator/context/project:{foreign}", "?project={mine}")]
    [InlineData("GET", "/api/orchestrator/context/global", "?project={mine}")]
    [InlineData("GET", "/api/runner/global/status", "?project={mine}")]
    [InlineData("GET", "/api/projects/{mine}/security", "?project={foreign}")]
    [InlineData("POST", "/api/tasks/reorder", "?project={foreign}")]
    [InlineData("GET", "/api/tasks", "?project={foreign}")]
    [InlineData("GET", "/api/projects/settings", "?project={foreign}")]
    public async Task A_project_query_never_replaces_the_route_project(string method, string pathTemplate, string queryTemplate)
    {
        var fixture = NewTaskFixture();
        var login = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id]);
        string Fill(string value) => value.Replace("{mine}", fixture.Mine.Id).Replace("{foreign}", fixture.Foreign.Id);

        var result = await fixture.Invoke(method, Fill(pathTemplate), Fill(queryTemplate), login);

        Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
        Assert.False(result.Called.Value);
    }

    [Fact]
    public async Task Orchestrator_task_routes_use_the_project_before_the_task_key()
    {
        var fixture = NewTaskFixture();
        var login = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id]);
        Assert.NotEqual(MineTask, fixture.Mine.Id);

        foreach (var (method, prefix, suffix) in new[]
        {
            ("GET", "/api/orchestrator/context/task:", ""),
            ("POST", "/api/orchestrator/context/task:", "/refresh"),
            ("POST", "/api/orchestrator/sessions/task:", "/turns"),
        })
        {
            var own = await fixture.Invoke(method, $"{prefix}{fixture.Mine.Id}/{MineTask}{suffix}", "", login);
            Assert.True(own.Called.Value, $"{method} {prefix} should authorize the project ID before the task key.");

            var foreign = await fixture.Invoke(method, $"{prefix}{fixture.Foreign.Id}/{ForeignTask}{suffix}",
                $"?project={fixture.Mine.Id}", login);
            Assert.Equal(StatusCodes.Status403Forbidden, foreign.Context.Response.StatusCode);
            Assert.Equal("project-scope-denied", ErrorCode(foreign.Context));
            Assert.False(foreign.Called.Value);

            var narrowed = await fixture.Invoke(method, $"{prefix}{fixture.Mine.Id}/{MineTask}{suffix}",
                $"?project={fixture.Foreign.Id}", login);
            Assert.Equal(StatusCodes.Status403Forbidden, narrowed.Context.Response.StatusCode);
            Assert.False(narrowed.Called.Value);
        }
    }

    // --- 2a. /api/v1 bypass requires the proxy and a well-formed bearer ---

    [Theory]
    [InlineData("Bearer abc.DEF-123_~+/xyz==", true)]
    [InlineData("bearer tss.abcdefghijklmnop", true)]
    [InlineData("Bearer ", false)]
    [InlineData("Bearer", false)]
    [InlineData("Bearer  leading-space", false)]
    [InlineData("Bearer two words", false)]
    [InlineData("Bearer ==", false)]
    [InlineData("Bearer abc=def", false)]
    [InlineData("Bearer abc\tdef", false)]
    [InlineData("Basic dXNlcjpwYXNz", false)]
    [InlineData("", false)]
    public void Bearer_shape_is_checked_strictly(string header, bool expected)
        => Assert.Equal(expected, AccessSecurityMiddleware.IsWellFormedBearer(new StringValues(header)));

    [Fact]
    public void Missing_oversized_or_repeated_authorization_headers_are_not_well_formed()
    {
        Assert.False(AccessSecurityMiddleware.IsWellFormedBearer(StringValues.Empty));
        Assert.False(AccessSecurityMiddleware.IsWellFormedBearer(new StringValues("Bearer " + new string('a', 4097))));
        Assert.True(AccessSecurityMiddleware.IsWellFormedBearer(new StringValues("Bearer " + new string('a', 4096))));
        Assert.False(AccessSecurityMiddleware.IsWellFormedBearer(new StringValues(["Bearer one", "Bearer two"])));
    }

    [Fact]
    public async Task V1_skips_local_auth_only_for_the_routed_proxy_with_a_well_formed_bearer()
    {
        var store = NewStore(out var config, proxied: true);
        var proxyEndpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(TaskServerPlaneProxyEndpoint.Instance), "task-server-proxy");
        var localEndpoint = new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "local-v1");

        var proxied = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: proxyEndpoint, authorization: "Bearer tss.valid-token");
        Assert.True(proxied.Called.Value);

        var malformed = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: proxyEndpoint, authorization: "Bearer not a token");
        Assert.Equal(StatusCodes.Status401Unauthorized, malformed.Context.Response.StatusCode);
        Assert.False(malformed.Called.Value);

        var otherScheme = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: proxyEndpoint, authorization: "Basic dXNlcjpwYXNz");
        Assert.False(otherScheme.Called.Value);

        var notProxied = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: localEndpoint, authorization: "Bearer tss.valid-token");
        Assert.Equal(StatusCodes.Status401Unauthorized, notProxied.Context.Response.StatusCode);
        Assert.False(notProxied.Called.Value);

        var unrouted = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: null, authorization: "Bearer tss.valid-token");
        Assert.False(unrouted.Called.Value);
    }

    [Fact]
    public async Task V1_proxy_marker_is_ignored_when_no_standalone_task_server_is_configured()
    {
        var store = NewStore(out var config, proxied: false);
        var proxyEndpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(TaskServerPlaneProxyEndpoint.Instance), "task-server-proxy");

        var result = await InvokeRaw(config, store, "GET", "/api/v1/runners", endpoint: proxyEndpoint, authorization: "Bearer tss.valid-token");

        Assert.Equal(StatusCodes.Status401Unauthorized, result.Context.Response.StatusCode);
        Assert.False(result.Called.Value);
    }

    // --- 2b. Empty memberships never widen a scoped role -------------------

    [Theory]
    [InlineData(StudioRoles.Operator)]
    [InlineData(StudioRoles.Viewer)]
    public void Empty_membership_grants_no_project_to_a_scoped_role(string role)
    {
        var user = User(role, []);

        Assert.False(ProjectAccessAuthorization.HasUnrestrictedProjectAccess(user));
        Assert.False(ProjectAccessAuthorization.Allows(user, "PROJ-001"));
        Assert.False(ProjectAccessAuthorization.AllowsTasks(Context(user), ["PROJ-001"]));
    }

    [Fact]
    public void Owner_and_the_explicit_all_projects_grant_are_unrestricted()
    {
        Assert.True(ProjectAccessAuthorization.HasUnrestrictedProjectAccess(User(StudioRoles.Owner, [])));
        var granted = User(StudioRoles.Operator, [ProjectAccessAuthorization.AllProjectsGrant]);
        Assert.True(ProjectAccessAuthorization.HasUnrestrictedProjectAccess(granted));
        Assert.True(ProjectAccessAuthorization.Allows(granted, "PROJ-042"));
        Assert.True(ProjectAccessAuthorization.AllowsTasks(Context(granted), ["PROJ-042", null]));
    }

    [Fact]
    public async Task Empty_membership_operator_is_denied_project_routes_that_the_all_projects_grant_allows()
    {
        var fixture = NewTaskFixture();
        var empty = await SignedIn(fixture.Store, StudioRoles.Operator, []);
        var granted = await SignedIn(fixture.Store, StudioRoles.Operator, [ProjectAccessAuthorization.AllProjectsGrant], "granted.op");

        foreach (var (method, path) in new[]
        {
            ("GET", $"/api/projects/{fixture.Mine.Id}/security"),
            ("GET", $"/api/runner/{fixture.Mine.Id}/orchestrator-log"),
            ("POST", $"/api/tasks/{MineTask}/move-to-top"),
            ("GET", "/api/orchestrator/context/global"),
        })
        {
            var denied = await fixture.Invoke(method, path, "", empty);
            Assert.Equal(StatusCodes.Status403Forbidden, denied.Context.Response.StatusCode);
            Assert.False(denied.Called.Value);
            var allowed = await fixture.Invoke(method, path, "", granted);
            Assert.True(allowed.Called.Value, $"{method} {path} should be allowed by the all-projects grant.");
        }
    }

    // --- 3. Hubs honour the forced password change and the project scope --

    [Fact]
    public async Task Hub_requests_are_refused_until_the_temporary_password_is_changed()
    {
        var store = NewStore(out var config);
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var created = store.CreateUser(new CreateUserRequest("temp.user", "Temp", StudioRoles.Operator, ["PROJ-001"], "temporary hub phrase!"));
        var login = store.Login(created.User.Username, created.TemporaryPassword, "temp|127.0.0.1");

        foreach (var path in new[] { "/hubs/jobs/negotiate", "/hubs/jobs" })
        {
            var refused = await InvokeRaw(config, store, "POST", path, sessionToken: login.SessionToken);
            Assert.Equal(StatusCodes.Status403Forbidden, refused.Context.Response.StatusCode);
            Assert.Equal("password-change-required", ErrorCode(refused.Context));
            Assert.False(refused.Called.Value);
        }

        store.ChangePassword(new HumanPrincipal(login.User, store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(created.TemporaryPassword, "new permanent hub phrase!"));
        var admitted = await InvokeRaw(config, store, "POST", "/hubs/jobs/negotiate", sessionToken: login.SessionToken);
        Assert.True(admitted.Called.Value);
    }

    [Fact]
    public async Task Hub_connection_with_a_forced_password_change_is_aborted()
    {
        var fixture = NewTaskFixture();
        fixture.Store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var created = fixture.Store.CreateUser(new CreateUserRequest("temp.hub", "Temp", StudioRoles.Operator, [fixture.Mine.Id], "temporary hub phrase!"));
        var login = fixture.Store.Login(created.User.Username, created.TemporaryPassword, "temp|127.0.0.1");
        var (hub, caller, groups) = fixture.Hub(login.SessionToken);

        var error = await Assert.ThrowsAsync<HubException>(hub.OnConnectedAsync);
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(caller.Aborted);
        Assert.Empty(groups.Joined);
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToConversation(MineTask));
    }

    [Fact]
    public async Task Hub_groups_follow_the_project_scope_and_empty_membership_joins_nothing()
    {
        var fixture = NewTaskFixture();
        var scoped = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id], "scoped.hub");
        var empty = await SignedIn(fixture.Store, StudioRoles.Viewer, [], "empty.hub");
        var granted = await SignedIn(fixture.Store, StudioRoles.Viewer, [ProjectAccessAuthorization.AllProjectsGrant], "granted.hub");

        var (scopedHub, _, scopedGroups) = fixture.Hub(scoped);
        await scopedHub.OnConnectedAsync();
        Assert.Equal([TaskHub.ProjectGroup(fixture.Mine.Id, fixture.Registry)], scopedGroups.Joined);

        var (emptyHub, _, emptyGroups) = fixture.Hub(empty);
        await emptyHub.OnConnectedAsync();
        Assert.Empty(emptyGroups.Joined);

        var (grantedHub, _, grantedGroups) = fixture.Hub(granted);
        await grantedHub.OnConnectedAsync();
        Assert.Contains(TaskHub.UnscopedSecurityGroup, grantedGroups.Joined);
        Assert.Contains(TaskHub.ProjectGroup(fixture.Foreign.Id, fixture.Registry), grantedGroups.Joined);
    }

    [Fact]
    public async Task Hub_conversation_subscription_is_limited_to_member_projects()
    {
        var fixture = NewTaskFixture();
        var scoped = await SignedIn(fixture.Store, StudioRoles.Operator, [fixture.Mine.Id], "scoped.sub");
        var empty = await SignedIn(fixture.Store, StudioRoles.Operator, [], "empty.sub");

        var (hub, _, groups) = fixture.Hub(scoped);
        await hub.SubscribeToConversation(MineTask);
        Assert.Contains(ConversationProjector.GroupName(MineTask), groups.Joined);
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToConversation(ForeignTask));

        var (emptyHub, _, _) = fixture.Hub(empty);
        await Assert.ThrowsAsync<HubException>(() => emptyHub.SubscribeToConversation(MineTask));
    }

    // --- Fixture -----------------------------------------------------------

    private const string MineTask = "AGT-mine-1";
    private const string ForeignTask = "AGT-foreign-1";

    private TaskFixture NewTaskFixture()
    {
        var minePath = Path.Combine(_root, "projects", "mine");
        var foreignPath = Path.Combine(_root, "projects", "foreign");
        Seed(minePath, MineTask);
        Seed(foreignPath, ForeignTask);
        var store = NewStore(out _, extra: new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Mine",
            ["WatchPaths:0:Path"] = minePath,
            ["WatchPaths:1:Name"] = "Foreign",
            ["WatchPaths:1:Path"] = foreignPath,
        }, configure: out var config);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mine = registry.EnsureProjectForStorage(minePath, "Mine", "workspace");
        var foreign = registry.EnsureProjectForStorage(foreignPath, "Foreign", "workspace");
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        Assert.Equal("Foreign", scanner.FindJob(ForeignTask)?.ProjectName);
        return new TaskFixture(config, store, scanner, registry, mine, foreign, minePath);
    }

    private static void Seed(string watchPath, string id)
    {
        var folder = Path.Combine(watchPath, TaskStates.Ready, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = id, title = "Scoped authorization fixture", state = TaskStates.Ready, order = 10,
            enteredLaneAt = DateTime.UtcNow, mode = "coding",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "fixture");
    }

    private sealed record SignedInUser(string SessionToken, string CsrfToken);

    private static int _userCounter;

    private static Task<SignedInUser> SignedIn(AccessSecurityStore store, string role, string[] projects, string? username = null)
    {
        if (store.ListUsers().Count == 0) store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        username ??= $"scoped.user{Interlocked.Increment(ref _userCounter)}";
        var created = store.CreateUser(new CreateUserRequest(username, username, role, projects, "temporary scoped phrase!"));
        var login = store.Login(created.User.Username, created.TemporaryPassword, $"{username}|127.0.0.1");
        store.ChangePassword(new HumanPrincipal(login.User, store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(created.TemporaryPassword, "new scoped password phrase!"));
        return Task.FromResult(new SignedInUser(login.SessionToken, login.CsrfToken));
    }

    private sealed record TaskFixture(
        IConfiguration Config,
        AccessSecurityStore Store,
        TaskScannerService Scanner,
        ProjectRegistry Registry,
        ProjectRecord Mine,
        ProjectRecord Foreign,
        string MinePath)
    {
        public Task<(DefaultHttpContext Context, BoolBox Called)> Invoke(string method, string path, string query, SignedInUser user)
            => InvokeRaw(Config, Store, method, path, query, user.SessionToken, user.CsrfToken, scanner: Scanner, registry: Registry);

        public (TaskHub Hub, FakeCallerContext Caller, RecordingGroups Groups) Hub(SignedInUser user) => Hub(user.SessionToken);

        public (TaskHub Hub, FakeCallerContext Caller, RecordingGroups Groups) Hub(string sessionToken)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={sessionToken}");
            var caller = new FakeCallerContext(http);
            var groups = new RecordingGroups();
            var hub = new TaskHub(Config, Store, Scanner, Registry, new PublicDemoViewerSessionStore())
            {
                Context = caller,
                Groups = groups,
            };
            return (hub, caller, groups);
        }
    }

    private AccessSecurityStore NewStore(out IConfiguration config, bool proxied = false)
        => NewStore(out _, extra: proxied ? new Dictionary<string, string?> { ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071" } : null, configure: out config);

    private AccessSecurityStore NewStore(out string repository, Dictionary<string, string?>? extra, out IConfiguration configure)
    {
        Directory.CreateDirectory(_root);
        repository = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = repository,
            ["Security:Profile"] = "networked",
        };
        if (extra is not null) foreach (var pair in extra) values[pair.Key] = pair.Value;
        configure = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AccessSecurityStore(configure, NullLogger<AccessSecurityStore>.Instance, TimeProvider.System);
    }

    private static async Task<(DefaultHttpContext Context, BoolBox Called)> InvokeRaw(
        IConfiguration config, AccessSecurityStore store, string method, string path, string query = "",
        string? sessionToken = null, string? csrf = null, string? authorization = null, Endpoint? endpoint = null,
        TaskScannerService? scanner = null, ProjectRegistry? registry = null)
    {
        var called = new BoolBox();
        var middleware = new AccessSecurityMiddleware(_ => { called.Value = true; return Task.CompletedTask; },
            config, store, scanner, projects: registry);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Scheme = "https";
        if (sessionToken is not null) context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={sessionToken}");
        if (csrf is not null) context.Request.Headers["X-CSRF-Token"] = new StringValues(csrf);
        if (authorization is not null) context.Request.Headers.Authorization = new StringValues(authorization);
        if (endpoint is not null) context.SetEndpoint(endpoint);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return (context, called);
    }

    private static string? ErrorCode(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.GetProperty("error").GetString();
    }

    private static StudioUser User(string role, List<string> projects) => new()
    {
        Id = "usr_" + role, Username = role + ".user", DisplayName = role, Role = role,
        PasswordHash = "unused", Projects = projects,
        CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow,
    };

    private static HttpContext Context(StudioUser user)
    {
        var context = new DefaultHttpContext();
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, new StudioSession
        {
            Id = "sess", UserId = user.Id, TokenHash = "h", CsrfHash = "h",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTime.UtcNow.AddHours(8),
        });
        return context;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class BoolBox { public bool Value; }

    private sealed class FakeCallerContext : HubCallerContext
    {
        private readonly FeatureCollection _features = new();

        public FakeCallerContext(HttpContext http)
            => _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });

        public bool Aborted { get; private set; }
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }

    private sealed class RecordingGroups : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Remove(groupName);
            return Task.CompletedTask;
        }
    }
}
