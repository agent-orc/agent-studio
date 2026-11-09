using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProtocolStartupRefusalTests
{
    [Fact]
    public async Task Newer_engine_protocol_is_refused_and_emitted_to_the_operator_feed()
    {
        using var directory = new TempDirectory();
        await using var app = new ProtocolApiFactory(directory.Path);
        using (var client = app.CreateClient())
        {
            client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            client.DefaultRequestHeaders.Add("X-Client-Id", "protocol-test");
            using var response = await client.PostAsJsonAsync("/api/v1/protocol/compatibility",
                new ProtocolCompatibilityRequest(TaskServerProtocol.EngineClientKind, "1.1.0",
                    TaskServerProtocol.Current + 1));
            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        }
        var feed = await app.Services.GetRequiredService<TaskServerStore>()
            .GetOrchestratorFeedAsync(20, CancellationToken.None);
        Assert.Contains(feed.Entries, entry => entry.Kind == "protocol.startup-refused"
            && entry.Text.Contains("task-server-protocol-older", StringComparison.Ordinal));
    }

    private sealed class ProtocolApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
}
