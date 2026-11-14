using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed instants and an explicit now are the only inputs to the pure threshold.
public sealed class BatchGateRunningWatchdogTests
{
    [Fact]
    public void Running_without_a_flight_alerts_only_after_gate_timeout_and_lease_ttl()
    {
        var started = new DateTimeOffset(2026, 11, 9, 21, 11, 0, TimeSpan.Zero);
        var threshold = started.AddMinutes(152);
        Assert.False(BatchGatePilotService.IsStaleRunning(BatchPhase.Running, started, threshold, false, 150));
        Assert.True(BatchGatePilotService.IsStaleRunning(BatchPhase.Running, started,
            threshold.AddTicks(1), false, 150));
        Assert.False(BatchGatePilotService.IsStaleRunning(BatchPhase.Running, started,
            threshold.AddHours(1), true, 150));
        Assert.False(BatchGatePilotService.IsStaleRunning(BatchPhase.Published, started,
            threshold.AddHours(1), false, 150));
    }
}
