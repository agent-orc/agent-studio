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

    [Fact]
    public void A_review_input_handoff_owes_what_the_applier_did_before_it_stopped()
    {
        string[] Owed(TransitionAction action) => EngineApplierContract.Owed(EngineApplierContract.For(action),
            TaskStates.HumanReview, false, true, false, reviewInputHandoff: true)
            .Select(effect => effect.ToString()).ToArray();
        Assert.Equal(["IntegrationJournal", "WaitMarkerCleared", "DecisionReceipt", "ReviewInputHandoff"],
            Owed(TransitionAction.AcceptAndIntegrate));
        Assert.Equal(["LinkedFollowUp", "IntegrationJournal", "WaitMarkerCleared", "DecisionReceipt",
            "ReviewInputHandoff"], Owed(TransitionAction.DegradeWithFollowUp));
    }

    public static TheoryData<string, string, bool, bool, int, bool, string> CanaryVerdicts() => new()
    {
        // case, basis, verified, completed, minutes since completion (or since the observation), downstream, verdict
        { "verified row", EngineEffectBasis.Row, true, true, 0, false, "Verified" },
        { "ask", EngineEffectBasis.Ask, true, true, 0, false, "NotCounted" },
        { "checkpoint", EngineEffectBasis.Checkpoint, true, true, 0, false, "NotCounted" },
        { "review-input handoff", EngineEffectBasis.ReviewInputHandoff, true, true, 0, false, "NotCounted" },
        { "manual move", EngineEffectBasis.ManualMove, false, false, 600, false, "NotCounted" },
        { "applying within the deadline", EngineEffectBasis.Row, false, false, 170, false, "Waiting" },
        { "applying past the deadline", EngineEffectBasis.Row, false, false, 180, false, "Missing" },
        { "completed within the window", EngineEffectBasis.Row, false, true, 1, false, "Waiting" },
        { "completed past the window", EngineEffectBasis.Row, false, true, 2, false, "Missing" },
        { "downstream within the deadline", EngineEffectBasis.Row, false, true, 170, true, "Waiting" },
        { "downstream past the deadline", EngineEffectBasis.Row, false, true, 180, true, "Missing" },
    };

    /// <summary>
    /// The effect window counts from CompletedAt; an occurrence the applier has
    /// not completed, and a downstream effect, wait for the apply deadline. The
    /// observation was made an hour before the completion, as a long
    /// integration makes it.
    /// </summary>
    [Theory]
    [MemberData(nameof(CanaryVerdicts))]
    public void Canary_policy_judges_each_check(string name, string basis, bool verified, bool completed,
        int minutes, bool downstream, string expected)
    {
        var observedAt = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var completedAt = observedAt.AddHours(1);
        var now = (completed ? completedAt : observedAt).AddMinutes(minutes);
        var check = new EngineEffectCheck("orch_run", "AcceptAndIntegrate", verified,
            verified ? [] : ["IntegrationJournal"], name, now, basis, completed ? completedAt : null, downstream);
        Assert.Equal(Enum.Parse<ApplierCanaryVerdict>(expected), ApplierCanaryPolicy.Judge(check, observedAt, now,
            TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
    }
}
