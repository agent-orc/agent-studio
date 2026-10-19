using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the canary verdict samples are fixed instants compared only with each other.

/// <summary>
/// AGT-3218: the applier contract table is complete. Every transition action
/// declares the effect the applier owes, its lane agrees with the applier's
/// lane rule, and every action a rollout profile can complete has a contract
/// case that runs the real applier
/// (<see cref="BatchGatePilotServiceTests.ApplierContractCases"/>).
/// </summary>
[Trait("Category", "Guard")]
public sealed class EngineApplierContractGuardTests
{
    private const string PayloadLane = "payload-lane";

    [Fact]
    public void Every_transition_action_declares_its_applier_effect_and_lane()
    {
        foreach (var action in Enum.GetValues<TransitionAction>())
        {
            var row = EngineApplierContract.For(action);
            Assert.Equal(action.ToString(), row.Action);
            Assert.False(string.IsNullOrWhiteSpace(row.Summary), $"{action} has no effect summary.");
            Assert.Contains(EngineApplierEffectKind.DecisionReceipt, row.Effects);
            Assert.Contains(EngineApplierEffectKind.WaitMarkerCleared, row.Effects);
            Assert.True(row.Lane is not null == row.Effects.Contains(EngineApplierEffectKind.LaneMove),
                $"{action}: a lane and a lane move go together.");
            var applied = TransitionDecisionPolicy.TargetLane(action, PayloadLane, OrchestrationAction.Continue);
            Assert.Equal(row.Lane == EngineApplierContract.StageLane ? PayloadLane : row.Lane, applied);
        }
    }

    [Fact]
    public void Every_profile_action_has_an_effect_row_and_a_contract_case()
    {
        Assert.Equal(OrchestrationRolloutStore.Profiles.Order(StringComparer.Ordinal),
            EngineApplierContract.ProfileActions.Keys.Order(StringComparer.Ordinal));
        foreach (var (profile, actions) in EngineApplierContract.ProfileActions)
        {
            Assert.Equal("covered", OrchestrationRolloutStore.ApplierContract(profile).Status);
            foreach (var action in actions)
            {
                Assert.NotNull(EngineApplierContract.Find(action));
                Assert.Contains(BatchGatePilotServiceTests.ApplierContractCases,
                    item => item.Profile == profile && item.Action == action);
            }
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("deferToBatch")]
    public void An_action_outside_the_table_has_no_effect_row(string action)
    {
        Assert.Null(EngineApplierContract.Find(action));
        Assert.Equal(EngineApplierContract.BatchPlan, EngineApplierContract.Find("BatchPlan"));
    }

    [Theory]
    // A passed gate without a payload lane keeps the lane and dispatches the aspect review.
    [InlineData(TransitionAction.Continue, null, true, false, false,
        "LaneKept,GateDispatch,WaitMarkerCleared,DecisionReceipt")]
    // A coding completion continues to its payload lane.
    [InlineData(TransitionAction.Continue, TaskStates.AutoReview, false, false, false,
        "LaneMove,WaitMarkerCleared,DecisionReceipt")]
    [InlineData(TransitionAction.HumanReview, TaskStates.HumanReview, false, true, false,
        "LaneMove,ParkVerdict,WaitMarkerCleared,DecisionReceipt")]
    [InlineData(TransitionAction.HumanReview, TaskStates.HumanReview, false, false, false,
        "LaneMove,WaitMarkerCleared,DecisionReceipt")]
    [InlineData(TransitionAction.CreateReview, TaskStates.AutoReview, false, false, true,
        "LaneMove,ReviewAttempt,WaitMarkerCleared,DecisionReceipt")]
    [InlineData(TransitionAction.DeferToBatch, null, false, true, false,
        "BatchAdmission,WaitMarkerCleared,DecisionReceipt")]
    [InlineData(TransitionAction.ReviewInfrastructureRetry, null, false, true, false,
        "LaneKept,ReviewRetryScheduled,WaitMarkerCleared,DecisionReceipt")]
    public void Owed_effects_narrow_the_row_by_lane_and_payload(TransitionAction action, string? lane,
        bool gateStage, bool reviewPayload, bool reviewRequest, string expected)
        => Assert.Equal(expected.Split(','), EngineApplierContract.Owed(EngineApplierContract.For(action), lane,
            gateStage, reviewPayload, reviewRequest).Select(effect => effect.ToString()));

    [Theory]
    [InlineData(true, 0, ApplierCanaryVerdict.Verified)]
    [InlineData(true, 600, ApplierCanaryVerdict.Verified)]
    [InlineData(false, 0, ApplierCanaryVerdict.Waiting)]
    [InlineData(false, 119, ApplierCanaryVerdict.Waiting)]
    [InlineData(false, 120, ApplierCanaryVerdict.Missing)]
    [InlineData(false, 600, ApplierCanaryVerdict.Missing)]
    public void Canary_counts_a_decision_missing_only_after_its_window(bool verified, int elapsedSeconds,
        ApplierCanaryVerdict expected)
    {
        var observedAt = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ApplierCanaryPolicy.Judge(verified, observedAt,
            observedAt.AddSeconds(elapsedSeconds), TimeSpan.FromSeconds(120)));
    }
}
