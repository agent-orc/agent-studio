using System.Net.Http;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The daemon used to exit with code 4 whenever a claim poll hit a momentarily
/// unreachable Task Server, which killed the whole service cgroup and stranded
/// every in-flight lease (the 2026-07-21 twin crashes). The fix hinges on
/// classifying which faults are a transient server blip - retried in the poll
/// loop - versus a real fault that must still surface. These pin that boundary.
/// </summary>
public class RemoteRunnerDaemonFaultTests
{
    [Fact]
    public void Transport_failures_are_transient()
        => Assert.True(RemoteRunnerDaemon.IsTransientServerFault(
            new HttpRequestException("An error occurred while sending the request.")));

    [Fact]
    public void Request_timeouts_are_transient()
        => Assert.True(RemoteRunnerDaemon.IsTransientServerFault(new TaskCanceledException()));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Server_side_status_codes_are_transient(int status)
        => Assert.True(RemoteRunnerDaemon.IsTransientServerFault(
            new TaskServerException(status, $"POST /claim -> {status}")));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(409)]
    [InlineData(426)]
    public void Client_side_status_codes_are_not_transient(int status)
        => Assert.False(RemoteRunnerDaemon.IsTransientServerFault(
            new TaskServerException(status, $"POST /claim -> {status}")));

    [Fact]
    public void Unexpected_exceptions_are_not_transient()
        => Assert.False(RemoteRunnerDaemon.IsTransientServerFault(
            new InvalidOperationException("Git push capability is read-only")));

    [Fact]
    public void Unregistered_review_claim_requires_full_registration()
        => Assert.True(ReviewClaimRegistrationRecovery.IsRequired(
            new TaskServerException(409, "claim rejected", "review-executor-not-registered")));

    // AGT-2762: ClaimNextReview now requeues a stale lease at the queue head
    // itself instead of surfacing LeaseExpired to the poller, so a claim
    // response answering LeaseExpired no longer means this daemon identity
    // lost registration - treating it as one caused an unnecessary
    // re-registration loop while the identity was still valid.
    [Theory]
    [InlineData("LeaseExpired")]
    [InlineData("review-baseline-comparison-required")]
    public void Other_claim_conflicts_do_not_trigger_registration_recovery(string errorCode)
        => Assert.False(ReviewClaimRegistrationRecovery.IsRequired(
            new TaskServerException(409, "claim rejected", errorCode)));
}
