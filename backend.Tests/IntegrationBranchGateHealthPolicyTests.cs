using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2819. The component-size gate was red on <c>develop</c> from 2026-08-19 to
/// 2026-09-14 and nothing said so. These tests pin the transition table that
/// turns one merge-base measurement per delivery into exactly one operator alert
/// per red transition.
/// </summary>
public sealed class IntegrationBranchGateHealthPolicyTests
{
    private const string Green = "1111111111111111111111111111111111111111";
    private const string Red = "2222222222222222222222222222222222222222";
    private const string LaterRed = "3333333333333333333333333333333333333333";

    [Theory]
    [InlineData(false, false, IntegrationBranchGateTransition.StillGreen, false)]
    [InlineData(false, true, IntegrationBranchGateTransition.TurnedRed, true)]
    [InlineData(true, true, IntegrationBranchGateTransition.StillRed, false)]
    [InlineData(true, false, IntegrationBranchGateTransition.Recovered, false)]
    public void Transitions_alert_only_on_the_green_to_red_edge(
        bool wasRed,
        bool isRed,
        IntegrationBranchGateTransition expected,
        bool shouldAlert)
    {
        var decision = IntegrationBranchGateHealthPolicy.Decide(
            Record(wasRed),
            Observation(isRed, isRed ? Red : Green));

        Assert.Equal(expected, decision.Transition);
        Assert.Equal(shouldAlert, decision.ShouldAlert);
    }

    [Fact]
    public void A_gate_never_seen_before_that_is_red_alerts_once()
    {
        var decision = IntegrationBranchGateHealthPolicy.Decide(previous: null, Observation(red: true, Red));

        Assert.Equal(IntegrationBranchGateTransition.TurnedRed, decision.Transition);
        Assert.True(decision.ShouldAlert);
        Assert.True(decision.Next.Red);
        Assert.Equal(Red, decision.Next.RedSinceSha);
        Assert.Null(decision.Next.LastGreenSha);
        Assert.Contains("verify-5", decision.Summary, StringComparison.Ordinal);
        Assert.Contains("No earlier green measurement", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alert_names_the_step_and_the_range_the_breaking_commit_lies_in()
    {
        var green = IntegrationBranchGateHealthPolicy.Decide(
            previous: null,
            Observation(red: false, Green));
        var turned = IntegrationBranchGateHealthPolicy.Decide(green.Next, Observation(red: true, Red));

        Assert.True(turned.ShouldAlert);
        Assert.Contains("verify-5", turned.Summary, StringComparison.Ordinal);
        Assert.Contains("npm --prefix frontend run lint", turned.Summary, StringComparison.Ordinal);
        Assert.Contains($"{Green[..12]}..{Red[..12]}", turned.Summary, StringComparison.Ordinal);
        Assert.Equal(Green, turned.Next.LastGreenSha);
    }

    [Fact]
    public void A_branch_that_stays_red_keeps_the_commit_it_first_turned_red_at()
    {
        var turned = IntegrationBranchGateHealthPolicy.Decide(previous: null, Observation(red: true, Red));
        var again = IntegrationBranchGateHealthPolicy.Decide(turned.Next, Observation(red: true, LaterRed));

        Assert.False(again.ShouldAlert);
        Assert.Equal(IntegrationBranchGateTransition.StillRed, again.Transition);
        Assert.Equal(Red, again.Next.RedSinceSha);
        Assert.Contains(Red[..12], again.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gate that simply passed on the delivery never ran on the merge base, so
    /// it clears a stale red flag without claiming to have measured the branch.
    /// Naming a delivery commit as the branch's last green would mislead the
    /// operator reading the next alert's range.
    /// </summary>
    [Fact]
    public void An_unmeasured_green_clears_the_flag_without_naming_a_branch_commit()
    {
        var red = IntegrationBranchGateHealthPolicy.Decide(previous: null, Observation(red: true, Red));

        var cleared = IntegrationBranchGateHealthPolicy.Decide(
            red.Next,
            Observation(red: false, mergeBaseSha: null));

        Assert.Equal(IntegrationBranchGateTransition.Recovered, cleared.Transition);
        Assert.False(cleared.Next.Red);
        Assert.Null(cleared.Next.RedSinceSha);
        Assert.Null(cleared.Next.LastGreenSha);
    }

    [Fact]
    public void A_cleared_flag_lets_a_later_regression_alert_again()
    {
        var red = IntegrationBranchGateHealthPolicy.Decide(previous: null, Observation(red: true, Red));
        var cleared = IntegrationBranchGateHealthPolicy.Decide(
            red.Next,
            Observation(red: false, mergeBaseSha: null));

        var again = IntegrationBranchGateHealthPolicy.Decide(cleared.Next, Observation(red: true, LaterRed));

        Assert.True(again.ShouldAlert);
        Assert.Equal(LaterRed, again.Next.RedSinceSha);
    }

    private static IntegrationBranchGateRecord Record(bool red) => new(
        "refs/heads/develop",
        "verify-5",
        red,
        "sh -lc npm --prefix frontend run lint",
        RedSinceSha: red ? Red : null);

    private static IntegrationBranchGateObservation Observation(bool red, string? mergeBaseSha) => new(
        "refs/heads/develop",
        "verify-5",
        "npm --prefix frontend run lint",
        red,
        mergeBaseSha,
        new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc));
}
