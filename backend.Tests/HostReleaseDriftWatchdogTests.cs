using AgentStudio.Bus;
using AgentStudio.Runner;
using AgentStudio.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2826: the drift has to reach an operator. The watchdog projects the
/// registry for Execution Hosts and raises exactly one operator-feed alarm per
/// host release that stays behind Stable past the grace window.
/// </summary>
public sealed class HostReleaseDriftWatchdogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly IConfigurationRoot _config;
    private readonly AgentMessageBusBridge _bridge;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly V1ReviewExecutorRegistry _registry;

    public HostReleaseDriftWatchdogTests()
    {
        _registry = new V1ReviewExecutorRegistry(_time);
        _workspace = Path.Combine(Path.GetTempPath(), "host-release-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        _store = new AgentMessageBusStore();
        _bridge = new AgentMessageBusBridge(_store, _config, NullLogger<AgentMessageBusBridge>.Instance, _time);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_host_behind_stable_is_reported_and_alarms_exactly_once()
    {
        Advertise("runner-old", release: OldRelease);
        var watchdog = NewWatchdog(StableBuild);

        var first = await watchdog.RefreshAsync();
        var second = await watchdog.RefreshAsync();

        var entry = Assert.Single(first.Hosts);
        Assert.Equal(HostReleaseDriftStates.Behind, entry.State);
        Assert.Equal("coding", entry.Role);
        Assert.Equal(OldRelease.ReleaseId, entry.Release!.ReleaseId);
        Assert.True(entry.AlarmDue);
        Assert.Equal(1, first.BehindCount);
        Assert.Equal("0.3.0", first.Stable.Version);
        Assert.Equal(1, second.BehindCount);

        var alarms = _store.Recent(_workspace, null, 20)
            .Where(message => message.Topic == "host_release_drift")
            .ToList();
        var alarm = Assert.Single(alarms);
        Assert.Equal("Warn", alarm.Severity);
        Assert.Contains(OldRelease.ReleaseId, alarm.Summary);
        Assert.Contains("0.3.0", alarm.Summary);
        Assert.Contains(alarm.Tags!, tag => tag == "host:host-runner-old");
    }

    [Fact]
    public async Task A_host_on_the_stable_release_never_alarms()
    {
        Advertise("runner-current", release: CurrentRelease);
        var watchdog = NewWatchdog(StableBuild);

        var snapshot = await watchdog.RefreshAsync();

        Assert.Equal(HostReleaseDriftStates.Current, Assert.Single(snapshot.Hosts).State);
        Assert.Equal(0, snapshot.BehindCount);
        Assert.DoesNotContain(_store.Recent(_workspace, null, 20), m => m.Topic == "host_release_drift");
    }

    /// <summary>
    /// An upgraded host is a different release, so a later regression must be
    /// able to alarm again rather than being suppressed by the earlier alarm.
    /// </summary>
    [Fact]
    public async Task Upgrading_clears_the_alarm_and_a_later_regression_alarms_again()
    {
        Advertise("runner-flapping", release: OldRelease);
        var watchdog = NewWatchdog(StableBuild);
        await watchdog.RefreshAsync();

        Advertise("runner-flapping", release: CurrentRelease, generation: 2);
        var upgraded = await watchdog.RefreshAsync();
        Assert.Equal(HostReleaseDriftStates.Current, Assert.Single(upgraded.Hosts).State);

        Advertise("runner-flapping", release: OldRelease, generation: 3);
        await watchdog.RefreshAsync();

        Assert.Equal(
            2,
            _store.Recent(_workspace, null, 20).Count(m => m.Topic == "host_release_drift"));
    }

    /// <summary>A host that stopped reporting is an offline problem, not a drift problem.</summary>
    [Fact]
    public async Task A_stale_heartbeat_reports_the_drift_without_alarming()
    {
        Advertise("runner-silent", release: OldRelease);
        var watchdog = NewWatchdog(StableBuild);

        var snapshot = await watchdog.RefreshAsync(Now.UtcDateTime.AddHours(2));

        var entry = Assert.Single(snapshot.Hosts);
        Assert.Equal(HostReleaseDriftStates.Behind, entry.State);
        Assert.True(entry.HeartbeatStale);
        Assert.False(entry.AlarmDue);
        Assert.DoesNotContain(_store.Recent(_workspace, null, 20), m => m.Topic == "host_release_drift");
    }

    [Fact]
    public async Task Failed_emission_remains_pending_and_next_pass_succeeds_exactly_once()
    {
        Advertise("runner-retry", OldRelease);
        var watchdog = NewWatchdog(StableBuild);
        var now = Now.UtcDateTime;
        // A file cannot be a workspace directory: exercise a real append failure.
        var blocked = Path.Combine(_workspace, "blocked");
        File.WriteAllText(blocked, "not a directory");
        _config["TaskRepository"] = blocked;
        await watchdog.RefreshAsync(now);
        _config["TaskRepository"] = _workspace;
        await watchdog.RefreshAsync(now.AddSeconds(1));
        Assert.Empty(_store.Recent(_workspace, null, 20));
        await watchdog.RefreshAsync(now.AddMinutes(5));
        await watchdog.RefreshAsync(now.AddMinutes(6));
        Assert.Single(_store.Recent(_workspace, null, 20), m => m.Topic == "host_release_drift");
    }

    [Fact]
    public async Task Resolved_drift_clears_pending_retry_and_rearms_immediately()
    {
        Advertise("runner-retry", OldRelease);
        var watchdog = NewWatchdog(StableBuild);
        var now = Now.UtcDateTime;
        _config["TaskRepository"] = null;
        await watchdog.RefreshAsync(now);
        Advertise("runner-retry", CurrentRelease, generation: 2);
        _config["TaskRepository"] = _workspace;
        await watchdog.RefreshAsync(now.AddSeconds(1));
        Assert.Empty(_store.Recent(_workspace, null, 20));
        Advertise("runner-retry", OldRelease, generation: 3);
        await watchdog.RefreshAsync(now.AddSeconds(2));
        Assert.Single(_store.Recent(_workspace, null, 20), m => m.Topic == "host_release_drift");
    }

    [Fact]
    public async Task Repeated_failures_back_off_to_five_minutes_and_log_once_per_pass()
    {
        Advertise("runner-one", OldRelease);
        Advertise("runner-two", OldRelease);
        var logger = new WarningLogger();
        var watchdog = NewWatchdog(StableBuild, logger);
        var now = Now.UtcDateTime;
        _config["TaskRepository"] = null;
        var pass = 0;
        foreach (var seconds in new[] { 0, 30, 90, 210, 450 })
        {
            await watchdog.RefreshAsync(now.AddSeconds(seconds));
            Assert.Equal(++pass, logger.Warnings);
            await watchdog.RefreshAsync(now.AddSeconds(seconds + 1));
            Assert.Equal(pass, logger.Warnings);
        }
        _config["TaskRepository"] = _workspace;
        await watchdog.RefreshAsync(now.AddSeconds(749));
        Assert.Empty(_store.Recent(_workspace, null, 20));
        await watchdog.RefreshAsync(now.AddSeconds(750));
        await watchdog.RefreshAsync(now.AddSeconds(751));
        Assert.Equal(2, _store.Recent(_workspace, null, 20).Count(m => m.Topic == "host_release_drift"));
        Assert.Equal(5, logger.Warnings);
    }

    private sealed class WarningLogger : ILogger<HostReleaseDriftWatchdog>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    // -- fixtures ---------------------------------------------------------

    private static readonly Contract.RunnerReleaseIdentityDto OldRelease = new(
        "agt-host-20260823T060000Z-bbbbbbb",
        "0.2.7",
        "bbbbbbb2222",
        new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc));

    private static readonly Contract.RunnerReleaseIdentityDto CurrentRelease = new(
        "agt-host-20260911T080000Z-aaaaaaa",
        "0.3.0",
        "aaaaaaa1111",
        new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc));

    private static BuildIdentity StableBuild => new(
        1,
        "Agent Studio",
        "v0.3.0",
        "0.3.0",
        "aaaaaaa1111",
        false,
        new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero),
        "sha256-stable",
        new ReleaseArtifactIdentity("CodingAgentRunner", "0.3.0", "v0.3.0", "aaaaaaa1111", "sha256-a"),
        new ReleaseArtifactIdentity("coding-agent-chat", "0.3.0", "v0.3.0", "aaaaaaa1111", "sha256-b"));

    private HostReleaseDriftWatchdog NewWatchdog(BuildIdentity build, ILogger<HostReleaseDriftWatchdog>? logger = null) => new(
        _registry,
        StableReleaseIdentity.FromBuildIdentity(build),
        _bridge,
        new ConfigurationBuilder().Build(),
        logger ?? NullLogger<HostReleaseDriftWatchdog>.Instance,
        _time);

    private void Advertise(
        string runnerId,
        Contract.RunnerReleaseIdentityDto release,
        long generation = 1)
    {
        _registry.Register(runnerId, new Contract.RegisterRunnerRequest(
            runnerId,
            $"host-{runnerId}",
            $"instance-{generation}",
            release.ReleaseId,
            Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor],
            Release: release));
        _registry.AdvertiseCapabilities(runnerId, new Contract.CapabilityAdvertisementRequest(
            runnerId,
            $"instance-{generation}",
            Contract.CapabilityProtocol.CurrentSchemaVersion,
            Now.UtcDateTime,
            180,
            generation,
            [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.CodingExecutor, "executor")],
            Release: release));
    }
}
