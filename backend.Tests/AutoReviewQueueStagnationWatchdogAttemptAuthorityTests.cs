using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2848: the stagnation watchdog used to read only the legacy local
/// post-processing queue (<see cref="AutoReviewPostProcessingQueue.PendingCount"/>),
/// which stays at zero on a remote-review fleet where cards queue as canonical
/// ReviewAttempts in attempt-authority instead. These tests pin the combined
/// backlog number it now reports and the progress signal (a canonical claim,
/// not just a legacy dequeue) that resets its stagnation clock.
/// </summary>
public sealed class AutoReviewQueueStagnationWatchdogAttemptAuthorityTests : IDisposable
{
    private const string ProjectName = "PROJ-1";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "auto-review-watchdog-authority-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Refresh_combines_the_legacy_queue_and_pending_canonical_review_attempts()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        CreatePendingReviewAttempt(authority, "AGT-2", "sha-2");
        var queue = new AutoReviewPostProcessingQueue();
        queue.Enqueue(new AutoReviewPostProcessingRequest(ProjectName, "job-1", "/watch", DateTime.UtcNow, "run-boundary"));

        var snapshot = NewWatchdog(queue, authority).Refresh();

        Assert.Equal(1, snapshot.LegacyQueueDepth);
        Assert.Equal(2, snapshot.PendingReviewAttempts);
        Assert.Equal(3, snapshot.QueueDepth);
    }

    [Fact]
    public void Refresh_resets_the_stagnation_clock_when_a_canonical_review_attempt_is_claimed()
    {
        var now = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        var watchdog = NewWatchdog(new AutoReviewPostProcessingQueue(), authority, thresholdMinutes: 20);

        Assert.False(watchdog.Refresh(now).IsStagnant);

        now = now.AddMinutes(25);
        Assert.True(
            watchdog.Refresh(now).IsStagnant,
            "no legacy dequeue and no canonical claim happened in 25 minutes; the queue should read stagnant");

        // AGT-1 is still queued and unclaimed - the legacy queue never moves -
        // but a second attempt gets claimed by an executor: real drain
        // progress on the canonical side of the combined backlog.
        CreatePendingReviewAttempt(authority, "AGT-2", "sha-2");
        var second = authority.ListPendingReviewAttempts()
            .Single(review => string.Equals(review.TaskKey, "AGT-2", StringComparison.OrdinalIgnoreCase));
        authority.ClaimReview(second.AttemptId, "reviewer", "review-host", 60, "claim-agt-2", "instance-1");

        now = now.AddMinutes(1);
        Assert.False(
            watchdog.Refresh(now).IsStagnant,
            "a canonical claim is drain progress and must reset the stagnation clock even though AGT-1 is still pending");
    }

    /// <summary>
    /// AGT-2987: the lane went silent for 24 hours while <c>isStagnant</c> read
    /// false, because any legacy dequeue reset the one shared clock. Coding
    /// runs and legacy starts must not restart the review-claim clock; only a
    /// delivered claim may.
    /// </summary>
    [Fact]
    public void Review_claim_stagnation_fires_after_the_threshold_even_while_coding_runs_start()
    {
        var now = new DateTime(2026, 9, 27, 15, 8, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        var queue = new AutoReviewPostProcessingQueue();
        var watchdog = NewWatchdog(queue, authority, thresholdMinutes: 20);
        Assert.False(watchdog.Refresh(now).IsStagnant);

        for (var minute = 5; minute <= 25; minute += 5)
        {
            now = now.AddMinutes(5);
            // Coding keeps running: new run attempts start and the legacy
            // post-processing queue hands a card to a slot.
            authority.AcquireRun(
                $"CODE-{minute}", ProjectName, null, "coding-runner", "coding-host", 60, $"coding-{minute}");
            var legacy = new AutoReviewPostProcessingRequest(
                ProjectName, $"legacy-{minute}", "/watch", now, "run-boundary");
            queue.Enqueue(legacy);
            queue.MarkStarted(legacy);
            var snapshot = watchdog.Refresh(now);
            Assert.Equal(minute >= 20, snapshot.IsStagnant);
            Assert.Equal(minute >= 20, snapshot.ReviewClaimStagnant);
        }

        var stalled = watchdog.Refresh(now);
        var oldest = Assert.Single(authority.ListPendingReviewAttempts());
        Assert.Equal(oldest.AttemptId, stalled.OldestPendingAttemptId);
        Assert.Equal("AGT-1", stalled.OldestPendingTaskKey);
        Assert.Null(stalled.LastReviewClaimAt);
    }

    [Fact]
    public void A_claim_the_server_deferred_before_delivery_does_not_reset_the_review_claim_clock()
    {
        var now = new DateTime(2026, 9, 27, 15, 8, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        var watchdog = NewWatchdog(new AutoReviewPostProcessingQueue(), authority, thresholdMinutes: 20);

        now = now.AddMinutes(25);
        var pending = Assert.Single(authority.ListPendingReviewAttempts());
        authority.ClaimReview(pending.AttemptId, "reviewer", "review-host", 60, "claim-deferred", "instance-1");
        Assert.True(authority.DeferReviewClaim(pending.AttemptId, "reviewer", "instance-1"));

        var snapshot = watchdog.Refresh(now.AddMinutes(1));
        Assert.True(snapshot.ReviewClaimStagnant);
        Assert.Null(snapshot.LastReviewClaimAt);
    }

    [Fact]
    public void Concurrent_claims_deferred_out_of_order_leave_no_claim_on_the_clock()
    {
        var now = new DateTime(2026, 9, 27, 15, 8, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        CreatePendingReviewAttempt(authority, "AGT-2", "sha-2");
        var watchdog = NewWatchdog(new AutoReviewPostProcessingQueue(), authority, thresholdMinutes: 20);
        var pending = authority.ListPendingReviewAttempts().OrderBy(attempt => attempt.TaskKey).ToArray();

        now = now.AddMinutes(25);
        authority.ClaimReview(pending[0].AttemptId, "reviewer", "review-host", 60, "claim-a", "instance-1");
        now = now.AddSeconds(1);
        authority.ClaimReview(pending[1].AttemptId, "reviewer", "review-host", 60, "claim-b", "instance-1");
        // A is relinquished first, then B: the old single rollback slot restored
        // A's time when B was deferred, although A was never delivered either.
        Assert.True(authority.DeferReviewClaim(pending[0].AttemptId, "reviewer", "instance-1"));
        Assert.True(authority.DeferReviewClaim(pending[1].AttemptId, "reviewer", "instance-1"));

        Assert.Null(authority.LastReviewClaimAtUtc);
        var snapshot = watchdog.Refresh(now.AddMinutes(1));
        Assert.True(snapshot.ReviewClaimStagnant);
        Assert.Null(snapshot.LastReviewClaimAt);
    }

    [Fact]
    public void Deferring_one_concurrent_claim_keeps_the_time_of_the_claim_that_was_delivered()
    {
        var now = new DateTime(2026, 9, 27, 15, 8, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        CreatePendingReviewAttempt(authority, "AGT-1", "sha-1");
        CreatePendingReviewAttempt(authority, "AGT-2", "sha-2");
        var pending = authority.ListPendingReviewAttempts().OrderBy(attempt => attempt.TaskKey).ToArray();

        var claimedA = now.AddMinutes(1);
        now = claimedA;
        authority.ClaimReview(pending[0].AttemptId, "reviewer", "review-host", 60, "claim-a", "instance-1");
        now = now.AddMinutes(1);
        authority.ClaimReview(pending[1].AttemptId, "reviewer", "review-host", 60, "claim-b", "instance-1");
        Assert.Equal(now, authority.LastReviewClaimAtUtc);

        Assert.True(authority.DeferReviewClaim(pending[1].AttemptId, "reviewer", "instance-1"));

        Assert.Equal(claimedA, authority.LastReviewClaimAtUtc);
        Assert.Equal(claimedA, authority.ReadReviewClaimActivity().LastClaimAt);
    }

    private static void CreatePendingReviewAttempt(AttemptAuthorityService authority, string taskKey, string sha)
    {
        var run = authority.AcquireRun(
            taskKey, ProjectName, null, "coding-runner", "coding-host", 60, "acquire-" + taskKey).RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + taskKey),
            Outcome = "done",
            ResultSha = sha,
        });
        authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, ProjectName, sha, run.AttemptId, "requirements-hash", "policy-hash", [], "review-create-" + taskKey));
    }

    private AttemptAuthorityService NewAuthority(Func<DateTime>? now = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        return new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance, now);
    }

    private static AutoReviewQueueStagnationWatchdog NewWatchdog(
        AutoReviewPostProcessingQueue queue,
        AttemptAuthorityService authority,
        int thresholdMinutes = AutoReviewQueueStagnationWatchdog.DefaultStagnantThresholdMinutes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoReviewQueueStagnation:ThresholdMinutes"] = thresholdMinutes.ToString(),
        }).Build();
        return new AutoReviewQueueStagnationWatchdog(
            queue,
            authority,
            new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance),
            new AutoReviewStatusSnapshot(),
            config,
            NullLogger<AutoReviewQueueStagnationWatchdog>.Instance);
    }
}
