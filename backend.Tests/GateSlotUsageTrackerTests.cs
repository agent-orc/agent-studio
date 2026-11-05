using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateSlotUsageTrackerTests
{
    [Fact]
    public void Lower_limit_keeps_a_running_gate_visible_in_its_old_slot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-slot-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RemoteGate:GateSlots"] = "5", ["RemoteGate:ProcessorCount"] = "6",
            }).Build();
            var capacity = new GateCapacitySettings(config, Path.Combine(root, "capacity.json"));
            var tracker = new GateSlotUsageTracker(config, capacity: capacity);
            tracker.Queued("run-1", "demo", "gate-1", GateDurationClass.Long);
            tracker.Acquired("run-1", 4, 5);
            tracker.RemoteWaiting(1, 5);
            capacity.Set(new GateCapacity(4, 6, 24, false), "operator-1");
            Assert.Equal(new GateSlotUsage(1, 5), tracker.ReportedOrLocalSnapshot());
            Assert.Equal(new GateSlotUsage(1, 4), tracker.Snapshot());
            var board = tracker.Board();
            Assert.Equal(4, board.Total);
            Assert.Equal("gate-1", board.Slots[4].GateId);
            tracker.Released("run-1");
            Assert.Equal(4, tracker.Board().Slots.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recent_host_report_overrides_process_local_slots_until_it_expires()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:GateSlots"] = "3",
        }).Build();
        var tracker = new GateSlotUsageTracker(config, clock);
        Assert.Equal(new GateSlotUsage(0, 3), tracker.ReportedOrLocalSnapshot());

        tracker.RemoteWaiting(1, 5);
        Assert.Equal(new GateSlotUsage(1, 5), tracker.ReportedOrLocalSnapshot());

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(new GateSlotUsage(0, 3), tracker.ReportedOrLocalSnapshot());
    }
}
