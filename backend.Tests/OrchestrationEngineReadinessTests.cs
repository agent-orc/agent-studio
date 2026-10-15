using System.Net;
using AgentStudio.Diagnostics;
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
/// AGT-3060 (I18): the per-transition switch selects who decides. Readiness is
/// a report: it is required only while a switch profile selects the engine,
/// and it never holds a decision.
/// </summary>
public sealed class OrchestrationEngineReadinessTests : IDisposable
{
    private const string EngineSwitch = "Orchestration:Transitions:OperatorDecision";
    private readonly FakeTimeProvider _time = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "engine-readiness-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Engine_switched_host_is_live_only_within_the_contact_window()
    {
        var readiness = Readiness(new()
        {
            [EngineSwitch] = "Engine",
            [OrchestrationEngineReadiness.ContactWindowKey] = "20",
        });

        Assert.True(readiness.Required);
        Assert.False(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.NotLive, readiness.State);

        readiness.Observe("engine_one");
        Assert.True(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.Ready, readiness.State);
        Assert.Equal("engine_one", readiness.LastEngineId);

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(readiness.IsLive);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(readiness.IsLive);
    }

    [Theory]
    [InlineData("Orchestration:Transitions:CodingCompletion", "Engine")]
    [InlineData("Orchestration:Transitions:ReviewReport", "Shadow")]
    [InlineData("Orchestration:Transitions:OperatorSweep", "Engine")]
    [InlineData(EngineSwitch, "Engine")]
    public void Readiness_is_required_once_any_switch_profile_selects_the_engine(string key, string mode)
        => Assert.True(Readiness(new() { [key] = mode }).Required);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Monolith", null, null)]
    [InlineData("Engine", "TaskServer:BaseUrl", "http://task-server:5071")]
    [InlineData("Engine", "Security:Profile", "public-demo-readonly")]
    public void Readiness_is_not_required_on_the_monolith_switch_behind_the_proxy_or_for_public_demo(
        string? mode, string? key, string? value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Orchestration:ExecutionMode"] = "Monolith",
            [EngineSwitch] = mode,
        };
        if (key is not null) settings[key] = value;
        var readiness = Readiness(settings);

        Assert.False(readiness.Required);
        Assert.True(readiness.IsLive);
        Assert.Equal(OrchestrationEngineReadiness.NotRequired, readiness.State);
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
                    [EngineSwitch] = "Engine",
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

    private OrchestrationEngineReadiness Readiness(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new OrchestrationEngineReadiness(configuration, _time, NullLogger<OrchestrationEngineReadiness>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "OrchestrationEngineReadinessTests: fixture cleanup is best-effort");
        }
    }
}
