using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateSlotUsageTrackerTests
{
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

        tracker.RemoteWaiting(1, 3);
        Assert.Equal(new GateSlotUsage(1, 3), tracker.ReportedOrLocalSnapshot());

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(new GateSlotUsage(0, 3), tracker.ReportedOrLocalSnapshot());
    }
}
