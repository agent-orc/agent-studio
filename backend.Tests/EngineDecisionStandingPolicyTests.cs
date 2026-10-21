using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3281: whether an engine occurrence can still apply to its card, as a direct matrix.</summary>
public sealed class EngineDecisionStandingPolicyTests
{
    private static readonly TimeSpan PastGrace = EngineDecisionStandingPolicy.PreparationGrace + TimeSpan.FromSeconds(1);

    [Theory]
    // Settled occurrences owe nothing, whatever the lane.
    [InlineData(OrchestrationStage.ReviewDecision, "completed", false, TaskStates.Ready, EngineDecisionStanding.Settled)]
    [InlineData(OrchestrationStage.ReviewDecision, "cancelled", false, TaskStates.AutoReview, EngineDecisionStanding.Settled)]
    // An undecided lane decision whose card left the lane can never apply.
    [InlineData(OrchestrationStage.ReviewDecision, "pending", false, TaskStates.Ready, EngineDecisionStanding.Superseded)]
    [InlineData(OrchestrationStage.ReviewDecision, "leased", false, TaskStates.Completed, EngineDecisionStanding.Superseded)]
    [InlineData(OrchestrationStage.GateDispatch, "pending", false, TaskStates.Archive, EngineDecisionStanding.Superseded)]
    [InlineData(OrchestrationStage.ReviewInputDecision, "pending", false, TaskStates.Backlog, EngineDecisionStanding.Superseded)]
    // In its lane it stays live.
    [InlineData(OrchestrationStage.ReviewDecision, "pending", false, TaskStates.AutoReview, EngineDecisionStanding.Live)]
    [InlineData(OrchestrationStage.ReviewDecision, "applying", false, TaskStates.AutoReview, EngineDecisionStanding.Live)]
    // Its applier tells an owed move from one it made, so applying stays live here.
    [InlineData(OrchestrationStage.ReviewDecision, "applying", false, TaskStates.HumanReview, EngineDecisionStanding.Live)]
    // A shadow occurrence never applies; its comparison resolves it.
    [InlineData(OrchestrationStage.ReviewDecision, "pending", true, TaskStates.Ready, EngineDecisionStanding.Live)]
    // Stages that are not lane-bound check their own facts when they apply.
    [InlineData(OrchestrationStage.OperatorSweep, "pending", false, TaskStates.Ready, EngineDecisionStanding.Live)]
    [InlineData(OrchestrationStage.BatchPlan, "pending", false, TaskStates.Ready, EngineDecisionStanding.Live)]
    [InlineData(OrchestrationStage.OperatorDecision, "leased", false, TaskStates.Ready, EngineDecisionStanding.Live)]
    public void Standing_follows_status_stage_and_lane(OrchestrationStage stage, string status, bool shadow,
        string cardState, EngineDecisionStanding expected)
    {
        var facts = new EngineDecisionStandingFacts(stage, status, shadow, TaskStates.AutoReview, cardState, PastGrace);

        Assert.Equal(expected, EngineDecisionStandingPolicy.Decide(facts));
    }

    [Fact]
    public void A_card_can_still_be_moving_into_the_lane_of_a_new_occurrence()
    {
        var fresh = new EngineDecisionStandingFacts(OrchestrationStage.ReviewDecision, "pending", false,
            TaskStates.AutoReview, TaskStates.Progress, EngineDecisionStandingPolicy.PreparationGrace - TimeSpan.FromSeconds(1));

        Assert.Equal(EngineDecisionStanding.Live, EngineDecisionStandingPolicy.Decide(fresh));
        Assert.Equal(EngineDecisionStanding.Superseded,
            EngineDecisionStandingPolicy.Decide(fresh with { SinceUpdate = PastGrace }));
    }

    [Theory]
    [InlineData(OrchestrationStage.ReviewDecision, "applying", false, true)]
    [InlineData(OrchestrationStage.ReviewDecision, "pending", false, true)]
    [InlineData(OrchestrationStage.GateDispatch, "leased", false, true)]
    [InlineData(OrchestrationStage.ReviewInputDecision, "pending", false, true)]
    [InlineData(OrchestrationStage.ReviewDecision, "completed", false, false)]
    [InlineData(OrchestrationStage.ReviewDecision, "cancelled", false, false)]
    [InlineData(OrchestrationStage.ReviewDecision, "pending", true, false)]
    [InlineData(OrchestrationStage.OperatorSweep, "pending", false, false)]
    [InlineData(OrchestrationStage.OperatorDecision, "pending", false, false)]
    [InlineData(OrchestrationStage.BatchPlan, "applying", false, false)]
    public void An_operator_move_holds_every_open_lane_decision(OrchestrationStage stage, string status, bool shadow,
        bool held)
        => Assert.Equal(held, EngineDecisionStandingPolicy.HeldByOperatorMove(stage, status, shadow));
}
