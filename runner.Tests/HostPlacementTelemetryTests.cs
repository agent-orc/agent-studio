using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>AGT-3954: placement and network rates travel in the host telemetry of every role.</summary>
public sealed class HostPlacementTelemetryTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Placement_and_network_rates_travel_in_host_telemetry()
    {
        var placement = new HostPlacementDto(HostPlacementKinds.Container, "review-host-1", "linux-vm",
            [new("home-lab", HostPlacementKinds.Physical), new("linux-vm", HostPlacementKinds.Vm)], "docker", "0123456789ab");
        var sample = new HostTelemetrySample(Now, null, null, null, null, null, null, null, null, null, null, 4, 0,
            Placement: placement, NetworkReceiveBytesPerSecond: 1200, NetworkTransmitBytesPerSecond: 3400);

        var roundTrip = JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(
            JsonSerializer.Serialize(RunnerCapabilityProbe.Telemetry(sample)))!;

        Assert.Equal(("container", "review-host-1", "linux-vm"),
            (roundTrip.Placement!.Kind, roundTrip.Placement.ContainerName, roundTrip.Placement.ParentMachineId));
        Assert.Equal(placement.Ancestors, roundTrip.Placement.Ancestors);
        Assert.Equal((1200L, 3400L), (roundTrip.NetworkReceiveBytesPerSecond, roundTrip.NetworkTransmitBytesPerSecond));
        // A sample of an older sampler carries neither and still maps.
        var bare = RunnerCapabilityProbe.Telemetry(new HostTelemetrySample(Now, null, null, null, null, null, null, null, null, null, null, 4, 0))!;
        Assert.Null(bare.Placement);
        Assert.Null(bare.NetworkReceiveBytesPerSecond);
    }

    [Fact]
    public void Sampler_reports_where_the_daemon_runs_with_the_configured_enclosing_machines()
    {
        if (!OperatingSystem.IsLinux()) return;
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost", RunnerId = "r", RunnerName = "r", Hostname = "role-host", BackendName = "b",
            WorkDir = Path.GetTempPath(), BaseBranch = "main", ParentMachine = "home-lab/linux-vm",
            ContainerName = "review-host-1",
        };
        var sampler = new HostTelemetrySampler(options, FixedHostFilesystemProbe.Healthy);

        var placement = sampler.SampleNow(0).Placement!;

        Assert.Contains(placement.Kind, new[] { HostPlacementKinds.Container, HostPlacementKinds.Vm,
            HostPlacementKinds.Physical, HostPlacementKinds.Unknown });
        Assert.Equal("linux-vm", placement.ParentMachineId);
        Assert.Equal(["home-lab", "linux-vm"], placement.Ancestors!.Select(item => item.Id));
        Assert.Equal("role-host", placement.MachineName);
        Assert.Equal(placement.Kind == HostPlacementKinds.Container ? "review-host-1" : null, placement.ContainerName);
        var second = sampler.SampleNow(0);
        Assert.True(second.NetworkReceiveBytesPerSecond is null or >= 0);
    }
}
