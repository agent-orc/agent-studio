using System.Net;
using AgentStudio.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3060 (I18): the engine is the only decider, so a production host has no
/// monolith fallback. Every automatic decision waits for engine readiness, which
/// the authority observes from engine requests on the orchestration endpoints.
/// </summary>
public sealed class OrchestrationEngineReadinessTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "engine-readiness-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Engine_only_host_is_live_only_within_the_contact_window()
    {
        var readiness = Readiness(new() { [OrchestrationEngineReadiness.ContactWindowKey] = "20" });

        Assert.True(readiness.Required);
        Assert.False(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.NotLive, readiness.State);
        Assert.False(readiness.AllowsResidualDecision("test-decider"));

        readiness.Observe("engine_one");
        Assert.True(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.Ready, readiness.State);
        Assert.Equal("engine_one", readiness.LastEngineId);
        Assert.True(readiness.AllowsResidualDecision("test-decider"));

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(readiness.IsLive);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(readiness.IsLive);
        Assert.False(readiness.AllowsResidualDecision("test-decider"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Engine", "TaskServer:BaseUrl", "http://task-server:5071")]
    [InlineData("Engine", "Security:Profile", "public-demo-readonly")]
    public void Readiness_is_not_required_on_a_test_host_behind_the_proxy_or_for_public_demo(
        string? authority, string? key, string? value)
    {
        var settings = new Dictionary<string, string?> { [OrchestrationDecisionAuthority.ConfigurationKey] = authority };
        if (key is not null) settings[key] = value;
        var readiness = Readiness(settings, engineOnly: false);

        Assert.False(readiness.Required);
        Assert.True(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.NotRequired, readiness.State);
        Assert.True(OrchestrationEngineReadiness.Allows(readiness, "test-decider"));
        Assert.True(OrchestrationEngineReadiness.Allows(null, "test-decider"));
    }

    [Fact]
    public async Task Acceptance_rail_holds_without_reading_a_card_while_the_engine_is_not_live()
    {
        var readiness = Readiness([]);
        // Null collaborators prove that the held pass reads and moves nothing.
        var rail = new AgentStudio.Tasks.AcceptanceRailHostedService(null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build(), NullLogger<AgentStudio.Tasks.AcceptanceRailHostedService>.Instance,
            engine: readiness);

        var snapshot = await rail.RunOnceAsync();

        Assert.Null(snapshot.LastRunAtUtc);
        Assert.Equal(0, snapshot.Accepted + snapshot.Requeued + snapshot.Escalated);
    }

    [Fact]
    public async Task Probe_reports_the_engine_and_the_alert_names_the_outage_once_per_watch_path()
    {
        var projectPath = Path.Combine(_workspace, "projects", "readiness");
        Directory.CreateDirectory(projectPath);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "readiness",
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    [OrchestrationDecisionAuthority.ConfigurationKey] = OrchestrationDecisionAuthority.Engine,
                }));
        });
        using var client = factory.CreateClient();
        var readiness = factory.Services.GetRequiredService<OrchestrationEngineReadiness>();

        var down = await client.GetAsync("/healthz/orchestration-engine");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal(OrchestrationEngineReadiness.NotLive, await down.Content.ReadAsStringAsync());

        var service = factory.Services.GetServices<IHostedService>().OfType<OrchestrationEngineReadinessService>().Single();
        Assert.False(service.HasEnrolledEngine());
        var outage = _time.GetUtcNow();
        service.Alert(outage);
        service.Alert(outage);
        var alert = Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(projectPath),
            entry => entry.Topic == "engine-not-live:" + outage.UtcTicks);
        Assert.Equal(OrchestratorLogKinds.Alert, alert.Kind);
        Assert.Contains("No engine identity is enrolled", alert.Reasoning);

        readiness.Observe("engine_test");
        var up = await client.GetAsync("/healthz/orchestration-engine");
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal(OrchestrationEngineReadiness.Ready, await up.Content.ReadAsStringAsync());
    }

    private OrchestrationEngineReadiness Readiness(Dictionary<string, string?> settings, bool engineOnly = true)
    {
        if (engineOnly) settings[OrchestrationDecisionAuthority.ConfigurationKey] = OrchestrationDecisionAuthority.Engine;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new OrchestrationEngineReadiness(configuration, _time, NullLogger<OrchestrationEngineReadiness>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
