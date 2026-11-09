using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3575: GET /api/security/posture declares security.posture.read, which
/// only the Owner and the operator-agent role hold, and the answer collected
/// from a networked host carries metadata only.
/// </summary>
public sealed class SecurityPostureEndpointTests : IClassFixture<SecurityPostureEndpointTests.NetworkedHost>, IDisposable
{
    private const string Pattern = "/api/security/posture";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-posture-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly NetworkedHost _host;

    public SecurityPostureEndpointTests(NetworkedHost host) => _host = host;

    [Fact]
    public void The_route_declares_security_posture_read()
    {
        var endpoint = _host.Find(Pattern);

        Assert.Equal(IamPermissions.SecurityPostureRead, endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission);
        Assert.Equal(IamPermissions.SecurityPostureRead, RoutePermissionCatalog.Resolve(endpoint, "GET")?.Permission);
        Assert.Equal(PermissionLevel.Workspace, IamPermissions.Level(IamPermissions.SecurityPostureRead));
        Assert.True(IamPermissions.IsKnown(IamPermissions.SecurityPostureRead));
    }

    [Fact]
    public void Only_the_owner_and_the_operator_agent_role_hold_the_permission()
        => Assert.Equal([IamRoles.OperatorAgent, IamRoles.Owner],
            IamRoles.All.Where(role => role.Permissions.Contains(IamPermissions.SecurityPostureRead)).Select(role => role.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void The_owner_is_admitted_and_an_admin_or_viewer_user_is_not()
    {
        var (store, _) = NewStore();
        var owner = Owner(store);
        var admin = store.CreateUser(new CreateUserRequest("admin", "Admin", StudioRoles.Operator, ["*"], null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), owner.Id).User;
        var viewer = store.CreateUser(new CreateUserRequest("viewer", "Viewer", StudioRoles.Viewer, ["*"], null), owner.Id).User;

        Assert.True(store.AuthorizeUser(owner.Id, IamPermissions.SecurityPostureRead, PermissionTarget.WorkspaceWide).Allowed);
        Assert.False(store.AuthorizeUser(admin.Id, IamPermissions.SecurityPostureRead, PermissionTarget.WorkspaceWide).Allowed);
        Assert.False(store.AuthorizeUser(viewer.Id, IamPermissions.SecurityPostureRead, PermissionTarget.WorkspaceWide).Allowed);
    }

    [Fact]
    public async Task Operator_agent_sessions_and_operator_tokens_read_the_view()
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("laptop agent", null,
            [new IamGrantRequest(IamRoles.OperatorAgent, IamScope.Workspace)]), owner.Id);
        var session = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest("console", null), owner.Id).Secret;
        var token = store.CreateApiToken(new CreateApiTokenRequest("operator", StudioRoles.Operator, ["*"],
            _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id).Secret;

        foreach (var bearer in new[] { session, token })
        {
            var result = await Invoke(store, configuration, bearer);
            Assert.True(result.Response.StatusCode == 200, $"{result.Response.StatusCode} {Body(result)}");
        }
    }

    [Fact]
    public async Task Viewer_tokens_and_admin_service_accounts_are_refused()
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var viewer = store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer, ["*"],
            _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id).Secret;
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("deploy bot", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), owner.Id);
        var admin = store.CreateServiceAccountToken(account.Id,
            new CreateServiceAccountTokenRequest("ops", _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id).Secret;

        foreach (var bearer in new[] { viewer, admin })
        {
            var result = await Invoke(store, configuration, bearer);
            Assert.True(result.Response.StatusCode == 403, $"{result.Response.StatusCode} {Body(result)}");
        }
    }

    [Fact]
    public async Task The_collected_view_lists_credentials_and_users_without_secret_material()
    {
        var store = _host.Services.GetRequiredService<AccessSecurityStore>();
        var now = _host.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var owner = store.ListUsers().FirstOrDefault(user => user.Role == StudioRoles.Owner)
                    ?? store.Bootstrap(new BootstrapRequest("posture.owner", "correct horse battery staple!", "Owner")).User;
        var token = store.CreateApiToken(new CreateApiTokenRequest("posture probe", StudioRoles.Operator, ["*"],
            now.AddDays(5)), owner.Id);
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("posture bot", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("mine"))]), owner.Id);
        var issued = store.CreateServiceAccountToken(account.Id,
            new CreateServiceAccountTokenRequest("ci", now.AddDays(60)), owner.Id);

        var view = await _host.Services.GetRequiredService<SecurityPostureService>().ReadAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(SecurityProfiles.Networked, view.Exposure.Profile);
        Assert.Contains("POST /api/auth/login", view.Exposure.AnonymousRoutes);
        Assert.DoesNotContain(view.Exposure.AnonymousRoutes, route => route.Contains("/api/security/posture", StringComparison.Ordinal));
        var probe = Assert.Single(view.Credentials.Tokens, row => row.Id == token.Token.Id);
        Assert.Equal(("api-token", "expiring"), (probe.Kind, probe.State));
        Assert.True(Assert.Single(view.Findings, finding => finding.Id == $"credentials:token:{token.Token.Id}:expiring").ActionSoon);
        var bot = Assert.Single(view.Credentials.Tokens, row => row.Holder == "posture bot");
        Assert.Equal("Developer", bot.Role);
        Assert.Equal(["mine"], bot.Projects);
        Assert.Contains(view.SecondFactor.Users, user => user.Id == owner.Id && user.State == "missing");
        Assert.Equal(SecurityPostureSources.Local, view.Versions.HostSource);
        foreach (var secret in new[] { token.Secret, issued.Secret, token.Token.SecretHash, owner.PasswordHash })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        foreach (var field in new[] { "secretHash", "passwordHash", "totpSecret", "recoveryCodeHashes" })
            Assert.DoesNotContain($"\"{field}\"", json, StringComparison.Ordinal);
    }

    private StudioUser Owner(AccessSecurityStore store)
        => store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;

    private (AccessSecurityStore Store, IConfiguration Configuration) NewStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["Security:Profile"] = "networked",
        }).Build();
        return (new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock), configuration);
    }

    /// <summary>Runs the access middleware against the mapped posture route; the next delegate answers 200.</summary>
    private async Task<DefaultHttpContext> Invoke(AccessSecurityStore store, IConfiguration configuration, string bearer)
    {
        var middleware = new AccessSecurityMiddleware(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, configuration, store);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "GET";
        context.Request.Path = Pattern;
        context.Request.Headers.Authorization = "Bearer " + bearer;
        context.SetEndpoint(_host.Find(Pattern));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A networked Studio host, built once for the class.</summary>
    public sealed class NetworkedHost : IDisposable
    {
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "posture-routes-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        private readonly IReadOnlyList<RouteEndpoint> _endpoints;

        public NetworkedHost()
        {
            _factory = new TestHostFactory().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = SecurityProfiles.Networked,
                    ["AllowedHosts"] = "*",
                }));
            });
            _ = _factory.Server;
            _endpoints = _factory.Services.GetServices<EndpointDataSource>()
                .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Distinct().ToList();
        }

        public IServiceProvider Services => _factory.Services;

        public RouteEndpoint Find(string pattern)
            => _endpoints.FirstOrDefault(endpoint
                   => string.Equals(RoutePermissionCatalog.Normalize(endpoint.RoutePattern), pattern, StringComparison.OrdinalIgnoreCase)
                      && RoutePermissionCatalog.Methods(endpoint).Contains("GET", StringComparer.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No mapped route GET {pattern}.");

        public void Dispose()
        {
            _factory.Dispose();
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        }
    }
}
