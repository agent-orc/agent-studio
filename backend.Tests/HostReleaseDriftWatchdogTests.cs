using AgentStudio.Bus;
using AgentStudio.Runner;
using AgentStudio.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly AgentMessageBusBridge _bridge;
    private readonly V1ReviewExecutorRegistry _registry = new();

    public HostReleaseDriftWatchdogTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "host-release-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        _store = new AgentMessageBusStore();
        _bridge = new AgentMessageBusBridge(_store, config, NullLogger<AgentMessageBusBridge>.Instance);
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

        var snapshot = await watchdog.RefreshAsync(DateTime.UtcNow.AddHours(2));

        var entry = Assert.Single(snapshot.Hosts);
        Assert.Equal(HostReleaseDriftStates.Behind, entry.State);
        Assert.True(entry.HeartbeatStale);
        Assert.False(entry.AlarmDue);
        Assert.DoesNotContain(_store.Recent(_workspace, null, 20), m => m.Topic == "host_release_drift");
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

    private HostReleaseDriftWatchdog NewWatchdog(BuildIdentity build) => new(
        _registry,
        build,
        _bridge,
        new ConfigurationBuilder().Build(),
        NullLogger<HostReleaseDriftWatchdog>.Instance);

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
            DateTime.UtcNow,
            180,
            generation,
            [new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.CodingExecutor, "executor")],
            Release: release));
    }
}
