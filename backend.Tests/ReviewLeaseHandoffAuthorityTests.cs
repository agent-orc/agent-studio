using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Server-side authority for the planned review daemon restart: a handed-off
/// lease stays renewable across the restart window, and the replacement
/// instance can take the attempt over under a higher fence when it cannot.
/// </summary>
public sealed class ReviewLeaseHandoffAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-handoff-authority-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_handed_off_lease_stays_renewable_across_the_restart_window()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var review = ClaimedReview(service, out var claimed);

        // The outgoing instance buys runway before it detaches the worker.
        var handoff = service.RenewReview(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "handoff-renew"),
            "reviewer",
            300);
        Assert.Equal(AttemptWriteStatus.Accepted, handoff.Status);
        var handoffExpiry = handoff.ReviewAttempt!.Lease!.ExpiresAt;

        // Stop, start, adopt: two minutes of restart, well inside the window a
        // plain 120-second TTL would have lost.
        now = now.AddSeconds(120);
        var adoption = service.RenewReview(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "adoption-renew"),
            "reviewer",
            120);

        Assert.Equal(AttemptWriteStatus.Accepted, adoption.Status);
        Assert.Equal(claimed.LastFence, adoption.ReviewAttempt!.LastFence);
        Assert.Equal(review.AttemptId, adoption.ReviewAttempt.AttemptId);
        Assert.Equal(handoffExpiry, adoption.ReviewAttempt.Lease!.ExpiresAt);
    }

    [Fact]
    public void Re_claim_re_fences_the_attempt_for_the_replacement_instance()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        ClaimedReview(service, out var claimed);
        var lease = claimed.Lease!;

        now = now.AddSeconds(600);
        var reclaimed = service.ReClaimReview(
            claimed.AttemptId,
            "reviewer",
            "review-host",
            "review-host:4242",
            lease.LeaseId,
            claimed.LastFence,
            120,
            "reclaim-1");

        Assert.Equal(AttemptWriteStatus.Accepted, reclaimed.Status);
        Assert.True(reclaimed.ReviewAttempt!.LastFence > claimed.LastFence);
        Assert.Equal(AttemptLifecycleState.Leased, reclaimed.ReviewAttempt.State);
        Assert.Equal("review-host:4242", reclaimed.ReviewAttempt.Lease!.ClientId);
        // The old fence loses its write authority the moment the new one exists.
        Assert.Equal(
            AttemptWriteStatus.StaleFence,
            service.RenewReview(
                new AttemptWriteReference(
                    claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "stale-renew"),
                "reviewer",
                120).Status);
        // ...and the new one can write immediately.
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            service.RenewReview(
                new AttemptWriteReference(
                    claimed.AttemptId,
                    reclaimed.ReviewAttempt.LastFence,
                    reclaimed.ReviewAttempt.AuthorityEpoch,
                    "fresh-renew"),
                "reviewer",
                120).Status);
    }

    [Fact]
    public void Re_claim_repairs_an_attempt_that_left_the_leased_state_with_a_live_lease()
    {
        // This is the 409 review-attempt-not-leased shape: the lease identity
        // still matches but the attempt is no longer Leased, so a renewal can
        // never succeed and the running worker would lose its gate work.
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        ClaimedReview(service, out var claimed);
        var lease = claimed.Lease!;

        RewriteReviewState(claimed.AttemptId, AttemptLifecycleState.Pending);
        service = NewService(() => now);

        var reclaimed = service.ReClaimReview(
            claimed.AttemptId,
            "reviewer",
            "review-host",
            "review-host:4242",
            lease.LeaseId,
            claimed.LastFence,
            120,
            "reclaim-live");

        Assert.Equal(AttemptWriteStatus.Accepted, reclaimed.Status);
        Assert.True(reclaimed.ReviewAttempt!.LastFence > claimed.LastFence);
    }

    [Fact]
    public void A_replacement_instance_cannot_re_fence_an_unexpired_live_lease()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        ClaimedReview(service, out var claimed);

        var refused = service.ReClaimReview(
            claimed.AttemptId,
            "reviewer",
            "review-host",
            "review-host:replacement",
            claimed.Lease!.LeaseId,
            claimed.LastFence,
            120,
            "reclaim-too-early");

        Assert.Equal(AttemptWriteStatus.InvalidState, refused.Status);
        Assert.Equal(claimed.LastFence, refused.ReviewAttempt!.LastFence);
        Assert.Equal("review-host:1234", refused.ReviewAttempt.Lease!.ClientId);
        Assert.Equal(claimed.LastFence, service.GetReview(claimed.AttemptId)!.LastFence);
    }

    [Fact]
    public void Re_claim_without_the_handed_off_lease_identity_is_refused()
    {
        var service = NewService();
        ClaimedReview(service, out var claimed);

        var wrongLease = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            "rls_not-the-handed-off-lease", claimed.LastFence, 120, "reclaim-wrong-lease");
        var wrongFence = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            claimed.Lease!.LeaseId, claimed.LastFence + 5, 120, "reclaim-wrong-fence");
        var strangeExecutor = service.ReClaimReview(
            claimed.AttemptId, "other-reviewer", "review-host", "other-host:1",
            claimed.Lease.LeaseId, claimed.LastFence, 120, "reclaim-stranger");

        Assert.Equal(AttemptWriteStatus.StaleFence, wrongLease.Status);
        Assert.Equal(AttemptWriteStatus.StaleFence, wrongFence.Status);
        Assert.Equal(AttemptWriteStatus.StaleFence, strangeExecutor.Status);
        Assert.Equal(claimed.LastFence, service.GetReview(claimed.AttemptId)!.LastFence);
    }

    [Fact]
    public void A_superseded_attempt_is_not_handed_back_by_a_restart()
    {
        var service = NewService();
        var review = CompletedRunWithReview(service, "sha-a").Review;
        var claimed = service.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 120, "claim-1").ReviewAttempt!;

        // A newer result supersedes the open attempt, exactly as the second
        // operator session did on 2026-09-07.
        var runB = service.AcquireRun(
            "AGT-1", "PROJ-1", review.SourceRunAttemptId, "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(runB.AttemptId, runB.LastFence, runB.AuthorityEpoch, "complete-b"),
            Outcome = "done",
            ResultSha = "sha-b",
        });

        var reclaimed = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            claimed.Lease!.LeaseId, claimed.LastFence, 120, "reclaim-superseded");

        Assert.Equal(AttemptWriteStatus.Superseded, reclaimed.Status);
    }

    [Fact]
    public void A_replayed_re_claim_returns_the_authority_it_already_minted()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        ClaimedReview(service, out var claimed);
        now = now.AddSeconds(121);

        var first = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            claimed.Lease!.LeaseId, claimed.LastFence, 120, "reclaim-1");
        var replay = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            claimed.Lease.LeaseId, claimed.LastFence, 120, "reclaim-1");

        Assert.Equal(AttemptWriteStatus.Accepted, first.Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, replay.Status);
        Assert.Equal(first.ReviewAttempt!.LastFence, replay.ReviewAttempt!.LastFence);
    }

    [Fact]
    public void An_instance_that_already_holds_live_authority_does_not_burn_a_fence()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var review = CompletedRunWithReview(service, "sha-a").Review;
        var claimed = service.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 120, "claim-1", "review-host:4242").ReviewAttempt!;

        var noop = service.ReClaimReview(
            claimed.AttemptId, "reviewer", "review-host", "review-host:4242",
            claimed.Lease!.LeaseId, claimed.LastFence, 120, "reclaim-noop");

        Assert.Equal(AttemptWriteStatus.Duplicate, noop.Status);
        Assert.Equal(claimed.LastFence, noop.ReviewAttempt!.LastFence);
    }

    private ReviewAttemptDto ClaimedReview(
        AttemptAuthorityService service,
        out ReviewAttemptDto claimed)
    {
        var review = CompletedRunWithReview(service, "sha-a").Review;
        claimed = service.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 120, "claim-1", "review-host:1234").ReviewAttempt!;
        return review;
    }

    private (RunAttemptDto Run, ReviewAttemptDto Review) CompletedRunWithReview(
        AttemptAuthorityService service,
        string sha)
    {
        var run = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"),
            Outcome = "done",
            ResultSha = sha,
        });
        var review = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", sha, run.AttemptId, "req", "policy", [], $"review-create-{sha}")).ReviewAttempt!;
        return (service.GetRun(run.AttemptId)!, review);
    }

    private AttemptAuthorityService NewService(Func<DateTime>? now = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        return new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance,
            now);
    }

    private void RewriteReviewState(string attemptId, AttemptLifecycleState state)
    {
        var path = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var review = root["reviewAttempts"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(
                node["attemptId"]!.GetValue<string>(),
                attemptId,
                StringComparison.Ordinal));
        review["state"] = (int)state;
        File.WriteAllText(path, root.ToJsonString());
    }
}
