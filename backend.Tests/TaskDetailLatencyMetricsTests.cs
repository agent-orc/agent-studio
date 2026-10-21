using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskDetailLatencyMetricsTests
{
    [Fact]
    public void Snapshot_ReportsRecentCardReadQuantiles_AndExpiresOldSamples()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
        var metrics = new TaskDetailLatencyMetrics(clock);
        for (var i = 1; i <= 100; i++) metrics.Observe(i);

        Assert.Equal(new TaskDetailLatencySnapshot(100, 50, 95, 100), metrics.Snapshot());

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(new TaskDetailLatencySnapshot(0, null, null, null), metrics.Snapshot());
    }
}
