using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Matrix for <see cref="FollowUpAdmissionPolicy"/> (AGT-2747).
///
/// <para>
/// Bug class guarded: <c>POST /api/tasks/{id}/continue</c> used to spawn a
/// local CLI run for a card in any lane, answer <c>200 started</c>, and then
/// have the lane watchdog kill the fresh process within a second - three
/// operator steers were lost that way on 2026-09-07 (AGT-2743 sat in
/// <c>4-auto-review</c> and was routed to <c>agent-runner-01</c>). Every
/// non-runnable combination below must resolve to <c>Queue</c> so the
/// follow-up is persisted instead of discarded.
/// </para>
/// </summary>
public sealed class FollowUpAdmissionPolicyTests
{
    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public void RunnableLane_LocalProject_NoDelivery_StartsLocally(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(lane, phase: null, isLocalExecution: true);

        Assert.Equal(FollowUpAdmission.StartLocally, decision.Outcome);
        Assert.Null(decision.QueueReason);
        Assert.False(decision.Queues);
    }

    [Theory]
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    [InlineData(TaskStates.FailedPickup)]
    [InlineData(TaskStates.CodeNotComplete)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void NonRunnableLane_Queues(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(lane, phase: null, isLocalExecution: true);

        Assert.True(decision.Queues, $"a follow-up on '{lane}' must never start a local process");
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
        Assert.Contains(lane, decision.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9-does-not-exist")]
    public void UnknownLane_Queues(string? lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(lane, phase: null, isLocalExecution: true);

        Assert.True(decision.Queues, "an unreadable lane is never proof that a local run is safe");
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
    }

    /// <summary>
    /// The 2026-09-07 reproduction: AGT-2743 in <c>4-auto-review</c>, routed to
    /// the remote runner, receiving a steer with a cliType override.
    /// </summary>
    [Fact]
    public void ReviewLaneOnRemoteProject_Queues_LaneWinsOverRemote()
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            TaskStates.AutoReview, LifecyclePhases.AwaitingReview,
            isLocalExecution: false, configuredRunnerId: "agent-runner-01");

        Assert.True(decision.Queues);
        Assert.Equal(FollowUpQueueReasons.LaneNotRunnable, decision.QueueReason);
    }

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public void DeliveryAwaitingReview_Queues(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            lane, LifecyclePhases.AwaitingReview, isLocalExecution: true);

        Assert.True(decision.Queues, "re-entering the session would race the reviewer");
        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, decision.QueueReason);
    }

    [Fact]
    public void DeliveryIntegrating_Queues()
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            TaskStates.Progress, LifecyclePhases.Integrating, isLocalExecution: true);

        Assert.True(decision.Queues);
        Assert.Equal(FollowUpQueueReasons.DeliveryUnderReview, decision.QueueReason);
    }

    /// <summary>
    /// The UI-iteration and concept sight-review gates are designed to take the
    /// operator's verdict through the ordinary continue path. Queueing them
    /// would break the human gate they implement, so <c>steer-pending</c> is
    /// deliberately outside the delivery-under-review set.
    /// </summary>
    [Theory]
    [InlineData(LifecyclePhases.SteerPending)]
    [InlineData(LifecyclePhases.ExecutionRunning)]
    [InlineData(LifecyclePhases.LoopWaiting)]
    [InlineData(LifecyclePhases.QuotaWaiting)]
    [InlineData(LifecyclePhases.PostProcessingRunning)]
    public void HumanGateAndWorkingPhases_StillStartLocally(string phase)
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            TaskStates.Progress, phase, isLocalExecution: true);

        Assert.Equal(FollowUpAdmission.StartLocally, decision.Outcome);
    }

    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    public void RemoteConfiguredProject_Queues_AndNamesTheRunner(string lane)
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            lane, phase: null, isLocalExecution: false, configuredRunnerId: "agent-runner-01");

        Assert.True(decision.Queues, "this backend must not run a card another host owns");
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, decision.QueueReason);
        Assert.Contains("agent-runner-01", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteWithoutRunnerName_StillQueues()
    {
        var decision = FollowUpAdmissionPolicy.Decide(
            TaskStates.Ready, phase: null, isLocalExecution: false, configuredRunnerId: null);

        Assert.True(decision.Queues);
        Assert.Equal(FollowUpQueueReasons.RemoteExecution, decision.QueueReason);
        Assert.Contains("a remote runner", decision.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only admission writes the operator's words into <c>prompt.md</c> before
    /// queueing, so only admission-queued intents may ride along on a run that
    /// was started for a different prompt. Getting this wrong in either
    /// direction re-creates the loss the ticket exists to prevent: too narrow
    /// replays a follow-up twice, too wide silently drops an auto-answer that
    /// lives nowhere else.
    /// </summary>
    [Theory]
    [InlineData(FollowUpQueueReasons.ProjectBusy)]
    [InlineData(FollowUpQueueReasons.LaneNotRunnable)]
    [InlineData(FollowUpQueueReasons.DeliveryUnderReview)]
    [InlineData(FollowUpQueueReasons.RemoteExecution)]
    public void AdmissionQueuedReasons_MayRideAlong(string reason)
        => Assert.True(FollowUpQueueReasons.IsAdmissionQueued(reason));

    [Theory]
    [InlineData("steer-timeout-auto-answer")]
    [InlineData("loop-continuation-slot-wait")]
    [InlineData("attribution-ambiguous")]
    [InlineData("acceptance-rail:merge-conflict:retry-1")]
    [InlineData(null)]
    [InlineData("")]
    public void OtherProducersIntents_MustSurvive(string? reason)
        => Assert.False(FollowUpQueueReasons.IsAdmissionQueued(reason));

    [Fact]
    public void RunnableLanes_AreExactlyReadyAndProgress()
    {
        Assert.Equal(
            new[] { TaskStates.Ready, TaskStates.Progress },
            FollowUpAdmissionPolicy.RunnableLanes);
        Assert.True(FollowUpAdmissionPolicy.IsRunnableLane(TaskStates.Progress));
        Assert.False(FollowUpAdmissionPolicy.IsRunnableLane(TaskStates.AutoReview));
    }
}
