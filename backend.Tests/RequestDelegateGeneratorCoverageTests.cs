using System.CodeDom.Compiler;
using System.Reflection;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The minimal-API request delegate generator (AGT-3418) replaces the runtime
/// expression compilation of <c>RequestDelegateFactory</c>, which made every
/// host start build each route twice. A handler shape the generator cannot
/// intercept silently falls back to the runtime path; this test names it.
/// </summary>
public sealed class RequestDelegateGeneratorCoverageTests : IDisposable
{
    private const string GeneratorTool = "Microsoft.AspNetCore.Http.RequestDelegateGenerator";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "rdg-coverage-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SecurityProfiles.Local, null)]
    [InlineData(SecurityProfiles.Networked, "http://127.0.0.1:5071")]
    public void Every_minimal_api_route_is_built_by_the_generator(string profile, string? taskServer)
    {
        using var factory = Factory(profile, taskServer);
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<MethodInfo>() is not null)
            .ToList();

        var runtimeCompiled = routes
            .Where(endpoint => !endpoint.Metadata.GetOrderedMetadata<GeneratedCodeAttribute>()
                .Any(attribute => attribute.Tool?.StartsWith(GeneratorTool, StringComparison.Ordinal) == true))
            .Select(endpoint => endpoint.DisplayName ?? endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(routes);
        Assert.True(runtimeCompiled.Count == 0,
            "Routes the request delegate generator did not intercept (use a lambda or method group handler):\n  "
            + string.Join("\n  ", runtimeCompiled));
    }

    private WebApplicationFactory<Program> Factory(string profile, string? taskServer)
    {
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
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

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
