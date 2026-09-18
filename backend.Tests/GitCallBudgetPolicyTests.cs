using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The rule that bounds a file-history lookup as a whole rather than one git
/// call at a time.
///
/// <para>A lookup spends three to four git calls per commit it reports, so a
/// fixed per-call timeout bounds the request at that timeout times an unknown
/// commit count. When that product passed the HTTP client's own deadline the
/// client gave up first and the server logged an aborted request instead of
/// answering (AGT-2867). The matrix below pins the replacement: one budget for
/// the whole lookup, a cap per call inside it, and a hard stop once it is spent.
/// <see cref="TaskFileHistoryEndpointsTests"/> then shows an endpoint honouring
/// it.</para>
/// </summary>
public class GitCallBudgetPolicyTests
{
    private static readonly TimeSpan Total = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PerCall = TimeSpan.FromSeconds(10);

    [Theory]
    // Plenty of budget left: the per-call cap is what limits the call, so one
    // wedged git cannot eat the whole lookup.
    [InlineData(0, 10)]
    [InlineData(5, 10)]
    [InlineData(19_999, 10)]
    // Less than one cap left: the call gets exactly the remainder, so the sum of
    // the calls never exceeds the total.
    [InlineData(20_000, 10)]
    [InlineData(25_000, 5)]
    [InlineData(29_999, 0.001)]
    // Spent: zero means "do not start this call at all".
    [InlineData(30_000, 0)]
    [InlineData(30_001, 0)]
    [InlineData(600_000, 0)]
    public void NextCallTimeout_SpendsTheTotalBudgetWithoutExceedingThePerCallCap(
        double elapsedMs, double expectedSeconds)
    {
        var timeout = GitCallBudgetPolicy.NextCallTimeout(
            TimeSpan.FromMilliseconds(elapsedMs), Total, PerCall);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Fact]
    public void DefaultBudget_LeavesTheServerAheadOfAnyReasonableClientDeadline()
    {
        // The whole point of the change: the two deadlines are ordered, not
        // racing. A lookup that runs long must produce a reported git failure
        // from the server, never a client abort, so the total has to stay well
        // below the timeout callers are told to allow.
        Assert.True(
            GitCallBudgetPolicy.DefaultTotalBudget < TaskFileHistoryEndpointsTests.ClientTimeout,
            "The server budget must expire before the client's timeout.");
        Assert.True(
            GitCallBudgetPolicy.DefaultPerCallCap <= GitCallBudgetPolicy.DefaultTotalBudget,
            "A single call may not be allowed more than the whole lookup.");
    }

    [Fact]
    public void ABudgetInstance_StartsWithAFullPerCallAllowance()
    {
        var budget = new GitCallBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var first = budget.NextCallTimeout();

        Assert.True(first > TimeSpan.Zero);
        Assert.True(first <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AnExhaustedBudget_RefusesToStartAnotherCall()
    {
        // Total already zero, so the very first call is over budget.
        var budget = new GitCallBudget(TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, budget.NextCallTimeout());
    }
}
