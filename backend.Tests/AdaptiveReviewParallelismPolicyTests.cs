using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AdaptiveReviewParallelismPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdaptiveReviewParallelismOptions Options = AdaptiveReviewParallelismOptions.Default;

    [Theory]
    // depth, current, lastChangeMinutesAgo, expectedAction, expectedTarget
    [InlineData(5, 2, null, ReviewParallelismAction.Raise, 3)]
    [InlineData(4, 2, null, ReviewParallelismAction.Hold, 2)]
    [InlineData(10, 6, null, ReviewParallelismAction.Hold, 6)]
    [InlineData(5, 2, 2d, ReviewParallelismAction.Hold, 2)]
    [InlineData(5, 2, 5d, ReviewParallelismAction.Raise, 3)]
    public void Evaluate_RaisesOneStepAtATimeWithinCooldownAndSanctionedMax(
        int queueDepth,
        int current,
        double? lastChangeMinutesAgo,
        ReviewParallelismAction expectedAction,
        int expectedTarget)
    {
        var lastChangeAt = lastChangeMinutesAgo is { } minutes ? Now.AddMinutes(-minutes) : (DateTime?)null;

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            current, queueDepth, isStagnant: false, Now, lastChangeAt, queueEmptySinceUtc: null, Options);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedTarget, decision.RecommendedParallelism);
    }

    [Fact]
    public void Evaluate_StagnationRaisesEvenBelowTheDepthThreshold()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 1,
            isStagnant: true,
            Now,
            lastChangeAtUtc: null,
            queueEmptySinceUtc: null,
            Options);

        Assert.Equal(ReviewParallelismAction.Raise, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("stagnant", decision.Reason);
    }

    [Theory]
    // emptySinceMinutesAgo, lastChangeMinutesAgo, expectedAction, expectedTarget
    [InlineData(15, 20, ReviewParallelismAction.Lower, 3)]
    [InlineData(5, 20, ReviewParallelismAction.Hold, 4)]
    [InlineData(15, 5, ReviewParallelismAction.Hold, 4)]
    public void Evaluate_LowersOnlyAfterSustainedEmptyQueueAndCooldown(
        double emptySinceMinutesAgo,
        double lastChangeMinutesAgo,
        ReviewParallelismAction expectedAction,
        int expectedTarget)
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 4,
            queueDepth: 0,
            isStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-lastChangeMinutesAgo),
            queueEmptySinceUtc: Now.AddMinutes(-emptySinceMinutesAgo),
            Options);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedTarget, decision.RecommendedParallelism);
    }

    /// <summary>
    /// AGT-2848: a raised configured baseline must reach the running
    /// recommendation on the next refresh while the queue is non-empty,
    /// ahead of (and unblocked by) the ordinary raise cooldown.
    /// </summary>
    [Fact]
    public void Evaluate_AdoptsARaisedBaselineOnTheNextRefreshWhenTheQueueIsNonEmpty()
    {
        var options = Options with { BaselineParallelism = 3 };

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 1,
            isStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddSeconds(-1),
            queueEmptySinceUtc: null,
            options);

        Assert.Equal(ReviewParallelismAction.Raise, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("baseline", decision.Reason);
    }

    [Fact]
    public void Evaluate_DoesNotAdoptARaisedBaselineWhileTheQueueIsEmpty()
    {
        var options = Options with { BaselineParallelism = 3 };

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 0,
            isStagnant: false,
            Now,
            lastChangeAtUtc: null,
            queueEmptySinceUtc: Now,
            options);

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
    }

    [Fact]
    public void Evaluate_ClampsAnAdoptedBaselineToTheSanctionedMax()
    {
        var options = Options with { BaselineParallelism = 9 };

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 1,
            isStagnant: false,
            Now,
            lastChangeAtUtc: null,
            queueEmptySinceUtc: null,
            options);

        Assert.Equal(ReviewParallelismAction.Raise, decision.Action);
        Assert.Equal(AdaptiveReviewParallelismPolicy.SanctionedMax, decision.RecommendedParallelism);
    }

    [Fact]
    public void Evaluate_NeverLowersBelowTheConfiguredBaseline()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: Options.BaselineParallelism,
            queueDepth: 0,
            isStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-30),
            queueEmptySinceUtc: Now.AddMinutes(-30),
            Options);

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(Options.BaselineParallelism, decision.RecommendedParallelism);
    }

    [Fact]
    public void Evaluate_RefusesRaiseBeyondTheAdvertisedPlaneBudget()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 9,
            isStagnant: true,
            Now,
            lastChangeAtUtc: null,
            queueEmptySinceUtc: null,
            Options,
            Plane(planeCores: 4, durationSeconds: 900, throttledShare: 0.02));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("raise refused: plane 400% supports 2 workers at 2 cores each", decision.Reason);
    }

    [Fact]
    public void Evaluate_WithdrawsRaiseWhenDurationRegressesUnderThrottling()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 3,
            queueDepth: 8,
            isStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-2),
            queueEmptySinceUtc: null,
            Options,
            Plane(planeCores: 8, durationSeconds: 1_200, throttledShare: 0.35),
            durationBeforeLastRaiseSeconds: 900);

        Assert.Equal(ReviewParallelismAction.Lower, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("raise withdrawn", decision.Reason);
        Assert.Contains("throttled share", decision.Reason);
    }

    [Fact]
    public void Evaluate_ClosesThePlaneWhenQuotaCannotSupplyTheDocumentedMinimum()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 1,
            queueDepth: 9,
            isStagnant: true,
            Now,
            lastChangeAtUtc: null,
            queueEmptySinceUtc: null,
            Options,
            Plane(planeCores: 1, durationSeconds: 900, throttledShare: 0.20));

        Assert.Equal(ReviewParallelismAction.Lower, decision.Action);
        Assert.Equal(0, decision.RecommendedParallelism);
        Assert.Contains("supports 0 workers", decision.Reason);
    }

    private static ReviewPlaneBudgetDto Plane(
        double planeCores,
        double durationSeconds,
        double throttledShare)
        => new(
            Now,
            $"{planeCores * 100_000:0} 100000",
            planeCores,
            planeCores * 100,
            HostCores: 12,
            WorkerEnvelopeCores: 2.4,
            WorkerEnvelopeCpuQuotaPercent: 480,
            CurrentCeiling: 3,
            durationSeconds,
            throttledShare,
            SustainedThrottling: throttledShare >= 0.1);
}
