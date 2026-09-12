namespace AgentRunner;

/// <summary>
/// Identifies claim failures that mean this daemon identity itself has lost
/// registration and must register again before polling. A 409 LeaseExpired no
/// longer belongs here: <c>ClaimNextReview</c> requeues a stale lease at the
/// queue head itself (AGT-2762) instead of surfacing LeaseExpired to the
/// poller, so a claim response no longer carries that code, and treating it as
/// a registration-recovery trigger only caused an unnecessary re-registration
/// loop while the daemon's identity was still perfectly valid.
/// </summary>
internal static class ReviewClaimRegistrationRecovery
{
    public static bool IsRequired(TaskServerException exception)
        => exception.StatusCode == 409
           && string.Equals(
               exception.ErrorCode,
               "review-executor-not-registered",
               StringComparison.OrdinalIgnoreCase);
}
