using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodexExecutionModelCatalogueTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"codex-catalogue-{Guid.NewGuid():N}");

    [Fact]
    public async Task Models_endpoint_uses_the_configured_task_servers_host_inventory()
    {
        const string taskServerUrl = "https://task-server.test";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "local",
                ["TaskServer:BaseUrl"] = taskServerUrl,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(Now)));
                services.AddHttpClient(TaskServerPlaneProxy.ClientName)
                    .ConfigureHttpClient(client => client.BaseAddress = new Uri(taskServerUrl))
                    .ConfigurePrimaryHttpMessageHandler(() => new HostInventoryHandler());
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var catalogue = await client.GetFromJsonAsync<CliModelCatalog>("/api/cli/codex/models");

        Assert.NotNull(catalogue);
        Assert.Equal("execution-hosts", catalogue.Source);
        Assert.True(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt62Astra).Available);
        Assert.False(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt62Sol).Available);
        using var otherCli = await client.GetAsync("/api/cli/claude/models");
        Assert.Equal(HttpStatusCode.NotFound, otherCli.StatusCode);
    }

    [Fact]
    public void Fleet_advertisements_drive_availability_and_keep_older_pins_selectable()
    {
        var coding = Host("coding", Now,
            Capability("cli-execution:codex", models: [ModelIds.Gpt62Sol, ModelIds.Gpt6Sol]));
        var review = Host("review", Now,
            Capability("cli-execution:codex"),
            Capability("model-acceptance:codex:gpt-6.2-astra"));
        var stale = Host("stale", Now.AddMinutes(-4),
            Capability("cli-execution:codex", models: [ModelIds.Gpt61Sol]));

        var catalogue = CodexExecutionModelCatalogue.Build([coding, review, stale], Now);
        var sol = Assert.Single(catalogue.Models, model => model.IsDefault);
        Assert.Equal("execution-hosts", catalogue.Source);
        Assert.Equal(ModelIds.Gpt62Sol, sol.Id);
        Assert.True(sol.Available);
        Assert.Equal("medium", sol.DefaultThinkingLevel);
        Assert.True(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt62Astra).Available);
        Assert.False(Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt61Sol).Available);
        var superseded = Assert.Single(catalogue.Models, model => model.Id == ModelIds.Gpt6Sol);
        Assert.True(superseded.Available);
        Assert.False(superseded.Deprecated);
        Assert.Contains("Superseded", superseded.AvailabilityNote);
    }

    [Fact]
    public void A_fresh_refusal_overrides_the_same_hosts_cli_model_list()
    {
        var host = Host("coding", Now,
            Capability("cli-execution:codex", models: [ModelIds.Gpt62Sol]),
            Capability("model-acceptance:codex:gpt-6.2-sol", status: "blocked"),
            Capability("model-acceptance:codex:gpt-6.2-astra", freshUntil: Now));

        var catalogue = CodexExecutionModelCatalogue.Build([host], Now);
        Assert.All(catalogue.Models, model => Assert.False(model.Available));
        Assert.Equal(ModelIds.Gpt62Sol, Assert.Single(catalogue.Models, model => model.IsDefault).Id);
    }

    private static RunnerCapabilitySnapshotDto Host(string id, DateTime lastSeen, params CapabilityHealthDto[] capabilities)
        => new(id, id, id, "instance", "test", 1, "active", lastSeen, lastSeen,
            new RemoteHostAdmissionDto(id, "open", null, null, null, null), capabilities, null);

    private static CapabilityHealthDto Capability(string key, string status = "ready",
        DateTime? freshUntil = null, string[]? models = null)
        => new(key, "test", status, "healthy", null, Now, freshUntil ?? Now.AddMinutes(1), true,
            null, null, null, null, 0, "0.166.0", null, null, [], [], SupportedModels: models);

    private sealed class HostInventoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/api/v1/management/remote-hosts")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var host = Host("task-server-review-host", Now,
                Capability("cli-execution:codex", models: [ModelIds.Gpt62Astra]));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { host }) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }
}
