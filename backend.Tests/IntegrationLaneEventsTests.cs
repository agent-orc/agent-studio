using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationLaneEventsTests
{
    // Injected clock: every event and the projection window read this instant.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Gate_wait_can_name_the_project_currently_holding_the_machine_slot()
    {
        var health = new PipelineHealthDetector();
        Assert.Null(health.HoldingProject());
        health.GateAcquired(new PipelineGateContext("run-1", "Agent Studio", "/repo", "AGT-1", Now.UtcDateTime));
        Assert.Equal("Agent Studio", health.HoldingProject());
    }

    [Fact]
    public void Typed_events_keep_sequence_and_attribute_wait_to_merge_lock()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lane-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const long sequence = 17;
            IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "enqueued",
                sequence: sequence, deliveredAtUtc: Now.AddMinutes(-5), atUtc: Now.AddMinutes(-4));
            IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "merge-lock-acquired",
                mergeLockWaitMs: 55000, holdingProject: "Agent Studio", atUtc: Now.AddMinutes(-3));
            IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "gate-finished",
                queueWaitMs: 0, collisionDetected: false, durationMs: 1000,
                level: "full", outcome: "Ok", atUtc: Now.AddMinutes(-1));

            var events = IntegrationLaneEvents.Read(folder);
            Assert.Equal(["enqueued", "merge-lock-acquired", "gate-finished"],
                events.Select(e => e.Kind));
            Assert.All(events, e => Assert.Equal(sequence, e.Sequence));
            Assert.Equal(55000, events[1].MergeLockWaitMs);
            Assert.Equal("Agent Studio", events[1].HoldingProject);
            Assert.Equal(0, events[2].QueueWaitMs);
            Assert.False(events[2].CollisionDetected);
            var row = GateEconomicsEndpoints.ProjectRow("Quality Studio", [folder],
                since: Now.AddDays(-7), now: Now, retest: null);
            Assert.Equal(55000, row.MergeLockWaitP95Ms);
            Assert.Equal(0, row.GateTokenWaitP95Ms);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
