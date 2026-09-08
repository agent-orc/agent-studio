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

    [Fact]
    public void Lease_expired_no_longer_triggers_registration_recovery()
        // AGT-2762: ClaimNextReview requeues a stale lease itself instead of
        // surfacing LeaseExpired, so this code no longer means the daemon's
        // own registration was lost.
        => Assert.False(ReviewClaimRegistrationRecovery.IsRequired(
            new TaskServerException(409, "claim rejected", "LeaseExpired")));

    [Fact]
    public void Other_claim_conflicts_do_not_trigger_registration_recovery()
        => Assert.False(ReviewClaimRegistrationRecovery.IsRequired(
            new TaskServerException(409, "claim rejected", "review-baseline-comparison-required")));
}
