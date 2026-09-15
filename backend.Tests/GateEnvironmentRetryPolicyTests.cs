using AgentStudio.Pipeline;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824: a card whose review passed and whose merge gate then failed with
/// <c>gate-environment-failure</c> must retry the integration on a bounded
/// backoff, reuse the passed review for the same delivery SHA, and park with a
/// named reason once the ladder is spent. Before this policy existed the card
/// said "will be retried" and nothing ever did; the only operator path was a
/// full new remote review.
/// </summary>
public sealed class GateEnvironmentRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string DeliverySha = "1111111111111111111111111111111111111111";

    private static GateEnvironmentRetryState Candidate(
        string? failureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
        bool integrationRequired = true,
        bool alreadyIntegrated = false,
        bool reviewPassed = true,
        string? reviewedSha = DeliverySha,
        string? deliverySha = DeliverySha,
        int completedRetries = 0,
        DateTimeOffset? failedAt = null,
        DateTimeOffset? lastRetryAt = null,
        bool parked = false,
        GateEnvironmentRetryTrigger trigger = GateEnvironmentRetryTrigger.Sweep)
        => new(
            integrationRequired,
            alreadyIntegrated,
            failureCode,
            failedAt ?? Now.AddHours(-1),
            reviewPassed,
            reviewedSha,
            deliverySha,
            completedRetries,
            lastRetryAt,
            parked,
            trigger);

    public static TheoryData<string, GateEnvironmentRetryState, GateEnvironmentRetryAction> IgnoreMatrix => new()
    {
        {
            "no integration expected",
            Candidate(integrationRequired: false),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "delivery already on the integration branch",
            Candidate(alreadyIntegrated: true),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "merge conflict is a product problem, not an environment one",
            Candidate(failureCode: AcceptedIntegrationFailureCodes.MergeConflict),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "red build gate is a product problem, not an environment one",
            Candidate(failureCode: AcceptedIntegrationFailureCodes.BuildGateFailed),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "no failure recorded at all",
            Candidate(failureCode: null),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "no passed review to reuse",
            Candidate(reviewPassed: false),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "the passed review describes an older delivery SHA",
            Candidate(reviewedSha: "2222222222222222222222222222222222222222"),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "the fenced delivery sidecar is gone",
            Candidate(deliverySha: null),
            GateEnvironmentRetryAction.Ignore
        },
        {
            "already parked with its named reason",
            Candidate(completedRetries: 3, parked: true),
            GateEnvironmentRetryAction.Ignore
        },
    };

    [Theory]
    [MemberData(nameof(IgnoreMatrix))]
    public void Decide_NonCandidates_AreIgnored(
        string because,
        GateEnvironmentRetryState state,
        GateEnvironmentRetryAction expected)
    {
        Assert.Equal(expected, GateEnvironmentRetryPolicy.Decide(state, Now).Action);
        Assert.False(string.IsNullOrWhiteSpace(because));
    }

    [Theory]
    [InlineData(0, 4, GateEnvironmentRetryAction.Wait)]
    [InlineData(0, 6, GateEnvironmentRetryAction.Retry)]
    [InlineData(1, 14, GateEnvironmentRetryAction.Wait)]
    [InlineData(1, 16, GateEnvironmentRetryAction.Retry)]
    [InlineData(2, 44, GateEnvironmentRetryAction.Wait)]
    [InlineData(2, 46, GateEnvironmentRetryAction.Retry)]
    public void Decide_BackoffLadder_IsFiveFifteenFortyFive(
        int completedRetries,
        int minutesSinceLastFailure,
        GateEnvironmentRetryAction expected)
    {
        var anchor = Now.AddMinutes(-minutesSinceLastFailure);
        var state = Candidate(
            completedRetries: completedRetries,
            failedAt: anchor,
            lastRetryAt: completedRetries == 0 ? null : anchor);

        var decision = GateEnvironmentRetryPolicy.Decide(state, Now);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(completedRetries + 1, decision.AttemptNumber);
    }

    [Fact]
    public void Decide_FirstRetry_IsAnchoredOnTheGateFailureNotOnTheSweep()
    {
        // The gate failed 10 minutes ago and nothing retried since: the first
        // 5-minute step is long past due, so the very next sweep retries.
        var state = Candidate(failedAt: Now.AddMinutes(-10));

        var decision = GateEnvironmentRetryPolicy.Decide(state, Now);

        Assert.Equal(GateEnvironmentRetryAction.Retry, decision.Action);
        Assert.Equal(1, decision.AttemptNumber);
    }

    [Fact]
    public void Decide_AfterTheLadderIsSpent_Parks()
    {
        var state = Candidate(
            completedRetries: GateEnvironmentRetryPolicy.MaxRetries,
            lastRetryAt: Now.AddHours(-5));

        var decision = GateEnvironmentRetryPolicy.Decide(state, Now);

        Assert.Equal(GateEnvironmentRetryAction.Park, decision.Action);
        Assert.Contains("gate failed before verification", decision.Reason);
    }

    [Fact]
    public void Decide_SweepNeverRetriesMoreThanTheBound()
    {
        // Walk the whole ladder the way the sweep does: retry, receipt, retry,
        // receipt, retry, receipt, then park - never a fourth retry.
        var actions = new List<GateEnvironmentRetryAction>();
        var completed = 0;
        var lastRetryAt = (DateTimeOffset?)null;
        var clock = Now;

        for (var tick = 0; tick < 6; tick++)
        {
            var decision = GateEnvironmentRetryPolicy.Decide(
                Candidate(
                    completedRetries: completed,
                    failedAt: Now.AddHours(-1),
                    lastRetryAt: lastRetryAt),
                clock);
            actions.Add(decision.Action);
            if (decision.Action != GateEnvironmentRetryAction.Retry) break;
            completed++;
            lastRetryAt = clock;
            clock = clock.AddHours(1);
        }

        Assert.Equal(
            [
                GateEnvironmentRetryAction.Retry,
                GateEnvironmentRetryAction.Retry,
                GateEnvironmentRetryAction.Retry,
                GateEnvironmentRetryAction.Park,
            ],
            actions);
        Assert.Equal(GateEnvironmentRetryPolicy.MaxRetries, completed);
    }

    [Fact]
    public void Decide_OperatorTrigger_SkipsTheBackoffWait()
    {
        var state = Candidate(
            completedRetries: 1,
            lastRetryAt: Now.AddSeconds(-30),
            trigger: GateEnvironmentRetryTrigger.Operator);

        var decision = GateEnvironmentRetryPolicy.Decide(state, Now);

        Assert.Equal(GateEnvironmentRetryAction.Retry, decision.Action);
        Assert.Equal(2, decision.AttemptNumber);
    }

    [Fact]
    public void Decide_OperatorTrigger_StillRetriesAfterThePark()
    {
        // The park exists because the gate host was broken. Repairing it and
        // asking by hand must not cost a whole new remote review.
        var state = Candidate(
            completedRetries: GateEnvironmentRetryPolicy.MaxRetries,
            parked: true,
            trigger: GateEnvironmentRetryTrigger.Operator);

        Assert.Equal(
            GateEnvironmentRetryAction.Retry,
            GateEnvironmentRetryPolicy.Decide(state, Now).Action);
    }

    [Fact]
    public void Decide_OperatorTrigger_KeepsEveryEligibilityGuard()
    {
        Assert.Equal(
            GateEnvironmentRetryAction.Ignore,
            GateEnvironmentRetryPolicy.Decide(
                Candidate(reviewPassed: false, trigger: GateEnvironmentRetryTrigger.Operator),
                Now).Action);
        Assert.Equal(
            GateEnvironmentRetryAction.Ignore,
            GateEnvironmentRetryPolicy.Decide(
                Candidate(
                    failureCode: AcceptedIntegrationFailureCodes.MergeConflict,
                    trigger: GateEnvironmentRetryTrigger.Operator),
                Now).Action);
    }

    [Fact]
    public void ParkedReason_NamesTheEnvironmentFailureAndTheSpentLadder()
    {
        var reason = GateEnvironmentRetryPolicy.ParkedReason(
            "Tool 'node' version v24.18.0 does not match .nvmrc");

        Assert.Contains("Gate environment failure", reason);
        Assert.Contains("does not match .nvmrc", reason);
        Assert.Contains("5 min, 15 min, 45 min", reason);
        Assert.Contains("Retry integration", reason);
        Assert.DoesNotContain("will be retried", reason);
    }
}
