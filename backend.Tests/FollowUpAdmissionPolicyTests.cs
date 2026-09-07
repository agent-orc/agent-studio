using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over <see cref="FollowUpAdmissionPolicy"/> (AGT-2747). The
/// decision is the guard that stops a user follow-up from being accepted with
/// <c>200 {"status":"started"}</c> and then discarded a second later by lane
/// reconciliation, so every branch is pinned here without a filesystem, a
/// process, or a DI graph.
/// </summary>
public class FollowUpAdmissionPolicyTests
{
    private static FollowUpAdmissionFacts Facts(
        string lane,
        string? phase = null,
        string? executionLocation = null,
        string? localRunnerId = null,
        string? localRunnerName = null)
        => new(lane, phase, executionLocation, localRunnerId, localRunnerName);

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public void RunnableLane_LocalProject_StartsLocally(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(lane));

        Assert.Equal(FollowUpAdmissionAction.StartLocally, decision.Action);
        Assert.Null(decision.QueueReason);
    }

    [Theory]
    // The lanes named in the operator finding: a delivery sits there and the
    // watchdog kills anything started against them.
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    // The 0/1 lanes: nothing has been picked up yet.
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    // The park lanes look runnable but RunPlanner never moves them into
    // 3-progress, so a run started there dies the same way.
    [InlineData(TaskStates.FailedPickup)]
    [InlineData(TaskStates.CodeNotComplete)]
    public void NonRunnableLane_IsQueued(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(lane));

        Assert.Equal(FollowUpAdmissionAction.Queue, decision.Action);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
        Assert.Contains(lane, decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLaneIsEitherRunnableOrQueued()
    {
        // No lane may fall through the matrix unclassified: an unclassified lane
        // is exactly the hole this policy exists to close.
        foreach (var lane in TaskStates.All)
        {
            var decision = FollowUpAdmissionPolicy.Decide(Facts(lane));
            var runnable = FollowUpAdmissionPolicy.RunnableLanes.Contains(lane);
            Assert.Equal(
                runnable ? FollowUpAdmissionAction.StartLocally : FollowUpAdmissionAction.Queue,
                decision.Action);
        }
    }

    [Fact]
    public void UnknownLane_IsQueued()
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts("9-something-new"));

        Assert.Equal(FollowUpAdmissionAction.Queue, decision.Action);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
    }

    [Theory]
    [InlineData(LifecyclePhases.PostProcessingRunning)]
    [InlineData(LifecyclePhases.PostProcessingBlocked)]
    [InlineData(LifecyclePhases.AwaitingReview)]
    [InlineData(LifecyclePhases.Integrating)]
    public void DeliveryUnderReview_InProgressLane_IsQueued(string phase)
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(TaskStates.Progress, phase));

        Assert.Equal(FollowUpAdmissionAction.Queue, decision.Action);
        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, decision.QueueReason);
    }

    [Theory]
    [InlineData(LifecyclePhases.ExecutionRunning)]
    [InlineData(LifecyclePhases.ExecutionStalled)]
    [InlineData(LifecyclePhases.LoopWaiting)]
    [InlineData(LifecyclePhases.SteerPending)]
    [InlineData(LifecyclePhases.QuotaWaiting)]
    public void WorkingOrWaitingPhase_StillStartsLocally(string phase)
    {
        // Steering a run that is waiting for the user is the whole point of the
        // continue endpoint; the guard must not swallow it.
        var decision = FollowUpAdmissionPolicy.Decide(Facts(TaskStates.Progress, phase));

        Assert.Equal(FollowUpAdmissionAction.StartLocally, decision.Action);
    }

    [Fact]
    public void RemoteExecutionLocation_IsQueued()
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            Facts(TaskStates.Ready, executionLocation: "agent-runner-01", localRunnerId: "stable@studio-host"));

        Assert.Equal(FollowUpAdmissionAction.Queue, decision.Action);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, decision.QueueReason);
        Assert.Contains("agent-runner-01", decision.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ExecutionLocations.Local)]
    [InlineData("LOCAL")]
    public void LocalExecutionLocation_StartsLocally(string? location)
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(TaskStates.Progress, executionLocation: location));

        Assert.Equal(FollowUpAdmissionAction.StartLocally, decision.Action);
    }

    [Theory]
    [InlineData("agent-runner-01", null)]
    [InlineData("AGENT-RUNNER-01", null)]
    [InlineData(null, "Agent-Runner-01")]
    public void ExecutionLocationNamingThisBackend_IsNotRemote(string? matchingId, string? matchingName)
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(
            TaskStates.Progress,
            executionLocation: "agent-runner-01",
            localRunnerId: matchingId,
            localRunnerName: matchingName));

        Assert.Equal(FollowUpAdmissionAction.StartLocally, decision.Action);
    }

    [Fact]
    public void LaneCheckWinsOverRemoteAndDelivery()
    {
        // Deterministic precedence, so the 202 body always names the most
        // fundamental obstacle rather than whichever check ran first.
        var decision = FollowUpAdmissionPolicy.Decide(Facts(
            TaskStates.AutoReview,
            phase: LifecyclePhases.AwaitingReview,
            executionLocation: "agent-runner-01"));

        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
    }

    [Fact]
    public void DeliveryCheckWinsOverRemote()
    {
        var decision = FollowUpAdmissionPolicy.Decide(Facts(
            TaskStates.Progress,
            phase: LifecyclePhases.PostProcessingRunning,
            executionLocation: "agent-runner-01"));

        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, decision.QueueReason);
    }

    // ── the honest-"started" half: what counts as a lost start window ────────

    private static PendingIntent Intent(string savedReason, DateTime savedAt)
        => new() { Mode = ContinueModes.Steer, Prompt = "steer", SavedReason = savedReason, SavedAt = savedAt };

    [Fact]
    public void NoIntentAfterTheWindow_IsNotALoss()
        => Assert.False(FollowUpAdmissionPolicy.IsStartWindowLoss(before: null, observed: null));

    [Fact]
    public void FreshRunStoppedIntent_IsALoss()
    {
        var observed = Intent(FollowUpQueueReasons.RunStopped("moved out of 3-progress"), new DateTime(2026, 9, 7, 1, 56, 0, DateTimeKind.Utc));

        Assert.True(FollowUpAdmissionPolicy.IsStartWindowLoss(before: null, observed));
    }

    [Theory]
    [InlineData(FollowUpQueueReasons.ProjectBusy)]
    [InlineData(FollowUpQueueReasons.LaneNotRunnable)]
    [InlineData(FollowUpQueueReasons.RemoteExecution)]
    [InlineData(FollowUpQueueReasons.DeliveryUnderReview)]
    public void QueuedIntentLeftOnTheCard_IsNotALoss(string savedReason)
    {
        // Continuing a card that still carries an unconsumed queued intent must
        // not be reported as a killed run.
        var observed = Intent(savedReason, new DateTime(2026, 9, 7, 1, 0, 0, DateTimeKind.Utc));

        Assert.False(FollowUpAdmissionPolicy.IsStartWindowLoss(before: null, observed));
    }

    [Fact]
    public void PreExistingRunStoppedIntent_IsNotALoss()
    {
        // An earlier kill left an intent behind and the operator continued the
        // card by hand. The same file re-read is evidence about the old run.
        var stale = Intent(FollowUpQueueReasons.RunStopped("watchdog"), new DateTime(2026, 9, 6, 23, 0, 0, DateTimeKind.Utc));

        Assert.False(FollowUpAdmissionPolicy.IsStartWindowLoss(before: stale, observed: stale));
    }

    [Fact]
    public void NewerRunStoppedIntentOverAStaleOne_IsALoss()
    {
        var stale = Intent(FollowUpQueueReasons.RunStopped("watchdog"), new DateTime(2026, 9, 6, 23, 0, 0, DateTimeKind.Utc));
        var fresh = Intent(FollowUpQueueReasons.RunStopped("moved out of 3-progress"), new DateTime(2026, 9, 7, 1, 56, 0, DateTimeKind.Utc));

        Assert.True(FollowUpAdmissionPolicy.IsStartWindowLoss(stale, fresh));
    }

    [Fact]
    public void ReproductionOf20260907_ContinueOnAutoReviewCard_IsQueued()
    {
        // AGT-2743, three steers lost in one night: the card sat in
        // 4-auto-review with a pending delivery when the follow-up arrived.
        var decision = FollowUpAdmissionPolicy.Decide(Facts(
            TaskStates.AutoReview,
            phase: LifecyclePhases.AwaitingReview,
            executionLocation: "agent-runner-01",
            localRunnerId: "dev@studio-host"));

        Assert.Equal(FollowUpAdmissionAction.Queue, decision.Action);
        Assert.NotNull(decision.QueueReason);
    }
}
