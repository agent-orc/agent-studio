using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for <see cref="AutoReviewResumePolicy"/> (AGT-2860). The
/// 17.09.2026 restart produced two stranded shapes the deferral budget could
/// not distinguish from a healthy wait; every row below is one of the states
/// that sweep has to tell apart before it touches Git or a lane.
/// </summary>
public sealed class AutoReviewResumePolicyTests
{
    private static AutoReviewResumeDecision Decide(
        string lane = TaskStates.AutoReview,
        bool fixtureCard = false,
        AttemptLifecycleState? reviewState = AttemptLifecycleState.Completed,
        ReviewTerminalOutcome? outcome = ReviewTerminalOutcome.Pass,
        bool deliveryMerged = false,
        RemoteDeliverySettlementStage? stage = RemoteDeliverySettlementStage.IntegrationPending,
        bool shouldIntegrate = true,
        bool gateInFlight = false)
        => AutoReviewResumePolicy.Decide(
            lane, fixtureCard, reviewState, outcome, deliveryMerged, stage, shouldIntegrate, gateInFlight);

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.Completed)]
    public void A_card_outside_auto_review_is_never_resumed(string lane)
    {
        var decision = Decide(lane: lane);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.OutsideAutoReview, decision.Reason);
    }

    [Fact]
    public void A_fixture_card_carries_no_real_delivery()
    {
        var decision = Decide(fixtureCard: true);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.OutsideAutoReview, decision.Reason);
    }

    [Fact]
    public void A_card_without_a_canonical_review_attempt_is_not_this_services_business()
    {
        var decision = Decide(reviewState: null, outcome: null);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.NoCanonicalReviewAttempt, decision.Reason);
    }

    [Theory]
    [InlineData(AttemptLifecycleState.Pending)]
    [InlineData(AttemptLifecycleState.Leased)]
    public void An_open_attempt_still_belongs_to_the_executor(AttemptLifecycleState state)
    {
        var decision = Decide(reviewState: state, outcome: null);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(PostProcessingCardResult.AwaitingCanonicalReviewVerdict, decision.Reason);
    }

    [Theory]
    [InlineData(ReviewTerminalOutcome.ProductFailure)]
    [InlineData(ReviewTerminalOutcome.InfrastructureFailure)]
    [InlineData(ReviewTerminalOutcome.Inconclusive)]
    [InlineData(ReviewTerminalOutcome.Cancellation)]
    [InlineData(ReviewTerminalOutcome.Superseded)]
    public void An_outcome_that_never_earned_integration_is_left_alone(ReviewTerminalOutcome outcome)
    {
        var decision = Decide(outcome: outcome);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.ReviewNotPassed, decision.Reason);
    }

    [Fact]
    public void A_passed_review_whose_integration_never_started_starts_it()
    {
        // The AGT-2855 shape: Pass at 08:39, integration queued behind another
        // card's gate, restart, nothing left to pick it up.
        var decision = Decide(stage: RemoteDeliverySettlementStage.IntegrationPending);

        Assert.Equal(AutoReviewResumeAction.StartIntegration, decision.Action);
        Assert.Equal(PostProcessingCardResult.AwaitingDeliveryIntegration, decision.Reason);
    }

    [Theory]
    [InlineData(ReviewTerminalOutcome.Pass)]
    [InlineData(ReviewTerminalOutcome.IntegrationBranchDefect)]
    public void Both_admissible_outcomes_reach_integration(ReviewTerminalOutcome outcome)
    {
        var decision = Decide(outcome: outcome);

        Assert.Equal(AutoReviewResumeAction.StartIntegration, decision.Action);
    }

    [Fact]
    public void A_delivery_already_on_the_integration_branch_is_never_merged_again()
    {
        // The AGT-2854 shape: the killed gate's merge was pushed by a later
        // gate, so only the transition is owed. Ancestry outranks the sidecar -
        // the record still says the merge had not returned.
        var decision = Decide(
            deliveryMerged: true,
            stage: RemoteDeliverySettlementStage.IntegrationPending);

        Assert.Equal(AutoReviewResumeAction.CompleteTransition, decision.Action);
        Assert.Equal(PostProcessingCardResult.AwaitingIntegrationCompletion, decision.Reason);
    }

    [Fact]
    public void A_merged_delivery_completes_even_without_any_settlement_record()
    {
        var decision = Decide(deliveryMerged: true, stage: null);

        Assert.Equal(AutoReviewResumeAction.CompleteTransition, decision.Action);
        Assert.Equal(PostProcessingCardResult.AwaitingIntegrationCompletion, decision.Reason);
    }

    [Fact]
    public void Without_a_settlement_record_an_unmerged_delivery_is_not_integrated_on_trust()
    {
        // The verdicts that decided the build/test gate live only in the report
        // payload. "Pass" alone would admit exactly the delivery the gate
        // exists to refuse, so this waits for an operator instead of guessing.
        var decision = Decide(stage: null);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.NoSettlementRecord, decision.Reason);
    }

    [Fact]
    public void A_refused_delivery_gate_still_owes_the_card_its_transition()
    {
        var decision = Decide(shouldIntegrate: false);

        Assert.Equal(AutoReviewResumeAction.CompleteTransition, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.DeliveryGateFailed, decision.Reason);
    }

    [Theory]
    [InlineData(RemoteDeliverySettlementStage.IntegrationSettled)]
    [InlineData(RemoteDeliverySettlementStage.LaneSettled)]
    public void An_integration_that_already_returned_only_owes_the_transition(
        RemoteDeliverySettlementStage stage)
    {
        var decision = Decide(stage: stage);

        Assert.Equal(AutoReviewResumeAction.CompleteTransition, decision.Action);
        Assert.Equal(PostProcessingCardResult.AwaitingIntegrationCompletion, decision.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_open_integration_gate_journal_keeps_the_resume_out(bool deliveryMerged)
    {
        // A live gate owns the card, or AGT-2849's boot recovery has not judged
        // its merge yet. Ancestry proves nothing while that is true: the merge
        // on the branch may be exactly the one about to be rolled back.
        var decision = Decide(deliveryMerged: deliveryMerged, gateInFlight: true);

        Assert.Equal(AutoReviewResumeAction.None, decision.Action);
        Assert.Equal(AutoReviewResumePolicy.Reasons.GateRecoveryPending, decision.Reason);
    }

    [Fact]
    public void Every_resumable_reason_is_a_wait_the_deferral_budget_must_never_exhaust()
    {
        // Contract between the policy and AutoReviewPostProcessingWorker: the
        // reasons the resume acts on are exactly the ones that never exhaust.
        Assert.True(PostProcessingCardResult.IsDeliveryResumeWait(
            PostProcessingCardResult.AwaitingDeliveryIntegration));
        Assert.True(PostProcessingCardResult.IsDeliveryResumeWait(
            PostProcessingCardResult.AwaitingIntegrationCompletion));
        Assert.False(PostProcessingCardResult.IsDeliveryResumeWait(
            PostProcessingCardResult.AwaitingCanonicalReviewVerdict));
        Assert.False(PostProcessingCardResult.IsDeliveryResumeWait(
            PostProcessingCardResult.AwaitingReviewExecutorRegistration));
    }

    [Fact]
    public void Every_canonical_wait_keeps_the_review_plane_backoff()
    {
        Assert.All(
            new[]
            {
                PostProcessingCardResult.AwaitingReviewExecutorRegistration,
                PostProcessingCardResult.AwaitingCanonicalReviewVerdict,
                PostProcessingCardResult.AwaitingDeliveryIntegration,
                PostProcessingCardResult.AwaitingIntegrationCompletion,
            },
            reason => Assert.True(PostProcessingCardResult.IsCanonicalReviewWait(reason)));
        Assert.False(PostProcessingCardResult.IsCanonicalReviewWait("card-already-in-flight"));
    }
}
