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
    [InlineData("PUT", "/api/projects/{projectName}/auto-commit", IamPermissions.ProjectSettingsWrite)]
    [InlineData("PUT", "/api/projects/{projectName}/wiki/files/{**relPath}", IamPermissions.ProjectDocsWrite)]
    [InlineData("POST", "/api/runner/{projectName}/orchestrator-chat", IamPermissions.ProjectChatWrite)]
    [InlineData("POST", "/api/runner/claim", IamPermissions.RunnerService)]
    [InlineData("GET", "/api/v1/management/status", IamPermissions.WorkspaceOperate)]
    [InlineData("PUT", "/api/workspaces/{id}/usage-calendar", IamPermissions.WorkspaceAdminister)]
    [InlineData("PUT", "/api/workspaces/{id}/autonomy", IamPermissions.WorkspaceSettingsWrite)]
    [InlineData("POST", "/api/auth/invitations", IamPermissions.AccessManage)]
    [InlineData("GET", "/api/auth/users", IamPermissions.AccessRead)]
    [InlineData("POST", "/api/auth/logout", IamPermissions.SelfSession)]
    [InlineData("POST", "/api/auth/login", IamPermissions.Anonymous)]
    [InlineData("GET", "/healthz", IamPermissions.Anonymous)]
    [InlineData("GET", "/hubs/jobs", IamPermissions.WorkspaceRead)]
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
    }

    [Fact]
    public void An_unlisted_write_route_resolves_to_nothing()
    {
        Assert.Null(RoutePermissionCatalog.Resolve("POST", "/api/tasks/{}/brand-new-mutation"));
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
