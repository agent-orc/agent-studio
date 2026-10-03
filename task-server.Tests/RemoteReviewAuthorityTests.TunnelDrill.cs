using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport.TunnelDrill;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-2937 (Dossier AGT-W65 D9): review half of the tunnel-loss drill. Review
/// recovery uses its persisted attempt identity, not a coding result branch.
/// All times are synthetic drill time from a manual clock.
/// </summary>
public sealed partial class RemoteReviewAuthorityTests
{
    private static readonly DateTimeOffset ReviewDrillStart = new(2030, 1, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Tunnel_drill_review_attempt_beyond_expiry_is_re_adopted_exactly_and_its_report_settles_once()
    {
        var clock = new ManualTimeProvider(ReviewDrillStart);
        using var temp = new TempDirectory();
        var evidence = new List<string>();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var lease = claim.Lease!;
        evidence.Add($"{clock.GetUtcNow():o} review claim attempt={claim.Attempt!.AttemptId} fence={lease.Fence} expiresAt={lease.ExpiresAt:o}");

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(clock.GetUtcNow().UtcDateTime > lease.ExpiresAt);
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        evidence.Add($"{clock.GetUtcNow():o} task-server restart after lease expiry");
        var registration = await restarted.RegisterRunnerAsync(
            "review-a", ReviewerRestart("replacement-instance", claim), "review-a", default);
        var adoption = Assert.Single(registration.AttemptAdoptions!);
        Assert.Equal("adopted", adoption.Status);
        Assert.True(adoption.ExpiresAt > clock.GetUtcNow().UtcDateTime);
        evidence.Add($"{clock.GetUtcNow():o} review re-adoption status={adoption.Status} expiresAt={adoption.ExpiresAt:o}");

        var request = PassingReport(claim);
        var report = await restarted.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        var replay = await restarted.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        Assert.Equal("Pass", report.Outcome);
        Assert.Equal(report.Outcome, replay.Outcome);
        Assert.Equal(report.ReportId, replay.ReportId);
        evidence.Add($"{clock.GetUtcNow():o} review report key={request.IdempotencyKey} outcome={report.Outcome} replay={replay.Outcome} report={report.ReportId}");

        TunnelDrillReport.Record(
            "task-server",
            new TunnelDrillOutage("review-exact-readoption", ReviewDrillStart.UtcDateTime, clock.GetUtcNow().UtcDateTime, "review-executor->task-server (silent executor, manual clock)"),
            [
                new TunnelDrillAttempt("review-exact-readoption", "review", claim.Attempt.AttemptId, lease.Fence, 0,
                    lease.ExpiresAt, null, null, null, null, null, request.IdempotencyKey,
                    $"adopted; report {report.ReportId} settled once", report.Outcome),
            ],
            evidence);
    }

    [Fact]
    public async Task Tunnel_drill_superseded_review_generation_is_rejected_after_an_explicit_refenced_reclaim()
    {
        var clock = new ManualTimeProvider(ReviewDrillStart);
        using var temp = new TempDirectory();
        var evidence = new List<string>();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var original = await store.ClaimReviewAsync(new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var lease = original.Lease!;

        clock.Advance(TimeSpan.FromMinutes(11));
        await RegisterReviewerAsync(store, "review-a", "replacement-instance", "host-a");
        var reclaimed = await store.ReClaimReviewAsync(
            original.Attempt!.AttemptId,
            new ReviewReClaimRequest("review-a", "replacement-instance", lease.LeaseId, lease.Fence, "drill-reclaim"),
            "review-a",
            default);
        Assert.True(reclaimed.Lease!.Fence > lease.Fence);
        evidence.Add($"{clock.GetUtcNow():o} explicit re-fenced reclaim attempt={reclaimed.Attempt!.AttemptId} fence={reclaimed.Lease.Fence}");

        var stale = await store.RegisterRunnerAsync(
            "review-a", ReviewerRestart("successor-instance", original), "review-a", default);
        var staleAdoption = Assert.Single(stale.AttemptAdoptions!);
        Assert.Equal("stale-authority", staleAdoption.Status);
        var staleReport = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.ReportReviewAsync(
            original.Attempt.AttemptId, PassingReport(original), "review-a", default));
        Assert.Equal("stale-review-fence", staleReport.Code);
        evidence.Add($"{clock.GetUtcNow():o} stale review re-adoption status={staleAdoption.Status}; stale report rejected: {staleReport.Message}");

        TunnelDrillReport.Record(
            "task-server",
            new TunnelDrillOutage("review-superseded", ReviewDrillStart.UtcDateTime, clock.GetUtcNow().UtcDateTime, "review-executor->task-server (silent executor, manual clock)"),
            [
                new TunnelDrillAttempt("review-superseded", "review", original.Attempt.AttemptId, lease.Fence, 0,
                    lease.ExpiresAt, null, null, null, null, null, null,
                    $"re-adoption {staleAdoption.Status}; report rejected", "superseded"),
                new TunnelDrillAttempt("review-superseded", "review", reclaimed.Attempt.AttemptId, reclaimed.Lease.Fence, 0,
                    reclaimed.Lease.ExpiresAt, null, null, null, null, null, null, "explicit re-fenced reclaim", "leased"),
            ],
            evidence);
    }

    private static RegisterRunnerRequest ReviewerRestart(string instance, ReviewClaimResponse claim)
        => new(
            "review-a", "host-a", instance, "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.ReviewExecutor],
            ActiveAttempts:
            [
                new RunnerActiveAttempt(
                    RunnerAttemptKinds.Review,
                    claim.Attempt!.AttemptId,
                    claim.Attempt.TaskId,
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    LeaseInstanceId: claim.Lease.InstanceId),
            ]);
}
