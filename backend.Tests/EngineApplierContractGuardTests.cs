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

    [Fact]
    public void An_agent_round_handoff_owes_the_settled_journal_and_no_lane_move()
        => Assert.Equal(["IntegrationJournal", "WaitMarkerCleared", "DecisionReceipt", "AgentRoundHandoff"],
            EngineApplierContract.Owed(EngineApplierContract.For(TransitionAction.AcceptAndIntegrate),
                TaskStates.HumanReview, false, true, false, agentRoundHandoff: true)
                .Select(effect => effect.ToString()));

    public static TheoryData<string, string, bool, bool, int, bool, string?, string> CanaryVerdicts() => new()
    {
        // case, basis, verified, completed, minutes since completion (or since the observation), downstream,
        // waiting on, verdict
        { "verified row", EngineEffectBasis.Row, true, true, 0, false, null, "Verified" },
        { "verified row not completed", EngineEffectBasis.Row, true, false, 0, false, null, "Waiting" },
        { "ask", EngineEffectBasis.Ask, true, true, 0, false, null, "NotCounted" },
        { "checkpoint", EngineEffectBasis.Checkpoint, true, true, 0, false, null, "NotCounted" },
        { "review-input handoff", EngineEffectBasis.ReviewInputHandoff, true, true, 0, false, null, "NotCounted" },
        { "agent-round handoff", EngineEffectBasis.AgentRoundHandoff, true, true, 0, false, null, "NotCounted" },
        { "source exit", EngineEffectBasis.SourceExit, false, false, 600, false, null, "NotCounted" },
        { "gate superseded", EngineEffectBasis.GateSuperseded, false, true, 600, true, null, "NotCounted" },
        { "probe failed", EngineEffectBasis.ProbeFailed, false, false, 600, false, null, "NotCounted" },
        { "applying within the deadline", EngineEffectBasis.Row, false, false, 170, false, null, "Waiting" },
        { "applying past the deadline", EngineEffectBasis.Row, false, false, 180, false, null, "Missing" },
        { "integration in flight", EngineEffectBasis.Row, false, false, 170, false,
            EngineEffectWait.IntegrationInFlight, "Waiting" },
        { "integration in flight at the deadline", EngineEffectBasis.Row, false, false, 180, false,
            EngineEffectWait.IntegrationInFlight, "NotCounted" },
        { "card not found", EngineEffectBasis.Row, false, true, 170, false, EngineEffectWait.CardNotFound, "Waiting" },
        { "card not found at the deadline", EngineEffectBasis.Row, false, true, 180, false,
            EngineEffectWait.CardNotFound, "NotCounted" },
        { "completed within the window", EngineEffectBasis.Row, false, true, 1, false, null, "Waiting" },
        { "completed past the window", EngineEffectBasis.Row, false, true, 2, false, null, "Missing" },
        { "downstream within the deadline", EngineEffectBasis.Row, false, true, 170, true, null, "Waiting" },
        { "downstream past the deadline", EngineEffectBasis.Row, false, true, 180, true, null, "Missing" },
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
        int minutes, bool downstream, string? waitingOn, string expected)
    {
        var observedAt = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var completedAt = observedAt.AddHours(1);
        var now = (completed ? completedAt : observedAt).AddMinutes(minutes);
        var check = new EngineEffectCheck("orch_run", "AcceptAndIntegrate", verified,
            verified ? [] : ["IntegrationJournal"], name, now, basis, completed ? completedAt : null, downstream,
            waitingOn);
        Assert.Equal(Enum.Parse<ApplierCanaryVerdict>(expected), ApplierCanaryPolicy.Judge(check, observedAt, now,
            TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
    }

    /// <summary>
    /// An applier that has not completed gets at least one effect window after
    /// its integration ended, even when that is past the apply deadline.
    /// </summary>
    [Theory]
    [InlineData(1, "Waiting")]
    [InlineData(2, "Missing")]
    public void Canary_policy_gives_one_effect_window_after_the_integration_ended(int minutesSinceEnd,
        string expected)
    {
        var observedAt = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var endedAt = observedAt.AddHours(3).AddMinutes(-1);
        var now = endedAt.AddMinutes(minutesSinceEnd);
        var check = new EngineEffectCheck("orch_run", "AcceptAndIntegrate", false, ["DecisionReceipt"], "ended",
            now, IntegrationEndedAt: endedAt);
        Assert.Equal(Enum.Parse<ApplierCanaryVerdict>(expected), ApplierCanaryPolicy.Judge(check, observedAt, now,
            TimeSpan.FromMinutes(2), TimeSpan.FromHours(3)));
    }

    /// <summary>
    /// Downtime is left out: the apply deadline and the effect window start no
    /// earlier than the process start, so a host that was down for longer than
    /// the remaining deadline does not roll back at its first check.
    /// </summary>
    [Theory]
    [InlineData(false, 179, "Waiting")]
    [InlineData(false, 180, "Missing")]
    [InlineData(true, 1, "Waiting")]
    [InlineData(true, 2, "Missing")]
    public void Canary_policy_counts_deadlines_from_the_process_start(bool completed, int minutesSinceStart,
        string expected)
    {
        var observedAt = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var startedAt = observedAt.AddHours(5);
        var now = startedAt.AddMinutes(minutesSinceStart);
        var check = new EngineEffectCheck("orch_run", "HumanReview", false, ["LaneMove"], "restart", now,
            CompletedAt: completed ? observedAt.AddMinutes(1) : null);
        Assert.Equal(Enum.Parse<ApplierCanaryVerdict>(expected), ApplierCanaryPolicy.Judge(check, observedAt, now,
            TimeSpan.FromMinutes(2), TimeSpan.FromHours(3), startedAt));
    }
}
