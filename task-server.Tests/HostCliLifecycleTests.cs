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

        var modelEvent = Assert.Single(publisher.Messages,
            item => item.Kind == "host.model-cli-version.blocked");
        var modelPayload = JsonSerializer.Serialize(modelEvent.Payload);
        Assert.Contains("gpt-6-astra needs codex-cli", modelPayload);
        Assert.Contains("host has 0.144.1", modelPayload);
        Assert.DoesNotContain(publisher.Messages, item => item.Kind == "host.cli-drift.alarm");

        clock.Advance(TimeSpan.FromHours(24));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 3);
        clock.Advance(TimeSpan.FromMinutes(1));
        await AdvertiseAsync(store, clock, activeSlots: 0, generation: 4);

        Assert.Equal(2, publisher.Messages.Count(item => item.Kind == "host.cli-drift.alarm"));
        Assert.Equal(1, publisher.Messages.Count(item => item.Kind == "host.model-cli-version.blocked"));
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
        long generation = 1)
    {
        if (generation == 1)
            await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest(
                "runner-a", "host-a", "instance-a", "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), "runner-a", default);
        var now = clock.GetUtcNow().UtcDateTime;
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-a", "instance-a", CapabilityProtocol.CurrentSchemaVersion,
            now, 300, generation,
            [
                new(CapabilityProtocol.CodingExecutor, "executor"),
                new(CapabilityProtocol.CliExecution("codex"), "cli-execution", Version: "0.144.1", Identity: "/usr/local/bin/codex"),
                new(CapabilityProtocol.CliExecution("claude"), "cli-execution", Version: "2.1.202", Identity: "/usr/local/bin/claude"),
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
