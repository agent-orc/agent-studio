namespace AgentStudio.Runner;

/// <summary>What a restarted backend owes a card that is still sitting in <c>4-auto-review</c>.</summary>
public enum AutoReviewResumeAction
{
    /// <summary>Nothing to resume: the canonical review data plane still owns this card.</summary>
    None,

    /// <summary>The review passed and the delivery is not on the integration branch: run <c>remote-delivery-integration</c>.</summary>
    StartIntegration,

    /// <summary>
    /// The delivery is already on the integration branch, or the gate refused
    /// it. Nothing may touch Git; only the lane transition out of Auto Review
    /// is still owed.
    /// </summary>
    CompleteTransition,
}

/// <param name="Action">What to do.</param>
/// <param name="Reason">Stable token, shared with the deferral wait reasons so the log and the board agree.</param>
public sealed record AutoReviewResumeDecision(AutoReviewResumeAction Action, string Reason);

/// <summary>
/// Pure decision behind restart-safe resume of the post-review delivery
/// sequence (AGT-2860).
///
/// <para>
/// The sequence "settle review -> decide the delivery gate -> integrate -> move
/// out of Auto Review" ran inside one HTTP request. A restart in the middle of
/// it left two shapes on the board on 17.09.2026: AGT-2855 with a terminal
/// <c>Pass</c> at 08:39, <c>integration: pending</c>, and an integration that
/// never started; and AGT-2854 whose merge had been pushed by a later gate
/// while the card itself never left <c>4-auto-review</c>. Both are resumable
/// without a new review - the subject already passed - so the deferral budget
/// must never be what decides their fate.
/// </para>
/// <para>
/// Ancestry on the integration branch outranks the sidecar. A delivery the
/// branch already contains must never be merged again, no matter what a record
/// written before the merge says; that is the AGT-2854 half, and it is also the
/// reason this policy cannot be expressed as "replay the interrupted request".
/// </para>
/// </summary>
public static class AutoReviewResumePolicy
{
    public static class Reasons
    {
        public const string OutsideAutoReview = "outside-auto-review";
        public const string NoCanonicalReviewAttempt = "no-canonical-review-attempt";
        public const string ReviewNotPassed = "review-not-passed";
        public const string NoSettlementRecord = "no-delivery-settlement-record";
        public const string GateRecoveryPending = "integration-gate-recovery-pending";
        public const string DeliveryGateFailed = "delivery-gate-failed";
    }

    /// <summary>
    /// Decides one card. Every input is a primitive fact the caller has already
    /// read, so the matrix is testable without a workspace, an authority store,
    /// or a Git repository.
    /// </summary>
    /// <param name="laneState">Lane the card currently occupies.</param>
    /// <param name="fixtureCard">Demo/fixture cards never carry a real delivery.</param>
    /// <param name="reviewState">Lifecycle state of the card's current ReviewAttempt, or null when it has none.</param>
    /// <param name="reviewOutcome">Terminal outcome of that attempt, or null while it is still open.</param>
    /// <param name="deliveryMerged">The delivery is reachable from the integration branch (<see cref="IntegrationStatuses.IsMerged"/>).</param>
    /// <param name="settlementStage">Stage of the delivery settlement sidecar, or null when absent or superseded by a newer attempt.</param>
    /// <param name="settlementShouldIntegrate">What the delivery gate decided when the review settled.</param>
    /// <param name="integrationGateInFlight">An integration-gate journal entry is still open for this card (AGT-2849).</param>
    public static AutoReviewResumeDecision Decide(
        string? laneState,
        bool fixtureCard,
        AttemptLifecycleState? reviewState,
        ReviewTerminalOutcome? reviewOutcome,
        bool deliveryMerged,
        RemoteDeliverySettlementStage? settlementStage,
        bool settlementShouldIntegrate,
        bool integrationGateInFlight = false)
    {
        if (fixtureCard || !string.Equals(laneState, TaskStates.AutoReview, StringComparison.Ordinal))
            return new AutoReviewResumeDecision(AutoReviewResumeAction.None, Reasons.OutsideAutoReview);

        if (reviewState is null)
        {
            return new AutoReviewResumeDecision(
                AutoReviewResumeAction.None, Reasons.NoCanonicalReviewAttempt);
        }

        if (reviewState is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased)
        {
            // The executor still owns the attempt. Whether the wait is healthy
            // is the registry's question, not this policy's.
            return new AutoReviewResumeDecision(
                AutoReviewResumeAction.None, PostProcessingCardResult.AwaitingCanonicalReviewVerdict);
        }

        if (!IsAdmissibleOutcome(reviewOutcome))
            return new AutoReviewResumeDecision(AutoReviewResumeAction.None, Reasons.ReviewNotPassed);

        // An open gate journal means either a live gate owns this card right
        // now or AGT-2849's boot recovery has not judged its merge yet. Until
        // that is settled, ancestry on the integration branch proves nothing -
        // the merge it shows may be about to be rolled back - and a second
        // driver of the same card is exactly what nobody needs.
        if (integrationGateInFlight)
            return new AutoReviewResumeDecision(AutoReviewResumeAction.None, Reasons.GateRecoveryPending);

        // AGT-2854: the merge is on the branch, so the gate that created it has
        // nothing left to do and the record it would have been read from is
        // irrelevant. Only the transition is owed.
        if (deliveryMerged)
        {
            return new AutoReviewResumeDecision(
                AutoReviewResumeAction.CompleteTransition,
                PostProcessingCardResult.AwaitingIntegrationCompletion);
        }

        // Without the sidecar the gate verdict cannot be reconstructed: the
        // aspect verdicts that decided it live in the report payload, not in
        // the attempt. Integrating on the strength of "Pass" alone would admit
        // exactly the delivery the gate exists to refuse, so this waits for an
        // operator instead of guessing.
        if (settlementStage is null)
            return new AutoReviewResumeDecision(AutoReviewResumeAction.None, Reasons.NoSettlementRecord);

        if (!settlementShouldIntegrate)
        {
            return new AutoReviewResumeDecision(
                AutoReviewResumeAction.CompleteTransition, Reasons.DeliveryGateFailed);
        }

        return settlementStage == RemoteDeliverySettlementStage.IntegrationPending
            ? new AutoReviewResumeDecision(
                AutoReviewResumeAction.StartIntegration,
                PostProcessingCardResult.AwaitingDeliveryIntegration)
            : new AutoReviewResumeDecision(
                AutoReviewResumeAction.CompleteTransition,
                PostProcessingCardResult.AwaitingIntegrationCompletion);
    }

    /// <summary>
    /// Projects a resume decision onto the stable post-processing wait tokens.
    /// Internal safety reasons remain canonical-review waits; a missing
    /// settlement retains the existing "integration not started" label because
    /// there is no durable evidence that integration returned.
    /// </summary>
    public static string ClassifyPostProcessingWait(AutoReviewResumeDecision decision)
        => decision.Action switch
        {
            AutoReviewResumeAction.StartIntegration =>
                PostProcessingCardResult.AwaitingDeliveryIntegration,
            AutoReviewResumeAction.CompleteTransition =>
                PostProcessingCardResult.AwaitingIntegrationCompletion,
            _ when decision.Reason == Reasons.NoSettlementRecord =>
                PostProcessingCardResult.AwaitingDeliveryIntegration,
            _ => PostProcessingCardResult.AwaitingCanonicalReviewVerdict,
        };

    /// <summary>
    /// The two outcomes that earn integration. <see cref="ReviewTerminalOutcome.IntegrationBranchDefect"/>
    /// is admissible for the same reason the live gate admits it (AGT-2819):
    /// the red gate belongs to the branch, not to this delivery.
    /// </summary>
    public static bool IsAdmissibleOutcome(ReviewTerminalOutcome? outcome)
        => outcome is ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect;
}
