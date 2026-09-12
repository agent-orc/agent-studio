namespace AgentRunner;

/// <summary>What a review heartbeat does with a refused lease renewal.</summary>
internal enum ReviewLeaseRecoveryAction
{
    /// <summary>Transient fault: keep the lease and renew again on the next tick.</summary>
    RetryLater,

    /// <summary>
    /// The renewal window closed but the durable authority may still match.
    /// Re-register the executor with its fenced inventory and renew again.
    /// </summary>
    ReRegister,

    /// <summary>
    /// Re-adoption is impossible or was refused. The worker is still proven
    /// alive, so take the attempt over under a higher fence instead of dropping
    /// its report.
    /// </summary>
    ReClaim,

    /// <summary>
    /// The attempt is gone or was superseded on purpose. No fence can be
    /// regained; the run reports what it has and lets the server reject it.
    /// </summary>
    Abandon,
}

/// <summary>
/// Pure decision for a refused review lease renewal. The runner never guesses
/// from a message: it branches on the transport outcome and the published
/// Task Server error code only.
/// </summary>
internal static class ReviewLeaseRecoveryPolicy
{
    /// <summary>Superseded authority is deliberate and must never be re-claimed.</summary>
    private const string Superseded = "Superseded";

    private static readonly HashSet<string> RecoverableReportAuthorityCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "LeaseExpired",
            "review-lease-expired",
            "review-attempt-not-leased",
            "review-lease-not-active",
            "stale-review-authority",
            "stale-review-fence",
            "review-execution-attribution-mismatch",
        };

    internal static ReviewLeaseRecoveryAction Decide(
        int? statusCode,
        string? errorCode,
        bool transportFailure)
    {
        if (transportFailure || statusCode is null or 408 or 429 or >= 500)
            return ReviewLeaseRecoveryAction.RetryLater;
        if (string.Equals(errorCode, Superseded, StringComparison.OrdinalIgnoreCase))
            return ReviewLeaseRecoveryAction.Abandon;
        return statusCode switch
        {
            // The attempt no longer exists on this Task Server. A takeover has
            // nothing to fence.
            404 => ReviewLeaseRecoveryAction.Abandon,
            // The durable record disagrees with the handed-off lease. Expiry
            // alone is repairable by re-registration; anything else falls
            // through to the takeover below.
            409 => ReviewLeaseRecoveryAction.ReRegister,
            _ => ReviewLeaseRecoveryAction.Abandon,
        };
    }

    /// <summary>
    /// Follow-up decision once the re-registration answer is known. Adoption
    /// restores the same fence; a refusal leaves the takeover as the only way
    /// to keep the running worker's gate work.
    /// </summary>
    internal static ReviewLeaseRecoveryAction AfterReRegistration(bool adopted, bool workerLive)
        => adopted
            ? ReviewLeaseRecoveryAction.RetryLater
            : workerLive
                ? ReviewLeaseRecoveryAction.ReClaim
                : ReviewLeaseRecoveryAction.Abandon;

    /// <summary>
    /// A report-side 409 is not automatically terminal. These codes mean the
    /// report was built from authority the server no longer accepts, so the
    /// executor must run the same re-adopt/re-claim flow as a heartbeat before
    /// it is allowed to delete the expensive completed workspace.
    /// </summary>
    internal static bool IsRecoverableReportAuthorityRejection(
        int? statusCode,
        string? errorCode)
        => statusCode == 409
           && errorCode is not null
           && RecoverableReportAuthorityCodes.Contains(errorCode);
}
