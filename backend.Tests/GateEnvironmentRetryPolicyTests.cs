using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824: a gate environment failure rolls the merge back and tells the
/// operator the integration "will be retried". Nothing retried it. These tests
/// pin the pure schedule behind that promise: which cards are eligible, when
/// each rung is due, and where the ladder stops.
/// </summary>
public sealed class GateEnvironmentRetryPolicyTests
{
    private static readonly DateTimeOffset Failed =
        new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decide_FirstRungIsDueFiveMinutesAfterTheGateEnvironmentFailure()
    {
        var tooEarly = Decide(now: Failed.AddMinutes(4).AddSeconds(59));
        Assert.Equal(GateEnvironmentRetryAction.Wait, tooEarly.Action);
        Assert.Equal("gate-environment-backoff", tooEarly.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), tooEarly.Wait);
        Assert.Equal(Failed.AddMinutes(5), tooEarly.DueAt);

        var due = Decide(now: Failed.AddMinutes(5));
        Assert.Equal(GateEnvironmentRetryAction.Retry, due.Action);
        Assert.Equal(0, due.AttemptsSpent);
        Assert.Equal(3, due.MaxAttempts);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, 45)]
    public void Decide_LadderRungsFollowTheBoundedBackoff(int spent, int minutes)
    {
        var lastAttempt = Failed.AddHours(1);
        var anchor = spent == 0 ? Failed : lastAttempt;

        var waiting = Decide(
            attemptsSpent: spent,
            lastAttemptAt: spent == 0 ? null : lastAttempt,
            now: anchor.AddMinutes(minutes).AddSeconds(-1));
        Assert.Equal(GateEnvironmentRetryAction.Wait, waiting.Action);

        var due = Decide(
            attemptsSpent: spent,
            lastAttemptAt: spent == 0 ? null : lastAttempt,
            now: anchor.AddMinutes(minutes));
        Assert.Equal(GateEnvironmentRetryAction.Retry, due.Action);
        Assert.Equal(spent, due.AttemptsSpent);
    }

    [Fact]
    public void Decide_AfterTheLastRung_Parks()
    {
        var decision = Decide(
            attemptsSpent: 3,
            lastAttemptAt: Failed.AddHours(2),
            now: Failed.AddDays(1));

        Assert.Equal(GateEnvironmentRetryAction.Park, decision.Action);
        Assert.Equal("gate-environment-retry-budget-exhausted", decision.Reason);
        Assert.Equal(3, decision.AttemptsSpent);
        Assert.Null(decision.DueAt);
    }

    [Fact]
    public void Decide_WithoutAPassedReviewForTheDeliverySha_IsIgnored()
    {
        // Replaying an integration reuses a review verdict. Without one there is
        // nothing to reuse, and the replay would integrate unreviewed work.
        var decision = Decide(reviewPassed: false, now: Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("no-passed-review-for-delivery-sha", decision.Reason);
    }

    [Theory]
    [InlineData(AcceptedIntegrationFailureCodes.MergeConflict)]
    [InlineData(AcceptedIntegrationFailureCodes.BuildGateFailed)]
    [InlineData(AcceptedIntegrationFailureCodes.IntegrationPushBlocked)]
    public void Decide_ForAnyOtherFailureCode_IsIgnored(string code)
    {
        // Those codes describe the delivery or the push, not the gate host. They
        // have their own owners (rebase recovery, the acceptance rail, the push
        // backstop) and must not also be replayed here.
        var decision = Decide(failureCode: code, now: Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("not-a-gate-environment-failure", decision.Reason);
    }

    [Theory]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    [InlineData(TaskStates.AutoReview)]
    public void Decide_OutsideTheDeliveredReviewLanes_IsIgnored(string state)
    {
        // Completed and Archive belong to the accepted-integration backstop;
        // Auto Review is still being reviewed.
        var decision = Decide(state: state, now: Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("outside-retry-lanes", decision.Reason);
    }

    [Fact]
    public void Decide_ForACardThatExpectsNoBranch_IsIgnored()
    {
        var task = Task(TaskStates.HumanReview) with { NoBranchExpected = true };
        var decision = GateEnvironmentRetryPolicy.Decide(
            task,
            Status(AcceptedIntegrationFailureCodes.GateEnvironmentFailure),
            reviewPassed: true,
            attemptsSpent: 0,
            lastAttemptAt: null,
            failedAt: Failed,
            GateEnvironmentRetryOptions.Default,
            Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("no-code-delivery", decision.Reason);
    }

    [Fact]
    public void Decide_WhileAnAcceptanceTransactionIsIntegrating_IsIgnored()
    {
        // The accepted-integration backstop owns a card with phase=integrating.
        // Two owners would run two merges and spend a rung on the other's work.
        var task = Task(TaskStates.HumanReview) with { Phase = LifecyclePhases.Integrating };
        var decision = GateEnvironmentRetryPolicy.Decide(
            task,
            Status(AcceptedIntegrationFailureCodes.GateEnvironmentFailure),
            reviewPassed: true,
            attemptsSpent: 0,
            lastAttemptAt: null,
            failedAt: Failed,
            GateEnvironmentRetryOptions.Default,
            Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("acceptance-integration-in-flight", decision.Reason);
    }

    [Fact]
    public void Decide_WhenDisabled_IsIgnored()
    {
        var options = GateEnvironmentRetryOptions.Default with { Enabled = false };
        var decision = GateEnvironmentRetryPolicy.Decide(
            Task(TaskStates.HumanReview),
            Status(AcceptedIntegrationFailureCodes.GateEnvironmentFailure),
            reviewPassed: true,
            attemptsSpent: 0,
            lastAttemptAt: null,
            failedAt: Failed,
            options,
            Failed.AddHours(1));

        Assert.Equal(GateEnvironmentRetryAction.Ignore, decision.Action);
        Assert.Equal("gate-environment-retry-disabled", decision.Reason);
    }

    [Fact]
    public void Decide_LegacyEvidenceWithoutATimestamp_IsDueImmediately()
    {
        // A missing anchor must not mean "never retry"; it means the evidence is
        // too old to schedule from.
        var decision = GateEnvironmentRetryPolicy.Decide(
            Task(TaskStates.HumanReview),
            Status(AcceptedIntegrationFailureCodes.GateEnvironmentFailure),
            reviewPassed: true,
            attemptsSpent: 0,
            lastAttemptAt: null,
            failedAt: null,
            GateEnvironmentRetryOptions.Default,
            Failed);

        Assert.Equal(GateEnvironmentRetryAction.Retry, decision.Action);
        Assert.Equal("gate-environment-retry-due-without-anchor", decision.Reason);
    }

    [Fact]
    public void ParkedReason_NamesTheEnvironmentFailureAndTheCheapRecovery()
    {
        var reason = GateEnvironmentRetryPolicy.ParkedReason(
            3,
            "develop",
            "Tool 'node' version v24.18.0 does not match .nvmrc");

        Assert.Contains("Parked after 3 automatic gate-environment retries", reason, StringComparison.Ordinal);
        Assert.Contains("develop", reason, StringComparison.Ordinal);
        Assert.Contains("Tool 'node' version v24.18.0 does not match .nvmrc", reason, StringComparison.Ordinal);
        Assert.Contains("no new review round is needed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_FromConfiguration_OverridesAndClampsTheLadder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GateEnvironmentRetry:SweepIntervalSeconds"] = "1",
                ["GateEnvironmentRetry:BackoffMinutes:0"] = "2",
                ["GateEnvironmentRetry:BackoffMinutes:1"] = "99999",
            })
            .Build();

        var options = GateEnvironmentRetryOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(10), options.SweepInterval);
        Assert.Equal(
            [TimeSpan.FromMinutes(2), TimeSpan.FromHours(24)],
            options.Backoff);
        Assert.Equal(2, options.MaxAttempts);
    }

    [Fact]
    public void Options_WithoutConfiguration_UsesTheDocumentedLadder()
    {
        var options = GateEnvironmentRetryOptions.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45)],
            options.Backoff);
    }

    private static GateEnvironmentRetryDecision Decide(
        string state = TaskStates.HumanReview,
        string failureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
        bool reviewPassed = true,
        int attemptsSpent = 0,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? failedAt = null,
        DateTimeOffset? now = null)
        => GateEnvironmentRetryPolicy.Decide(
            Task(state),
            Status(failureCode),
            reviewPassed,
            attemptsSpent,
            lastAttemptAt,
            failedAt ?? Failed,
            GateEnvironmentRetryOptions.Default,
            now ?? Failed);

    private static TaskInfo Task(string state) => new()
    {
        Id = "gate-environment",
        Key = "AGT-2811",
        TaskKey = "fixture::gate-environment",
        State = state,
        Mode = TaskModes.Coding,
        ProjectName = "Fixture",
    };

    private static TaskIntegrationStatus Status(string failureCode) => new()
    {
        // CAC-18 keeps a gate environment failure Pending, never conflict-skipped.
        Status = failureCode == AcceptedIntegrationFailureCodes.GateEnvironmentFailure
            ? IntegrationStatuses.Pending
            : IntegrationStatuses.ConflictSkipped,
        IntegrationBranch = "develop",
        Failure = new TaskIntegrationFailure { Code = failureCode },
    };
}
