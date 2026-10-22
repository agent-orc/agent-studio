using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Route inventory for the rights model (IP-8): every mapped route must
/// declare a named permission, either as <see cref="RequiredPermissionMetadata"/>
/// or through one rule of <see cref="RoutePermissionCatalog"/>. A new route
/// without a declaration fails here, in every host profile that maps it.
/// </summary>
public sealed class RoutePermissionInventoryTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "route-permissions-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string?> Profiles => new()
    {
        { SecurityProfiles.Local, null },
        { SecurityProfiles.Networked, null },
        { SecurityProfiles.Networked, "http://127.0.0.1:5071" },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Every_route_declares_a_known_permission(string profile, string? taskServer)
    {
        using var factory = Factory(profile, taskServer);
        var undeclared = new List<string>();
        var unknown = new List<string>();
        foreach (var endpoint in Routes(factory.Services))
        foreach (var method in RoutePermissionCatalog.Methods(endpoint))
        {
            var required = RoutePermissionCatalog.Resolve(endpoint, method);
            var route = $"{method} {endpoint.RoutePattern.RawText}";
            if (required is null) undeclared.Add(route);
            else if (!IamPermissions.IsKnown(required.Permission)) unknown.Add($"{route} -> {required.Permission}");
        }

        Assert.True(undeclared.Count == 0,
            "Routes without a declared permission (add RequirePermission or a RoutePermissionCatalog rule):\n  "
            + string.Join("\n  ", undeclared.Distinct().Order(StringComparer.Ordinal)));
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("GET", "/api/tasks/{jobId}", IamPermissions.ProjectRead)]
    [InlineData("PUT", "/api/tasks/{jobId}/title", IamPermissions.ProjectCardsWrite)]
    [InlineData("POST", "/api/tasks/{jobId}/move", IamPermissions.ProjectCardsWrite)]
    [InlineData("POST", "/api/tasks/{jobId}/preview-comments", IamPermissions.ProjectCardsWrite)]
    [InlineData("POST", "/api/tasks/{jobId}/start", IamPermissions.ProjectRunsOperate)]
    [InlineData("POST", "/api/tasks/reference-status", IamPermissions.ProjectRead)]
    [InlineData("PUT", "/api/tasks/{jobId}/release", IamPermissions.EnvironmentProductionDeployApprove)]
    [InlineData("POST", "/api/projects/{projectName}/environments/promotions/approve", IamPermissions.EnvironmentProductionDeployApprove)]
    [InlineData("POST", "/api/projects/{projectName}/environments/releases", IamPermissions.EnvironmentDeploy)]
    [InlineData("POST", "/api/projects/{projectName}/environments/{environmentId}/rollback", IamPermissions.EnvironmentProductionDeploy)]
    [InlineData("GET", "/api/deploy/matrix", IamPermissions.WorkspaceRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/environments", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/status", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/releases", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/releases/page", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/releases/{releaseId}", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/deployments", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/deployments/page", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/deployments/{deploymentId}", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/deployments/{deploymentId}/log", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/deploy/audit", IamPermissions.ProjectRead)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/releases/{releaseId}/deployments", IamPermissions.EnvironmentDeploy)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/deployments/{deploymentId}/approve", IamPermissions.EnvironmentProductionDeployApprove)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/deployments/{deploymentId}/reject", IamPermissions.EnvironmentProductionDeployApprove)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/deployments/{deploymentId}/cancel", IamPermissions.EnvironmentProductionDeploy)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/environments/{environmentId}/redeploy", IamPermissions.EnvironmentProductionDeploy)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/environments/{environmentId}/rollback", IamPermissions.EnvironmentProductionDeploy)]
    [InlineData("PUT", "/api/projects/{projectName}/auto-commit", IamPermissions.ProjectSettingsWrite)]
    [InlineData("PUT", "/api/projects/{projectName}/wiki/files/{**relPath}", IamPermissions.ProjectDocsWrite)]
    [InlineData("POST", "/api/runner/{projectName}/orchestrator-chat", IamPermissions.ProjectChatWrite)]
    [InlineData("POST", "/api/runner/claim", IamPermissions.RunnerService)]
    [InlineData("POST", "/api/projects/{project}/operator-candidate-verifications", IamPermissions.WorkspaceOperate)]
    [InlineData("POST", "/api/projects/{projectName}/batch-gate/queue-stalls/release", IamPermissions.ProjectRunsOperate)]
    [InlineData("POST", "/api/projects/{projectName}/deploy/deployments/{deploymentId}/intervention", IamPermissions.EnvironmentProductionDeploy)]
    [InlineData("POST", "/api/tasks/{jobId}/review-observations", IamPermissions.ProjectRunsOperate)]
    [InlineData("POST", "/api/tasks/{jobId}/review-observations/{reviewId}/later-evidence", IamPermissions.ProjectRunsOperate)]
    [InlineData("POST", "/api/tasks/{jobId}/review-observations/{reviewId}/finding-outcomes", IamPermissions.ProjectRunsOperate)]
    [InlineData("GET", "/api/v1/management/status", IamPermissions.WorkspaceOperate)]
    [InlineData("PUT", "/api/workspaces/{id}/usage-calendar", IamPermissions.WorkspaceAdminister)]
    [InlineData("PUT", "/api/workspaces/{id}/autonomy", IamPermissions.WorkspaceSettingsWrite)]
    [InlineData("POST", "/api/auth/invitations", IamPermissions.AccessManage)]
    [InlineData("GET", "/api/auth/users", IamPermissions.AccessRead)]
    [InlineData("POST", "/api/auth/logout", IamPermissions.SelfSession)]
    [InlineData("POST", "/api/auth/login", IamPermissions.Anonymous)]
    [InlineData("GET", "/healthz", IamPermissions.Anonymous)]
    [InlineData("GET", "/hubs/jobs", IamPermissions.WorkspaceRead)]
    [InlineData("GET", "/api/auth/api-tokens", IamPermissions.AccessRead)]
    [InlineData("POST", "/api/auth/api-tokens", IamPermissions.AccessManage)]
    [InlineData("DELETE", "/api/auth/api-tokens/{id}", IamPermissions.AccessManage)]
    [InlineData("GET", "/api/auth/runner-enrollments", IamPermissions.AccessRead)]
    [InlineData("DELETE", "/api/auth/runner-enrollments/{id}", IamPermissions.AccessManage)]
    [InlineData("GET", "/api/first-run/projects/{projectId}/repository", IamPermissions.WorkspaceAdminister)]
    [InlineData("POST", "/api/first-run/git-setups", IamPermissions.WorkspaceAdminister)]
    [InlineData("POST", "/api/pull-gates/claim", IamPermissions.RunnerService)]
    [InlineData("PUT", "/api/pull-gates/runs/{runId}/artifacts/{name}", IamPermissions.RunnerService)]
    [InlineData("GET", "/api/gate-artifacts/{runId}/{name}", IamPermissions.RunnerService)]
    [InlineData("POST", "/api/tasks", IamPermissions.ProjectCardsWrite)]
    [InlineData("GET", "/api/secrets", IamPermissions.SecretsRead)]
    [InlineData("POST", "/api/secrets/workspace", IamPermissions.SecretsWrite)]
    [InlineData("GET", "/api/projects/{project}/secrets/{name}", IamPermissions.SecretsRead)]
    [InlineData("DELETE", "/api/projects/{project}/secrets/{name}", IamPermissions.SecretsWrite)]
    [InlineData("POST", "/api/projects/{project}/environments/{environment}/secrets/{name}/rotate", IamPermissions.SecretsWrite)]
    [InlineData("POST", "/api/runner/secrets/resolve", IamPermissions.RunnerService)]
    [InlineData("POST", "/api/projects/{projectName}/pipeline-definition/migration", IamPermissions.ProjectSettingsWrite)]
    [InlineData("GET", "/api/bus/{project}/messages", IamPermissions.ProjectRead)]
    [InlineData("POST", "/api/telemetry/otlp/v1/metrics", IamPermissions.Anonymous)]
    [InlineData("POST", "/api/telemetry/otlp/v1/logs", IamPermissions.Anonymous)]
    [InlineData("GET", "/api/projects/{projectName}/environments/{environmentId}/telemetry/logs", IamPermissions.ProjectRead)]
    [InlineData("GET", "/api/projects/{projectName}/environments/{environmentId}/telemetry/tokens", IamPermissions.ProjectRead)]
    [InlineData("POST", "/api/projects/{projectName}/environments/{environmentId}/telemetry/tokens", IamPermissions.ProjectSettingsWrite)]
    [InlineData("DELETE", "/api/projects/{projectName}/environments/{environmentId}/telemetry/tokens/{tokenId}", IamPermissions.ProjectSettingsWrite)]
    [InlineData("GET", "/api/orchestration/decision-receipts/{receiptId}", IamPermissions.ProjectRead)]
    [InlineData("POST", "/api/orchestration/decision-receipts/{receiptId}/operator-intent", IamPermissions.ProjectRunsOperate)]
    public void Representative_routes_resolve_to_their_permission(string method, string pattern, string permission)
    {
        var resolved = RoutePermissionCatalog.Resolve(method,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(pattern));

        Assert.Equal(permission, resolved?.Permission);
    }

    [Fact]
    public void Environment_routes_carry_their_environment()
    {
        var approve = RoutePermissionCatalog.Resolve("PUT", "/api/tasks/{}/release");
        var rollback = RoutePermissionCatalog.Resolve("POST", "/api/projects/{}/environments/{}/rollback");

        Assert.Equal("production", approve?.Environment);
        Assert.Equal("environmentId", rollback?.EnvironmentRouteValue);
        Assert.Equal("environmentId", RoutePermissionCatalog.Resolve("POST",
            "/api/projects/{}/deploy/releases/{}/deployments")?.EnvironmentBodyField);
        Assert.Equal("environment", RoutePermissionCatalog.Resolve("GET", "/api/projects/{}/environments/{}/secrets")?.EnvironmentRouteValue);
    }

    [Fact]
    public void Owner_role_routes_resolve_to_a_permission_only_the_owner_holds()
    {
        using var factory = Factory(SecurityProfiles.Networked, null);
        var ownerRoutes = Routes(factory.Services)
            .Where(endpoint => endpoint.Metadata.GetMetadata<StudioRoleRequirement>()?.MinimumRole == StudioRoles.Owner)
            .ToList();

        var accessRead = RoutePermissionCatalog.Resolve("GET", "/api/auth/iam")?.Permission;
        Assert.Equal(IamPermissions.AccessRead, accessRead);
        Assert.Equal([IamRoles.Owner], IamRoles.All.Where(role => role.Permissions.Contains(accessRead!)).Select(role => role.Id));
        foreach (var endpoint in ownerRoutes)
        foreach (var method in RoutePermissionCatalog.Methods(endpoint))
        {
            var permission = RoutePermissionCatalog.Resolve(endpoint, method)?.Permission;
            Assert.NotNull(permission);
            Assert.Equal([IamRoles.Owner], IamRoles.All.Where(role => role.Permissions.Contains(permission!)).Select(role => role.Id));
        }
    }

    [Fact]
    public void The_task_server_proxy_declares_read_and_operate_and_the_old_catch_all_is_gone()
    {
        using var factory = Factory(SecurityProfiles.Networked, "http://127.0.0.1:5071");
        var proxy = Assert.Single(Routes(factory.Services), endpoint => endpoint.RoutePattern.RawText == "/api/v1/{**path}");

        Assert.Equal(IamPermissions.WorkspaceRead, RoutePermissionCatalog.Resolve(proxy, "GET")?.Permission);
        Assert.Equal(IamPermissions.WorkspaceOperate, RoutePermissionCatalog.Resolve(proxy, "POST")?.Permission);
        Assert.Equal(IamPermissions.WorkspaceOperate, RoutePermissionCatalog.Resolve(proxy, "DELETE")?.Permission);
        Assert.Null(RoutePermissionCatalog.Resolve("POST", "/api/v1/{}"));
        Assert.Null(RoutePermissionCatalog.Resolve("GET", "/api/v1/{}"));
    }

    [Fact]
    public void Only_handler_checked_unaddressed_writes_accept_project_bindings()
    {
        Assert.True(RoutePermissionCatalog.Resolve("POST", "/api/tasks")?.ProjectBindingsSuffice);
        Assert.True(RoutePermissionCatalog.Resolve("POST", "/api/tasks/batch-move")?.ProjectBindingsSuffice);
        Assert.True(RoutePermissionCatalog.Resolve("POST", "/api/tasks/reorder")?.ProjectBindingsSuffice);
        Assert.True(RoutePermissionCatalog.Resolve("POST", "/api/orchestration/decision-receipts/{}/operator-intent")?.ProjectBindingsSuffice);
        Assert.False(RoutePermissionCatalog.Resolve("POST", "/api/supervisor/{}")?.ProjectBindingsSuffice);
        Assert.False(RoutePermissionCatalog.Resolve("PUT", "/api/workspaces/{}/autonomy")?.ProjectBindingsSuffice);
    }

    [Fact]
    public void Arbitrary_method_tokens_resolve_without_caching()
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/tasks/{jobId}"), 0, Microsoft.AspNetCore.Http.EndpointMetadataCollection.Empty, null);

        Assert.Null(RoutePermissionCatalog.Resolve(endpoint, "X-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(IamPermissions.ProjectRead, RoutePermissionCatalog.Resolve(endpoint, "get")?.Permission);
    }

    [Fact]
    public void An_unlisted_write_route_resolves_to_nothing()
    {
        Assert.Null(RoutePermissionCatalog.Resolve("POST", "/api/tasks/{}/brand-new-mutation"));
        Assert.Null(RoutePermissionCatalog.Resolve("POST", "/api/runner/epic-planning-prompt"));
        Assert.Null(RoutePermissionCatalog.Resolve("POST", "/api/brand-new-area/{}"));
        Assert.Null(RoutePermissionCatalog.Resolve("GET", "/api/brand-new-area"));
    }

    [Fact]
    public void Iam_routes_declare_their_permission_explicitly()
    {
        using var factory = Factory(SecurityProfiles.Networked, null);
        var iam = Routes(factory.Services)
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/auth/iam", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(iam);
        Assert.All(iam, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()));
    }

    private WebApplicationFactory<Program> Factory(string profile, string? taskServer)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = profile,
                ["AllowedHosts"] = "*",
                ["TaskServer:BaseUrl"] = taskServer,
            }));
        });
        _ = factory.Server;
        return factory;
    }

    private static IReadOnlyList<RouteEndpoint> Routes(IServiceProvider services)
        => services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Distinct()
            .ToList();

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
