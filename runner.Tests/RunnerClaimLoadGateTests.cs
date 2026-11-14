using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerClaimLoadGateTests
{
    [Fact]
    public void Linux_host_core_count_ignores_process_cgroup_quota()
    {
        var count = HostTelemetrySampler.ParseLinuxHostCpuCount([
            "cpu 100 20 30 400", "cpu0 10 2 3 40", "cpu1 10 2 3 40",
            "cpu2 10 2 3 40", "cpu3 10 2 3 40", "intr 0",
        ]);

        Assert.Equal(4, count);
    }

    [Fact]
    public void Telemetry_preserves_claim_gate_snapshot_fields()
    {
        // clock-independent: this fixed instant is only serialized and compared to itself.
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var sample = new HostTelemetrySample(now, 80, 44.08, null, null,
            null, null, null, null, null, null, 8, 7);
        var gate = new ClaimLoadGateDto("closed", 5.51, 4, 702, now.AddMinutes(-12), 7);

        var wire = RunnerCapabilityProbe.Telemetry(sample, claimLoadGate: gate);
        var read = JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(JsonSerializer.Serialize(wire));

        Assert.Equal(gate, read?.ClaimLoadGate);
        Assert.Equal(7, read?.ActiveSlots);
    }
}
