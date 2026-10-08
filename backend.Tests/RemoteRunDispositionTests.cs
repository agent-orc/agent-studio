using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteRunDispositionTests
{
    [Fact]
    public void Operator_revoke_fences_the_old_completion_and_renewal()
    {
        var authority = new AttemptAuthorityService(NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun("AGT-3010", "PROJ-002", null, "runner", "host", 120,
            "claim").RunAttempt!;

        Assert.True(authority.RevokeRunForOperatorMove("AGT-3010", run.AttemptId, "operator moved to backlog"));
        Assert.True(authority.CurrentFence("AGT-3010") > run.LastFence);
        Assert.Equal(AttemptWriteStatus.Superseded, authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "complete"),
            ExecutorId = "runner", LeaseId = run.Lease!.LeaseId, ExpectedTaskKey = "AGT-3010", Outcome = "done",
            ResultSha = new string('a', 40),
        }).Status);
        Assert.Equal(AttemptWriteStatus.Superseded, authority.RenewRun(
            new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "renew"),
            "runner", 120, run.Lease.LeaseId).Status);
        var leaseReply = new RunLeaseService(NullLogger<RunLeaseService>.Instance, authority).Renew(
            new RunLeaseHeartbeatRequest("AGT-3010", run.Lease.LeaseId, run.LastFence,
                "runner", 120, run.AttemptId, run.AuthorityEpoch, "heartbeat"));
        Assert.False(leaseReply.Granted);
        Assert.Contains("revoked", leaseReply.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_steer_keeps_the_claimed_attempt_deliverable()
    {
        var authority = new AttemptAuthorityService(NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun("AGT-3010", "PROJ-002", null, "runner", "host", 120,
            "claim").RunAttempt!;

        Assert.Equal(RunMoveIntent.Steer, RunDispositionPolicy.ParseMoveIntent("steer"));
        Assert.Equal(AttemptWriteStatus.Accepted, authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "complete"),
            ExecutorId = "runner", LeaseId = run.Lease!.LeaseId, ExpectedTaskKey = "AGT-3010", Outcome = "done",
            ResultSha = new string('a', 40),
        }).Status);
    }

    [Fact]
    public void Edited_brief_offers_completion_instead_of_applying_it()
    {
        var claimed = RunDispositionPolicy.BriefVersion("Original brief");
        var current = RunDispositionPolicy.BriefVersion("Sharpened brief");
        Assert.Equal(RemoteCompletionDisposition.OfferOlderBrief,
            RunDispositionPolicy.DecideCompletion(claimed, current));
        Assert.Equal(RemoteCompletionDisposition.Apply,
            RunDispositionPolicy.DecideCompletion(claimed, claimed));
    }
}
