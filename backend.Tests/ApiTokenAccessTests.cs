using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ApiTokenAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-api-token-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Token_is_hashed_shown_once_expires_and_can_be_revoked()
    {
        var (store, _) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var made = store.CreateApiToken(new CreateApiTokenRequest("build bot", StudioRoles.Operator,
            ["PROJ-002"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);

        Assert.StartsWith("api.", made.Secret, StringComparison.Ordinal);
        // An API token is a service account with one token (AGT-3228).
        var principal = store.AuthenticateServiceAccount(made.Secret);
        Assert.Equal(("sa:sa_" + made.Token.Id, made.Token.Id), (principal?.ActorId, principal?.TokenId));
        Assert.Equal("PROJ-002", Assert.Single(store.ListApiTokens().Single(token => token.Id == made.Token.Id).Projects));
        Assert.Equal(IamRoles.Admin, Assert.Single(store.ReadIamSnapshot().Bindings,
            binding => binding.PrincipalId == principal!.AccountId).Role);
        Assert.DoesNotContain(made.Secret, File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json")), StringComparison.Ordinal);
        var listed = Assert.Single(store.ListApiTokens());
        Assert.DoesNotContain(made.Secret, JsonSerializer.Serialize(new ApiTokenResponse(listed.Id, listed.Name,
            listed.Role, listed.Projects, listed.CreatedBy, listed.CreatedAt, listed.ExpiresAt,
            listed.LastUsedAt, listed.RevokedAt)), StringComparison.Ordinal);

        store.RevokeApiToken(made.Token.Id);
        Assert.Null(store.AuthenticateServiceAccount(made.Secret));
        var second = store.CreateApiToken(new CreateApiTokenRequest("short lived", StudioRoles.Viewer,
            ["PROJ-002"], _clock.GetUtcNow().UtcDateTime.AddMinutes(1)), owner.Id);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.AuthenticateServiceAccount(second.Secret));
    }

    [Fact]
    public async Task Bearer_scope_role_and_audit_are_enforced_per_call()
    {
        var (store, configuration) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var operatorToken = store.CreateApiToken(new CreateApiTokenRequest("automation", StudioRoles.Operator,
            ["mine"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var viewerToken = store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer,
            ["mine"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var called = 0;
        var middleware = new AccessSecurityMiddleware(context => { called++; context.Response.StatusCode = 204; return Task.CompletedTask; },
            configuration, store);

        var allowed = await Invoke(middleware, "PUT", "/api/projects/mine/settings", operatorToken.Secret);
        Assert.Equal(204, allowed.Response.StatusCode);
        Assert.Equal("sa:sa_" + operatorToken.Token.Id, allowed.Items["ClientId"]);
        Assert.Equal(StudioRoles.Operator,
            (allowed.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Role);
        var foreign = await Invoke(middleware, "GET", "/api/projects/foreign", operatorToken.Secret);
        Assert.Equal(403, foreign.Response.StatusCode);
        var globalMutation = await Invoke(middleware, "POST", "/api/settings", operatorToken.Secret);
        Assert.Equal(403, globalMutation.Response.StatusCode);
        var viewerMutation = await Invoke(middleware, "PUT", "/api/projects/mine/settings", viewerToken.Secret);
        Assert.Equal(403, viewerMutation.Response.StatusCode);
        var admin = await Invoke(middleware, "GET", "/api/auth/users", operatorToken.Secret);
        Assert.Equal(403, admin.Response.StatusCode);
        var runner = await Invoke(middleware, "POST", "/api/runner/claim", operatorToken.Secret);
        Assert.Equal(401, runner.Response.StatusCode);
        var versioned = await Invoke(middleware, "GET", "/api/v1/projects/foreign/tasks", operatorToken.Secret);
        Assert.Equal(403, versioned.Response.StatusCode);
        Assert.Equal(1, called);

        var audit = File.ReadAllLines(Path.Combine(store.SecurityFolder, "api-token-audit.jsonl"));
        Assert.Equal(7, audit.Length);
        Assert.All(audit, row => Assert.DoesNotContain(operatorToken.Secret, row, StringComparison.Ordinal));
        Assert.All(audit, row => Assert.DoesNotContain(viewerToken.Secret, row, StringComparison.Ordinal));
        Assert.Contains(audit, row => row.Contains($"\"tokenId\":\"{operatorToken.Token.Id}\"", StringComparison.Ordinal)
            && row.Contains($"\"actor\":\"sa:sa_{operatorToken.Token.Id}\"", StringComparison.Ordinal));
        Assert.Contains(audit, row => row.Contains("\"status\":204", StringComparison.Ordinal));
        Assert.Contains(audit, row => row.Contains("\"status\":403", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_projects_token_cannot_mutate_installation_settings()
    {
        var (store, configuration) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var token = store.CreateApiToken(new CreateApiTokenRequest("all projects", StudioRoles.Operator,
            ["*"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var called = 0;
        var middleware = new AccessSecurityMiddleware(_ => { called++; return Task.CompletedTask; }, configuration, store);

        var projectWrite = await Invoke(middleware, "PUT", "/api/projects/mine/settings", token.Secret);
        Assert.Equal(200, projectWrite.Response.StatusCode);
        var installationWrite = await Invoke(middleware, "PUT", "/api/settings/cli", token.Secret);
        Assert.Equal(403, installationWrite.Response.StatusCode);
        // Even a routed installation-wide write the workspace binding grants
        // stays closed to an API token.
        var workspaceWrite = await Invoke(middleware, "PUT", "/api/workspaces/main/autonomy", token.Secret);
        Assert.Equal(403, workspaceWrite.Response.StatusCode);
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task Project_scoped_token_cannot_read_any_workspace_route()
    {
        var (store, configuration) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var scoped = store.CreateApiToken(new CreateApiTokenRequest("one project", StudioRoles.Viewer,
            ["mine"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var allProjects = store.CreateApiToken(new CreateApiTokenRequest("all projects", StudioRoles.Viewer,
            ["*"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var called = 0;
        var middleware = new AccessSecurityMiddleware(_ => { called++; return Task.CompletedTask; }, configuration, store);

        foreach (var path in new[]
        {
            "/api/workspace/tokens/timeline",
            "/api/workspace/tokens/timeline/cached",
            "/api/workspace/tokens/expensive-jobs",
            "/api/workspace/tokens/expensive-jobs/cached",
            "/api/workspace/screenshots",
            "/api/workspace/future-summary"
        })
        {
            var denied = await Invoke(middleware, "GET", path, scoped.Secret);
            Assert.Equal(403, denied.Response.StatusCode);
            var deniedWithProject = await Invoke(middleware, "GET", path, scoped.Secret, "?project=mine");
            Assert.Equal(403, deniedWithProject.Response.StatusCode);
            var allowed = await Invoke(middleware, "GET", path, allProjects.Secret);
            Assert.Equal(200, allowed.Response.StatusCode);
        }
        Assert.Equal(6, called);
    }

    [Fact]
    public async Task Project_path_without_endpoint_scope_metadata_is_denied()
    {
        var (store, configuration) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var scoped = store.CreateApiToken(new CreateApiTokenRequest("one project", StudioRoles.Viewer,
            ["mine"], _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner.Id);
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; }, configuration, store);

        var result = await Invoke(middleware, "GET", "/api/projects/mine/future-feature", scoped.Secret,
            "?project=mine", declareProjectScope: false);

        Assert.Equal(403, result.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public void Invalid_role_scope_and_expiry_are_rejected()
    {
        var (store, _) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;
        var now = _clock.GetUtcNow().UtcDateTime;
        Assert.Throws<SecurityOperationException>(() => store.CreateApiToken(
            new CreateApiTokenRequest("owner token", StudioRoles.Owner, ["*"], now.AddDays(1)), owner.Id));
        Assert.Throws<SecurityOperationException>(() => store.CreateApiToken(
            new CreateApiTokenRequest("unscoped", StudioRoles.Operator, [], now.AddDays(1)), owner.Id));
        Assert.Throws<SecurityOperationException>(() => store.CreateApiToken(
            new CreateApiTokenRequest("expired", StudioRoles.Operator, ["mine"], now), owner.Id));
    }

    private (AccessSecurityStore Store, IConfiguration Configuration) NewStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["Security:Profile"] = "networked"
        }).Build();
        return (new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock), configuration);
    }

    /// <summary>
    /// Routed requests carry a real route pattern so the declared permission
    /// resolves; <c>/api/projects/mine/*</c> is the project settings route and
    /// <c>/api/workspace*</c> routes are mapped as literals. Anything else has
    /// no endpoint, which a bearer principal may never pass.
    /// </summary>
    private static async Task<DefaultHttpContext> Invoke(AccessSecurityMiddleware middleware, string method, string path,
        string token, string query = "", bool declareProjectScope = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers.Authorization = "Bearer " + token;
        var pattern = path switch
        {
            "/api/projects/mine/settings" => "/api/projects/{projectName}/settings",
            "/api/workspaces/main/autonomy" => "/api/workspaces/{id}/autonomy",
            _ when path.StartsWith("/api/workspace/", StringComparison.Ordinal) => path,
            _ => null,
        };
        if (declareProjectScope && pattern is not null)
            context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0,
                new EndpointMetadataCollection(new HttpMethodMetadata([method])), pattern));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
