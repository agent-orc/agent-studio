using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.TestSupport;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// A raised Watcher finding reaches the central bus with a severity the
/// agent-message contract accepts. A critical finding used to be sent as
/// "Error", which the bus refused ("severity 'Error' not in enum").
/// </summary>
public sealed class WatcherActivityProjectorBusTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "watcher-bus-" + Guid.NewGuid().ToString("N"));

    public WatcherActivityProjectorBusTests() => Directory.CreateDirectory(_workspace);

    public void Dispose() => TestTempRoot.TryDelete(_workspace);

    [Theory]
    [InlineData("critical", "High")]
    [InlineData("warning", "Warn")]
    [InlineData(null, "Warn")]
    public void Finding_severity_maps_into_the_bus_enum(string? watcherSeverity, string expected)
    {
        var severity = WatcherActivityProjector.FindingBusSeverity(watcherSeverity);

        Assert.Equal(expected, severity);
        Assert.Contains(severity, AgentMessageValidator.Severities);
    }

    [Fact]
    public async Task Critical_finding_is_appended_to_the_bus_as_High()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var store = new AgentMessageBusStore();
        var bus = new AgentMessageBusBridge(store, configuration, NullLogger<AgentMessageBusBridge>.Instance);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var projector = new WatcherActivityProjector(
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance), scanner, configuration,
            NullLogger<WatcherActivityProjector>.Instance, bus);
        var watcherCase = new WatcherCase
        {
            Id = "wc-critical-1",
            Fingerprint = "fp-critical",
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = QuotaProbeSignalProbe.WorkspaceProject,
            Severity = "critical",
            LastSummary = "Quota probe silent for 30 minutes.",
        };

        projector.ProjectFindingRaised(watcherCase, new WatcherEvidencePack { CaseId = watcherCase.Id });

        var emitted = await WaitForMessageAsync(store, "watcher-finding-raised");
        Assert.Equal("High", emitted.Severity);
        Assert.True(AgentMessageValidator.TryValidate(emitted, out var error), error);
    }

    private async Task<AgentMessage> WaitForMessageAsync(AgentMessageBusStore store, string topic)
    {
        // The projector mirrors to the bus without awaiting the append.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var match = store.Recent(_workspace, null, 20).FirstOrDefault(message => message.Topic == topic);
            if (match is not null) return match;
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"No bus message with topic {topic} was appended.");
            await Task.Delay(20);
        }
    }
}
