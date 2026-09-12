using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Direct matrix for the refused-renewal decision. The distinction that matters
/// operationally: a deliberately superseded attempt is never taken back, while a
/// merely expired or moved lease is repaired so the running worker keeps its
/// gate work.
/// </summary>
public sealed class ReviewLeaseRecoveryPolicyTests
{
    [Theory]
    [InlineData(null, null, true, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(null, null, false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(408, "timeout", false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(429, "too-many-requests", false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(500, "server-error", false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(503, "unavailable", false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(404, "not-found", false, ReviewLeaseRecoveryAction.Abandon)]
    [InlineData(409, "review-attempt-not-leased", false, ReviewLeaseRecoveryAction.ReRegister)]
    [InlineData(409, "stale-review-authority", false, ReviewLeaseRecoveryAction.ReRegister)]
    [InlineData(409, "LeaseExpired", false, ReviewLeaseRecoveryAction.ReRegister)]
    [InlineData(409, "Superseded", false, ReviewLeaseRecoveryAction.Abandon)]
    [InlineData(400, "invalid-request", false, ReviewLeaseRecoveryAction.Abandon)]
    [InlineData(401, "unauthorized", false, ReviewLeaseRecoveryAction.Abandon)]
    internal void Refused_renewal_maps_to_its_recovery(
        int? statusCode,
        string? errorCode,
        bool transportFailure,
        ReviewLeaseRecoveryAction expected)
        => Assert.Equal(
            expected,
            ReviewLeaseRecoveryPolicy.Decide(statusCode, errorCode, transportFailure));

    [Fact]
    internal void A_transport_failure_outranks_the_status_code_it_carries()
        => Assert.Equal(
            ReviewLeaseRecoveryAction.RetryLater,
            ReviewLeaseRecoveryPolicy.Decide(404, "not-found", transportFailure: true));

    [Theory]
    [InlineData(true, true, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(true, false, ReviewLeaseRecoveryAction.RetryLater)]
    [InlineData(false, true, ReviewLeaseRecoveryAction.ReClaim)]
    [InlineData(false, false, ReviewLeaseRecoveryAction.Abandon)]
    internal void Takeover_needs_a_refused_re_adoption_and_a_living_worker(
        bool adopted,
        bool workerLive,
        ReviewLeaseRecoveryAction expected)
        => Assert.Equal(
            expected,
            ReviewLeaseRecoveryPolicy.AfterReRegistration(adopted, workerLive));

    [Theory]
    [InlineData(409, "LeaseExpired", true)]
    [InlineData(409, "review-lease-expired", true)]
    [InlineData(409, "review-lease-not-active", true)]
    [InlineData(409, "stale-review-fence", true)]
    [InlineData(409, "review-execution-attribution-mismatch", true)]
    [InlineData(409, "Superseded", false)]
    [InlineData(409, "review-subject-mismatch", false)]
    [InlineData(404, "LeaseExpired", false)]
    internal void Report_rejections_recover_only_published_authority_conflicts(
        int? statusCode,
        string? errorCode,
        bool expected)
        => Assert.Equal(
            expected,
            ReviewLeaseRecoveryPolicy.IsRecoverableReportAuthorityRejection(
                statusCode,
                errorCode));
}
