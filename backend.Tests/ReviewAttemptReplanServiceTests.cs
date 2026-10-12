using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2827: a project build-profile or review pipeline-step edit must reach a
/// ReviewAttempt still queued (Pending, unclaimed) for that project before an
/// executor can claim its plan frozen at creation. A claimed (Leased) attempt
/// already handed its plan to an executor and must keep it. This is the
/// incident QS-103 hit: a corrected build profile never took effect because
/// the queued attempt's command was already frozen from before the fix.
/// </summary>
public sealed class ReviewAttemptReplanServiceTests : IDisposable
{
    private static readonly DateTime FixedUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private readonly TaskLanePipelineFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ReplanQueuedReviewAttempts_rewrites_the_plan_of_a_still_queued_attempt_and_records_the_timeline_event()
    {
        const string id = "replan-queued";
        const string key = "AGT-REPLAN-QUEUED";
        _fixture.SeedTask(TaskStates.AutoReview, id, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => FixedUtc);
        var review = CreateOpenReviewAttempt(authority, key, "queued");
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        var freshPlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
            [], [], BuildProfileFingerprint: "corrected-profile");
        var replanned = lifecycle.ReplanQueuedReviewAttempts(
            TaskLanePipelineFixture.Project,
            task =>
            {
                Assert.Equal(id, task.Id);
                return freshPlan;
            });

        var updated = Assert.Single(replanned);
        Assert.Equal(review.AttemptId, updated.AttemptId);
        Assert.Same(freshPlan, authority.GetReview(review.AttemptId)!.Subject.Plan);

        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        var replanEvent = Assert.Single(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptReplanned && item.RunId == review.AttemptId);
        Assert.Equal(review.AttemptId, replanEvent.Details!["attemptId"]);
    }

    [Fact]
    public void ReplanQueuedReviewAttempts_leaves_a_claimed_attempts_plan_and_timeline_untouched()
    {
        const string id = "replan-claimed";
        const string key = "AGT-REPLAN-CLAIMED";
        _fixture.SeedTask(TaskStates.AutoReview, id, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => FixedUtc);
        var review = CreateOpenReviewAttempt(authority, key, "claimed");
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);
        var claimed = authority.ClaimReview(review.AttemptId, "reviewer", "review-host", 60, "review-claim").ReviewAttempt!;
        var originalPlan = claimed.Subject.Plan;

        var freshPlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
            [], [], BuildProfileFingerprint: "corrected-profile");
        var replanned = lifecycle.ReplanQueuedReviewAttempts(
            TaskLanePipelineFixture.Project,
            _ => freshPlan);

        Assert.Empty(replanned);
        Assert.Same(originalPlan, authority.GetReview(review.AttemptId)!.Subject.Plan);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        Assert.DoesNotContain(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptReplanned);
    }

    [Fact]
    public void ReplanQueuedReviewAttempts_ignores_queued_attempts_from_a_different_project()
    {
        const string id = "replan-other-project";
        const string key = "AGT-REPLAN-OTHER-PROJECT";
        _fixture.SeedTask(TaskStates.AutoReview, id, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => FixedUtc);
        CreateOpenReviewAttempt(authority, key, "other-project");
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        var replanned = lifecycle.ReplanQueuedReviewAttempts(
            "a-different-project",
            _ => throw new InvalidOperationException("must not build a plan for a task outside the project"));

        Assert.Empty(replanned);
    }

    [Fact]
    public async Task Slow_plan_build_does_not_hold_the_review_claim_lock()
    {
        const string id = "replan-concurrent";
        const string key = "AGT-REPLAN-CONCURRENT";
        _fixture.SeedTask(TaskStates.AutoReview, id, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => FixedUtc);
        var review = CreateOpenReviewAttempt(authority, key, "concurrent");
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);
        var original = review.Subject.Plan;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var replan = Task.Run(() => lifecycle.ReplanQueuedReviewAttempts(
            TaskLanePipelineFixture.Project, (_, _) =>
            {
                entered.TrySetResult();
                release.Wait();
                return new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
                    [], [], BuildProfileFingerprint: "late-profile");
            }));
        try
        {
            await entered.Task;
            var claimed = await Task.Run(() => lifecycle.ClaimReview(
                review.AttemptId, "reviewer", "review-host", 60, "concurrent-claim"));
            Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        }
        finally { release.Set(); }
        Assert.Empty(await replan);
        Assert.Same(original, authority.GetReview(review.AttemptId)!.Subject.Plan);
    }

    private static ReviewAttemptDto CreateOpenReviewAttempt(
        AttemptAuthorityService authority,
        string taskKey,
        string suffix)
    {
        var run = authority.AcquireRun(
            taskKey, "PROJ-002", null, "runner-a", "host-a", 60, $"run-claim-{suffix}").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, $"run-complete-{suffix}"),
            Outcome = "done",
            ResultSha = $"sha-{suffix}",
        });
        return authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-002", $"sha-{suffix}", run.AttemptId, "requirements", "policy", [],
            $"review-create-{suffix}")).ReviewAttempt!;
    }
}
