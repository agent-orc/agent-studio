using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2749 item 3. The fixed 30-minute pre-develop budget parked AGT-2707 and
/// AGT-2710 on 2026-09-06 after their suite consumed 1800488 ms against a
/// 1800000 ms limit: 488 milliseconds over on an overloaded host.
/// </summary>
public sealed class GateRunBudgetPolicyTests
{
    [Fact]
    public void NoSettingAndNoHistory_UsesTheSixtyMinuteDefault()
        => Assert.Equal(TimeSpan.FromMinutes(60), GateRunBudgetPolicy.Resolve(null));

    [Fact]
    public void TheDefaultWouldHaveCoveredTheRecordedOverrun()
    {
        var recordedConsumed = TimeSpan.FromMilliseconds(1_800_488);

        Assert.True(GateRunBudgetPolicy.Resolve(null) > recordedConsumed);
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(5, 5)]
    // Clamped at both ends so a typo cannot disable or hang the gate.
    [InlineData(1, GateRunBudgetPolicy.MinimumMinutes)]
    [InlineData(9_999, GateRunBudgetPolicy.MaximumMinutes)]
    public void ExplicitProjectSettingWinsAndIsClamped(int configured, int expectedMinutes)
        => Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            GateRunBudgetPolicy.Resolve(configured));

    [Fact]
    public void SlowHistoryRaisesTheBudgetToP95PlusHalf()
    {
        // Twenty runs; the slowest is 50 minutes, so p95 (nearest rank 19) is
        // 48 minutes and the budget becomes 72.
        var history = Enumerable.Range(1, 18)
            .Select(_ => TimeSpan.FromMinutes(10))
            .Append(TimeSpan.FromMinutes(48))
            .Append(TimeSpan.FromMinutes(50))
            .ToArray();

        Assert.Equal(TimeSpan.FromMinutes(72), GateRunBudgetPolicy.Resolve(null, history));
    }

    [Fact]
    public void FastHistoryNeverTightensTheBudgetBelowTheDefault()
    {
        var history = Enumerable.Repeat(TimeSpan.FromMinutes(2), 20);

        Assert.Equal(GateRunBudgetPolicy.Default, GateRunBudgetPolicy.Resolve(null, history));
    }

    [Fact]
    public void OnlyTheMostRecentWindowCountsAndNonPositiveEntriesAreIgnored()
    {
        var history = Enumerable.Repeat(TimeSpan.FromMinutes(100), GateRunBudgetPolicy.HistoryWindow + 10)
            .Append(TimeSpan.Zero)
            .Append(TimeSpan.FromMinutes(-5));

        var budget = GateRunBudgetPolicy.Resolve(null, history);

        Assert.Equal(TimeSpan.FromMinutes(150), budget);
    }

    [Fact]
    public void EmptyHistoryIsTreatedAsNoHistory()
        => Assert.Equal(GateRunBudgetPolicy.Default, GateRunBudgetPolicy.Resolve(null, []));
}
