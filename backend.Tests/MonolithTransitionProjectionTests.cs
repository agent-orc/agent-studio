using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MonolithTransitionProjectionTests
{
    [Theory]
    [InlineData(TransitionAction.FindingRound, TaskStates.Ready)]
    [InlineData(TransitionAction.ConcernRound, TaskStates.Ready)]
    [InlineData(TransitionAction.Escalate, TaskStates.Escalated)]
    [InlineData(TransitionAction.HumanReview, TaskStates.HumanReview)]
    [InlineData(TransitionAction.AcceptAndIntegrate, TaskStates.HumanReview)]
    [InlineData(TransitionAction.DegradeWithFollowUp, TaskStates.HumanReview)]
    [InlineData(TransitionAction.DeferToBatch, null)]
    [InlineData(TransitionAction.ReviewInfrastructureRetry, null)]
    [InlineData(TransitionAction.Halt, null)]
    public void Engine_action_projects_to_its_authority_lane(TransitionAction action, string? expected)
        => Assert.Equal(expected, TransitionDecisionPolicy.TargetLane(action,
            TaskStates.Progress, OrchestrationAction.Continue));

    [Fact]
    public void Coding_completion_uses_the_validated_source_target()
        => Assert.Equal(TaskStates.AutoReview, TransitionDecisionPolicy.TargetLane(null,
            TaskStates.AutoReview, OrchestrationAction.Continue));

    [Fact]
    public void Stage_failure_without_a_typed_action_escalates()
        => Assert.Equal(TaskStates.Escalated, TransitionDecisionPolicy.TargetLane(null,
            null, OrchestrationAction.Escalate));

    [Theory]
    [InlineData(0, 0, 4, TransitionAction.Continue)]
    [InlineData(1, 2, 4, TransitionAction.ReturnToReady)]
    [InlineData(1, 4, 4, TransitionAction.Escalate)]
    public void Gate_result_respects_the_lifetime_card_budget(int failed, int used, int allowed,
        TransitionAction expected)
        => Assert.Equal(expected, StageDecisionPolicy.GateTransition("card", "gate", failed,
            used, allowed).Transition!.Action);
}
