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
            // The runner's events carry the coordinator's delivery scope.
            using (IntegrationLaneEvents.BeginDelivery(sequence))
            {
                IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "merge-lock-acquired",
                    mergeLockWaitMs: 55000, holdingProject: "Agent Studio", atUtc: Now.AddMinutes(-3));
                IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "gate-finished",
                    queueWaitMs: 0, collisionDetected: false, durationMs: 1000,
                    level: "full", outcome: "Ok", atUtc: Now.AddMinutes(-1));
            }
            // Outside any delivery (a local merge) nothing borrows sequence 17.
            IntegrationLaneEvents.Append(folder, "Quality Studio", "QS-1", "published",
                atUtc: Now);
            Assert.Null(IntegrationLaneEvents.CurrentSequence);

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

    [Fact]
    public void Batch_deliveries_produce_lag_queue_and_throughput_like_coordinator_deliveries()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lane-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            void Event(long sequence, string kind, int minutesAgo, string? outcome = null,
                long? durationMs = null, DateTimeOffset? deliveredAtUtc = null)
                => IntegrationLaneEvents.Append(folder, "Agent Studio", "AGT-1", kind,
                    sequence: sequence, deliveredAtUtc: deliveredAtUtc, outcome: outcome,
                    durationMs: durationMs, atUtc: Now.AddMinutes(-minutesAgo));

            // Batch member 1: enqueued, batched, gated green and published.
            Event(1, "enqueued", 120, deliveredAtUtc: Now.AddMinutes(-125));
            Event(1, "dequeued", 60);
            Event(1, "gate-requested", 55);
            Event(1, "gate-finished", 10, outcome: "Ok", durationMs: 45 * 60_000);
            Event(1, "published", 5, outcome: "pushed");
            // Batch member 2: conflict-ejected, handed to the per-task fallback,
            // then integrated by the coordinator under its own sequence.
            Event(2, "enqueued", 100, deliveredAtUtc: Now.AddMinutes(-100));
            Event(2, "ejected", 58, outcome: "Conflict");
            Event(2, "handed-off", 30);
            Event(900, "enqueued", 30, deliveredAtUtc: Now.AddMinutes(-100));
            Event(900, "published", 1);
            // Batch member 3: still waiting after its batch was abandoned.
            Event(3, "enqueued", 50, deliveredAtUtc: Now.AddMinutes(-50));
            Event(3, "requeued", 20);

            var row = GateEconomicsEndpoints.ProjectRow("Agent Studio", [folder],
                since: Now.AddDays(-7), now: Now, retest: null);

            Assert.Equal(109.5, row.DeliveryLagP50Minutes);
            Assert.Equal(50, row.OldestWaitingMinutes);
            Assert.Equal(1, row.Queued);
            Assert.Equal(0, row.Gating + row.ReadyToPublish + row.Prepared);
            Assert.Equal(5, row.PreparationP50Minutes);
            Assert.Equal(45, row.LaneGateDurationP50Minutes);
            Assert.Equal(1, row.EjectedConflict);
            Assert.Equal(3 / 24d, row.ArrivalsPerHour);
            Assert.Equal(2 / 24d, row.MergesPerHour);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
