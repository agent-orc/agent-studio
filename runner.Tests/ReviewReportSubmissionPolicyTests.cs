using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewReportSubmissionPolicyTests
{
    [Theory]
    [InlineData(null, null, true, ReviewReportSubmissionAction.Retry)]
    [InlineData(null, null, false, ReviewReportSubmissionAction.Retry)]
    [InlineData(408, "request-timeout", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(429, "rate-limited", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(500, null, false, ReviewReportSubmissionAction.Retry)]
    [InlineData(503, "temporary-overload", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(503, "task-not-found", false, ReviewReportSubmissionAction.TerminalTaskMissing)]
    [InlineData(404, "not-found", false, ReviewReportSubmissionAction.TerminalTaskMissing)]
    [InlineData(409, "Superseded", false, ReviewReportSubmissionAction.TerminalSuperseded)]
    [InlineData(409, "stale-review-authority", false, ReviewReportSubmissionAction.TerminalRejected)]
    // AGT-2762: ClaimNextReview requeues stale leases itself, so a claim no
    // longer answers LeaseExpired - but a report submission still could in
    // principle, and it is not replayable (a fresh claim already superseded
    // this fence), so it stays a terminal rejection rather than a retry.
    [InlineData(409, "LeaseExpired", false, ReviewReportSubmissionAction.TerminalRejected)]
    public void Decide_classifies_retryable_and_terminal_failures(
        int? statusCode,
        string? errorCode,
        bool transportFailure,
        ReviewReportSubmissionAction expected)
        => Assert.Equal(
            expected,
            ReviewReportSubmissionPolicy.Decide(statusCode, errorCode, transportFailure));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(10, 60)]
    public void RetryDelay_backs_off_exponentially_on_transport_failures(
        int consecutiveFailures,
        int expectedSeconds)
        => Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            ReviewReportSubmissionPolicy.RetryDelay(consecutiveFailures, transportFailure: true, pollSeconds: 5));

    [Fact]
    public void RetryDelay_uses_the_poll_scaled_delay_for_non_transport_failures()
        => Assert.Equal(
            TaskServerConnectivityMonitor.RetryDelay(pollSeconds: 5, consecutiveFailures: 3),
            ReviewReportSubmissionPolicy.RetryDelay(3, transportFailure: false, pollSeconds: 5));
}
