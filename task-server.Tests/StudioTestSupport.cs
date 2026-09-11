using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TaskServer.Tests;

/// <summary>
/// Shared xunit fixture for the P1 "task detail and hosts" Studio route test
/// files (<c>Studio*Tests.cs</c>). Mirrors <c>StudioEndpointsTests.StudioApiFactory</c>
/// so every group's tests boot the same way without duplicating the
/// WebApplicationFactory wiring in each file.
/// </summary>
internal sealed class StudioTestApiFactory(string dataDirectory) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TaskServer:DataDirectory"] = dataDirectory,
                ["TaskServer:ListenUrl"] = string.Empty,
                ["TaskServer:RetentionSchedulerEnabled"] = "false",
            }));
    }
}

internal static class StudioTestClient
{
    public static HttpClient Create(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-api-test");
        return client;
    }
}
