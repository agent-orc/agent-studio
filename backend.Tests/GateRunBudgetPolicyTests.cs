using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2749: the gate-run budget must default to something sane (60 minutes),
/// let a project override it outright, and size itself off measured history
/// once enough of it exists.
/// </summary>
public sealed class GateRunBudgetPolicyTests
{
    [Fact]
    public void ResolveSeconds_WithNoOverrideOrHistory_UsesTheSixtyMinuteDefault()
        => Assert.Equal(
            GateRunBudgetDefaults.DefaultSeconds,
            GateRunBudgetPolicy.ResolveSeconds(projectOverrideSeconds: null));

    [Fact]
    public void ResolveSeconds_ProjectOverride_WinsOutrightOverHistory()
    {
        var history = Enumerable.Repeat(100.0, 30).ToList();

        Assert.Equal(120, GateRunBudgetPolicy.ResolveSeconds(120, history));
    }

    [Fact]
    public void ResolveSeconds_WithFewerThanTwentySamples_IgnoresHistory()
    {
        var history = Enumerable.Repeat(9000.0, 19).ToList();

        Assert.Equal(
            GateRunBudgetDefaults.DefaultSeconds,
            GateRunBudgetPolicy.ResolveSeconds(null, history));
    }

    [Fact]
    public void ResolveSeconds_WithTwentyOrMoreSamples_SizesFromP95PlusMargin()
    {
        // 1..100 seconds, uniform: p95 (nearest-rank) is the 95th value = 95.
        var history = Enumerable.Range(1, 100).Select(v => (double)v).ToList();

        var resolved = GateRunBudgetPolicy.ResolveSeconds(null, history);

        Assert.Equal((int)Math.Ceiling(95 * GateRunBudgetDefaults.HistoryMargin), resolved);
    }

    [Fact]
    public void ResolveSeconds_HistoryOrderDoesNotMatter()
    {
        var ascending = Enumerable.Range(1, 20).Select(v => (double)v).ToList();
        var shuffled = ascending.OrderByDescending(v => v).ToList();

        Assert.Equal(
            GateRunBudgetPolicy.ResolveSeconds(null, ascending),
            GateRunBudgetPolicy.ResolveSeconds(null, shuffled));
    }
}
