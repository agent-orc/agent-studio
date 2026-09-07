using Xunit;

namespace AgentStudio.Tests;

public sealed class GateRunBudgetPolicyTests
{
    [Fact]
    public void NoSettingOrHistory_UsesSixtyMinuteOperatorDefault()
    {
        var budget = GateRunBudgetPolicy.Resolve(null, []);

        Assert.Equal(TimeSpan.FromMinutes(60), budget);
    }

    [Fact]
    public void ExplicitProjectSetting_WinsOverHistory()
    {
        var budget = GateRunBudgetPolicy.Resolve(75, [1_000, 2_000]);

        Assert.Equal(TimeSpan.FromMinutes(75), budget);
    }

    [Fact]
    public void History_UsesLatestTwentyRunP95PlusFiftyPercent()
    {
        var olderOutlier = (long)TimeSpan.FromMinutes(170).TotalMilliseconds;
        var latest = Enumerable.Range(1, 20)
            .Select(index => (long)TimeSpan.FromMinutes(index).TotalMilliseconds);

        var budget = GateRunBudgetPolicy.Resolve(
            null,
            new[] { olderOutlier }.Concat(latest));

        Assert.Equal(TimeSpan.FromMinutes(28.5), budget);
    }

    [Fact]
    public void MeasuredBudget_IsClampedToSafeBounds()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(GateRunBudgetPolicy.MinimumMinutes),
            GateRunBudgetPolicy.Resolve(null, [1_000]));
        Assert.Equal(
            TimeSpan.FromMinutes(GateRunBudgetPolicy.MaximumMinutes),
            GateRunBudgetPolicy.Resolve(null, [(long)TimeSpan.FromHours(4).TotalMilliseconds]));
    }
}
