using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RunnerReleaseIdentityStoreTests
{
    private static readonly RunnerReleaseIdentityDto Old = new(
        "agt-host-20260823T060000Z-bbbbbbb",
        "0.2.7",
        "bbbbbbb2222",
        new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc));

    private static readonly RunnerReleaseIdentityDto New = new(
        "agt-host-20260911T080000Z-aaaaaaa",
        "0.3.0",
        "aaaaaaa1111",
        new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Same_instance_registration_without_release_keeps_last_known_identity()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-1", release: null);

        Assert.Equal(Old, Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    [Fact]
    public async Task Replacement_instance_without_release_clears_previous_identity()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-2", release: null);

        Assert.Null(Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    [Fact]
    public async Task Replacement_instance_uses_the_release_it_reports()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-2", New);

        Assert.Equal(New, Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    [Fact]
    public async Task Optional_container_summary_survives_store_restart_and_older_reports()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var store = await CreateStoreAsync(temp.Path, clock);
        await RegisterAsync(store, "instance-1", New);
        var now = clock.GetUtcNow().UtcDateTime;
        var telemetry = new HostTelemetrySnapshotDto(now, null, null, null, null,
            null, null, null, null, null, null, 0, 0,
            Containers: [new("Gate", "sha256:abc", "running")]);
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-1", "instance-1", 2, now, 180, 1,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
            Telemetry: telemetry), "test", default);

        var reopened = await CreateStoreAsync(temp.Path, clock);
        Assert.Equal("Gate", Assert.Single(Assert.Single(
            await reopened.ListRunnerCapabilitySnapshotsAsync(default)).Containers!).Name);
        await reopened.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-1", "instance-1", 2, now.AddSeconds(1), 180, 2,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
            Telemetry: telemetry with { ObservedAt = now.AddSeconds(1), Containers = null }),
            "test", default);
        Assert.Null(Assert.Single(await reopened.ListRunnerCapabilitySnapshotsAsync(default)).Containers);
    }

    private static async Task<TaskServerStore> CreateStoreAsync(string dataDirectory, TimeProvider? clock = null)
    {
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            clock ?? TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }

    private static Task RegisterAsync(
        TaskServerStore store,
        string instanceId,
        RunnerReleaseIdentityDto? release)
        => store.RegisterRunnerAsync(
            "runner-1",
            new RegisterRunnerRequest(
                "runner-1",
                "host-1",
                instanceId,
                release?.ReleaseId ?? "unknown",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor],
                Release: release),
            "test",
            default);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
