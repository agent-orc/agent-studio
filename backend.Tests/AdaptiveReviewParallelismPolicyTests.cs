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
    [InlineData(5, 2, null, ReviewParallelismAction.Hold, 2)]
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
            current, queueDepth, isStagnant: false, Now, lastChangeAt, queueEmptySinceUtc: null, Options,
            Plane(12, 900, 0.02), hostTelemetry: Host(load1: 4, memoryUsedGb: 20));

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedTarget, decision.RecommendedParallelism);
    }

    [Fact]
    public void Evaluate_StagnationBelowTheDepthThresholdDoesNotRaise()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            currentRecommendation: 2,
            queueDepth: 1,
            isStagnant: true,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-20),
            queueEmptySinceUtc: null,
            Options,
            Plane(12, 900, 0.02),
            hostTelemetry: Host(load1: 4, memoryUsedGb: 20));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("stagnant at depth 1, below the raise threshold (5)", decision.Reason);
    }

    /// <summary>
    /// AGT-3073 replay of 12.10.2026: Stable ran baseline 3 with a raise
    /// threshold of 1000 and no RaiseOnStagnation key. A stagnant queue at depth
    /// 2-3 ratcheted the advisor 3 -> 6 even though the host had headroom by
    /// every plane-budget measure (a 12-core plane is never throttled).
    /// </summary>
    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public void Evaluate_StableConfigurationNeverRaisesAStagnantShallowQueue(int current, int queueDepth)
    {
        var stable = Options with { BaselineParallelism = 3, RaiseQueueDepthThreshold = 1_000 };

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            current, queueDepth, true, Now, Now.AddMinutes(-30), null, stable,
            Plane(12, 900, 0.0), hostTelemetry: Host(load1: 4, memoryUsedGb: 20));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(current, decision.RecommendedParallelism);
        Assert.Contains("below the raise threshold (1000)", decision.Reason);
    }

    [Fact]
    public void Evaluate_IncidentHostLowersTowardTheBaselineWithoutPlaneThrottling()
    {
        var stable = Options with { BaselineParallelism = 3, RaiseQueueDepthThreshold = 1_000 };

        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            6, 3, true, Now, Now.AddMinutes(-20), null, stable,
            Plane(12, 900, 0.0), hostTelemetry: Host(load1: 118, memoryUsedGb: 56));

        Assert.Equal(ReviewParallelismAction.Lower, decision.Action);
        Assert.Equal(5, decision.RecommendedParallelism);
        Assert.Contains("host saturated: load/core", decision.Reason);
        Assert.Contains("memory available", decision.Reason);
    }

    [Fact]
    public void Evaluate_RaiseWithoutAFreshPlaneBudgetIsRefused()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            2, 9, false, Now, Now.AddMinutes(-20), null, Options);

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Equal("raise refused: no fresh review plane budget", decision.Reason);
    }

    [Fact]
    public void Evaluate_RaiseInsideTheCooldownSaysWhy()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            2, 9, false, Now, Now.AddMinutes(-2), null, Options, Plane(12, 900, 0.02));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Contains("waiting for raise cooldown", decision.Reason);
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
            options,
            Plane(12, 900, 0.02));

        Assert.Equal(ReviewParallelismAction.Raise, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("baseline", decision.Reason);
    }

    [Fact]
    public void Evaluate_ARaisedBaselineWaitsForAFreshPlaneBudget()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            2, 1, false, Now, Now.AddMinutes(-20), null, Options with { BaselineParallelism = 3 });

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("no fresh review plane budget", decision.Reason);
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
            options,
            Plane(12, 900, 0.02));

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
            lastChangeAtUtc: Now.AddMinutes(-20),
            queueEmptySinceUtc: null,
            Options,
            Plane(planeCores: 8, durationSeconds: 1_200, throttledShare: 0.35),
            durationAtBaselineSeconds: 900);

        Assert.Equal(ReviewParallelismAction.Lower, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("host saturated", decision.Reason);
        Assert.Contains("throttled share", decision.Reason);
    }

    [Theory]
    // throttledShare, rollingSeconds, referenceSeconds, load1, memoryUsedGb, expectedSignal
    [InlineData(0.35, 900, 900, 4, 20, "throttled share")]
    [InlineData(0.02, 1_200, 900, 4, 20, "duration grew")]
    [InlineData(0.0, 900, 900, 12, 20, "load/core")]
    [InlineData(0.0, 900, 900, 4, 56, "memory available")]
    public void Evaluate_StagnantQueueHoldsWhenHostIsSaturated(
        double throttledShare,
        double durationSeconds,
        double referenceSeconds,
        double load1,
        long memoryUsedGb,
        string expectedSignal)
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 2, true, Now, Now.AddMinutes(-20), null,
            Options with { BaselineParallelism = 3, RaiseQueueDepthThreshold = 2 },
            Plane(12, durationSeconds, throttledShare), referenceSeconds, Host(load1, memoryUsedGb));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("stagnant but host saturated", decision.Reason);
        Assert.Contains(expectedSignal, decision.Reason);
    }

    [Fact]
    public void Evaluate_StagnantQueueAtTheThresholdRaisesWithHeadroom()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 2, true, Now, Now.AddMinutes(-20), null,
            Options with { BaselineParallelism = 3, RaiseQueueDepthThreshold = 2 },
            Plane(12, 900, 0.02), 900, Host(load1: 6, memoryUsedGb: 30));

        Assert.Equal(ReviewParallelismAction.Raise, decision.Action);
        Assert.Equal(4, decision.RecommendedParallelism);
        Assert.StartsWith("queue stagnant at depth 2; host headroom: load/core", decision.Reason);
        Assert.Contains("memory available", decision.Reason);
    }

    [Fact]
    public void Evaluate_DepthRaiseHoldsWhenHostIsSaturated()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 5, false, Now, Now.AddMinutes(-20), null,
            Options with { BaselineParallelism = 3, RaiseOnStagnation = false },
            Plane(8, 900, 0.35));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("host saturated", decision.Reason);
    }

    [Fact]
    public void Evaluate_OperatorBaselineRaiseWaitsForHostHeadroom()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            2, 1, false, Now, Now.AddMinutes(-20), null,
            Options with { BaselineParallelism = 3 }, Plane(8, 900, 0.35));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(2, decision.RecommendedParallelism);
        Assert.Contains("host saturated", decision.Reason);
    }

    [Fact]
    public void Evaluate_SaturationLowersOneStepPerLowerCooldownUntilBaseline()
    {
        var options = Options with { BaselineParallelism = 3 };
        var budget = Plane(12, 1_200, 0.35);
        var first = AdaptiveReviewParallelismPolicy.Evaluate(
            5, 3, true, Now, Now.AddMinutes(-20), null, options, budget, 900);
        var insideCooldown = AdaptiveReviewParallelismPolicy.Evaluate(
            4, 3, true, Now.AddMinutes(1), Now, null, options, budget, null);
        var next = AdaptiveReviewParallelismPolicy.Evaluate(
            4, 3, true, Now.AddMinutes(15), Now, null, options, budget, null);
        var baseline = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 3, true, Now.AddMinutes(30), Now.AddMinutes(15), null, options, budget, null);

        Assert.Equal((ReviewParallelismAction.Lower, 4), (first.Action, first.RecommendedParallelism));
        Assert.Equal((ReviewParallelismAction.Hold, 4), (insideCooldown.Action, insideCooldown.RecommendedParallelism));
        Assert.Equal((ReviewParallelismAction.Lower, 3), (next.Action, next.RecommendedParallelism));
        Assert.Equal((ReviewParallelismAction.Hold, 3), (baseline.Action, baseline.RecommendedParallelism));
    }

    /// <summary>
    /// A duration-only regression (no throttling, no load signal) keeps
    /// stepping down while the advisor keeps its baseline reference above the
    /// baseline, and the reference is dropped once the baseline is reached.
    /// </summary>
    [Fact]
    public void Evaluate_DurationRegressionLowersEveryCooldownUntilTheReferenceIsDroppedAtBaseline()
    {
        var options = Options with { BaselineParallelism = 3 };
        var budget = Plane(12, 1_500, 0.0);
        double? reference = 900;
        var current = 5;
        var lastChange = Now.AddMinutes(-20);
        var steps = new List<(ReviewParallelismAction, int)>();

        for (var minute = 0; minute <= 45; minute += 15)
        {
            var at = Now.AddMinutes(minute);
            var decision = AdaptiveReviewParallelismPolicy.Evaluate(
                current, 3, true, at, lastChange, null, options, budget, reference);
            steps.Add((decision.Action, decision.RecommendedParallelism));
            if (decision.Action != ReviewParallelismAction.Hold && decision.RecommendedParallelism != current)
            {
                reference = AdaptiveReviewParallelismPolicy.NextDurationReference(
                    decision, options, reference, budget.RollingReviewDurationSeconds);
                current = decision.RecommendedParallelism;
                lastChange = at;
            }
        }

        Assert.Equal(
            [
                (ReviewParallelismAction.Lower, 4),
                (ReviewParallelismAction.Lower, 3),
                (ReviewParallelismAction.Hold, 3),
                (ReviewParallelismAction.Hold, 3),
            ],
            steps);
        Assert.Null(reference);
    }

    [Theory]
    // action, from -> to, reference, rolling, expectedReference
    [InlineData(ReviewParallelismAction.Raise, 4, null, 900d, 900d)]
    [InlineData(ReviewParallelismAction.Raise, 5, 900d, 1_400d, 900d)]
    [InlineData(ReviewParallelismAction.Lower, 4, 900d, 1_400d, 900d)]
    [InlineData(ReviewParallelismAction.Lower, 3, 900d, 1_400d, null)]
    [InlineData(ReviewParallelismAction.Raise, 3, null, 900d, null)]
    public void NextDurationReference_AnchorsAtTheBaselineAndClearsOnReturn(
        ReviewParallelismAction action,
        int target,
        double? reference,
        double? rolling,
        double? expected)
    {
        var next = AdaptiveReviewParallelismPolicy.NextDurationReference(
            new AdaptiveReviewParallelismDecision(action, target, "test"),
            Options with { BaselineParallelism = 3 },
            reference,
            rolling);

        Assert.Equal(expected, next);
    }

    [Fact]
    public void Evaluate_StartupRefreshRespectsRaiseCooldown()
    {
        var startup = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 5, false, Now, null, null, Options with { BaselineParallelism = 3 }, Plane(8, 900, 0.02));
        var afterCooldown = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 5, false, Now.AddMinutes(5), Now, null,
            Options with { BaselineParallelism = 3 }, Plane(8, 900, 0.02));

        Assert.Equal(ReviewParallelismAction.Hold, startup.Action);
        Assert.Equal(ReviewParallelismAction.Raise, afterCooldown.Action);
    }

    [Fact]
    public void Evaluate_StagnationRaiseCanBeDisabled()
    {
        var decision = AdaptiveReviewParallelismPolicy.Evaluate(
            3, 2, true, Now, Now.AddMinutes(-20), null,
            Options with { BaselineParallelism = 3, RaiseQueueDepthThreshold = 2, RaiseOnStagnation = false },
            Plane(8, 900, 0.02));

        Assert.Equal(ReviewParallelismAction.Hold, decision.Action);
        Assert.Equal(3, decision.RecommendedParallelism);
        Assert.Contains("RaiseOnStagnation=false", decision.Reason);
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

    private static HostTelemetrySnapshotDto Host(double load1, long memoryUsedGb)
        => new(
            ObservedAt: Now,
            CpuPercent: null,
            Load1: load1,
            Load5: null,
            Load15: null,
            MemoryUsedBytes: memoryUsedGb << 30,
            MemoryTotalBytes: 62L << 30,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: null,
            IoWaitPercent: null,
            CpuCores: 12,
            ActiveSlots: 3);
}
