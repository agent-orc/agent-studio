using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewSlotReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"review-slot-reconciler-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Restart_with_mixed_live_and_stale_records_counts_only_the_live_worker()
    {
        var now = DateTime.UtcNow;
        var state = new ReviewStateStore(_root);
        var live = state.Save(state.Create(
            Claim("review-live", now, now.AddHours(1)),
            Workspace("review-live")) with
        {
            ProcessId = 101,
            ProcessStartedAtUtc = now.AddMinutes(-5),
            Phase = "running",
        });
        var stale = state.Save(state.Create(
            Claim("review-stale", now, now.AddHours(1)),
            Workspace("review-stale")) with
        {
            ProcessId = 202,
            ProcessStartedAtUtc = now.AddMinutes(-5),
            Phase = "running",
        });
        var reconciler = new ReviewSlotReconciler(
            state,
            (attemptId, _) => Task.FromResult<ReviewAttemptDto?>(
                attemptId == stale.AttemptId
                    ? stale.Claim.Attempt! with { Status = "cleaned", CleanedAt = now }
                    : null),
            slot => new ReviewProcessObservation(
                slot.AttemptId == live.AttemptId,
                slot.AttemptId == live.AttemptId
                    ? "live fixture process"
                    : "fixture process exited"));

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);

        var continuation = Assert.Single(result.Continuations);
        Assert.Equal(live.AttemptId, continuation.Slot.AttemptId);
        Assert.Equal(ReviewSlotContinuationKind.Reattach, continuation.Kind);
        Assert.Equal(1, result.Purged);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(live.AttemptId, Assert.Single(state.LoadAll()).AttemptId);
        Assert.Equal(
            "review-slot-reconciliation scope=startup scanned=2 recovered=1 purged=1 deferred=0",
            result.JournalLine("startup"));
    }

    [Fact]
    public async Task Crash_then_restart_purges_a_dead_worker_without_server_authority()
    {
        var now = DateTime.UtcNow;
        var state = new ReviewStateStore(_root);
        state.Save(state.Create(
            Claim("review-crashed", now, now.AddHours(1)),
            Workspace("review-crashed")) with
        {
            ProcessId = 303,
            ProcessStartedAtUtc = now.AddMinutes(-1),
            Phase = "running",
        });
        var reconciler = new ReviewSlotReconciler(
            state,
            (_, _) => Task.FromResult<ReviewAttemptDto?>(null),
            _ => new ReviewProcessObservation(false, "worker crashed"));

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);

        Assert.Empty(result.Continuations);
        Assert.Equal(1, result.Purged);
        Assert.Equal(0, result.AgedPurged);
        Assert.Empty(state.LoadAll());
    }

    [Fact]
    public async Task Unexpired_matching_server_lease_is_settled_without_a_live_worker()
    {
        var now = DateTime.UtcNow;
        var state = new ReviewStateStore(_root);
        var slot = state.Save(state.Create(
            Claim("review-leased", now, now.AddHours(1)),
            Workspace("review-leased")) with
        {
            Phase = "running",
        });
        var reconciler = new ReviewSlotReconciler(
            state,
            (_, _) => Task.FromResult<ReviewAttemptDto?>(slot.Claim.Attempt),
            _ => new ReviewProcessObservation(false, "worker exited without a result"));

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);

        var continuation = Assert.Single(result.Continuations);
        Assert.Equal(ReviewSlotContinuationKind.SettleNonAdoptable, continuation.Kind);
        Assert.Equal(0, result.Purged);
        Assert.Single(state.LoadAll());
    }

    /// <summary>
    /// AGT-2750. The worker is still running, but a daemon restart took its
    /// /tmp away, so every build and test it has left is guaranteed to fail.
    /// The slot must settle as infrastructure at startup instead of being
    /// reattached and graded an hour later.
    /// </summary>
    [Fact]
    public async Task Worker_that_lost_its_temp_namespace_settles_instead_of_reattaching()
    {
        var now = DateTime.UtcNow;
        var state = new ReviewStateStore(_root);
        var slot = state.Save(state.Create(
            Claim("review-lost-tmp", now, now.AddHours(1)),
            Workspace("review-lost-tmp")) with
        {
            ProcessId = 404,
            ProcessStartedAtUtc = now.AddMinutes(-30),
            Phase = "running",
        });
        var reconciler = new ReviewSlotReconciler(
            state,
            (_, _) => Task.FromResult<ReviewAttemptDto?>(slot.Claim.Attempt),
            // What DurableReviewProcess.VerifyLive reports once
            // DetachedWorkerTempNamespace sees the deleted /tmp mount.
            _ => new ReviewProcessObservation(
                false,
                "review process is unusable: worker holds a deleted /tmp mount after a "
                + "daemon restart; builds and tests in this namespace cannot succeed"));

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);

        var continuation = Assert.Single(result.Continuations);
        Assert.Equal(ReviewSlotContinuationKind.SettleNonAdoptable, continuation.Kind);
        Assert.Contains("deleted /tmp mount", continuation.Reason);
    }

    [Fact]
    public async Task Dormant_record_older_than_the_safety_limit_is_purged_without_server_lookup()
    {
        var now = DateTime.UtcNow;
        var createdAt = now - ReviewSlotReconciler.MaximumDormantAge - TimeSpan.FromMinutes(1);
        var state = new ReviewStateStore(_root);
        state.Save(state.Create(
            Claim("review-aged", createdAt, now.AddHours(1)),
            Workspace("review-aged")) with
        {
            Phase = "report-pending",
            CreatedAtUtc = createdAt,
        });
        var lookups = 0;
        var reconciler = new ReviewSlotReconciler(
            state,
            (_, _) =>
            {
                lookups++;
                return Task.FromResult<ReviewAttemptDto?>(null);
            },
            _ => new ReviewProcessObservation(false, "no live worker"));

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);

        Assert.Equal(1, result.Purged);
        Assert.Equal(1, result.AgedPurged);
        Assert.Equal(0, lookups);
        Assert.Empty(state.LoadAll());
        Assert.Equal(
            "review-slot-aging scope=periodic purged=1 thresholdHours=24",
            result.AgingJournalLine("periodic", ReviewSlotReconciler.MaximumDormantAge));
    }

    [Theory]
    [InlineData(true, false, false, null, ReviewSlotRecoveryAction.Reattach)]
    [InlineData(false, true, false, true, ReviewSlotRecoveryAction.Reattach)]
    [InlineData(false, false, false, true, ReviewSlotRecoveryAction.SettleNonAdoptable)]
    [InlineData(false, true, false, false, ReviewSlotRecoveryAction.PurgeInvalidAuthority)]
    [InlineData(false, true, true, true, ReviewSlotRecoveryAction.PurgeAged)]
    [InlineData(false, true, false, null, ReviewSlotRecoveryAction.Defer)]
    internal void Recovery_policy_keeps_only_proven_process_or_lease_authority(
        bool processLive,
        bool hasDurableResult,
        bool aged,
        bool? leaseValid,
        ReviewSlotRecoveryAction expected)
    {
        Assert.Equal(
            expected,
            ReviewSlotRecoveryPolicy.Decide(
                processLive,
                hasDurableResult,
                aged,
                leaseValid));
    }

    [Theory]
    [InlineData("leased", 7, "review-runner", "review-host", 60, true)]
    [InlineData("cleaned", 7, "review-runner", "review-host", 60, false)]
    [InlineData("leased", 8, "review-runner", "review-host", 60, false)]
    [InlineData("leased", 7, "other-runner", "review-host", 60, false)]
    [InlineData("leased", 7, "review-runner", "other-host", 60, false)]
    [InlineData("leased", 7, "review-runner", "review-host", -1, false)]
    internal void Lease_match_requires_exact_unexpired_server_authority(
        string status,
        long fence,
        string executorId,
        string hostId,
        int expiresInMinutes,
        bool expected)
    {
        var now = DateTime.UtcNow;
        var claim = Claim("review-authority", now, now.AddMinutes(expiresInMinutes));
        var state = new ReviewStateStore(_root);
        var slot = state.Create(claim, Workspace("review-authority"));
        var serverAttempt = claim.Attempt! with
        {
            Status = status,
            Fence = fence,
            ExecutorId = executorId,
            HostId = hostId,
        };

        Assert.Equal(
            expected,
            ReviewSlotRecoveryPolicy.LeaseMatches(
                slot,
                serverAttempt,
                now,
                out _));
    }

    public void Dispose()
    {
        ResilientDirectory.TryDelete(_root);
    }

    private string Workspace(string attemptId)
        => Path.Combine(_root, "work", attemptId, "repository");

    private static ReviewClaimResponse Claim(
        string attemptId,
        DateTime createdAt,
        DateTime expiresAt)
    {
        var subjectId = $"subject-{attemptId}";
        var attempt = new ReviewAttemptDto(
            attemptId,
            subjectId,
            "AGT-2650",
            1,
            "leased",
            "review-runner",
            "review-host",
            7,
            createdAt,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            subjectId,
            "AGT-2650",
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            createdAt);
        var lease = new ReviewLeaseDto(
            $"lease-{attemptId}",
            attemptId,
            subjectId,
            "review-runner",
            "instance-1",
            "review-host",
            7,
            createdAt,
            expiresAt,
            "active",
            $"resource-{attemptId}",
            25000,
            11);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }
}
