using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824 - direct matrix over the pure retry policy. The failure this card
/// fixes was a scheduling gap, not a merge bug: a card whose review passed and
/// whose integration died on a gate environment failure had no automatic path
/// back, so the matrix below is the contract.
/// </summary>
public sealed class GateEnvironmentRetryPolicyTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset FailedAt = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static GateEnvironmentRetryState State(
        string? taskState = TaskStates.HumanReview,
        string? failureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
        bool reviewPassed = true,
        string? deliverySha = Sha,
        string? reviewedSha = Sha,
        DateTimeOffset? failedAt = null,
        IntegrationRetryLedgerRecord? ledger = null)
        => new(
            taskState,
            failureCode,
            reviewPassed,
            deliverySha,
            reviewedSha,
            failedAt ?? FailedAt,
            ledger);

    private static IntegrationRetryLedgerRecord Ledger(
        int attempts,
        DateTimeOffset lastAttemptAt,
        string deliverySha = Sha,
        bool parked = false)
        => new()
        {
            FailureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            DeliverySha = deliverySha,
            Attempts = attempts,
            LastAttemptAtUtc = lastAttemptAt,
            Parked = parked,
            ParkedReason = parked ? "parked" : null,
        };

    [Fact]
    public void Backoff_IsFiveFifteenFortyFiveMinutes()
    {
        Assert.Equal(
            new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45) },
            GateEnvironmentRetryPolicy.Backoff);
        Assert.Equal(3, GateEnvironmentRetryPolicy.MaxAutomaticAttempts);
    }

    public static TheoryData<int, double, GateEnvironmentRetryAction, int> ScheduleMatrix => new()
    {
        // attemptsMade, minutes since the anchor, expected action, expected attempt number
        { 0, 0, GateEnvironmentRetryAction.Wait, 1 },
        { 0, 4.9, GateEnvironmentRetryAction.Wait, 1 },
        { 0, 5, GateEnvironmentRetryAction.Retry, 1 },
        { 0, 600, GateEnvironmentRetryAction.Retry, 1 },
        { 1, 14.9, GateEnvironmentRetryAction.Wait, 2 },
        { 1, 15, GateEnvironmentRetryAction.Retry, 2 },
        { 2, 44.9, GateEnvironmentRetryAction.Wait, 3 },
        { 2, 45, GateEnvironmentRetryAction.Retry, 3 },
        { 3, 1000, GateEnvironmentRetryAction.Park, 3 },
    };

    [Theory]
    [MemberData(nameof(ScheduleMatrix))]
    public void Decide_SchedulesBoundedBackoffAndStopsAfterTheBudget(
        int attemptsMade,
        double minutesSinceAnchor,
        GateEnvironmentRetryAction expected,
        int expectedAttemptNumber)
    {
        // Attempt 1 is measured from the failure; every later attempt from the
        // previous attempt, so a slow gate cannot compress the window.
        var anchor = FailedAt;
        var state = attemptsMade == 0
            ? State()
            : State(ledger: Ledger(attemptsMade, anchor));

        var decision = GateEnvironmentRetryPolicy.Decide(
            state,
            anchor + TimeSpan.FromMinutes(minutesSinceAnchor));

        Assert.Equal(expected, decision.Action);
        Assert.Equal(expectedAttemptNumber, decision.AttemptNumber);
    }

    [Fact]
    public void Decide_WaitCarriesTheDueTimeSoTheSweepIsObservable()
    {
        var decision = GateEnvironmentRetryPolicy.Decide(State(), FailedAt);

        Assert.Equal(GateEnvironmentRetryAction.Wait, decision.Action);
        Assert.Equal(FailedAt + TimeSpan.FromMinutes(5), decision.DueAtUtc);
    }

    [Fact]
    public void Decide_ThirdAttemptWindowIsMeasuredFromTheSecondAttempt()
    {
        // Regression guard for the obvious off-by-one: anchoring every window on
        // the original failure would fire all three retries inside 45 minutes.
        var secondAttemptAt = FailedAt + TimeSpan.FromMinutes(20);
        var state = State(ledger: Ledger(2, secondAttemptAt));

        Assert.Equal(
            GateEnvironmentRetryAction.Wait,
            GateEnvironmentRetryPolicy.Decide(state, FailedAt + TimeSpan.FromMinutes(60)).Action);
        Assert.Equal(
            GateEnvironmentRetryAction.Retry,
            GateEnvironmentRetryPolicy.Decide(state, secondAttemptAt + TimeSpan.FromMinutes(45)).Action);
    }

    [Theory]
    [InlineData(AcceptedIntegrationFailureCodes.MergeConflict)]
    [InlineData(AcceptedIntegrationFailureCodes.BuildGateFailed)]
    [InlineData(AcceptedIntegrationFailureCodes.IntegrationError)]
    [InlineData(AcceptedIntegrationFailureCodes.IntegrationPushBlocked)]
    [InlineData(null)]
    public void Decide_OtherFailureCodes_AreNotThisRailsBusiness(string? failureCode)
    {
        var decision = GateEnvironmentRetryPolicy.Decide(
            State(failureCode: failureCode),
            FailedAt + TimeSpan.FromHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
    }

    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    [InlineData(TaskStates.Escalated)]
    public void Decide_OnlyDrivesCardsParkedInHumanReview(string lane)
    {
        var decision = GateEnvironmentRetryPolicy.Decide(
            State(taskState: lane),
            FailedAt + TimeSpan.FromHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
    }

    [Fact]
    public void Decide_WithoutAPassedReview_NeverReplaysTheIntegration()
    {
        var decision = GateEnvironmentRetryPolicy.Decide(
            State(reviewPassed: false),
            FailedAt + TimeSpan.FromHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Contains("passed review", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_ReviewGradedADifferentSha_IsNotReusable()
    {
        var decision = GateEnvironmentRetryPolicy.Decide(
            State(reviewedSha: new string('2', 40)),
            FailedAt + TimeSpan.FromHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Contains("different delivery SHA", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_LedgerFromAnOlderDelivery_StartsAFreshBudget()
    {
        // A new delivery is a new subject. Its budget must not be pre-spent by
        // the attempts the previous delivery burned.
        var state = State(ledger: Ledger(
            GateEnvironmentRetryPolicy.MaxAutomaticAttempts,
            FailedAt,
            deliverySha: new string('9', 40),
            parked: true));

        var decision = GateEnvironmentRetryPolicy.Decide(state, FailedAt + TimeSpan.FromMinutes(5));

        Assert.Equal(GateEnvironmentRetryAction.Retry, decision.Action);
        Assert.Equal(1, decision.AttemptNumber);
    }

    [Fact]
    public void Decide_AlreadyParked_StopsInsteadOfParkingAgain()
    {
        var state = State(ledger: Ledger(
            GateEnvironmentRetryPolicy.MaxAutomaticAttempts,
            FailedAt,
            parked: true));

        var decision = GateEnvironmentRetryPolicy.Decide(state, FailedAt + TimeSpan.FromDays(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
    }

    [Fact]
    public void Decide_WithoutAFailureTimestamp_HasNothingToScheduleFrom()
    {
        var decision = GateEnvironmentRetryPolicy.Decide(
            State() with { FailedAtUtc = null },
            FailedAt + TimeSpan.FromHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
    }

    [Fact]
    public void DecideOperatorRetry_OverridesTheWindowAndTheSpentBudget()
    {
        var parked = State(ledger: Ledger(
            GateEnvironmentRetryPolicy.MaxAutomaticAttempts,
            FailedAt,
            parked: true));

        var decision = GateEnvironmentRetryPolicy.DecideOperatorRetry(parked);

        Assert.Equal(GateEnvironmentRetryAction.Retry, decision.Action);
        Assert.Equal(1, decision.AttemptNumber);
    }

    [Theory]
    [InlineData(TaskStates.Completed, AcceptedIntegrationFailureCodes.GateEnvironmentFailure, true)]
    [InlineData(TaskStates.HumanReview, AcceptedIntegrationFailureCodes.MergeConflict, true)]
    [InlineData(TaskStates.HumanReview, AcceptedIntegrationFailureCodes.GateEnvironmentFailure, false)]
    public void DecideOperatorRetry_KeepsEveryAdmissionRuleThatIsNotATimer(
        string lane,
        string failureCode,
        bool reviewPassed)
    {
        var decision = GateEnvironmentRetryPolicy.DecideOperatorRetry(
            State(taskState: lane, failureCode: failureCode, reviewPassed: reviewPassed));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
    }

    [Fact]
    public void ParkedReason_NamesTheEnvironmentFailureAndTheRemainingOperatorPath()
    {
        var reason = GateEnvironmentRetryPolicy.ParkedReason("npm test exit 1 (testCaseInsensitiveFS)");

        Assert.Contains("gate environment failure", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 automatic integration retries", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 min, 15 min, 45 min", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry integration", reason, StringComparison.Ordinal);
        Assert.Contains("a new review is not needed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("testCaseInsensitiveFS", reason, StringComparison.Ordinal);
    }
}
