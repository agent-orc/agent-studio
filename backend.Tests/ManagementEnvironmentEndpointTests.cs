using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ManagementEnvironmentEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_ssh_gate_appears_with_slots_and_transport_identity(bool standaloneTaskServer)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "management-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var manifestPath = Path.Combine(workspace, "connectivity.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "schemaVersion": 1, "installationId": "test-installation", "revision": 1,
              "mode": "one-box", "package": "legacy-backend",
              "serverOrigin": "http://127.0.0.1:5031", "browserOrigin": "http://127.0.0.1:4011",
              "enabledListeners": ["legacy-api", "browser"], "runners": []
            }
            """);
        using (var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = workspace,
                ["Security:Profile"] = "local",
                ["RemoteGate:SshHost"] = "gate.example.internal",
                ["RemoteGate:Host"] = "obsolete.example.internal",
                ["RemoteGate:GateSlots"] = "3",
                ["Connectivity:ManifestPath"] = manifestPath,
                ["TaskServer:BaseUrl"] = standaloneTaskServer ? "http://127.0.0.1:1" : null,
            }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(
                new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero))));
        }))
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
            using var response = await client.GetAsync("/api/v1/management/environment");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("gate.example.internal", body.GetProperty("gateHost").GetString());
            Assert.Equal(3, body.GetProperty("gateSlots").GetProperty("total").GetInt32());
            Assert.Equal("test-installation", body.GetProperty("installationId").GetString());
            Assert.Equal("one-box", body.GetProperty("mode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("engine").GetProperty("state").GetString()));
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
    }
}
