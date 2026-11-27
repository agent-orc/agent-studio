using AgentStudio.Operator;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ControlPlaneAuditAlertTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-alert-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task An_audit_alert_reaches_the_bus_and_the_owners_needs_you_list()
    {
        var (log, monitor, bus, _) = Build();
        Assert.True(await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken,
            "Sequences 4 to 6 are missing.", 4, CancellationToken.None));
        Assert.False(await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken,
            "Sequences 4 to 6 are missing.", 4, CancellationToken.None));

        var message = Assert.Single(bus.Recent(_root, null, 100));
        Assert.Equal(("error", "High", "control_plane_audit_alert"), (message.Kind, message.Severity, message.Topic));
        Assert.Equal(4, message.Payload!.Value.GetProperty("sequence").GetInt64());
        var source = new ControlPlaneAuditAlertFactSource(monitor, log);
        var facts = source.Collect(_clock.GetUtcNow().UtcDateTime);
        var owner = OperatorAttention.Project(facts, OperatorDecisionReader.Everything);
        var item = Assert.Single(owner.Items);
        Assert.Equal(1, owner.Count);
        Assert.Equal(OperatorDecisionTypes.AuditAlert, item.Type);
        Assert.Contains("4 to 6", item.Reason);
        Assert.Contains(ControlPlaneAuditAlertCodes.ChainBroken, item.Title);
        Assert.NotEmpty(item.Evidence);
        Assert.Empty(OperatorAttention.Project(facts, new(false, _ => true)).Items);
        monitor.Clear(ControlPlaneAuditAlertCodes.ChainBroken);
        Assert.Empty(source.Collect(_clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task A_recovered_break_keeps_the_same_owner_notice_identity_after_restart()
    {
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
        var log = new ControlPlaneAuditLog(Path.Combine(_root, "audit"), clock);
        log.Append(new("operator", "system", "first", "200"));
        log.Append(new("operator", "system", "second", "200"));
        File.WriteAllText(log.FilePath, File.ReadAllLines(log.FilePath)[0] + "\n");
        var monitor = new ControlPlaneAuditMonitor(log, new("none", null, null, null), clock);
        using var verifier = new ControlPlaneAuditVerifier(log, monitor, null, clock);
        await verifier.VerifyAsync(CancellationToken.None);
        var before = Assert.Single(new ControlPlaneAuditAlertFactSource(monitor, log).Collect(clock.GetUtcNow().UtcDateTime));
        Assert.NotEqual(log.LatestRecoveryAnchor!.At, Assert.Single(monitor.Raised).RaisedAt);

        var restarted = new ControlPlaneAuditLog(Path.GetDirectoryName(log.FilePath)!, clock);
        var restartedMonitor = new ControlPlaneAuditMonitor(restarted, new("none", null, null, null), clock);
        using var restartedVerifier = new ControlPlaneAuditVerifier(restarted, restartedMonitor, null, clock);
        await restartedVerifier.VerifyAsync(CancellationToken.None);
        var after = Assert.Single(new ControlPlaneAuditAlertFactSource(restartedMonitor, restarted).Collect(clock.GetUtcNow().UtcDateTime));

        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.ObservedAtUtc, after.ObservedAtUtc);
        Assert.Empty(restartedMonitor.Raised);
        var diff = OperatorDecisionPolicy.Diff(new HashSet<string> { before.Key }, [after],
            new HashSet<string> { OperatorDecisionTypes.AuditAlert });
        Assert.Empty(diff.Publish);
    }

    [Fact]
    public async Task A_cancelled_delivery_is_logged_once_as_an_error_naming_the_alert()
    {
        var (_, monitor, bus, logger) = Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, "Missing sequence 2.", 2, cancellation.Token);
        await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, "Missing sequence 2.", 2, cancellation.Token);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(ControlPlaneAuditAlertCodes.ChainBroken, entry.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(entry.Exception);
        Assert.Empty(bus.Recent(_root, null, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delivery_failure_is_logged_once_as_an_error_naming_the_alert(bool missingWorkspace)
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "This is a file, not a workspace directory.");
        var (_, monitor, _, logger) = Build(missingWorkspace ? null : blocked, overrideWorkspace: true);
        await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, "Missing sequences 2 to 3.", 2, CancellationToken.None);
        await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, "Missing sequences 2 to 3.", 2, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(ControlPlaneAuditAlertCodes.ChainBroken, entry.Message);
        Assert.Contains("could not reach the operator feed", entry.Message);
        Assert.Contains($"pid:{Environment.ProcessId}", entry.Message);
        Assert.NotNull(entry.Exception);
    }

    private (ControlPlaneAuditLog, ControlPlaneAuditMonitor, AgentMessageBusStore, CaptureLogger) Build(
        string? workspace = null, bool overrideWorkspace = false)
    {
        var clock = _clock;
        var log = new ControlPlaneAuditLog(Path.Combine(_root, "audit"), clock);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = overrideWorkspace ? workspace : _root,
        }).Build();
        var bus = new AgentMessageBusStore();
        var logger = new CaptureLogger();
        var bridge = new AgentMessageBusBridge(bus, config, logger, clock);
        var monitor = new ControlPlaneAuditMonitor(log, new("none", null, null, null), clock,
            new OperatorFeedAuditAlertChannel(bridge), logger);
        return (log, monitor, bus, logger);
    }

    private sealed class CaptureLogger : ILogger<AgentMessageBusBridge>, ILogger<ControlPlaneAuditMonitor>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, exception), exception));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
