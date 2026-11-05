using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// OP-1 (Dossier operator-agent-level, OA-D4 B): an API token with the role
/// <c>operator</c> binds the narrow <c>operator-agent</c> role instead of
/// Admin. It reaches every operator surface route that exists today and is
/// refused secret writes, secret value reads, production approval and
/// deploys, access administration, and installation-wide settings. Existing
/// operator tokens move to the role when the store loads, one IAM audit entry
/// per token.
/// </summary>
public sealed class OperatorAgentRoleTests : IClassFixture<OperatorAgentRoleTests.NetworkedRoutes>, IDisposable
{
    private const string OperatorAgentRole = "operator-agent";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-operator-role-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly NetworkedRoutes _routes;

    public OperatorAgentRoleTests(NetworkedRoutes routes) => _routes = routes;

    /// <summary>The operator surface table of the Dossier, restricted to the routes that exist today.</summary>
    public static TheoryData<string, string, string> OperatorSurface => new()
    {
        // Wake when a decision is needed.
        { "GET", "/api/tasks/grouped", "/api/tasks/grouped" },
        { "GET", "/api/runner/{}/pending-decisions", "/api/runner/mine/pending-decisions" },
        { "GET", "/api/projects/{}/review-decisions-pending", "/api/projects/mine/review-decisions-pending" },
        { "GET", "/api/watcher/status", "/api/watcher/status" },
        { "GET", "/api/watcher/cases", "/api/watcher/cases" },
        { "GET", "/api/watcher/stalls", "/api/watcher/stalls" },
        { "GET", "/api/watcher/interventions", "/api/watcher/interventions" },
        { "GET", "/api/runner/orchestrator-feed", "/api/runner/orchestrator-feed" },
        // Board and card reads.
        { "GET", "/api/tasks/{}", "/api/tasks/DEMO-1" },
        // Batch gate state, pending pushes, eject, close-now.
        { "GET", "/api/projects/{}/batch-gate/state", "/api/projects/mine/batch-gate/state" },
        { "GET", "/api/projects/{}/batch-gate/report", "/api/projects/mine/batch-gate/report" },
        { "GET", "/api/projects/{}/batch-gate/queue-stalls", "/api/projects/mine/batch-gate/queue-stalls" },
        { "POST", "/api/projects/{}/batch-gate/members/{}/eject", "/api/projects/mine/batch-gate/members/DEMO-1/eject" },
        { "POST", "/api/projects/{}/batch-gate/close-now", "/api/projects/mine/batch-gate/close-now" },
        // Continue and steer rounds (steer is a continue with mode "steer").
        { "POST", "/api/tasks/{}/continue", "/api/tasks/DEMO-1/continue" },
        // Lane moves and acceptance.
        { "POST", "/api/tasks/{}/move", "/api/tasks/DEMO-1/move" },
        { "POST", "/api/tasks/batch-move", "/api/tasks/batch-move" },
        // Create cards in Ready, repair titles.
        { "POST", "/api/tasks", "/api/tasks" },
        { "PUT", "/api/tasks/{}/title", "/api/tasks/DEMO-1/title" },
        // Review step models.
        { "PUT", "/api/projects/{}/pipeline-step", "/api/projects/mine/pipeline-step" },
        // Quota and routing reads.
        { "GET", "/api/cli/quota", "/api/cli/quota" },
        { "GET", "/api/cli/quota/model-routes", "/api/cli/quota/model-routes" },
        // Deploy reads and stage deploys.
        { "GET", "/api/deploy/matrix", "/api/deploy/matrix" },
        { "GET", "/api/projects/{}/deploy/status", "/api/projects/mine/deploy/status" },
        { "GET", "/api/projects/{}/deploy/releases", "/api/projects/mine/deploy/releases" },
        { "POST", "/api/projects/{}/environments/releases", "/api/projects/mine/environments/releases" },
    };

    /// <summary>What stays with the Owner: secrets, production, access, installation-wide settings.</summary>
    public static TheoryData<string, string, string> OwnerReserved => new()
    {
        // Secret writes.
        { "POST", "/api/projects/{}/secrets", "/api/projects/mine/secrets" },
        { "POST", "/api/projects/{}/secrets/{}/rotate", "/api/projects/mine/secrets/DEPLOY_KEY/rotate" },
        { "POST", "/api/secrets/workspace", "/api/secrets/workspace" },
        // Secret value reads.
        { "POST", "/api/runner/secrets/resolve", "/api/runner/secrets/resolve" },
        // Production approval and production deploys.
        { "POST", "/api/projects/{}/environments/promotions/approve", "/api/projects/mine/environments/promotions/approve" },
        { "PUT", "/api/tasks/{}/release", "/api/tasks/DEMO-1/release" },
        { "POST", "/api/projects/{}/deploy/deployments/{}/approve", "/api/projects/mine/deploy/deployments/dep_1/approve" },
        { "POST", "/api/projects/{}/deploy/environments/{}/rollback", "/api/projects/mine/deploy/environments/prod/rollback" },
        // Access administration.
        { "GET", "/api/auth/iam", "/api/auth/iam" },
        { "POST", "/api/auth/api-tokens", "/api/auth/api-tokens" },
        // Installation-wide settings.
        { "PUT", "/api/cli/quota/model-routes", "/api/cli/quota/model-routes" },
        { "POST", "/api/projects", "/api/projects" },
        { "PUT", "/api/secrets/backends", "/api/secrets/backends" },
    };

    [Fact]
    public void Operator_token_binds_operator_agent_and_defaults_to_thirty_days()
    {
        var (store, _) = NewStore();
        var owner = Owner(store);
        // A request without an expiry, as the API reference documents it.
        var request = JsonSerializer.Deserialize<CreateApiTokenRequest>(
            """{"name":"operator laptop","role":"operator","projects":["*"]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var made = store.CreateApiToken(request, owner.Id);

        var binding = Assert.Single(store.ReadIamSnapshot().Bindings,
            candidate => candidate.PrincipalId == "sa_" + made.Token.Id);
        Assert.Equal((OperatorAgentRole, IamScopeKind.Workspace), (binding.Role, binding.Scope.Kind));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(30), made.Token.ExpiresAt);
        // The token still reports its legacy role.
        Assert.Equal(StudioRoles.Operator, made.Token.Role);
        // The maximum stays 365 days.
        Assert.Throws<SecurityOperationException>(() => store.CreateApiToken(new CreateApiTokenRequest(
            "too long", StudioRoles.Operator, ["*"], _clock.GetUtcNow().UtcDateTime.AddDays(366)), owner.Id));
        var longest = store.CreateApiToken(new CreateApiTokenRequest(
            "longest", StudioRoles.Operator, ["mine"], _clock.GetUtcNow().UtcDateTime.AddDays(365)), owner.Id);
        Assert.Equal(OperatorAgentRole, Assert.Single(store.ReadIamSnapshot().Bindings,
            candidate => candidate.PrincipalId == "sa_" + longest.Token.Id).Role);
    }

    [Fact]
    public void Operator_agent_role_holds_no_secret_production_access_or_installation_permission()
    {
        var role = IamRoles.Find(OperatorAgentRole);
        Assert.NotNull(role);
        Assert.True(role!.ServiceAccountBindable);
        foreach (var permission in new[]
        {
            IamPermissions.SecretsRead, IamPermissions.SecretsWrite, IamPermissions.SecretsResolve,
            IamPermissions.EnvironmentProductionDeploy, IamPermissions.EnvironmentProductionDeployApprove,
            IamPermissions.AccessRead, IamPermissions.AccessManage,
            IamPermissions.WorkspaceSettingsWrite, IamPermissions.WorkspaceAdminister, IamPermissions.ProjectSettingsWrite,
        })
            Assert.DoesNotContain(permission, role.Permissions);
        // The role is for non-human principals only.
        Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(OperatorAgentRole, IamScope.Workspace)));
        Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.Group,
            new IamGrantRequest(OperatorAgentRole, IamScope.Workspace)));
    }

    [Theory]
    [MemberData(nameof(OperatorSurface))]
    public async Task Operator_token_reaches_the_operator_surface(string method, string pattern, string path)
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var all = OperatorToken(store, owner, ProjectAccessAuthorization.AllProjectsGrant);

        var result = await Invoke(store, configuration, method, pattern, path, all);

        Assert.True(result.Response.StatusCode == 200, $"{method} {path}: {result.Response.StatusCode} {Body(result)}");
    }

    [Theory]
    [MemberData(nameof(OwnerReserved))]
    public async Task Operator_token_is_refused_what_stays_with_the_owner(string method, string pattern, string path)
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var all = OperatorToken(store, owner, ProjectAccessAuthorization.AllProjectsGrant);
        // The same role bound to an Access-area service account: the role
        // itself, not only the API-token write rule, keeps these closed.
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("operator account", null,
            [new IamGrantRequest(OperatorAgentRole, IamScope.Workspace)]), owner.Id);
        var issued = store.CreateServiceAccountToken(account.Id,
            new CreateServiceAccountTokenRequest("ops", _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id);

        foreach (var bearer in new[] { all, issued.Secret })
        {
            var result = await Invoke(store, configuration, method, pattern, path, bearer);
            Assert.True(result.Response.StatusCode == 403, $"{method} {path}: {result.Response.StatusCode} {Body(result)}");
        }
    }

    [Fact]
    public async Task Operator_agent_service_account_cannot_make_installation_wide_writes()
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("operator account", null,
            [new IamGrantRequest(OperatorAgentRole, IamScope.Workspace)]), owner.Id);
        var issued = store.CreateServiceAccountToken(account.Id,
            new CreateServiceAccountTokenRequest("ops", _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id);

        // Installation operation reads stay open (the watcher), writes do not.
        var read = await Invoke(store, configuration, "GET", "/api/watcher/cases", "/api/watcher/cases", issued.Secret);
        Assert.Equal(200, read.Response.StatusCode);
        var write = await Invoke(store, configuration, "POST", "/api/cause-breakers/{}/probe", "/api/cause-breakers/fp_1/probe", issued.Secret);
        Assert.Equal(403, write.Response.StatusCode);
    }

    [Fact]
    public async Task Project_scoped_operator_token_stays_in_its_project()
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var scoped = OperatorToken(store, owner, "mine");

        var own = await Invoke(store, configuration, "POST", "/api/projects/{}/batch-gate/close-now",
            "/api/projects/mine/batch-gate/close-now", scoped);
        var foreign = await Invoke(store, configuration, "POST", "/api/projects/{}/batch-gate/close-now",
            "/api/projects/other/batch-gate/close-now", scoped);

        Assert.Equal(200, own.Response.StatusCode);
        Assert.Equal(403, foreign.Response.StatusCode);
    }

    [Fact]
    public void Existing_operator_tokens_migrate_to_operator_agent_on_load_with_one_audit_entry_each()
    {
        var (store, configuration) = NewStore();
        var owner = Owner(store);
        var now = _clock.GetUtcNow().UtcDateTime;
        var all = store.CreateApiToken(new CreateApiTokenRequest("laptop", StudioRoles.Operator, ["*"], now.AddDays(90)), owner.Id);
        var scoped = store.CreateApiToken(new CreateApiTokenRequest("ci", StudioRoles.Operator, ["mine", "other"], now.AddDays(90)), owner.Id);
        var viewer = store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer, ["*"], now.AddDays(90)), owner.Id);
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("deploy bot", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("mine"))]), owner.Id);
        RewriteAsAdminBoundTokens(store);
        var auditPath = Path.Combine(store.SecurityFolder, AccessSecurityStore.IamAuditFileName);
        var auditBefore = File.ReadAllLines(auditPath).Length;

        var restarted = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
        var bindings = restarted.ReadIamSnapshot().Bindings;

        string[] Roles(string principal) => bindings.Where(binding => binding.PrincipalId == principal)
            .Select(binding => binding.Role).ToArray();
        Assert.Equal([OperatorAgentRole], Roles("sa_" + all.Token.Id));
        Assert.Equal([OperatorAgentRole, OperatorAgentRole], Roles("sa_" + scoped.Token.Id));
        Assert.Equal([IamRoles.Viewer], Roles("sa_" + viewer.Token.Id));
        // An Access-area service account keeps the binding the Owner chose.
        Assert.Equal([IamRoles.Admin], Roles(account.Id));
        Assert.Equal(StudioRoles.Operator, restarted.ListApiTokens().Single(token => token.Id == all.Token.Id).Role);
        Assert.NotNull(restarted.AuthenticateServiceAccount(all.Secret));

        var entries = File.ReadAllLines(auditPath).Skip(auditBefore)
            .Select(line => JsonNode.Parse(line)!).Where(entry => (string?)entry["action"] == "api-token.role-migration").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "sa_" + all.Token.Id, "sa_" + scoped.Token.Id }.Order(),
            entries.Select(entry => (string)entry["targetId"]!).Order());
        Assert.All(entries, entry => Assert.Equal(IamRoles.Admin, (string?)entry["details"]!["fromRole"]));
        Assert.All(entries, entry => Assert.Equal(OperatorAgentRole, (string?)entry["details"]!["toRole"]));
        Assert.All(File.ReadAllLines(auditPath), line => Assert.DoesNotContain(all.Secret, line, StringComparison.Ordinal));

        // A second start finds nothing left to migrate.
        var again = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
        _ = again.ReadIamSnapshot();
        Assert.Equal(2, File.ReadAllLines(auditPath).Count(line => line.Contains("api-token.role-migration", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Puts the state file back to how an installation before OP-1 stored it:
    /// IAM version 2 with operator tokens bound to Admin.
    /// </summary>
    private static void RewriteAsAdminBoundTokens(AccessSecurityStore store)
    {
        var path = Path.Combine(store.SecurityFolder, "access-state.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        state["IamVersion"] = 2;
        foreach (var binding in state["RoleBindings"]!.AsArray())
            if ((string?)binding!["Role"] == OperatorAgentRole) binding["Role"] = IamRoles.Admin;
        File.WriteAllText(path, state.ToJsonString());
    }

    private StudioUser Owner(AccessSecurityStore store)
        => store.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", "Owner")).User;

    private string OperatorToken(AccessSecurityStore store, StudioUser owner, string project)
        => store.CreateApiToken(new CreateApiTokenRequest("operator " + project, StudioRoles.Operator, [project],
            _clock.GetUtcNow().UtcDateTime.AddDays(30)), owner.Id).Secret;

    private (AccessSecurityStore Store, IConfiguration Configuration) NewStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["Security:Profile"] = "networked"
        }).Build();
        return (new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock), configuration);
    }

    /// <summary>
    /// Runs the access middleware against the real endpoint the networked host
    /// maps for <paramref name="pattern"/>, so the route's declared permission
    /// and metadata decide. The next delegate answers 200.
    /// </summary>
    private async Task<DefaultHttpContext> Invoke(AccessSecurityStore store, IConfiguration configuration,
        string method, string pattern, string path, string bearer)
    {
        var middleware = new AccessSecurityMiddleware(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, configuration, store);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer " + bearer;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.SetEndpoint(_routes.Find(method, pattern));
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

    /// <summary>The routes a networked Studio maps today, built once for the class.</summary>
    public sealed class NetworkedRoutes : IDisposable
    {
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-role-routes-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        private readonly IReadOnlyList<RouteEndpoint> _endpoints;

        public NetworkedRoutes()
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

        /// <summary>
        /// The mapped endpoint for a normalized pattern such as
        /// <c>/api/tasks/{}/move</c>.
        /// </summary>
        public RouteEndpoint Find(string method, string pattern)
            => _endpoints.FirstOrDefault(endpoint
                    => string.Equals(RoutePermissionCatalog.Normalize(endpoint.RoutePattern), pattern, StringComparison.OrdinalIgnoreCase)
                       && RoutePermissionCatalog.Methods(endpoint).Contains(method, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No mapped route {method} {pattern}; the operator surface table names a route that does not exist.");

        public void Dispose()
        {
            _factory.Dispose();
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        }
    }
}
