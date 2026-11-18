using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostCliLifecycleTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Snapshot_serializes_cli_versions_paths_targets_and_check_time()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await AdvertiseAsync(store, clock, activeSlots: 0);

        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        var codex = Assert.Single(snapshot.InstalledClis!, item => item.Name == "codex");
        Assert.Equal("0.144.1", codex.Version);
        Assert.Equal("/usr/local/bin/codex", codex.InstallPath);
        Assert.Equal(Start.UtcDateTime, codex.CheckedAt);
        Assert.Equal("0.154.0", codex.TargetVersion);
        Assert.True(codex.IsBelowTarget);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"installedClis\"", json);
        Assert.Contains("\"checkedAt\":\"2026-09-12T10:00:00", json);
    }

    [Fact]
    public async Task Drain_blocks_claims_cancel_reopens_and_success_resumes_after_probe()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await AdvertiseAsync(store, clock, activeSlots: 2);

        var draining = await store.RequestHostCliUpdateAsync("host-a", "operator", default);
        Assert.Equal(CliUpdateStates.Draining, draining.State);
        Assert.Equal(2, draining.ActiveSlots);
        Assert.True(draining.CanCancel);
        Assert.Equal("operator-draining",
            Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).HostAdmission.AdmissionState);

        var cancelled = await store.CancelHostCliUpdateAsync("host-a", "operator", default);
        Assert.Equal(CliUpdateStates.Cancelled, cancelled.State);
        Assert.Equal("open",
            Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).HostAdmission.AdmissionState);

        clock.Advance(TimeSpan.FromMinutes(1));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 2);
        var ready = await store.RequestHostCliUpdateAsync("host-a", "operator", default);
        Assert.Equal(CliUpdateStates.Ready, ready.State);
        await store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Upgrading, "staging"), "runner-a", default);
        await store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Probing, "version, login, models"), "runner-a", default);
        var succeeded = await store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Succeeded, "probe passed"), "runner-a", default);

        Assert.Equal(CliUpdateStates.Succeeded, succeeded.State);
        Assert.Equal("open",
            Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).HostAdmission.AdmissionState);
    }

    [Fact]
    public async Task Failed_activation_records_failure_and_resumes_previous_installation()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await AdvertiseAsync(store, clock, activeSlots: 0);
        await store.RequestHostCliUpdateAsync("host-a", "operator", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Succeeded, "skipped probes"), "runner-a", default));
        await store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Upgrading, "staging"), "runner-a", default);

        var failed = await store.RecordHostCliUpdateResultAsync(
            "host-a", new(CliUpdateStates.Failed, "staged model probe failed; previous prefix retained"),
            "runner-a", default);

        Assert.Equal(CliUpdateStates.Failed, failed.State);
        Assert.Contains("previous prefix retained", failed.Detail);
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal("open", snapshot.HostAdmission.AdmissionState);
        Assert.Equal("0.144.1", Assert.Single(snapshot.InstalledClis!, item => item.Name == "codex").Version);
    }

    [Theory]
    [InlineData("0.153.0-alpha.1", "0.153.0", true)]
    [InlineData("0.153.0", "0.153.0-alpha.1", false)]
    [InlineData("0.154.0", "0.154.0", false)]
    public void Snapshot_version_policy_obeys_semver_prerelease_order(
        string installed,
        string target,
        bool below)
        => Assert.Equal(below, InstalledCliVersionPolicy.IsBelow(installed, target));

    [Fact]
    public async Task Model_minimum_and_delayed_drift_feed_events_are_deduplicated()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var publisher = new RecordingEventPublisher();
        var store = Store(temp.Path, clock, publisher);
        await store.InitializeAsync();

        await AdvertiseAsync(store, clock, activeSlots: 0);
        clock.Advance(TimeSpan.FromMinutes(1));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 2);

        var modelEvents = publisher.Messages
            .Where(item => item.Kind == "host.model-cli-version.blocked")
            .Select(item => JsonSerializer.Serialize(item.Payload))
            .ToArray();
        Assert.Equal(4, modelEvents.Length);
        Assert.Contains(modelEvents, payload => payload.Contains("gpt-6-astra needs codex-cli", StringComparison.Ordinal));
        Assert.Contains(modelEvents, payload => payload.Contains("gpt-6.1-sol needs codex-cli", StringComparison.Ordinal));
        Assert.Contains(modelEvents, payload => payload.Contains("gpt-6.1-terra needs codex-cli", StringComparison.Ordinal));
        Assert.Contains(modelEvents, payload => payload.Contains("host has 0.144.1", StringComparison.Ordinal));
        Assert.Contains(modelEvents, payload => payload.Contains("claude-opus-5 needs claude-cli", StringComparison.Ordinal));
        Assert.Contains(modelEvents, payload => payload.Contains("host has 2.1.202", StringComparison.Ordinal));
        Assert.DoesNotContain(publisher.Messages, item => item.Kind == "host.cli-drift.alarm");

        clock.Advance(TimeSpan.FromMinutes(58));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 3);
        Assert.DoesNotContain(publisher.Messages, item => item.Kind == "host.cli-drift.alarm");
        var before = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.All(before.Capabilities.Where(item => item.Category == "cli-execution"),
            item => Assert.Equal("healthy", item.HealthState));

        clock.Advance(TimeSpan.FromMinutes(2));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 4);
        clock.Advance(TimeSpan.FromMinutes(1));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 5);

        Assert.Equal(2, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));
        Assert.Equal(4, publisher.Messages.Count(item => item.Kind == "host.model-cli-version.blocked"));
        var overdue = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(2, overdue.Capabilities.Count(item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal)));
        Assert.All(overdue.Capabilities.Where(item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal)), item =>
        {
            Assert.Equal("suspect", item.HealthState);
            Assert.Contains("host-a", item.Reason);
            Assert.Contains("sudo /usr/local/sbin/agent-runner-deploy update-clis 0.154.0 2.1.269", item.Reason);
        });
        Assert.Equal("open", overdue.HostAdmission.AdmissionState);
        Assert.All(publisher.Messages.Where(item => item.Kind == "host.cli-drift.alarm"), item =>
            Assert.Contains("update-clis 0.154.0 2.1.269", JsonSerializer.Serialize(item.Payload)));

        clock.Advance(TimeSpan.FromMinutes(1));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 6, current: true);
        var recovered = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.DoesNotContain(recovered.Capabilities, item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal));
        Assert.All(recovered.Capabilities.Where(item => item.Category == "cli-execution"),
            item => Assert.Equal("healthy", item.HealthState));
    }

    [Fact]
    public async Task Persisted_drift_becomes_visible_after_restart_without_another_advertisement()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var original = Store(temp.Path, clock);
        await original.InitializeAsync();
        await AdvertiseAsync(original, clock, activeSlots: 0);
        clock.Advance(TimeSpan.FromMinutes(61));

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var snapshot = Assert.Single(await restarted.ListRunnerCapabilitySnapshotsAsync(default));
        var attention = snapshot.Capabilities.Where(item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, attention.Count);
        Assert.All(attention, item => Assert.Equal(Start.UtcDateTime, item.FirstFailureAt));
        Assert.All(snapshot.Capabilities.Where(item => item.Category == "cli-execution"),
            item => Assert.Equal("healthy", item.HealthState));
    }

    [Fact]
    public async Task New_target_starts_another_hour_of_grace_and_can_alarm_again()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var publisher = new RecordingEventPublisher();
        var original = Store(temp.Path, clock, publisher);
        await original.InitializeAsync();
        await AdvertiseAsync(original, clock, activeSlots: 0);
        clock.Advance(TimeSpan.FromMinutes(61));
        await AdvertiseAsync(original, clock, activeSlots: 0, generation: 2);
        Assert.Equal(2, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));

        var changed = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = temp.Path,
            CodexCliTargetVersion = "0.167.0",
            ClaudeCliTargetVersion = "2.1.306",
        }), clock, new ApplicationResultFinalizationSummaryGenerator(), publisher, null);
        await changed.InitializeAsync();
        var initial = Assert.Single(await changed.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.DoesNotContain(initial.Capabilities, item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal));
        await AdvertiseAsync(changed, clock, activeSlots: 0, generation: 3);
        Assert.Equal(2, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));
        clock.Advance(TimeSpan.FromMinutes(61));
        await AdvertiseAsync(changed, clock, activeSlots: 0, generation: 4);
        Assert.Equal(4, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));
    }

    [Fact]
    public async Task Current_identity_does_not_clear_drift_of_another_identity_on_the_same_host()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var publisher = new RecordingEventPublisher();
        var store = Store(temp.Path, clock, publisher);
        await store.InitializeAsync();
        await AdvertiseAsync(store, clock, activeSlots: 0);
        await AdvertiseAsync(store, clock, activeSlots: 0, current: true, runnerId: "runner-b");
        clock.Advance(TimeSpan.FromMinutes(61));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 2, current: true, runnerId: "runner-b");

        Assert.Equal(2, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));
        var snapshots = await store.ListRunnerCapabilitySnapshotsAsync(default);
        var outdated = Assert.Single(snapshots, item => item.RunnerId == "runner-a");
        Assert.Equal(2, outdated.Capabilities.Count(item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal)));
        var current = Assert.Single(snapshots, item => item.RunnerId == "runner-b");
        Assert.DoesNotContain(current.Capabilities, item => item.Key.StartsWith("host-upgrade:cli:", StringComparison.Ordinal));
    }

    private static TaskServerStore Store(string path, TimeProvider clock)
        => new(Options.Create(new TaskServerOptions
        {
            DataDirectory = path,
            CodexCliTargetVersion = "0.154.0",
            ClaudeCliTargetVersion = "2.1.269",
        }), clock);

    private static TaskServerStore Store(
        string path,
        TimeProvider clock,
        ITaskServerEventPublisher publisher)
        => new(Options.Create(new TaskServerOptions
        {
            DataDirectory = path,
            CodexCliTargetVersion = "0.154.0",
            ClaudeCliTargetVersion = "2.1.269",
        }), clock, new ApplicationResultFinalizationSummaryGenerator(), publisher, null);

    private static async Task AdvertiseAsync(
        TaskServerStore store,
        ManualTimeProvider clock,
        int activeSlots,
        long generation = 1,
        bool current = false,
        string runnerId = "runner-a")
    {
        var instanceId = runnerId == "runner-a" ? "instance-a" : "instance-b";
        if (generation == 1)
            await store.RegisterRunnerAsync(runnerId, new RegisterRunnerRequest(
                runnerId, "host-a", instanceId, "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), "runner-a", default);
        var now = clock.GetUtcNow().UtcDateTime;
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            runnerId, instanceId, CapabilityProtocol.CurrentSchemaVersion,
            now, 300, generation,
            [
                new(CapabilityProtocol.CodingExecutor, "executor"),
                new(CapabilityProtocol.CliExecution("codex"), "cli-execution", Version: current ? "0.166.0" : "0.144.1", Identity: "/usr/local/bin/codex"),
                new(CapabilityProtocol.CliExecution("claude"), "cli-execution", Version: current ? "2.1.305" : "2.1.202", Identity: "/usr/local/bin/claude"),
            ],
            new HostTelemetrySnapshotDto(now, 10, 0, 0, 0, 1, 2, 0, 0, 0, 0, 4, activeSlots)),
            "runner-a", default);
    }

    private sealed class RecordingEventPublisher : ITaskServerEventPublisher
    {
        public List<TaskServerOperationalEvent> Messages { get; } = [];

        public Task PublishAsync(TaskServerOperationalEvent message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
