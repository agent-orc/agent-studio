namespace AgentStudio.Runner;

/// <summary>
/// Why one card's post-processing pass ended the way it did.
/// <para>
/// The run-boundary queue used to treat every pass that came back without a
/// verdict as a failure and wrote <c>post-processing-blocked</c> with a single
/// generic sentence. That conflated three very different things: a genuine
/// precondition failure, a decision that had already been recorded, and the
/// entirely legitimate hand-off "this card belongs to the canonical review
/// executor, not to me". The third case is self-healing and must never end
/// terminal - a card waiting for its fenced <c>ReviewAttempt</c> executor is
/// healthy, not broken.
/// </para>
/// </summary>
public enum PostProcessingCardStatus
{
    /// <summary>
    /// A decision path ran for this card and owns the lifecycle outcome. The
    /// queue keeps its safety net: if the card is still sitting in
    /// <c>4-auto-review</c> with an active lifecycle afterwards, no verdict
    /// actually landed and it is terminalized with the recorded reason.
    /// </summary>
    Decided,

    /// <summary>
    /// Not this engine's card right now - another owner is expected to move it,
    /// or a transient budget/concurrency limit was hit. Retryable and never
    /// terminal: the card rests in <c>awaiting-review</c> and is re-driven.
    /// </summary>
    Deferred,

    /// <summary>
    /// A precondition failed that no retry resolves (misconfiguration, an
    /// unresolvable watch path, an unreadable run log). Terminal, but always
    /// carrying the concrete reason rather than the generic sentence.
    /// </summary>
    Blocked,
}

/// <summary>
/// Outcome of <see cref="ReviewDecisionOrchestrator.ProcessCardAsync"/> for a
/// single card. <see cref="Reason"/> is a stable, greppable token (e.g.
/// <c>awaiting-delivery-integration</c>) so a blocked card names its cause
/// in <c>lifecycle.json</c> and in the log line, instead of only saying that
/// something did not happen.
/// </summary>
/// <param name="Status">How the pass ended.</param>
/// <param name="Reason">Stable machine-readable reason token.</param>
public sealed record PostProcessingCardResult(PostProcessingCardStatus Status, string Reason)
{
    public static PostProcessingCardResult Decided(string reason) =>
        new(PostProcessingCardStatus.Decided, reason);

    public static PostProcessingCardResult Deferred(string reason) =>
        new(PostProcessingCardStatus.Deferred, reason);

    public static PostProcessingCardResult Blocked(string reason) =>
        new(PostProcessingCardStatus.Blocked, reason);

    /// <summary>
    /// Reason tokens for the waits in which the card is held by the canonical
    /// remote review data plane. Shared with the tests so the contract cannot
    /// drift silently.
    ///
    /// <para>
    /// These used to be one token, <c>awaiting-canonical-review-executor</c>,
    /// which named the executor even when the executor was registered and busy
    /// and something else entirely was missing. After the 17.09.2026 restart
    /// fifteen cards logged that reason while the real states were three
    /// different ones - no executor registered, a passed review whose delivery
    /// integration had not started, and a delivery already on the integration
    /// branch whose lane transition was still pending. The split says which
    /// (AGT-2860).
    /// </para>
    /// </summary>
    public const string AwaitingReviewExecutorRegistration = "awaiting-review-executor-registration";

    /// <summary>An executor is registered; the canonical ReviewAttempt has not settled yet.</summary>
    public const string AwaitingCanonicalReviewVerdict = "awaiting-canonical-review-verdict";

    /// <summary>The review settled Pass; <c>remote-delivery-integration</c> has not started.</summary>
    public const string AwaitingDeliveryIntegration = "awaiting-delivery-integration";

    /// <summary>The delivery is on the integration branch; the lane transition out of Auto Review is pending.</summary>
    public const string AwaitingIntegrationCompletion = "awaiting-integration-completion";

    /// <summary>
    /// True for every wait whose owner is the canonical remote review data
    /// plane rather than this queue. Callers that used to compare against the
    /// single old token ask this instead, so adding a further split does not
    /// silently drop a card out of the tightened backoff or out of the named
    /// liveStatus wait.
    /// </summary>
    public static bool IsCanonicalReviewWait(string? reason)
        => reason is AwaitingReviewExecutorRegistration
            or AwaitingCanonicalReviewVerdict
            or AwaitingDeliveryIntegration
            or AwaitingIntegrationCompletion;

    /// <summary>
    /// True for the two waits that a terminal <c>Pass</c> attempt has already
    /// earned. They are never exhausted and never terminal: the delivery is
    /// reviewed, so the only honest states left are "integrate it" and "finish
    /// the transition", both of which this backend can drive itself.
    /// </summary>
    public static bool IsDeliveryResumeWait(string? reason)
        => reason is AwaitingDeliveryIntegration or AwaitingIntegrationCompletion;
}
