using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AttemptAuthorityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "attempt-authority-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Restart_preserves_attempt_lease_expiry_fence_epoch_and_review_subject()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var first = NewService(() => now);
        var run = first.AcquireRun("AGT-1", "PROJ-1", null, "runner-a", "host-a", 120, "claim-1");
        Assert.Equal(AttemptWriteStatus.Accepted, run.Status);
        var settled = first.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.RunAttempt!.AttemptId,
                run.RunAttempt.LastFence,
                run.RunAttempt.AuthorityEpoch,
                "completion-1"),
            Outcome = "done",
            ResultSha = "589c462f",
        });
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
        var cleanupReference = new AttemptWriteReference(
            run.RunAttempt.AttemptId, run.RunAttempt.LastFence, run.RunAttempt.AuthorityEpoch, "cleanup-1");
        Assert.Equal(AttemptWriteStatus.Accepted, first.ReleaseRun(cleanupReference, "runner-a").Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, first.ReleaseRun(cleanupReference, "runner-a").Status);
        var review = first.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "589c462f", run.RunAttempt.AttemptId,
            "requirements-hash", "policy-hash", ["artifact:abc"], "review-create-1"));
        var reviewLease = first.ClaimReview(
            review.ReviewAttempt!.AttemptId, "reviewer", "review-host", 120, "review-claim-1").ReviewAttempt!;

        var restarted = NewService(() => now.AddSeconds(30));
        var projection = restarted.GetTaskProjection("agt-1");

        Assert.Equal(run.RunAttempt.AttemptId, projection.CurrentRunAttempt!.AttemptId);
        Assert.Equal(run.RunAttempt.LastFence, projection.CurrentRunAttempt.LastFence);
        Assert.Equal(run.RunAttempt.AuthorityEpoch, projection.AuthorityEpoch);
        Assert.Equal(AttemptLifecycleState.Completed, projection.CurrentRunAttempt.State);
        Assert.Equal("runner-a", projection.CurrentRunAttempt.Lease!.ExecutorId);
        Assert.Equal("host-a", projection.CurrentRunAttempt.Lease.HostId);
        Assert.Equal("589c462f", projection.CurrentReviewSubject!.ExpectedResultSha);
        Assert.Equal(review.ReviewAttempt!.AttemptId, projection.CurrentReviewAttempt!.AttemptId);
        Assert.Equal(reviewLease.LastFence, projection.CurrentReviewAttempt.LastFence);
        Assert.Equal(reviewLease.Lease!.ExpiresAt, projection.CurrentReviewAttempt.Lease!.ExpiresAt);
        Assert.Equal(AttemptWriteStatus.Duplicate, restarted.ClaimReview(
            review.ReviewAttempt.AttemptId, "reviewer", "review-host", 120, "review-claim-1").Status);

        var next = restarted.AcquireRun(
            "agt-1", "PROJ-1", run.RunAttempt.AttemptId, "runner-b", "host-b", 120, "claim-2").RunAttempt!;
        Assert.True(next.LastFence > reviewLease.LastFence);
    }

    [Fact]
    public void Registration_re_adopts_expired_coding_and_review_leases_without_changing_fences()
    {
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var first = NewService(() => now);
        var run = first.AcquireRun(
            "AGT-1", "PROJ-1", null, "coding-runner", "host-a", 30, "coding-claim",
            clientId: "coding-runner", leaseInstanceId: "coding-instance").RunAttempt!;
        var completed = first.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "coding-complete"),
            Outcome = "done",
            ResultSha = "sha-a",
        });
        Assert.True(completed.Accepted);
        var review = first.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", run.AttemptId,
            "requirements", "policy", [], "review-create")).ReviewAttempt!;
        var claimedReview = first.ClaimReview(
            review.AttemptId,
            "review-runner",
            "host-a",
            30,
            "review-claim",
            "review-instance").ReviewAttempt!;

        var coding = first.AcquireRun(
            "AGT-2", "PROJ-1", null, "coding-runner", "host-a", 30, "coding-claim-2",
            clientId: "coding-runner", leaseInstanceId: "coding-instance").RunAttempt!;
        now = now.AddMinutes(2);
        var restarted = NewService(() => now);

        var codingAdoption = Assert.Single(restarted.ReAdoptRunnerAttempts(
            "coding-runner",
            "host-a",
            "coding-instance",
            [new AgentStudio.TaskServer.Contracts.RunnerActiveAttempt(
                AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Coding,
                coding.AttemptId,
                coding.TaskKey,
                coding.Lease!.LeaseId,
                coding.LastFence,
                coding.AuthorityEpoch,
                "coding-instance")],
            120));
        var reviewAdoption = Assert.Single(restarted.ReAdoptRunnerAttempts(
            "review-runner",
            "host-a",
            "replacement-instance",
            [new AgentStudio.TaskServer.Contracts.RunnerActiveAttempt(
                AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Review,
                claimedReview.AttemptId,
                claimedReview.TaskKey,
                claimedReview.Lease!.LeaseId,
                claimedReview.LastFence,
                claimedReview.AuthorityEpoch,
                "review-instance")],
            120));

        Assert.Equal("adopted", codingAdoption.Status);
        Assert.Equal("adopted", reviewAdoption.Status);
        Assert.Equal(coding.LastFence, restarted.GetRun(coding.AttemptId)!.LastFence);
        Assert.Equal(claimedReview.LastFence, restarted.GetReview(claimedReview.AttemptId)!.LastFence);
        Assert.True(restarted.GetRun(coding.AttemptId)!.Lease!.ExpiresAt > now);
        Assert.True(restarted.GetReview(claimedReview.AttemptId)!.Lease!.ExpiresAt > now);

        Assert.Equal(AttemptWriteStatus.Accepted, restarted.SettleReview(
            new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    claimedReview.AttemptId,
                    claimedReview.LastFence,
                    claimedReview.AuthorityEpoch,
                    "report-after-restart"),
                "sha-a",
                ReviewTerminalOutcome.Pass)).Status);
        Assert.Equal(AttemptWriteStatus.Accepted, restarted.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                coding.AttemptId,
                coding.LastFence,
                coding.AuthorityEpoch,
                "coding-report-after-restart"),
            Outcome = "done",
            ResultSha = "sha-b",
        }).Status);
    }

    [Fact]
    public void Registration_cannot_re_adopt_a_stale_fence_or_wrong_lease_instance()
    {
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(
            review.AttemptId,
            "review-runner",
            "host-a",
            30,
            "review-claim",
            "review-instance").ReviewAttempt!;
        var coding = service.AcquireRun(
            "AGT-2", "PROJ-1", null, "coding-runner", "host-a", 30, "coding-claim",
            clientId: "coding-runner", leaseInstanceId: "coding-instance").RunAttempt!;
        now = now.AddMinutes(2);

        var staleFence = Assert.Single(service.ReAdoptRunnerAttempts(
            "review-runner",
            "host-a",
            "review-instance",
            [new AgentStudio.TaskServer.Contracts.RunnerActiveAttempt(
                AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Review,
                claimed.AttemptId,
                claimed.TaskKey,
                claimed.Lease!.LeaseId,
                claimed.LastFence + 1,
                claimed.AuthorityEpoch,
                "review-instance")],
            120));
        var wrongLeaseInstance = Assert.Single(service.ReAdoptRunnerAttempts(
            "review-runner",
            "host-a",
            "replacement-instance",
            [new AgentStudio.TaskServer.Contracts.RunnerActiveAttempt(
                AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Review,
                claimed.AttemptId,
                claimed.TaskKey,
                claimed.Lease!.LeaseId,
                claimed.LastFence,
                claimed.AuthorityEpoch,
                "wrong-lease-instance")],
            120));
        var wrongCodingLeaseInstance = Assert.Single(service.ReAdoptRunnerAttempts(
            "coding-runner",
            "host-a",
            "replacement-instance",
            [new AgentStudio.TaskServer.Contracts.RunnerActiveAttempt(
                AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Coding,
                coding.AttemptId,
                coding.TaskKey,
                coding.Lease!.LeaseId,
                coding.LastFence,
                coding.AuthorityEpoch,
                "wrong-lease-instance")],
            120));

        Assert.Equal("stale-authority", staleFence.Status);
        Assert.Equal("stale-authority", wrongLeaseInstance.Status);
        Assert.Equal("stale-authority", wrongCodingLeaseInstance.Status);
        Assert.True(service.GetReview(claimed.AttemptId)!.Lease!.ExpiresAt <= now);
        Assert.True(service.GetRun(coding.AttemptId)!.Lease!.ExpiresAt <= now);
    }

    [Fact]
    public void Takeover_raises_persisted_fence_and_rejects_stale_or_duplicate_writes_deterministically()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var a = service.AcquireRun("AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        now = now.AddSeconds(31);
        var b = service.AcquireRun("AGT-1", "PROJ-1", a.AttemptId, "runner-b", "host-b", 30, "claim-b").RunAttempt!;

        var stale = service.AcceptRunWrite(new AttemptWriteReference(a.AttemptId, a.LastFence, a.AuthorityEpoch, "log-a"));
        var accepted = service.AcceptRunWrite(new AttemptWriteReference(b.AttemptId, b.LastFence, b.AuthorityEpoch, "log-b"));
        var duplicate = service.AcceptRunWrite(new AttemptWriteReference(b.AttemptId, b.LastFence, b.AuthorityEpoch, "log-b"));

        Assert.True(b.LastFence > a.LastFence);
        Assert.Equal(AttemptWriteStatus.Superseded, stale.Status);
        Assert.Equal(AttemptWriteStatus.Accepted, accepted.Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, duplicate.Status);
    }

    [Fact]
    public void Replayed_acquire_after_takeover_cannot_restore_superseded_authority()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        now = now.AddSeconds(31);
        var replacement = service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "claim-b");

        var replay = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a");

        Assert.Equal(AttemptWriteStatus.Accepted, replacement.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replay.Status);
        Assert.Equal(first.AttemptId, replay.AttemptId);
        Assert.Equal(replacement.AttemptId, service.GetTaskProjection("AGT-1").CurrentRunAttempt!.AttemptId);
    }

    [Fact]
    public void Live_run_cannot_be_renewed_by_reacquiring_with_only_the_same_executor_identity()
    {
        var now = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        var originalExpiry = first.Lease!.ExpiresAt;
        now = now.AddSeconds(10);

        var reacquire = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 120, "claim-b");

        Assert.Equal(AttemptWriteStatus.InvalidState, reacquire.Status);
        Assert.Equal(first.AttemptId, reacquire.AttemptId);
        Assert.Equal(originalExpiry, service.GetRun(first.AttemptId)!.Lease!.ExpiresAt);
    }

    [Fact]
    public void Infrastructure_retry_creates_new_review_attempt_for_same_subject_without_new_run()
    {
        var service = NewService();
        var (run, firstReview) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(firstReview.AttemptId, "reviewer-a", "host-a", 60, "claim-review-a").ReviewAttempt!;
        var renewed = service.RenewReview(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "renew-review-a"),
            "reviewer-a", 120).ReviewAttempt!;
        Assert.True(renewed.Lease!.ExpiresAt >= claimed.Lease!.ExpiresAt);
        var failed = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(renewed.AttemptId, renewed.LastFence, renewed.AuthorityEpoch, "settle-review-a"),
            "sha-a", ReviewTerminalOutcome.InfrastructureFailure, "worker-lost", "partition"));
        Assert.Equal(AttemptWriteStatus.Accepted, failed.Status);

        var retry = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", run.AttemptId, "req", "policy", [],
            "review-create-b", firstReview.AttemptId));
        var projection = service.GetTaskProjection("AGT-1");

        Assert.Equal(firstReview.Subject.SubjectId, retry.ReviewAttempt!.Subject.SubjectId);
        Assert.Equal(firstReview.AttemptId, retry.ReviewAttempt.SourceReviewAttemptId);
        Assert.Single(projection.RunAttempts);
        Assert.Equal(2, projection.ReviewAttempts.Count);
    }

    [Fact]
    public void Preparation_failure_retry_uses_rebuilt_plan_for_the_current_source()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"),
            Outcome = "done",
            ResultSha = "sha-a",
        });
        var stalePlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
            [], [], Preparation:
            [
                new AgentStudio.TaskServer.Contracts.ReviewPreparationCommandDto(
                    "prepare-1", "bash", ["-lc", "npm ci"], "stale-salvage"),
            ]);
        var firstReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", run.AttemptId, "req", "policy", [],
            "review-create-a", Plan: stalePlan)).ReviewAttempt!;
        var claimed = service.ClaimReview(
            firstReview.AttemptId, "reviewer", "host", 60, "review-claim").ReviewAttempt!;
        service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle"),
            "sha-a",
            ReviewTerminalOutcome.InfrastructureFailure,
            "PreparationFailed",
            "preparation directory is missing"));
        var rebuiltPlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto([], []);

        var retry = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", run.AttemptId, "req", "policy", [],
            "review-create-b", firstReview.AttemptId, Plan: rebuiltPlan)).ReviewAttempt!;

        Assert.Same(rebuiltPlan, retry.Subject.Plan);
        Assert.Empty(retry.Subject.Plan!.Preparation ?? []);
        Assert.NotEqual(firstReview.Subject.Plan, retry.Subject.Plan);
    }

    [Fact]
    public void Living_process_reclaim_of_its_own_dead_lease_keeps_answering_LeaseExpired()
    {
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var first = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-crash").ReviewAttempt!;

        // Same delivery key = the claiming PROCESS is still alive (a restart
        // changes the instance id and thus the key) and its executor may still
        // be running. Minting a fresh fence here would double-execute the
        // review and discard the first run as StaleFence, so the claim keeps
        // bouncing with LeaseExpired; the daemon's in-flight dedup skips it.
        // A genuine crash takeover arrives with a NEW key and is covered by
        // Review_takeover_on_same_attempt_rejects_old_claim_and_renewal_replays.
        now = now.AddSeconds(31);
        var reclaimed = service.ClaimReview(review.AttemptId, "reviewer", "host", 30, "claim-crash");

        Assert.Equal(AttemptWriteStatus.LeaseExpired, reclaimed.Status);
        Assert.Equal(first.LastFence, reclaimed.ReviewAttempt!.LastFence);

        var takeover = service.ClaimReview(review.AttemptId, "reviewer", "host", 30, "claim-after-restart");
        Assert.Equal(AttemptWriteStatus.Accepted, takeover.Status);
        Assert.True(takeover.ReviewAttempt!.LastFence > first.LastFence);
    }

    [Fact]
    public void Review_takeover_on_same_attempt_rejects_old_claim_and_renewal_replays()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var first = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-first").ReviewAttempt!;
        var renew = new AttemptWriteReference(
            first.AttemptId, first.LastFence, first.AuthorityEpoch, "renew-first");
        Assert.Equal(AttemptWriteStatus.Accepted, service.RenewReview(renew, "reviewer", 30).Status);
        Assert.Equal(AttemptWriteStatus.Duplicate,
            service.ClaimReview(review.AttemptId, "reviewer", "host", 30, "claim-first").Status);

        now = now.AddSeconds(31);
        var takeover = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-second").ReviewAttempt!;
        var oldClaim = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-first");
        var oldRenew = service.RenewReview(renew, "reviewer", 30);

        Assert.True(takeover.LastFence > first.LastFence);
        Assert.Equal(AttemptWriteStatus.StaleFence, oldClaim.Status);
        Assert.Equal(AttemptWriteStatus.StaleFence, oldRenew.Status);
    }

    [Fact]
    public void New_result_supersedes_old_review_and_late_report_is_retained_but_cannot_settle()
    {
        var service = NewService();
        var (_, oldReview) = CompletedRunWithReview(service, "sha-a");
        var oldClaim = service.ClaimReview(oldReview.AttemptId, "reviewer-a", "host-a", 60, "claim-old").ReviewAttempt!;
        var oldRenewWrite = new AttemptWriteReference(
            oldClaim.AttemptId, oldClaim.LastFence, oldClaim.AuthorityEpoch, "renew-old");
        Assert.Equal(AttemptWriteStatus.Accepted,
            service.RenewReview(oldRenewWrite, "reviewer-a", 60).Status);

        var runB = service.AcquireRun("AGT-1", "PROJ-1", oldReview.SourceRunAttemptId, "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                runB.AttemptId,
                runB.LastFence,
                runB.AuthorityEpoch,
                "complete-b"),
            Outcome = "done",
            ResultSha = "sha-b",
        });

        var replayedClaim = service.ClaimReview(
            oldReview.AttemptId, "reviewer-a", "host-a", 60, "claim-old");
        var replayedRenew = service.RenewReview(oldRenewWrite, "reviewer-a", 60);

        var replayedOldCompletion = service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                oldReview.SourceRunAttemptId,
                oldClaim.LastFence - 1,
                oldClaim.AuthorityEpoch,
                "run-complete"),
            Outcome = "done",
            ResultSha = "sha-a",
        });

        service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-b", runB.AttemptId, "req", "policy", [], "review-b"));

        var late = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(oldClaim.AttemptId, oldClaim.LastFence, oldClaim.AuthorityEpoch, "late-a"),
            "sha-a", ReviewTerminalOutcome.Pass));
        var lateCreate = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", oldReview.SourceRunAttemptId,
            "req", "policy", [], "late-review-a"));
        var projection = service.GetTaskProjection("AGT-1");

        Assert.Equal(AttemptWriteStatus.Superseded, late.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedOldCompletion.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, lateCreate.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedClaim.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedRenew.Status);
        var historical = Assert.Single(projection.ReviewAttempts, x => x.AttemptId == oldReview.AttemptId);
        Assert.Equal(AttemptLifecycleState.Superseded, historical.State);
        var retainedReport = Assert.Single(historical.Reports);
        Assert.Equal("late-a", retainedReport.IdempotencyKey);
        Assert.Equal("sha-a", retainedReport.MaterializedResultSha);
        Assert.Equal(ReviewTerminalOutcome.Pass, retainedReport.Outcome);
        Assert.Equal(AttemptWriteStatus.Superseded, retainedReport.AuthorityStatus);
        Assert.Equal("sha-b", projection.CurrentReviewSubject!.ExpectedResultSha);
    }

    [Fact]
    public void Replayed_review_settlement_is_superseded_after_a_new_subject_becomes_current()
    {
        var service = NewService();
        var (_, reviewA) = CompletedRunWithReview(service, "sha-a");
        var claimA = service.ClaimReview(
            reviewA.AttemptId, "reviewer-a", "host-a", 60, "claim-a").ReviewAttempt!;
        var settlement = new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimA.AttemptId, claimA.LastFence, claimA.AuthorityEpoch, "settle-a"),
            "sha-a",
            ReviewTerminalOutcome.Pass);
        Assert.Equal(AttemptWriteStatus.Accepted, service.SettleReview(settlement).Status);

        var runB = service.AcquireRun(
            "AGT-1", "PROJ-1", reviewA.SourceRunAttemptId,
            "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                runB.AttemptId,
                runB.LastFence,
                runB.AuthorityEpoch,
                "complete-b"),
            Outcome = "done",
            ResultSha = "sha-b",
        });
        service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-b", runB.AttemptId,
            "req", "policy", [], "review-b"));

        var replay = service.SettleReview(settlement);

        Assert.Equal(AttemptWriteStatus.Superseded, replay.Status);
        Assert.Equal(reviewA.AttemptId, replay.AttemptId);
        Assert.Equal("sha-b", service.GetTaskProjection("AGT-1").CurrentReviewSubject!.ExpectedResultSha);
    }

    [Fact]
    public void Daemon_generations_cannot_reuse_one_report_key_for_different_terminal_results()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(
            review.AttemptId,
            "reviewer",
            "review-host",
            60,
            "claim-generation-one").ReviewAttempt!;
        var write = new AttemptWriteReference(
            claimed.AttemptId,
            claimed.LastFence,
            claimed.AuthorityEpoch,
            $"review-report:{claimed.AttemptId}:{claimed.LastFence}");

        var first = service.SettleReview(new SettleReviewAttemptRequest(
            write,
            "sha-a",
            ReviewTerminalOutcome.Pass,
            Reason: "generation one completed the adopted worker"));
        var conflicting = service.SettleReview(new SettleReviewAttemptRequest(
            write,
            "sha-a",
            ReviewTerminalOutcome.ProductFailure,
            Reason: "generation two tried a different terminal payload"));

        Assert.Equal(AttemptWriteStatus.Accepted, first.Status);
        Assert.Equal(AttemptWriteStatus.Invalid, conflicting.Status);
        Assert.Contains("different terminal payload", conflicting.Message, StringComparison.Ordinal);
        var terminal = service.GetReview(review.AttemptId)!;
        Assert.Equal(ReviewTerminalOutcome.Pass, terminal.Outcome);
        Assert.Single(terminal.Reports);
    }

    [Fact]
    public void Idempotency_keys_are_scoped_by_task_and_cannot_alias_another_attempt()
    {
        var service = NewService();

        var first = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "same-key");
        var second = service.AcquireRun("AGT-2", "PROJ-1", null, "runner", "host", 60, "same-key");

        Assert.Equal(AttemptWriteStatus.Accepted, first.Status);
        Assert.Equal(AttemptWriteStatus.Accepted, second.Status);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal("AGT-2", second.RunAttempt!.TaskKey);

        var write = new AttemptWriteReference(
            first.AttemptId, first.RunAttempt!.LastFence, first.RunAttempt.AuthorityEpoch, "same-key");
        Assert.Equal(AttemptWriteStatus.Accepted, service.AcceptRunWrite(write).Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, service.AcceptRunWrite(write).Status);
    }

    [Fact]
    public void Real_remote_completion_subject_fails_closed_when_materialized_sha_differs()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "589c462f");
        var claimed = service.ClaimReview(review.AttemptId, "reviewer", "review-host", 60, "review-claim").ReviewAttempt!;

        var mismatch = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-result"),
            "61306343", ReviewTerminalOutcome.Pass));

        Assert.Equal(AttemptWriteStatus.SubjectMismatch, mismatch.Status);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, mismatch.ReviewAttempt!.Outcome);
        Assert.Equal("immutable-result-mismatch", mismatch.ReviewAttempt.FailureClassification);
        Assert.NotEqual(AttemptLifecycleState.Completed, mismatch.ReviewAttempt.State);
    }

    [Fact]
    public void Authority_epoch_rotation_drains_active_attempts_and_uses_the_new_epoch_for_new_claims()
    {
        var service = NewService();
        var activeRun = service.AcquireRun(
            "AGT-RUN", "PROJ-1", null, "runner-a", "host-a", 60, "run-a").RunAttempt!;
        var reviewSource = service.AcquireRun(
            "AGT-REVIEW", "PROJ-1", null, "runner-b", "host-b", 60, "review-source").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                reviewSource.AttemptId,
                reviewSource.LastFence,
                reviewSource.AuthorityEpoch,
                "review-source-complete"),
            Outcome = "done",
            ResultSha = "sha-review",
        });
        var activeReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-REVIEW",
            "PROJ-1",
            "sha-review",
            reviewSource.AttemptId,
            "requirements",
            "policy",
            [],
            "review-create")).ReviewAttempt!;
        activeReview = service.ClaimReview(
            activeReview.AttemptId,
            "reviewer-a",
            "review-host-a",
            60,
            "review-claim").ReviewAttempt!;

        var pendingSource = service.AcquireRun(
            "AGT-PENDING-REVIEW", "PROJ-1", null, "runner-c", "host-c", 60, "pending-source").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                pendingSource.AttemptId,
                pendingSource.LastFence,
                pendingSource.AuthorityEpoch,
                "pending-source-complete"),
            Outcome = "done",
            ResultSha = "sha-pending",
        });
        var pendingReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-PENDING-REVIEW",
            "PROJ-1",
            "sha-pending",
            pendingSource.AttemptId,
            "requirements",
            "policy",
            [],
            "pending-review-create")).ReviewAttempt!;

        var epoch = service.RotateAuthorityEpoch("planned credential rotation");

        Assert.Equal(activeRun.AuthorityEpoch + 1, epoch);
        Assert.Equal(activeRun.AuthorityEpoch, service.GetRun(activeRun.AttemptId)!.AuthorityEpoch);
        Assert.Equal(activeReview.AuthorityEpoch, service.GetReview(activeReview.AttemptId)!.AuthorityEpoch);
        Assert.Equal(AttemptLifecycleState.Leased, service.GetRun(activeRun.AttemptId)!.State);
        Assert.Equal(AttemptLifecycleState.Leased, service.GetReview(activeReview.AttemptId)!.State);
        var leases = new RunLeaseService(
            NullLogger<RunLeaseService>.Instance,
            service);
        Assert.Equal("Held", leases.Peek(activeRun.TaskKey).Outcome);
        Assert.True(leases.IsCurrent(
            activeRun.TaskKey,
            activeRun.Lease!.LeaseId,
            activeRun.LastFence,
            activeRun.Lease.ExecutorId));
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            service.RenewRun(
                new AttemptWriteReference(
                    activeRun.AttemptId,
                    activeRun.LastFence,
                    activeRun.AuthorityEpoch,
                    "run-renew-after-rotation"),
                "runner-a",
                60).Status);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            service.RenewReview(
                new AttemptWriteReference(
                    activeReview.AttemptId,
                    activeReview.LastFence,
                    activeReview.AuthorityEpoch,
                    "review-renew-after-rotation"),
                "reviewer-a",
                60).Status);

        var blockedRunClaim = service.AcquireRun(
            "AGT-RUN", "PROJ-1", activeRun.AttemptId, "runner-d", "host-d", 60, "run-after-rotation");
        var blockedReviewClaim = service.ClaimReview(
            activeReview.AttemptId,
            "reviewer-b",
            "review-host-b",
            60,
            "review-claim-after-rotation");
        Assert.Equal(AttemptWriteStatus.InvalidState, blockedRunClaim.Status);
        Assert.Equal(AttemptWriteStatus.InvalidState, blockedReviewClaim.Status);

        var freshRun = service.AcquireRun(
            "AGT-FRESH", "PROJ-1", null, "runner-d", "host-d", 60, "fresh-run").RunAttempt!;
        var freshReviewClaim = service.ClaimReview(
            pendingReview.AttemptId,
            "reviewer-b",
            "review-host-b",
            60,
            "pending-review-claim").ReviewAttempt!;
        Assert.Equal(epoch, freshRun.AuthorityEpoch);
        Assert.Equal(epoch, freshReviewClaim.AuthorityEpoch);

        var completedRun = service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                activeRun.AttemptId,
                activeRun.LastFence,
                activeRun.AuthorityEpoch,
                "run-complete-after-rotation"),
            Outcome = "done",
            ResultSha = "sha-run",
        });
        var completedReview = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                activeReview.AttemptId,
                activeReview.LastFence,
                activeReview.AuthorityEpoch,
                "review-complete-after-rotation"),
            "sha-review",
            ReviewTerminalOutcome.Pass));

        Assert.Equal(AttemptWriteStatus.Accepted, completedRun.Status);
        Assert.Equal(AttemptWriteStatus.Accepted, completedReview.Status);
        Assert.Equal(AttemptLifecycleState.Completed, completedRun.RunAttempt!.State);
        Assert.Equal(AttemptLifecycleState.Completed, completedReview.ReviewAttempt!.State);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            service.ReleaseRun(
                new AttemptWriteReference(
                    activeRun.AttemptId,
                    activeRun.LastFence,
                    activeRun.AuthorityEpoch,
                    "run-release-after-rotation"),
                "runner-a").Status);
        Assert.Equal(
            AttemptWriteStatus.AuthorityEpochMismatch,
            service.AcceptRunWrite(new AttemptWriteReference(
                freshRun.AttemptId,
                freshRun.LastFence,
                activeRun.AuthorityEpoch,
                "forged-old-epoch")).Status);
    }

    [Fact]
    public void Restart_preserves_draining_run_and_review_epochs_until_the_active_attempts_settle()
    {
        var now = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var draining = service.AcquireRun(
            "AGT-DRAINING", "PROJ-1", null, "runner-a", "host-a", 60, "run-a").RunAttempt!;
        var reviewSource = service.AcquireRun(
            "AGT-DRAINING-REVIEW", "PROJ-1", null, "runner-b", "host-b", 60, "review-source").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                reviewSource.AttemptId,
                reviewSource.LastFence,
                reviewSource.AuthorityEpoch,
                "review-source-complete"),
            Outcome = "done",
            ResultSha = "sha-review",
        });
        var drainingReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-DRAINING-REVIEW",
            "PROJ-1",
            "sha-review",
            reviewSource.AttemptId,
            "requirements",
            "policy",
            [],
            "review-create")).ReviewAttempt!;
        drainingReview = service.ClaimReview(
            drainingReview.AttemptId,
            "reviewer-a",
            "review-host-a",
            60,
            "review-claim").ReviewAttempt!;
        var currentEpoch = service.RotateAuthorityEpoch("planned restart");

        var restarted = NewService(() => now);

        Assert.Equal(currentEpoch, restarted.AuthorityEpoch);
        Assert.Equal(AttemptLifecycleState.Leased, restarted.GetRun(draining.AttemptId)!.State);
        Assert.Equal(AttemptLifecycleState.Leased, restarted.GetReview(drainingReview.AttemptId)!.State);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            restarted.RenewRun(
                new AttemptWriteReference(
                    draining.AttemptId,
                    draining.LastFence,
                    draining.AuthorityEpoch,
                    "renew-after-restart"),
                "runner-a",
                60).Status);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            restarted.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(
                    draining.AttemptId,
                    draining.LastFence,
                    draining.AuthorityEpoch,
                    "settle-after-restart"),
                Outcome = "done",
                ResultSha = "sha-drained",
            }).Status);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            restarted.RenewReview(
                new AttemptWriteReference(
                    drainingReview.AttemptId,
                    drainingReview.LastFence,
                    drainingReview.AuthorityEpoch,
                    "review-renew-after-restart"),
                "reviewer-a",
                60).Status);
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            restarted.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    drainingReview.AttemptId,
                    drainingReview.LastFence,
                    drainingReview.AuthorityEpoch,
                    "review-settle-after-restart"),
                "sha-review",
                ReviewTerminalOutcome.Pass)).Status);

        var fresh = restarted.AcquireRun(
            "AGT-FRESH", "PROJ-1", null, "runner-b", "host-b", 60, "run-b").RunAttempt!;
        Assert.Equal(currentEpoch, fresh.AuthorityEpoch);
    }

    [Fact]
    public void Expired_draining_lease_is_superseded_by_a_current_epoch_claim()
    {
        var now = new DateTime(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var draining = service.AcquireRun(
            "AGT-DRAINING", "PROJ-1", null, "runner-a", "host-a", 30, "run-a").RunAttempt!;
        var currentEpoch = service.RotateAuthorityEpoch("planned rotation");
        now = now.AddSeconds(31);

        var replacement = service.AcquireRun(
            "AGT-DRAINING",
            "PROJ-1",
            draining.AttemptId,
            "runner-b",
            "host-b",
            30,
            "run-b").RunAttempt!;

        var superseded = service.GetRun(draining.AttemptId)!;
        Assert.Equal(currentEpoch, replacement.AuthorityEpoch);
        Assert.True(replacement.LastFence > draining.LastFence);
        Assert.Equal(AttemptLifecycleState.Superseded, superseded.State);
        Assert.Equal(
            "draining authority epoch lease expired and a new executor took authority",
            superseded.TerminalReason);
    }

    [Fact]
    public void Failed_side_effect_does_not_consume_delivery_and_attempt_cannot_write_to_another_task()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 60, "run-a").RunAttempt!;
        var write = new AttemptWriteReference(
            run.AttemptId, run.LastFence, run.AuthorityEpoch, "log-batch-1");
        var calls = 0;

        Assert.Throws<IOException>(() => service.ExecuteRunWrite(
            write,
            "log",
            "AGT-1",
            () =>
            {
                calls++;
                throw new IOException("disk unavailable");
            }));

        var retried = service.ExecuteRunWrite(write, "log", "AGT-1", () => calls++);
        var duplicate = service.ExecuteRunWrite(write, "log", "AGT-1", () => calls++);
        var wrongTask = service.ExecuteRunWrite(
            write with { IdempotencyKey = "wrong-task" }, "log", "AGT-2", () => calls++);

        Assert.Equal(AttemptWriteStatus.Accepted, retried.Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, duplicate.Status);
        Assert.Equal(AttemptWriteStatus.SubjectMismatch, wrongTask.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Review_writes_require_complete_fenced_idempotency_identity()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 60, "review-claim").ReviewAttempt!;

        var missingKey = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, string.Empty),
            "sha-a",
            ReviewTerminalOutcome.Pass));
        var missingFence = service.RenewReview(
            new AttemptWriteReference(
                claimed.AttemptId, 0, claimed.AuthorityEpoch, "review-renew"),
            "reviewer",
            60);

        Assert.Equal(AttemptWriteStatus.Invalid, missingKey.Status);
        Assert.Equal(AttemptWriteStatus.Invalid, missingFence.Status);
        Assert.Equal(AttemptLifecycleState.Leased, service.GetReview(claimed.AttemptId)!.State);
    }

    [Fact]
    public void Review_infrastructure_retry_budget_allows_exactly_three_linked_retries()
    {
        var service = NewService();
        var (_, initial) = CompletedRunWithReview(service, "sha-a");
        var current = initial;

        for (var retryNumber = 1;
             retryNumber <= AttemptAuthorityService.ReviewInfrastructureRetryBudget;
             retryNumber++)
        {
            var claimed = service.ClaimReview(
                current.AttemptId,
                "reviewer",
                "review-host",
                60,
                $"claim-{retryNumber}").ReviewAttempt!;
            var settled = service.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    claimed.AttemptId,
                    claimed.LastFence,
                    claimed.AuthorityEpoch,
                    $"infra-{retryNumber}"),
                "sha-a",
                ReviewTerminalOutcome.InfrastructureFailure,
                "SnapshotUnavailable"));

            Assert.True(settled.Accepted);
            Assert.True(service.HasReviewInfrastructureRetryBudget(claimed.AttemptId));
            current = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
                claimed.TaskKey,
                claimed.RepositoryId,
                claimed.Subject.ExpectedResultSha,
                claimed.SourceRunAttemptId,
                claimed.Subject.TaskRequirementsHash,
                claimed.Subject.ReviewPolicyHash,
                claimed.Subject.EvidenceDigestInputs,
                $"retry-{retryNumber}",
                claimed.AttemptId)).ReviewAttempt!;
        }

        var finalClaim = service.ClaimReview(
            current.AttemptId,
            "reviewer",
            "review-host",
            60,
            "claim-terminal").ReviewAttempt!;
        var final = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                finalClaim.AttemptId,
                finalClaim.LastFence,
                finalClaim.AuthorityEpoch,
                "infra-terminal"),
            "sha-a",
            ReviewTerminalOutcome.InfrastructureFailure,
            "SnapshotUnavailable"));

        Assert.True(final.Accepted);
        Assert.False(service.HasReviewInfrastructureRetryBudget(finalClaim.AttemptId));
        Assert.Equal(
            AttemptAuthorityService.ReviewInfrastructureRetryBudget + 1,
            service.GetTaskProjection("AGT-1").ReviewAttempts.Count);
    }

    [Fact]
    public void Legacy_review_subject_without_result_envelope_is_terminalized_once()
    {
        var now = new DateTime(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, legacy) = CompletedRunWithReview(service, "sha-a");

        // A fresh envelope-less subject is inside the terminalization grace (the
        // completion ingest may still be in flight); only one that stayed
        // envelope-less past it is evidence of a pre-plane completion.
        Assert.Empty(service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());
        now = now.AddMinutes(16);

        var first = Assert.Single(
            service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());
        var terminalAt = first.TerminalAt;
        now = now.AddMinutes(5);
        var second = Assert.Single(
            service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());

        Assert.Equal(legacy.AttemptId, first.AttemptId);
        Assert.Equal(AttemptLifecycleState.Failed, first.State);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, first.Outcome);
        Assert.Equal("SnapshotUnavailable", first.FailureClassification);
        Assert.Equal(
            AttemptAuthorityService.UnmaterializableReviewSubjectReason,
            first.TerminalReason);
        Assert.Equal(terminalAt, second.TerminalAt);
        Assert.DoesNotContain(
            service.GetTaskProjection("AGT-1").ReviewAttempts,
            attempt => attempt.State == AttemptLifecycleState.Pending);
    }

    [Fact]
    public void Persistence_failure_rolls_memory_back_to_last_durable_fence_and_attempt()
    {
        var now = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var writer = new ControllableAtomicJsonFileWriter();
        var service = NewService(() => now, writer);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "run-a").RunAttempt!;
        now = now.AddSeconds(31);
        writer.ShouldFail = (_, writeNumber) => writeNumber == 2;

        Assert.Throws<IOException>(() => service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "run-b"));

        var afterFailure = service.GetTaskProjection("AGT-1");
        Assert.Equal(first.AuthorityEpoch, afterFailure.AuthorityEpoch);
        Assert.Equal(first.AttemptId, afterFailure.CurrentRunAttempt!.AttemptId);
        Assert.Equal(AttemptLifecycleState.Leased, afterFailure.CurrentRunAttempt.State);

        writer.ShouldFail = null;
        var restarted = NewService(() => now);
        var durable = restarted.GetTaskProjection("AGT-1");
        Assert.Equal(afterFailure.AuthorityEpoch, durable.AuthorityEpoch);
        Assert.Equal(afterFailure.CurrentRunAttempt.AttemptId, durable.CurrentRunAttempt!.AttemptId);
        Assert.Equal(afterFailure.CurrentRunAttempt.LastFence, durable.CurrentRunAttempt.LastFence);

        var takeover = service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "run-b").RunAttempt!;
        Assert.Equal(first.LastFence + 1, takeover.LastFence);
    }

    [Fact]
    public void Startup_migration_archives_terminal_history_beyond_count_and_keeps_current_and_nonterminal_records()
    {
        var now = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now, terminalRetentionCount: 1);
        var oldRun = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 60, "old-run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                oldRun.AttemptId,
                oldRun.LastFence,
                oldRun.AuthorityEpoch,
                "old-run-settle"),
            Outcome = "done",
            ResultSha = "sha-old",
        });
        var oldReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1",
            "PROJ-1",
            "sha-old",
            oldRun.AttemptId,
            "req",
            "policy",
            [],
            "old-review-create")).ReviewAttempt!;
        var oldClaim = service.ClaimReview(
            oldReview.AttemptId,
            "reviewer",
            "review-host",
            60,
            "old-review-claim").ReviewAttempt!;
        service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                oldClaim.AttemptId,
                oldClaim.LastFence,
                oldClaim.AuthorityEpoch,
                "old-review-settle"),
            "sha-old",
            ReviewTerminalOutcome.InfrastructureFailure,
            "SnapshotUnavailable"));

        now = now.AddMinutes(1);
        var currentRun = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            oldRun.AttemptId,
            "runner-b",
            "host-b",
            60,
            "current-run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                currentRun.AttemptId,
                currentRun.LastFence,
                currentRun.AuthorityEpoch,
                "current-run-settle"),
            Outcome = "done",
            ResultSha = "sha-current",
        });
        var currentReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1",
            "PROJ-1",
            "sha-current",
            currentRun.AttemptId,
            "req",
            "policy",
            [],
            "current-review-create")).ReviewAttempt!;
        var nonterminalRun = service.AcquireRun(
            "AGT-2",
            "PROJ-1",
            null,
            "runner-c",
            "host-c",
            60,
            "nonterminal-run-create").RunAttempt!;

        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var legacyJson = JsonNode.Parse(File.ReadAllText(livePath))!.AsObject();
        legacyJson["schemaVersion"] = 3;
        File.WriteAllText(livePath, legacyJson.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        now = now.AddHours(1);
        var legacyLiveJson = File.ReadAllText(livePath);
        var failingWriter = new ControllableAtomicJsonFileWriter
        {
            ShouldFail = (path, _) => string.Equals(
                path,
                livePath,
                StringComparison.OrdinalIgnoreCase),
        };
        Assert.Throws<IOException>(() => NewService(() => now, failingWriter, terminalRetentionCount: 1));
        Assert.Equal(legacyLiveJson, File.ReadAllText(livePath));

        var restarted = NewService(() => now, terminalRetentionCount: 1);
        var archivePath = Assert.Single(
            Directory.GetFiles(
                Path.GetDirectoryName(livePath)!,
                "attempt-authority.archive-*.json"));
        using var liveDocument = JsonDocument.Parse(File.ReadAllText(livePath));
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));

        var liveRunIds = liveDocument.RootElement
            .GetProperty("runAttempts")
            .EnumerateArray()
            .Select(record => record.GetProperty("attemptId").GetString())
            .ToList();
        var liveReviewIds = liveDocument.RootElement
            .GetProperty("reviewAttempts")
            .EnumerateArray()
            .Select(record => record.GetProperty("attemptId").GetString())
            .ToList();
        var archivedRun = Assert.Single(
            archiveDocument.RootElement.GetProperty("runAttempts").EnumerateArray());
        var archivedReview = Assert.Single(
            archiveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray());

        Assert.DoesNotContain(oldRun.AttemptId, liveRunIds);
        Assert.DoesNotContain(oldReview.AttemptId, liveReviewIds);
        Assert.Contains(currentRun.AttemptId, liveRunIds);
        Assert.Contains(nonterminalRun.AttemptId, liveRunIds);
        Assert.Contains(currentReview.AttemptId, liveReviewIds);
        Assert.Equal(oldRun.AttemptId, archivedRun.GetProperty("attemptId").GetString());
        Assert.Equal(oldReview.AttemptId, archivedReview.GetProperty("attemptId").GetString());
        Assert.Contains(
            "settle:old-run-settle",
            archivedRun.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
        Assert.Contains(
            "settle:old-review-settle",
            archivedReview.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));

        var liveProjection = restarted.GetTaskProjection("AGT-1");
        var historicalProjection = restarted.GetTaskProjection("AGT-1", includeArchived: true);
        Assert.Single(liveProjection.RunAttempts);
        Assert.Single(liveProjection.ReviewAttempts);
        Assert.Equal(2, historicalProjection.RunAttempts.Count);
        Assert.Equal(2, historicalProjection.ReviewAttempts.Count);
        Assert.Null(restarted.GetRun(oldRun.AttemptId));
        Assert.Null(restarted.GetReview(oldReview.AttemptId));

        var liveAfterMigration = File.ReadAllText(livePath);
        var archiveAfterMigration = File.ReadAllText(archivePath);
        _ = NewService(() => now.AddHours(1), terminalRetentionCount: 1);
        Assert.Equal(liveAfterMigration, File.ReadAllText(livePath));
        Assert.Equal(archiveAfterMigration, File.ReadAllText(archivePath));

        var sameDayWriter = new ControllableAtomicJsonFileWriter();
        var sameDayService = NewService(() => now.AddHours(2), sameDayWriter, terminalRetentionCount: 1);
        sameDayService.AcquireRun(
            "AGT-3",
            "PROJ-1",
            null,
            "runner-d",
            "host-d",
            60,
            "same-day-run-create");
        Assert.Equal(0, sameDayWriter.WritesFor(archivePath));
        Assert.Equal(1, sameDayWriter.WritesFor(livePath));
    }

    [Fact]
    public void Startup_migration_shrinks_representative_young_terminal_snapshot()
    {
        const int runCount = 273;
        const int reviewCount = 11_700;
        const int retentionCount = 2_000;
        var now = new DateTime(2026, 7, 28, 2, 0, 0, DateTimeKind.Utc);
        var padding = new string('x', 1_400);
        var runs = Enumerable.Range(0, runCount)
            .Select(index => new
            {
                attemptId = $"run-{index:D5}",
                taskKey = $"AGT-{index:D5}",
                repositoryId = "PROJ-002",
                state = AttemptLifecycleState.Completed,
                lastFence = 1,
                authorityEpoch = 1,
                createdAt = now.AddHours(-2).AddTicks(index),
                terminalAt = now.AddHours(-1).AddTicks(index),
                terminalOutcome = "done",
                idempotencyKeys = new[] { $"acquire:run-create-{index}", $"settle:run-settle-{index}" },
            })
            .ToList();
        var reviews = Enumerable.Range(0, reviewCount)
            .Select(index => new
            {
                attemptId = $"review-{index:D5}",
                taskKey = $"AGT-{index % runCount:D5}",
                repositoryId = "PROJ-002",
                sourceRunAttemptId = $"run-{index % runCount:D5}",
                state = AttemptLifecycleState.Failed,
                lastFence = 2,
                authorityEpoch = 1,
                createdAt = now.AddMinutes(-30).AddTicks(index),
                terminalAt = now.AddMinutes(-20).AddTicks(index),
                outcome = ReviewTerminalOutcome.InfrastructureFailure,
                failureClassification = "SnapshotUnavailable",
                terminalReason = padding,
                idempotencyKeys = new[] { $"create:review-create-{index}", $"settle:review-settle-{index}" },
                reports = new[]
                {
                    new
                    {
                        idempotencyKey = $"review-settle-{index}",
                        fence = 2,
                        authorityEpoch = 1,
                        materializedResultSha = "0123456789abcdef",
                        outcome = ReviewTerminalOutcome.InfrastructureFailure,
                        failureClassification = "SnapshotUnavailable",
                        reason = padding,
                        authorityStatus = AttemptWriteStatus.Accepted,
                        receivedAt = now.AddMinutes(-20).AddTicks(index),
                    },
                },
            })
            .ToList();
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        File.WriteAllText(
            livePath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 3,
                    authorityEpoch = 1,
                    runAttempts = runs,
                    reviewAttempts = reviews,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var beforeBytes = new FileInfo(livePath).Length;

        _ = NewService(() => now, terminalRetentionCount: retentionCount);

        var afterBytes = new FileInfo(livePath).Length;
        var archivePath = Assert.Single(
            Directory.GetFiles(
                Path.GetDirectoryName(livePath)!,
                "attempt-authority.archive-*.json"));
        using var liveDocument = JsonDocument.Parse(File.ReadAllText(livePath));
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));
        var liveReviews = liveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray().ToList();
        var archivedReviews = archiveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray().ToList();

        Assert.Equal(runCount, liveDocument.RootElement.GetProperty("runAttempts").GetArrayLength());
        Assert.Equal(retentionCount, liveReviews.Count);
        Assert.Equal(reviewCount - retentionCount, archivedReviews.Count);
        Assert.True(afterBytes < beforeBytes / 4, $"Expected at least a 75% reduction, got {beforeBytes} -> {afterBytes} bytes.");
        Assert.Contains(
            "settle:review-settle-0",
            archivedReviews[0].GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
        Console.WriteLine(
            $"Representative attempt-authority live size: {beforeBytes} -> {afterBytes} bytes; "
            + $"reviews: {reviewCount} -> {liveReviews.Count}; archived: {archivedReviews.Count}.");
    }

    [Fact]
    public void Startup_and_live_projection_do_not_load_archives()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            null,
            "runner",
            "host",
            60,
            "run-create").RunAttempt!;
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var archivePath = Path.Combine(
            Path.GetDirectoryName(livePath)!,
            "attempt-authority.archive-2026-07-01.json");
        File.WriteAllText(archivePath, "{ invalid archive");

        var restarted = NewService();
        var liveProjection = restarted.GetTaskProjection("AGT-1");

        Assert.Equal(run.AttemptId, liveProjection.CurrentRunAttempt!.AttemptId);
        Assert.Null(restarted.GetRun("run-archived"));
        Assert.Null(restarted.GetReview("review-archived"));
        Assert.Throws<InvalidDataException>(
            () => restarted.GetTaskProjection("AGT-1", includeArchived: true));
    }

    [Fact]
    public void Settlement_compaction_overwrites_interrupted_daily_archive_without_loading_it()
    {
        var now = new DateTime(2026, 7, 28, 3, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now, terminalRetentionCount: 1);
        var first = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            null,
            "runner-a",
            "host-a",
            60,
            "first-run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                first.AttemptId,
                first.LastFence,
                first.AuthorityEpoch,
                "first-run-settle"),
            Outcome = "done",
            ResultSha = "sha-first",
        });

        now = now.AddMinutes(1);
        var second = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            first.AttemptId,
            "runner-b",
            "host-b",
            60,
            "second-run-create").RunAttempt!;
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var archivePath = Path.Combine(
            Path.GetDirectoryName(livePath)!,
            "attempt-authority.archive-2026-07-28.json");
        File.WriteAllText(archivePath, "{ interrupted archive");

        var settled = service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                second.AttemptId,
                second.LastFence,
                second.AuthorityEpoch,
                "second-run-settle"),
            Outcome = "done",
            ResultSha = "sha-second",
        });

        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
        Assert.Null(service.GetRun(first.AttemptId));
        Assert.Equal(second.AttemptId, service.GetRun(second.AttemptId)!.AttemptId);
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));
        var archivedRun = Assert.Single(
            archiveDocument.RootElement.GetProperty("runAttempts").EnumerateArray());
        Assert.Equal(first.AttemptId, archivedRun.GetProperty("attemptId").GetString());
        Assert.Contains(
            "settle:first-run-settle",
            archivedRun.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
    }

    [Fact]
    public void ClaimNextReview_DeferredPreparationLeavesAttemptPendingAndUnfenced()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "sha-deferred");
        service.AgeReviewForTests(review.AttemptId, TimeSpan.FromMinutes(16));

        var result = service.ClaimNextReview(
            "reviewer",
            "review-host",
            "review-instance",
            60,
            _ => new ReviewClaimPreparation(
                CanClaim: false,
                Message: "waiting for codex quota reset"));

        Assert.Equal(AttemptWriteStatus.NotFound, result.Status);
        Assert.Equal("waiting for codex quota reset", result.Message);
        var unchanged = service.GetReview(review.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Pending, unchanged.State);
        Assert.Null(unchanged.Lease);
        Assert.Equal(0, unchanged.LastFence);
        Assert.Null(unchanged.EffectivePlan);
    }

    [Fact]
    public void ClaimNextReview_PersistsEffectivePlanWithClaimFenceAcrossRestart()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "run-complete"),
            Outcome = "done",
            ResultSha = "sha-effective",
        });
        var sourcePlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
            [new AgentStudio.TaskServer.Contracts.ReviewCommandDto(
                "aspect-code-quality",
                "code-quality",
                "codex",
                [],
                ExecutionKind: AgentStudio.TaskServer.Contracts.ReviewCommandKinds.AgentAspect,
                Prompt: "Review this result.",
                CliType: "codex",
                Model: "gpt-5.4-mini",
                ThinkingLevel: "high")],
            ["code-quality"]);
        var review = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-effective", run.AttemptId,
            "req", "policy", [], "review-create", Plan: sourcePlan)).ReviewAttempt!;
        service.AgeReviewForTests(review.AttemptId, TimeSpan.FromMinutes(16));
        var effectivePlan = sourcePlan with
        {
            Commands = [sourcePlan.Commands[0] with
            {
                FileName = "claude",
                CliType = "claude",
                Model = "claude-sonnet-5",
                ThinkingLevel = "medium",
            }],
        };

        var claimed = service.ClaimNextReview(
            "reviewer",
            "review-host",
            "review-instance",
            60,
            _ => new ReviewClaimPreparation(true, effectivePlan)).ReviewAttempt!;

        Assert.Equal(AttemptLifecycleState.Leased, claimed.State);
        Assert.NotNull(claimed.Lease);
        Assert.Equal("claude", Assert.Single(claimed.EffectivePlan!.Commands).CliType);
        Assert.Equal("codex", Assert.Single(claimed.Subject.Plan!.Commands).CliType);
        var restarted = NewService();
        var durable = restarted.GetReview(review.AttemptId)!;
        Assert.Equal(claimed.LastFence, durable.LastFence);
        Assert.Equal("claude-sonnet-5", Assert.Single(durable.EffectivePlan!.Commands).Model);
        Assert.Equal("gpt-5.4-mini", Assert.Single(durable.Subject.Plan!.Commands).Model);
    }

    private (RunAttemptDto Run, ReviewAttemptDto Review) CompletedRunWithReview(AttemptAuthorityService service, string sha)
    {
        var run = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "run-complete"),
            Outcome = "done",
            ResultSha = sha,
        });
        var review = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", sha, run.AttemptId, "req", "policy", [], "review-create")).ReviewAttempt!;
        return (service.GetRun(run.AttemptId)!, review);
    }

    private AttemptAuthorityService NewService(
        Func<DateTime>? now = null,
        IAtomicJsonFileWriter? writer = null,
        int? terminalRetentionCount = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["AttemptAuthority:TerminalRetentionCount"] = terminalRetentionCount?.ToString(),
        }).Build();
        return new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance,
            now,
            writer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
