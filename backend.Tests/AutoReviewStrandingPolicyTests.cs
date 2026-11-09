using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewStrandingPolicyTests
{
    private static AutoReviewStrandingFacts Stranded() => new(
        TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30), false, false,
        false, false, false, false, true, false, false);

    [Fact]
    public void Older_run_review_cannot_reenter_batch_for_a_new_delivery()
    {
        const string oldSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string newSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var run = new RunAttemptDto("run-new", "AGT-1", "project", null,
            AttemptLifecycleState.Completed, null, 1, 1, now, now,
            newSha, "done", null, [],
            new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
                "project", "run-new", oldSha, newSha, "refs/results/new", null, "digest"));
        var subject = new ReviewSubjectDto("subject", "project", oldSha, "run-old",
            "requirements", "policy", [], now);
        var review = new ReviewAttemptDto("review-old", "AGT-1", "project", "run-old",
            null, subject, AttemptLifecycleState.Completed, null, 1, 1, now,
            now, ReviewTerminalOutcome.Pass, null, oldSha, null, []);

        var currentReview = AutoReviewStrandingWatchdog.ReviewForCurrentDelivery(review, run);

        Assert.Null(currentReview);
        Assert.Equal(AutoReviewStrandingAction.StartStep,
            AutoReviewStrandingPolicy.Decide(Stranded() with
            {
                HasReview = currentReview is not null,
                PassedReview = currentReview?.Outcome == ReviewTerminalOutcome.Pass,
            }));
        Assert.NotNull(AutoReviewStrandingWatchdog.ReviewForCurrentDelivery(
            review with { SourceRunAttemptId = run.AttemptId,
                Subject = subject with { SourceRunAttemptId = run.AttemptId, ExpectedResultSha = newSha } }, run));
        Assert.Null(AutoReviewStrandingWatchdog.ReviewForCurrentDelivery(
            review with { SourceRunAttemptId = run.AttemptId,
                Subject = subject with { SourceRunAttemptId = run.AttemptId } }, run));
    }

    [Fact]
    public void Missing_review_step_is_started_after_threshold()
        => Assert.Equal(AutoReviewStrandingAction.StartStep,
            AutoReviewStrandingPolicy.Decide(Stranded()));

    [Fact]
    public void Report_only_summary_failure_restarts_post_processing()
        => Assert.Equal(AutoReviewStrandingAction.StartStep,
            AutoReviewStrandingPolicy.Decide(Stranded() with { ReportOnly = true, HasCompletedRun = false }));

    [Fact]
    public void Passed_review_without_batch_entry_reenters_gate()
        => Assert.Equal(AutoReviewStrandingAction.ReenterBatch,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasReview = true, PassedReview = true }));

    [Fact]
    public void Unrecoverable_card_escalates()
        => Assert.Equal(AutoReviewStrandingAction.Escalate,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasCompletedRun = false }));

    [Fact]
    public void Terminal_failed_review_without_owner_escalates()
        => Assert.Equal(AutoReviewStrandingAction.Escalate,
            AutoReviewStrandingPolicy.Decide(Stranded() with { HasReview = true }));

    [Fact]
    public void Successful_step_recovery_can_advance_to_batch_recovery_in_same_lane_visit()
    {
        var facts = Stranded() with { HasReview = true, PassedReview = true,
            SinceLastIntervention = TimeSpan.FromMinutes(31), StartedStepBefore = true };

        Assert.Equal(AutoReviewStrandingAction.ReenterBatch, AutoReviewStrandingPolicy.Decide(facts));
    }

    [Fact]
    public void Successful_intervention_gets_a_distinct_receipt_key_for_later_recovery()
    {
        const string prefix = "auto-review-stranding:project:AGT-1:123:";
        var first = AutoReviewStrandingWatchdog.NextWriteKey(prefix, []);
        var receipt = new StallInterventionRecord
        {
            Id = StallInterventionStore.IdFor(first), WriteKey = first,
            Subject = "auto-review:AGT-1", Kind = "auto-review-stranded",
            Host = "task-server", Action = AutoReviewStrandingAction.StartStep.ToString(),
            Autonomy = "A1", Disposition = "act", ReasonCode = "auto-review-no-owner",
            Status = StallInterventionStatuses.Verified,
        };

        Assert.NotEqual(first, AutoReviewStrandingWatchdog.NextWriteKey(prefix, [receipt]));
        Assert.Equal(prefix + "1", AutoReviewStrandingWatchdog.NextWriteKey(prefix, [receipt]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_recovery_gets_a_follow_up_after_threshold_and_escalates_if_it_failed_to_hold(
        bool passedReview)
    {
        var facts = Stranded() with { HasReview = passedReview, PassedReview = passedReview,
            SinceLastIntervention = TimeSpan.FromMinutes(29),
            StartedStepBefore = !passedReview, ReenteredBatchBefore = passedReview };

        Assert.Equal(AutoReviewStrandingAction.Leave, AutoReviewStrandingPolicy.Decide(facts));
        Assert.Equal(AutoReviewStrandingAction.Escalate,
            AutoReviewStrandingPolicy.Decide(facts with { SinceLastIntervention = TimeSpan.FromMinutes(31) }));
    }

    [Theory]
    [InlineData(BatchPhase.Formed)]
    [InlineData(BatchPhase.Assembling)]
    [InlineData(BatchPhase.Assembled)]
    [InlineData(BatchPhase.Running)]
    [InlineData(BatchPhase.Green)]
    [InlineData(BatchPhase.Publishing)]
    public void Active_formed_or_gating_batch_owns_card(BatchPhase phase)
        => Assert.True(AutoReviewStrandingWatchdog.IsActiveBatchPhase(phase));

    [Theory]
    [InlineData(BatchPhase.Published)]
    [InlineData(BatchPhase.Red)]
    [InlineData(BatchPhase.Abandoned)]
    public void Settled_batch_does_not_own_card(BatchPhase phase)
        => Assert.False(AutoReviewStrandingWatchdog.IsActiveBatchPhase(phase));

    [Fact]
    public void Batch_formed_during_reentry_still_owns_recovered_card_after_pending_resolves()
    {
        var root = Path.Combine(Path.GetTempPath(), "auto-review-stranding-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            // clock-independent: these fixture timestamps are compared only with each other.
            var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
            const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var scope = new BatchGateScope("project", "repo", "develop", "full", "digest", "v1");
            BatchGateSubject Subject(string key) => new(
                key, "project", "repo", "develop", "full", "digest", "v1",
                "refs/heads/agent-studio/results/one", sha, "run-1", 1, 1, sha,
                true, true, true, true, false, null, true, now, 0);
            var pending = store.Enqueue(new BatchGatePendingRecord("review-1", Subject("card-1"),
                root, root, root, "direct-merge", "task", now));
            Assert.True(AutoReviewStrandingWatchdog.IsBatchMember(store, "project", "card-1", "review-1"));

            var manifest = BatchGatePolicy.Form([pending.Subject,
                    Subject("card-2") with { EnqueueSequence = 2 },
                    Subject("card-3") with { EnqueueSequence = 3 },
                    Subject("card-4") with { EnqueueSequence = 4 }],
                scope, sha, new BatchGateFormationOptions(Enabled: true), now.AddMinutes(20)).Manifest!;
            store.CloseManifest(manifest);
            store.ResolvePending("review-1", "formed");

            Assert.Empty(store.ListPending());
            Assert.True(AutoReviewStrandingWatchdog.IsBatchMember(store, "project", "card-1", "review-1"));
            Assert.False(AutoReviewStrandingWatchdog.IsBatchMember(store, "other-project", "card-1", "review-1"));
            store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                BatchPhase.Published, null, 0, now.AddMinutes(21)));
            Assert.False(AutoReviewStrandingWatchdog.IsBatchMember(store, "project", "card-1", "review-1"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("running-step")]
    [InlineData("running-review")]
    [InlineData("pending-batch")]
    [InlineData("formed-batch")]
    [InlineData("gating-batch")]
    [InlineData("queued-round")]
    [InlineData("queued-step")]
    [InlineData("within-threshold")]
    public void Owned_cards_are_not_intervened_on(string owner)
    {
        var facts = owner switch
        {
            "running-step" => Stranded() with { RunningStep = true },
            "running-review" => Stranded() with { ActiveReview = true },
            "pending-batch" or "formed-batch" or "gating-batch" => Stranded() with { BatchMember = true },
            "queued-round" => Stranded() with { QueuedRound = true },
            "queued-step" => Stranded() with { QueuedStep = true },
            _ => Stranded() with { InLane = TimeSpan.FromMinutes(30) },
        };
        Assert.Equal(AutoReviewStrandingAction.Leave, AutoReviewStrandingPolicy.Decide(facts));
    }
}
