using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3225: one device-login service serves the first-run checklist and the Access and
/// Execution Hosts actions. A second session store or route family for the same flow fails here.
/// </summary>
[Trait("Category", "Guard")]
public sealed class DeviceLoginRouteTests
{
    private static readonly string[] ExpectedRoutes =
    [
        "POST /api/v1/management/device-logins",
        "GET /api/v1/management/device-logins/{handle}",
        "POST /api/v1/management/device-logins/{handle}/cancel",
        "POST /api/v1/runners/{runnerId}/device-login/claim",
        "GET /api/v1/runners/{runnerId}/device-login/{handle}",
        "POST /api/v1/runners/{runnerId}/device-login/{handle}/instructions",
        "POST /api/v1/runners/{runnerId}/device-login/{handle}/completion",
    ];

    private static readonly Regex DeviceLoginPath = new("device-?logins?|device-auth",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task Task_server_maps_exactly_one_device_login_route_family()
    {
        using var temp = new TempDirectory();
        await using var factory = new RouteFactory(temp.Path);
        _ = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => DeviceLoginPath.IsMatch(endpoint.RoutePattern.RawText ?? string.Empty))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText!.TrimEnd('/')}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedRoutes.Order(StringComparer.Ordinal), routes);
    }

    [Fact]
    public void No_other_host_source_maps_a_device_login_route()
    {
        var root = ProtocolTests.RepositoryRoot();
        var mapping = new Regex(
            @"Map(?:Get|Post|Put|Delete|Patch|Methods|Group)\s*\(\s*\$?@?""[^""]*(?:device-?logins?|device-auth)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var owner = Path.Combine("task-server", "DeviceLoginEndpoints.cs");
        var offenders = new[] { "backend", "task-server", "studio-bff", "operations-server", "runner" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(path => !string.Equals(path, owner, StringComparison.Ordinal)
                           && mapping.IsMatch(File.ReadAllText(Path.Combine(root, path))))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Device sign-in has one service and one route family in " + owner
            + ". Extend it instead of mapping a second device-login route in:\n" + string.Join('\n', offenders));
    }

    private sealed class RouteFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = "studio-" + Guid.NewGuid().ToString("N"),
                }));
        }
    }
}
