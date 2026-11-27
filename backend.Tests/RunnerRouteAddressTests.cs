using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RunnerRouteAddressTests : IDisposable
{
    private readonly RouteAdmissionFixture _fixture = new();

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("beta", false)]
    public async Task Chat_status_uses_projectName_query(string project, bool allowed)
    {
        var user = _fixture.Viewer("alpha");
        var result = await _fixture.Run("GET", "/api/runner/project-chat/status", "/api/runner/project-chat/status",
            user, query: "?projectName=" + project);
        Assert.Equal(allowed, result.Called);
    }

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("beta", false)]
    public async Task Lease_status_uses_project_path_even_with_a_different_query(string project, bool allowed)
    {
        var result = await _fixture.Run("GET", "/api/runner/integration-lease/{projectName}/{integrationBranch}",
            $"/api/runner/integration-lease/{project}/develop", _fixture.Viewer("alpha"), query: "?projectName=alpha");
        Assert.Equal(allowed, result.Called);
    }

    [Theory]
    [InlineData("/api/runner/{projectName}/status", "/api/runner/alpha/status", "/api/runner/beta/status")]
    [InlineData("/api/runner/project:{projectName}/orchestrator-chat", "/api/runner/project:alpha/orchestrator-chat", "/api/runner/project:beta/orchestrator-chat")]
    public async Task Project_parameter_routes_keep_their_real_project(string pattern, string own, string foreign)
    {
        var viewer = _fixture.Viewer("alpha");
        Assert.True((await _fixture.Run("GET", pattern, own, viewer)).Called);
        Assert.False((await _fixture.Run("GET", pattern, foreign, viewer)).Called);
    }

    [Theory]
    [InlineData("project-chat/usage")]
    [InlineData("queue-starvation")]
    [InlineData("auto-review-queue")]
    [InlineData("auto-review-parallelism-recommendation")]
    [InlineData("coding-yield-recommendation")]
    [InlineData("future-collection")]
    public async Task Fleet_literals_require_workspace_binding(string literal)
    {
        var path = "/api/runner/" + literal;
        var scoped = await _fixture.Run("GET", path, path, _fixture.Viewer(literal.Split('/')[0]), permission: IamPermissions.WorkspaceRead);
        Assert.False(scoped.Called);
        Assert.Equal("project-scope-denied", scoped.Error);
        Assert.True((await _fixture.Run("GET", path, path, _fixture.Viewer("*"), permission: IamPermissions.WorkspaceRead)).Called);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("orchestrator-feed")]
    [InlineData("token-summary-aggregate")]
    [InlineData("token-summary-aggregate/cached")]
    public async Task Existing_filtered_collections_keep_project_scoped_readers(string literal)
    {
        var path = "/api/runner/" + literal;
        Assert.True((await _fixture.Run("GET", path, path, _fixture.Viewer("alpha"))).Called);
    }

    [Fact]
    public async Task A_catch_all_does_not_declare_a_project_address()
    {
        var result = await _fixture.Run("GET", "/api/runner/{**path}", "/api/runner/alpha",
            _fixture.Viewer("alpha"), permission: IamPermissions.ProjectRead);
        Assert.Equal((false, "project-scope-denied"), (result.Called, result.Error));
    }

    [Theory]
    [InlineData("acquire")]
    [InlineData("heartbeat")]
    [InlineData("release")]
    public async Task Integration_writes_require_scope_project_and_enrolled_identity(string action)
    {
        var enrolled = _fixture.Store.EnrollRunner(_fixture.Store.CreateEnrollment(
            new RunnerEnrollmentRequest("lease-host", [RunnerScopes.Lease], null, null, ["alpha"])).Code);
        var principal = _fixture.Store.AuthenticateRunner(enrolled.Secret)!;
        var path = "/api/runner/integration-lease/" + action;
        string Body(string project, string runner) => JsonSerializer.Serialize(new { ProjectName = project, RunnerId = runner });
        Assert.True((await _fixture.Run("POST", path, path, enrolled.Secret, bearer: true,
            body: Body("alpha", principal.RunnerId))).Called);
        var foreign = await _fixture.Run("POST", path, path, enrolled.Secret, bearer: true, body: Body("beta", principal.RunnerId));
        Assert.Equal((false, "runner-project-denied"), (foreign.Called, foreign.Error));
        var identity = await _fixture.Run("POST", path, path, enrolled.Secret, bearer: true, body: Body("alpha", "other-host"));
        Assert.Equal((false, "runner-identity-denied"), (identity.Called, identity.Error));
        var limited = _fixture.Store.EnrollRunner(_fixture.Store.CreateEnrollment(
            new RunnerEnrollmentRequest("claim-host", [RunnerScopes.Claim], null, null, ["alpha"])).Code);
        Assert.Equal("runner-scope-denied", (await _fixture.Run("POST", path, path, limited.Secret,
            bearer: true, body: Body("alpha", principal.RunnerId))).Error);
    }

    [Fact]
    public async Task Runner_authorization_preserves_branch_and_fencing_checks()
    {
        var enrolled = _fixture.Store.EnrollRunner(_fixture.Store.CreateEnrollment(
            new RunnerEnrollmentRequest("fenced-host", [RunnerScopes.Lease], null, null, ["alpha"])).Code);
        var runnerId = _fixture.Store.AuthenticateRunner(enrolled.Secret)!.RunnerId;
        var leases = new IntegrationLeaseService(NullLogger<IntegrationLeaseService>.Instance);
        async Task<IntegrationLeaseResponse> Send(string action, object request)
        {
            IntegrationLeaseResponse? response = null;
            var path = "/api/runner/integration-lease/" + action;
            var result = await _fixture.Run("POST", path, path, enrolled.Secret, bearer: true,
                body: JsonSerializer.Serialize(request), handler: async context =>
                {
                    response = action switch
                    {
                        "acquire" => leases.TryAcquire((await JsonSerializer.DeserializeAsync<IntegrationLeaseAcquireRequest>(context.Request.Body))!),
                        "heartbeat" => leases.Renew((await JsonSerializer.DeserializeAsync<IntegrationLeaseHeartbeatRequest>(context.Request.Body))!),
                        _ => leases.Release((await JsonSerializer.DeserializeAsync<IntegrationLeaseReleaseRequest>(context.Request.Body))!),
                    };
                });
            Assert.True(result.Called, result.Error);
            return Assert.IsType<IntegrationLeaseResponse>(response);
        }
        var acquired = await Send("acquire", new IntegrationLeaseAcquireRequest("alpha", "develop", "AGT-1", runnerId, "host", 1, "backend"));
        Assert.True(acquired.Granted);
        var lease = Assert.IsType<IntegrationLeaseInfoDto>(acquired.Lease);
        var heartbeat = new IntegrationLeaseHeartbeatRequest("alpha", "develop", lease.LeaseId, lease.FencingToken, runnerId);
        Assert.Equal("StaleToken", (await Send("heartbeat", heartbeat with { FencingToken = lease.FencingToken + 1 })).Outcome);
        Assert.Equal("NotHeld", (await Send("heartbeat", heartbeat with { IntegrationBranch = "other" })).Outcome);
        Assert.True((await Send("heartbeat", heartbeat)).Granted);
        var release = new IntegrationLeaseReleaseRequest("alpha", "develop", lease.LeaseId, lease.FencingToken, runnerId);
        Assert.Equal("StaleToken", (await Send("release", release with { FencingToken = lease.FencingToken + 1 })).Outcome);
        Assert.Equal("Released", (await Send("release", release)).Outcome);
        Assert.Equal("NotHeld", (await Send("heartbeat", heartbeat)).Outcome);
    }

    [Fact]
    public async Task Duplicate_body_fields_cannot_authorize_a_different_project()
    {
        var enrolled = _fixture.Store.EnrollRunner(_fixture.Store.CreateEnrollment(
            new RunnerEnrollmentRequest("duplicate-host", [RunnerScopes.Lease], null, null, ["alpha"])).Code);
        var runnerId = _fixture.Store.AuthenticateRunner(enrolled.Secret)!.RunnerId;
        var body = "{\"ProjectName\":\"alpha\",\"projectName\":\"beta\",\"RunnerId\":" + JsonSerializer.Serialize(runnerId) + "}";
        var result = await _fixture.Run("POST", "/api/runner/integration-lease/acquire", "/api/runner/integration-lease/acquire",
            enrolled.Secret, bearer: true, body: body);
        Assert.Equal((false, "runner-project-denied"), (result.Called, result.Error));
    }

    public void Dispose() => _fixture.Dispose();
}

internal sealed class RouteAdmissionFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "route-admission-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly FakeTimeProvider _clock = new();
    public AccessSecurityStore Store { get; }
    public string OwnerId { get; }
    public string? Csrf { get; private set; }

    public RouteAdmissionFixture()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["Security:Profile"] = "networked",
        }).Build();
        Store = new AccessSecurityStore(_config, NullLogger<AccessSecurityStore>.Instance, _clock);
        OwnerId = Store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null)).User.Id;
    }

    public string Viewer(string project)
    {
        var invite = Store.InviteUser(new InviteUserRequest("viewer" + Guid.NewGuid().ToString("N"), "Viewer", "", null,
            [new IamGrantRequest(IamRoles.Viewer, project == "*" ? IamScope.Workspace : IamScope.ForProject(project))]));
        var accepted = Store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "accepted pass phrase!"));
        Csrf = accepted.CsrfToken;
        return accepted.SessionToken;
    }

    public async Task<(bool Called, string? Error)> Run(string method, string pattern, string path, string credential,
        bool bearer = false, string? query = null, string? body = null, bool csrf = true, string? permission = null,
        string origin = "https://studio.test", RequestDelegate? handler = null)
    {
        var called = false;
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.test");
        context.Request.Headers.Origin = origin;
        if (bearer) context.Request.Headers.Authorization = "Bearer " + credential;
        else
        {
            context.Request.Headers.Cookie = $"{AccessSecurityStore.SessionCookieName}={credential}";
            if (csrf) context.Request.Headers["X-CSRF-Token"] = Csrf;
        }
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentType = "application/json";
        }
        var metadata = new List<object> { new HttpMethodMetadata([method]) };
        if (permission is not null) metadata.Add(new RequiredPermissionMetadata(permission));
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0,
            new EndpointMetadataCollection(metadata), pattern));
        context.Response.Body = new MemoryStream();
        var middleware = new AccessSecurityMiddleware(async request =>
        {
            called = true;
            // Authorization must leave the body available to the endpoint binder.
            if (body is not null)
            {
                using var reader = new StreamReader(request.Request.Body, leaveOpen: true);
                Assert.Equal(body, await reader.ReadToEndAsync());
                request.Request.Body.Position = 0;
            }
            if (handler is not null) await handler(request);
        }, _config, Store, clock: _clock);
        await middleware.InvokeAsync(context);
        if (context.Response.Body.Length == 0) return (called, null);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (called, document.RootElement.GetProperty("error").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
