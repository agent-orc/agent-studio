using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3954: the place a host runs in and its network rates travel in the
/// telemetry and are served unchanged by <c>GET /api/v1/management/remote-hosts</c>,
/// which the structure view reads. A host that reports neither still lists.
/// </summary>
public sealed class HostPlacementReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Remote_host_report_carries_placement_network_and_round_trip_and_older_hosts_still_list()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var now = Start.UtcDateTime;
        foreach (var runner in new[] { "runner-new", "runner-old" })
            await store.RegisterRunnerAsync(runner, new RegisterRunnerRequest(
                runner, "host-" + runner, "instance", "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), runner, default);
        var placement = new HostPlacementDto(HostPlacementKinds.Container, "review-host-1", "linux-vm",
            [new("home-lab", HostPlacementKinds.Physical), new("linux-vm", HostPlacementKinds.Vm)],
            "docker", "0123456789ab", "role-host", "kvm");

        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-new", "instance", CapabilityProtocol.CurrentSchemaVersion, now, 300, 1,
            [new(CapabilityProtocol.CodingExecutor, "executor")],
            new HostTelemetrySnapshotDto(now, 10, 0, 0, 0, 1, 2, 0, 0, 0, 0, 4, 0, Placement: placement,
                NetworkReceiveBytesPerSecond: 1200, NetworkTransmitBytesPerSecond: 3400, TaskServerRoundTripMs: 4.5,
                Containers: [new("stack-api-1", "sha256:abc", "running", "0123456789ab")])),
            "runner-new", default);
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-old", "instance", CapabilityProtocol.CurrentSchemaVersion, now, 300, 1,
            [new(CapabilityProtocol.CodingExecutor, "executor")],
            new HostTelemetrySnapshotDto(now, 10, 0, 0, 0, 1, 2, 0, 0, 0, 0, 4, 0)),
            "runner-old", default);

        var snapshots = await store.ListRunnerCapabilitySnapshotsAsync(default);
        var reported = snapshots.Single(item => item.RunnerId == "runner-new").Telemetry!;
        Assert.Equal(placement with { Ancestors = null }, reported.Placement! with { Ancestors = null });
        Assert.Equal(placement.Ancestors, reported.Placement!.Ancestors);
        Assert.Equal((1200L, 3400L, 4.5), (reported.NetworkReceiveBytesPerSecond, reported.NetworkTransmitBytesPerSecond,
            reported.TaskServerRoundTripMs));
        Assert.Equal("0123456789ab", snapshots.Single(item => item.RunnerId == "runner-new").Containers!.Single().Id);

        var older = snapshots.Single(item => item.RunnerId == "runner-old").Telemetry!;
        Assert.Null(older.Placement);
        Assert.Null(older.NetworkReceiveBytesPerSecond);

        // A payload written before these fields existed still reads.
        var legacy = JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(
            """{"ObservedAt":"2026-11-17T12:00:00Z","CpuPercent":1,"CpuCores":2,"ActiveSlots":0}""");
        Assert.Null(legacy!.Placement);
        Assert.Null(JsonSerializer.Deserialize<HostContainerSummaryDto>("""{"Name":"a","ImageDigest":null,"State":"running"}""")!.Id);
    }
}
