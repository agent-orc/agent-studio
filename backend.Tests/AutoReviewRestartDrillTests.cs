using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2860: the restart drill for the post-review delivery sequence, built on
/// the AGT-2849 harness shape (real git with a real origin, real merge runner,
/// and a FRESH set of services afterwards - which is all a restarted process
/// has: durable files and a branch, and no memory of the request that died).
///
/// <para>
/// The incident: the Stable backend restarted at 08:48-08:55 on 17.09.2026.
/// AGT-2855 had passed review at 08:39 with its integration queued behind
/// AGT-2854's running gate; the restart killed that gate, the post-processing
/// deferral budget was exhausted five attempts later, and the card sat in
/// <c>4-auto-review</c> with <c>integration: pending</c> and a terminal Pass
/// that nothing would ever act on. AGT-2854 showed the other half: its merge was
/// published by the next green gate, its integration record said
/// <c>integrated</c>, and the card still never left the lane.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class AutoReviewRestartDrillTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _origin;

    public AutoReviewRestartDrillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "auto-review-restart-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        _origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "--bare", "-b", "develop", _origin);
        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Restart Drill Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        Git(_repo, "remote", "add", "origin", _origin);
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
    }

    /// <summary>
    /// Drill (a): the review Pass is durable, the backend restarts before the
    /// integration starts, and the integration then runs without anybody
    /// creating a second ReviewAttempt for the same already passed subject.
    /// </summary>
    [Fact]
    public async Task Restart_before_integration_starts_integrates_afterwards_without_a_new_review()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "agt-2855");
        // What the report endpoint writes before its first side effect. The
        // request then died here: no merge, no lane move.
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));

        // The restart.
        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(1, report.Integrated);
        Assert.Equal(0, report.Failed);
        Assert.True(IsOnIntegrationLane(card.DeliverySha), "the delivery did not reach the integration lane");
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, moved.State);

        // No second review: the subject passed once and stays passed. This is
        // the 45-minute re-review the operator had to order by hand.
        var attempts = restarted.Authority.GetTaskProjection(card.TaskKey).ReviewAttempts;
        var only = Assert.Single(attempts);
        Assert.Equal(card.ReviewAttemptId, only.AttemptId);
        Assert.Equal(ReviewTerminalOutcome.Pass, only.Outcome);

        var settlement = RemoteDeliverySettlementStore.Read(moved.FolderPath)!;
        Assert.Equal(RemoteDeliverySettlementStage.LaneSettled, settlement.Stage);
    }

    [Fact]
    public async Task Settled_report_without_sidecar_restores_delivery_and_pending_evidence_after_restart()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-window");
        var entry = JournalEntry(card, seeded) with { Delivery = Settlement(card, shouldIntegrate: true) };
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);

        var restarted = Build();
        var queue = new RemoteReviewEvidenceProjectionQueue();
        var reconciler = new RemoteReviewSettlementReconciler(
            restarted.Scanner, restarted.Authority, queue, restarted.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        var task = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(task));
        Assert.NotNull(RemoteDeliverySettlementStore.Read(card.FolderPath));
        Assert.True(queue.Reader.TryRead(out var pending));
        Assert.Equal(card.ReviewAttemptId, pending.AttemptId);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(task));
        Assert.False(queue.Reader.TryRead(out _));

        var resumed = await restarted.Resume.RunOnceAsync("journal-restart-drill");
        Assert.Equal(1, resumed.Integrated);
        Assert.Equal(TaskStates.HumanReview, restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
        Assert.Single(restarted.Authority.GetTaskProjection(card.TaskKey).ReviewAttempts);
    }

    [Fact]
    public void Settled_finding_round_reopens_once_after_restart()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "finding-crash-window",
            reviewOutcome: ReviewTerminalOutcome.ProductFailure);
        RemoteReviewSettlementJournal.Write(card.FolderPath, FindingRoundEntry(card, seeded));

        var restarted = Build();
        var reconciler = Reconciler(restarted);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence,
            reconciler.Reconcile(restarted.Scanner.FindJob(card.Id, _watchPath)!));
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.Ready, moved.State);
        var promptPath = Path.Combine(moved.FolderPath, "prompt.md");
        var oldWrite = DateTime.UnixEpoch;
        File.SetLastWriteTimeUtc(promptPath, oldWrite);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(moved));
        Assert.Equal(oldWrite, File.GetLastWriteTimeUtc(promptPath));
        AssertOneFindingRound(restarted, moved, card);
    }

    [Fact]
    public async Task Completed_finding_round_never_bounces_a_card_back_from_auto_review()
    {
        // The rework lands in 4-auto-review before the next review attempt is
        // minted; the settled ProductFailure is still the current attempt.
        var stack = Build();
        var card = SeedPassedDelivery(stack, "finding-no-bounce",
            reviewOutcome: ReviewTerminalOutcome.ProductFailure);
        RemoteReviewSettlementJournal.Write(card.FolderPath, FindingRoundEntry(card, stack));
        var reconciler = Reconciler(stack);
        reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!);
        Assert.Equal(TaskStates.Ready, stack.Scanner.FindJob(card.Id, _watchPath)!.State);

        var back = await stack.Transitions.MoveAsync(
            card.Id, TaskStates.AutoReview, _watchPath, cause: TimelineActors.System);
        Assert.Equal(MoveJobStatus.Success, back.Status);
        var returned = stack.Scanner.FindJob(card.Id, _watchPath)!;

        Assert.NotEqual(RemoteReviewSettlementReconcileStatus.Repair, reconciler.Reconcile(returned));
        var after = stack.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, after.State);
        AssertOneFindingRound(stack, after, card);
        Assert.Single(stack.Timeline.ReadAll(after.FolderPath), item =>
            item.Kind == TimelineEventKinds.LaneChanged
            && item.Details?.GetValueOrDefault("to") == TaskStates.Ready);
    }

    [Fact]
    public async Task Completed_finding_round_stays_complete_after_the_card_leaves_ready()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "finding-left-ready",
            reviewOutcome: ReviewTerminalOutcome.ProductFailure);
        RemoteReviewSettlementJournal.Write(card.FolderPath, FindingRoundEntry(card, stack));
        var reconciler = Reconciler(stack);
        reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!);
        var started = await stack.Transitions.MoveAsync(
            card.Id, TaskStates.Progress, _watchPath, cause: TimelineActors.System,
            suppressProductExecution: true);
        Assert.Equal(MoveJobStatus.Success, started.Status);

        // A completed continuation must not block the evidence and sidecar
        // repair that runs after it in the same reconcile pass.
        var inProgress = stack.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(inProgress));
        Assert.Equal(TaskStates.Progress, stack.Scanner.FindJob(card.Id, _watchPath)!.State);
    }

    [Fact]
    public async Task Restart_after_the_continuation_move_finishes_without_moving_again()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "finding-after-move",
            reviewOutcome: ReviewTerminalOutcome.ProductFailure);
        var entry = FindingRoundEntry(card, stack);
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        // The backend stopped right after the lane move; no prompt note or
        // receipt was written yet.
        var moved = await stack.Transitions.MoveAsync(
            card.Id, TaskStates.Ready, _watchPath,
            cause: $"remote-review-continuation:{entry.AttemptId}",
            authorityWrite: new AttemptWriteReference(entry.AttemptId, entry.Report.Fence,
                entry.Report.AuthorityEpoch, $"continuation:{entry.IdempotencyKey}"),
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        var restarted = Build();
        var reconciler = Reconciler(restarted);
        var ready = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(ready));
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingEvidence, reconciler.Reconcile(ready));
        var after = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.Ready, after.State);
        AssertOneFindingRound(restarted, after, card);
    }

    [Theory]
    [InlineData(false, false, TaskStates.AutoReview, RemoteReviewContinuationStep.Move)]
    [InlineData(false, false, TaskStates.Ready, RemoteReviewContinuationStep.Unapplicable)]
    [InlineData(false, true, TaskStates.Ready, RemoteReviewContinuationStep.Finish)]
    [InlineData(false, true, TaskStates.Progress, RemoteReviewContinuationStep.Finish)]
    [InlineData(false, true, TaskStates.AutoReview, RemoteReviewContinuationStep.Finish)]
    [InlineData(true, true, TaskStates.AutoReview, RemoteReviewContinuationStep.Completed)]
    [InlineData(true, true, TaskStates.Progress, RemoteReviewContinuationStep.Completed)]
    public void Continuation_step_is_decided_from_the_ledger_before_the_lane(
        bool receipt, bool laneRow, string lane, RemoteReviewContinuationStep expected)
    {
        var ledger = new List<TimelineEvent>();
        if (laneRow)
            ledger.Add(new TimelineEvent
            {
                Kind = TimelineEventKinds.LaneChanged,
                Details = new() { ["idempotencyKey"] = "continuation:report-1" },
            });
        if (receipt)
            ledger.Add(new TimelineEvent
            {
                Kind = TimelineEventKinds.QualityLoopReopened,
                Details = new() { ["reviewAttemptId"] = "review-1", ["cause"] = "multi-aspect-block" },
            });

        Assert.Equal(expected, RemoteReviewContinuationPolicy.Decide(
            ledger, "review-1", "multi-aspect-block", "continuation:report-1", lane));
    }

    [Fact]
    public async Task Spent_test_budget_escalates_with_one_receipt_after_restart()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "test-budget-crash-window",
            reviewOutcome: ReviewTerminalOutcome.ProductFailure);
        var original = JournalEntry(card, seeded);
        var report = original.Report with
        {
            Outcome = "ProductFailure",
            Verdicts = [new Contract.ReviewVerdictDto(
                "build-tests", "block", "ProductFailure", "The test suite failed.")],
        };
        RemoteReviewSettlementJournal.Write(card.FolderPath, original with
        {
            Report = report,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Continuation = new RemoteReviewContinuation(
                RemoteReviewContinuationKind.BudgetEscalation, string.Empty,
                ["build-tests"], 1, new CardRoundBudgetState(4, 4)),
        });

        var restarted = Build();
        var reconciler = Reconciler(restarted);
        reconciler.Reconcile(restarted.Scanner.FindJob(card.Id, _watchPath)!);
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.Escalated, moved.State);
        reconciler.Reconcile(moved);
        var receipt = Assert.Single(restarted.Timeline.ReadAll(moved.FolderPath), item =>
            item.Kind == TimelineEventKinds.OrchestratorEscalated
            && item.Details?.GetValueOrDefault("reviewAttemptId") == card.ReviewAttemptId);
        Assert.Equal("4", receipt.Details!["roundsUsed"]);
        Assert.Equal("4", receipt.Details["roundsAllowed"]);
        Assert.Equal("build-tests", receipt.Details["blockingAspects"]);

        // An operator returns the card to review before a new attempt exists;
        // the settled escalation must not push it back to 5e-escalated.
        var returned = await restarted.Transitions.MoveAsync(
            card.Id, TaskStates.AutoReview, _watchPath, cause: "human:operator");
        Assert.Equal(MoveJobStatus.Success, returned.Status);
        Assert.NotEqual(RemoteReviewSettlementReconcileStatus.Repair,
            reconciler.Reconcile(restarted.Scanner.FindJob(card.Id, _watchPath)!));
        var after = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.AutoReview, after.State);
        Assert.Single(restarted.Timeline.ReadAll(after.FolderPath), item =>
            item.Kind == TimelineEventKinds.OrchestratorEscalated
            && item.Details?.GetValueOrDefault("reviewAttemptId") == card.ReviewAttemptId);
    }

    [Fact]
    public void Settlement_journal_detects_a_changed_card_budget_receipt()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "budget-receipt-hash");
        var entry = JournalEntry(card, stack) with
        {
            ReviewBudgetDecision = new ReviewRoundBudgetDecision(4, 4, ["code-quality"],
                "card-round-budget"),
            ReviewBudgetOriginalVerdicts = [],
            CardBudget = new CardRoundBudgetState(4, 4),
        };
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);
        var path = RemoteReviewSettlementJournal.PathFor(card.FolderPath, card.ReviewAttemptId);
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "\"used\": 4", "\"used\": 9", StringComparison.Ordinal));
        Assert.Equal(RemoteReviewSettlementReadStatus.Repair,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);
    }

    [Fact]
    public void Corrupt_or_missing_journal_reports_repair_without_integrating()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-repair");
        var restarted = Build();
        var reconciler = new RemoteReviewSettlementReconciler(
            restarted.Scanner, restarted.Authority, new RemoteReviewEvidenceProjectionQueue(), restarted.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        var task = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(RemoteReviewSettlementReconcileStatus.Repair, reconciler.Reconcile(task));
        Directory.CreateDirectory(Path.GetDirectoryName(
            RemoteReviewSettlementJournal.PathFor(card.FolderPath, card.ReviewAttemptId))!);
        File.WriteAllText(RemoteReviewSettlementJournal.PathFor(card.FolderPath, card.ReviewAttemptId), "{ broken");
        Assert.Equal(RemoteReviewSettlementReconcileStatus.Repair, reconciler.Reconcile(task));
        Assert.Null(RemoteDeliverySettlementStore.Read(card.FolderPath));
    }

    [Fact]
    public void Successor_review_prevents_old_journal_from_restoring_delivery()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-stale");
        var entry = JournalEntry(card, seeded) with { Delivery = Settlement(card, shouldIntegrate: true) };
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        var old = seeded.Authority.GetReview(card.ReviewAttemptId)!;
        var successor = seeded.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            card.TaskKey, old.Subject.RepositoryId, card.DeliverySha, old.SourceRunAttemptId,
            "req", "policy", [], "successor-review"));
        Assert.True(successor.Accepted);

        var restarted = Build();
        var queue = new RemoteReviewEvidenceProjectionQueue();
        var reconciler = new RemoteReviewSettlementReconciler(
            restarted.Scanner, restarted.Authority, queue, restarted.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingAuthority,
            reconciler.Reconcile(restarted.Scanner.FindJob(card.Id, _watchPath)!));
        Assert.Null(RemoteDeliverySettlementStore.Read(card.FolderPath));
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public void Replayed_report_key_keeps_one_canonical_payload()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-duplicate");
        var entry = JournalEntry(card, seeded) with { Delivery = Settlement(card, shouldIntegrate: true) };
        var current = seeded.Authority.GetReview(card.ReviewAttemptId)!;
        Assert.True(RemoteReviewSettlementPolicy.MatchesAcceptedReview(entry, current));
        Assert.False(RemoteReviewSettlementPolicy.MatchesAcceptedReview(
            entry with { AttemptId = "review_stale" }, current));
        Assert.False(RemoteReviewSettlementPolicy.MatchesAcceptedReview(
            entry with { IdempotencyKey = "other-key" }, current));
        Assert.False(RemoteReviewSettlementPolicy.MatchesAcceptedReview(
            entry with { Report = entry.Report with
                { Workspace = entry.Report.Workspace with { ActualHead = new string('b', 40) } } }, current));
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        var acceptedKey = () => seeded.Authority.GetReview(card.ReviewAttemptId)!.Reports
            .LastOrDefault(report => report.AuthorityStatus == AttemptWriteStatus.Accepted)?.IdempotencyKey;
        var settleCalls = 0;
        AttemptWriteResult CountedSettle()
        {
            settleCalls++;
            return new AttemptWriteResult(AttemptWriteStatus.Duplicate, card.ReviewAttemptId);
        }
        var changed = entry.Report with { Summary = "Different verdict detail." };
        // The accepted key owns the journal. A changed payload under it is an
        // idempotency conflict even when it raced past the endpoint's replay
        // check; the authority would only answer Duplicate for its key.
        var conflict = RemoteReviewSettlementJournal.PrepareAndSettle(card.FolderPath,
            entry with { Report = changed, ReportSha256 = RemoteReviewSettlementJournal.Hash(changed) },
            acceptedKey, CountedSettle);
        Assert.True(RemoteReviewSettlementJournal.IsIdempotencyConflict(conflict));
        Assert.False(conflict.Accepted);
        Assert.Equal(0, settleCalls);
        // Any other key is answered by the authority and never rebinds the journal.
        var otherKey = RemoteReviewSettlementJournal.PrepareAndSettle(card.FolderPath,
            entry with
            {
                IdempotencyKey = "other-key",
                Report = changed with { IdempotencyKey = "other-key" },
                ReportSha256 = RemoteReviewSettlementJournal.Hash(changed with { IdempotencyKey = "other-key" }),
            },
            acceptedKey, CountedSettle);
        Assert.False(RemoteReviewSettlementJournal.IsIdempotencyConflict(otherKey));
        Assert.Equal(1, settleCalls);
        // The same payload under the accepted key stays an ordinary duplicate.
        var replay = RemoteReviewSettlementJournal.PrepareAndSettle(card.FolderPath, entry, acceptedKey, CountedSettle);
        Assert.Equal(AttemptWriteStatus.Duplicate, replay.Status);
        Assert.Equal(2, settleCalls);
        Assert.Equal(entry.ReportSha256,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!.ReportSha256);
        var path = RemoteReviewSettlementJournal.PathFor(card.FolderPath, card.ReviewAttemptId);
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "\"shouldIntegrate\": true", "\"shouldIntegrate\": false", StringComparison.Ordinal));
        Assert.Equal(RemoteReviewSettlementReadStatus.Repair,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);
    }

    [Fact]
    public async Task Restart_after_integration_before_lane_move_uses_the_journaled_generation()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-lane");
        var delivery = Settlement(card, shouldIntegrate: true) with
        {
            JournalRequired = true,
            Stage = RemoteDeliverySettlementStage.IntegrationSettled,
            IntegrationOutcome = MergeIntoIntegrationOutcome.Merged.ToString(),
        };
        RemoteReviewSettlementJournal.Write(card.FolderPath, JournalEntry(card, seeded) with { Delivery = delivery });
        RemoteDeliverySettlementStore.Write(card.FolderPath, delivery);
        Git(_repo, "merge", "-q", "--no-ff", "-m", "chore: publish journal-lane", "task/journal-lane");
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
        var publishedTip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        var resumed = await restarted.Resume.RunOnceAsync("journal-lane-restart");
        Assert.Equal(1, resumed.Completed);
        Assert.Equal(TaskStates.HumanReview, restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
        Assert.Equal(publishedTip, Git(_repo, "rev-parse", "develop"));
    }

    /// <summary>
    /// Code-quality finding of AGT-2936: a report the authority refuses (here a
    /// lost fence race) must not leave a journal that binds the attempt to its
    /// payload. A later valid report with a corrected payload settles normally,
    /// and so does one after a process death between journal and settlement.
    /// </summary>
    [Fact]
    public void Rejected_or_interrupted_settlement_never_blocks_a_later_valid_report()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-rejected", settleReview: false);
        var claimed = stack.Authority.ClaimReview(
            card.ReviewAttemptId, "reviewer", "review-host", 60, "review-claim-journal-rejected").ReviewAttempt!;
        var acceptedKey = () => stack.Authority.GetReview(card.ReviewAttemptId)!.Reports
            .LastOrDefault(report => report.AuthorityStatus == AttemptWriteStatus.Accepted)?.IdempotencyKey;
        AttemptWriteResult Settle(RemoteReviewSettlementEntry entry, long fence) =>
            RemoteReviewSettlementJournal.PrepareAndSettle(card.FolderPath, entry, acceptedKey,
                () => stack.Authority.SettleReview(new SettleReviewAttemptRequest(
                    new AttemptWriteReference(claimed.AttemptId, fence, claimed.AuthorityEpoch, entry.IdempotencyKey),
                    card.DeliverySha,
                    ReviewTerminalOutcome.Pass,
                    Reason: entry.Report.Summary)));
        RemoteReviewSettlementEntry Keyed(string key, string summary)
        {
            var report = JournalEntry(card, stack).Report with { IdempotencyKey = key, Summary = summary };
            return JournalEntry(card, stack) with
            {
                IdempotencyKey = key,
                Report = report,
                ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            };
        }

        var lost = Settle(Keyed("report-lost-fence", "First payload."), claimed.LastFence + 7);
        Assert.Equal(AttemptWriteStatus.StaleFence, lost.Status);
        Assert.Equal(RemoteReviewSettlementReadStatus.Missing,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);

        // Process death between the journal write and the authority settlement.
        RemoteReviewSettlementJournal.Write(card.FolderPath, Keyed("report-interrupted", "Interrupted payload."));
        var reconciler = new RemoteReviewSettlementReconciler(
            stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(), stack.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.PendingAuthority,
            reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!));

        var corrected = Keyed("report-corrected", "Corrected payload.");
        var accepted = Settle(corrected, claimed.LastFence);
        Assert.Equal(AttemptWriteStatus.Accepted, accepted.Status);
        var journal = RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, journal.Status);
        Assert.Equal(corrected.ReportSha256, journal.Entry!.ReportSha256);
        Assert.True(RemoteReviewSettlementPolicy.MatchesAcceptedReview(
            journal.Entry, stack.Authority.GetReview(card.ReviewAttemptId)));

        // After acceptance a late loser is refused and leaves the owned journal intact.
        Assert.False(Settle(Keyed("report-late", "Late payload."), claimed.LastFence).Accepted);
        Assert.Equal(corrected.ReportSha256,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!.ReportSha256);
    }

    [Fact]
    public void Reconciler_releases_an_orphan_journal_once_the_attempt_is_terminal_without_acceptance()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-orphan", settleReview: false);
        var claimed = stack.Authority.ClaimReview(
            card.ReviewAttemptId, "reviewer", "review-host", 60, "review-claim-journal-orphan").ReviewAttempt!;
        RemoteReviewSettlementJournal.Write(card.FolderPath, JournalEntry(card, stack));
        // The subject mismatch terminates the attempt without an accepted report.
        var mismatch = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "report-mismatch"),
            new string('c', 40),
            ReviewTerminalOutcome.Pass,
            Reason: "Wrong head."));
        Assert.Equal(AttemptWriteStatus.SubjectMismatch, mismatch.Status);

        var reconciler = new RemoteReviewSettlementReconciler(
            stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(), stack.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.NoWork,
            reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!));
        Assert.Equal(RemoteReviewSettlementReadStatus.Missing,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);
    }

    /// <summary>
    /// AGT-2936 code-quality: the journal gate is per attempt. A slow settlement
    /// of one review still serializes its own attempt but never stalls another.
    /// </summary>
    [Fact]
    public async Task Journal_gate_serializes_one_attempt_without_blocking_another()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-gate", settleReview: false);
        var entry = JournalEntry(card, stack);
        var folderA = Path.Combine(_root, "journal-gate-a");
        var folderB = Path.Combine(_root, "journal-gate-b");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        AttemptWriteResult Accepted(string attemptId) => new(AttemptWriteStatus.Accepted, attemptId);

        var slow = Task.Run(() => RemoteReviewSettlementJournal.PrepareAndSettle(
            folderA, entry with { AttemptId = "review_gate_a" }, () => null, () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return Accepted("review_gate_a");
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task<AttemptWriteResult> sameAttempt;
        try
        {
            var other = Task.Run(() => RemoteReviewSettlementJournal.PrepareAndSettle(
                folderB, entry with { AttemptId = "review_gate_b" }, () => null, () => Accepted("review_gate_b")));
            Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(AttemptWriteStatus.Accepted, (await other).Status);

            sameAttempt = Task.Run(() => RemoteReviewSettlementJournal.PrepareAndSettle(
                folderA, entry with { AttemptId = "review_gate_a" }, () => null, () => Accepted("review_gate_a")));
            Assert.NotSame(sameAttempt, await Task.WhenAny(sameAttempt, Task.Delay(TimeSpan.FromMilliseconds(300))));
        }
        finally
        {
            release.Set();
        }
        Assert.Equal(AttemptWriteStatus.Accepted, (await slow).Status);
        Assert.Equal(AttemptWriteStatus.Accepted, (await sameAttempt.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    /// <summary>
    /// AGT-2936 code-quality: an unrecoverable card is reported once per review
    /// generation, not on every 30-second tick.
    /// </summary>
    [Fact]
    public async Task Reconciler_reports_a_repair_once_per_review_generation()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "journal-repair-once");
        var restarted = Build();
        var entries = new List<string>();
        var reconciler = new RemoteReviewSettlementReconciler(
            restarted.Scanner, restarted.Authority, new RemoteReviewEvidenceProjectionQueue(), restarted.Resume,
            new CollectingLogger<RemoteReviewSettlementReconciler>(entries));

        await reconciler.RunOnceAsync();
        await reconciler.RunOnceAsync();
        await reconciler.RunOnceAsync();

        Assert.Single(entries, entry => entry.Contains("remote-review-settlement-repair-required", StringComparison.Ordinal)
            && entry.Contains(card.TaskKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// AGT-2936 code-quality: an archived card has no lane continuation. Once
    /// its current review reconciled to a settled status, ordinary ticks skip it
    /// instead of re-reading its folder; a new review generation re-admits it.
    /// </summary>
    [Fact]
    public async Task Reconciler_skips_a_settled_archived_card_on_ordinary_ticks()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-archived", lane: TaskStates.Archive);
        RemoteReviewSettlementJournal.Write(card.FolderPath, JournalEntry(card, stack) with { EvidenceComplete = true });
        var evidence = Path.Combine(card.FolderPath, RemoteReviewReportEvidence.EvidenceFileName(card.ReviewAttemptId));
        File.WriteAllText(evidence, "{}");
        var entries = new List<string>();
        var reconciler = new RemoteReviewSettlementReconciler(
            stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(), stack.Resume,
            new CollectingLogger<RemoteReviewSettlementReconciler>(entries));
        Assert.Equal(RemoteReviewSettlementReconcileStatus.Complete,
            reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!));

        await reconciler.RunOnceAsync();
        // Re-reading the folder now would report a repair; a settled archived
        // card is not re-read.
        File.Delete(evidence);
        await reconciler.RunOnceAsync();
        Assert.DoesNotContain(entries, entry => entry.Contains(card.TaskKey, StringComparison.Ordinal));

        // A fresh process reconciles the whole archive once at startup.
        var restarted = new RemoteReviewSettlementReconciler(
            stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(), stack.Resume,
            new CollectingLogger<RemoteReviewSettlementReconciler>(entries));
        await restarted.RunOnceAsync();
        Assert.Single(entries, entry => entry.Contains(card.TaskKey, StringComparison.Ordinal));
    }

    [Fact]
    public void Generation_fence_orders_every_continuation_against_successor_creation()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-fence");
        var applied = 0;
        Assert.True(stack.Authority.TryApplyForCurrentReview(card.ReviewAttemptId, () => applied++));
        var old = stack.Authority.GetReview(card.ReviewAttemptId)!;
        Assert.True(stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            card.TaskKey, old.Subject.RepositoryId, card.DeliverySha, old.SourceRunAttemptId,
            "req", "policy", [], "successor-fence")).Accepted);
        Assert.False(stack.Authority.TryApplyForCurrentReview(card.ReviewAttemptId, () => applied++));
        Assert.False(stack.Authority.TryApplyForCurrentReview("review_missing", () => applied++));
        Assert.Equal(1, applied);
    }

    /// <summary>
    /// Requirement-fit finding of AGT-2936: a successor created while the
    /// evidence worker is between its generation check and the task projection
    /// must stop the stale projection from applying, and the journal stays
    /// unfinished instead of claiming completion.
    /// </summary>
    [Fact]
    public async Task Successor_created_during_evidence_projection_prevents_the_stale_projection()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-evidence-race");
        var entry = JournalEntry(card, stack);
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        var projector = new RemotePipelineReviewEvidenceProjector(
            stack.Pipeline,
            stack.Timeline,
            new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance,
                new ConfigurationBuilder().Build()));
        var worker = new RemoteReviewEvidenceProjectionWorker(
            new RemoteReviewEvidenceProjectionQueue(), stack.Scanner, stack.Authority, projector,
            NullLogger<RemoteReviewEvidenceProjectionWorker>.Instance);
        var old = stack.Authority.GetReview(card.ReviewAttemptId)!;
        worker.BeforeFencedProjection = () => Assert.True(stack.Authority.CreateReviewAttempt(
            new CreateReviewAttemptRequest(card.TaskKey, old.Subject.RepositoryId, card.DeliverySha,
                old.SourceRunAttemptId, "req", "policy", [], "successor-during-evidence")).Accepted);
        var timelineBefore = TimelineLength(card.FolderPath);

        await worker.ProcessAsync(new RemoteReviewEvidenceProjectionRequest(
            card.ReviewAttemptId, card.TaskKey, old, entry.Report,
            RemoteReviewReportEvidence.EvidenceFileName(card.ReviewAttemptId), entry.ReportSha256,
            DateTime.UtcNow, DateTime.UtcNow), CancellationToken.None);

        Assert.NotEqual(card.ReviewAttemptId,
            stack.Authority.GetTaskProjection(card.TaskKey).CurrentReviewAttempt!.AttemptId);
        Assert.Equal(timelineBefore, TimelineLength(card.FolderPath));
        Assert.False(RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!.EvidenceComplete);
    }

    /// <summary>
    /// Code-quality concern of AGT-2936: the continuation's disk I/O ran under the
    /// global authority gate. It now holds only the task's continuation gate, so
    /// unrelated authority calls proceed while a successor for the same task still
    /// waits until the continuation has applied.
    /// </summary>
    [Fact]
    public void Continuation_runs_outside_the_global_authority_gate_and_still_fences_the_successor()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-fence-io");
        var old = stack.Authority.GetReview(card.ReviewAttemptId)!;
        Task<AttemptWriteResult>? successor = null;
        var readDuringApply = false;
        var successorHeldBack = false;

        Assert.True(stack.Authority.TryApplyForCurrentReview(card.ReviewAttemptId, () =>
        {
            readDuringApply = Task.Run(() => stack.Authority.GetTaskProjection("AGT-unrelated"))
                .Wait(TimeSpan.FromSeconds(10));
            successor = Task.Run(() => stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                card.TaskKey, old.Subject.RepositoryId, card.DeliverySha, old.SourceRunAttemptId,
                "req", "policy", [], "successor-during-apply")));
            successorHeldBack = !successor.Wait(TimeSpan.FromMilliseconds(500));
        }));

        Assert.True(readDuringApply);
        Assert.True(successorHeldBack);
        Assert.True(successor!.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(successor.Result.Accepted);
        Assert.False(stack.Authority.TryApplyForCurrentReview(card.ReviewAttemptId, () => { }));
    }

    /// <summary>
    /// Code-quality concerns of AGT-2936: the unused journal constant, the
    /// off-by-one exhaustion and the retry delay and log keyed off the volatile
    /// request counter. The journaled failure count now drives all of them, so a
    /// restarted worker keeps the same backoff and grants MaxRetries retries.
    /// </summary>
    [Fact]
    public async Task Evidence_retry_uses_the_journaled_failure_count_for_delay_and_exhaustion()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-evidence-retry");
        var entry = JournalEntry(card, stack) with
        {
            EvidenceFailures = RemoteReviewEvidenceProjectionWorker.MaxRetries - 1,
        };
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        var worker = EvidenceWorker(stack);
        var retries = new List<int>();
        worker.RetryDelayOverride = retry =>
        {
            retries.Add(retry);
            return TimeSpan.FromMinutes(30);
        };
        worker.BeforeFencedProjection = () => throw new IOException("disk full");

        // A fresh request after a restart starts its volatile counter at zero.
        await worker.ProcessAsync(EvidenceRequest(card, stack, entry), CancellationToken.None);
        var retried = RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!;
        Assert.Equal(RemoteReviewEvidenceProjectionWorker.MaxRetries, retried.EvidenceFailures);
        Assert.Null(retried.RepairReason);
        Assert.Equal([RemoteReviewEvidenceProjectionWorker.MaxRetries - 1], retries);
        Assert.True(retried.NextEvidenceAttemptUtc > DateTime.UtcNow.AddMinutes(20));

        await worker.ProcessAsync(EvidenceRequest(card, stack, retried), CancellationToken.None);
        var exhausted = RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!;
        Assert.Equal(RemoteReviewEvidenceProjectionWorker.MaxRetries + 1, exhausted.EvidenceFailures);
        Assert.Equal("review-evidence-projection-exhausted", exhausted.RepairReason);
        Assert.Single(retries);
    }

    /// <summary>
    /// Code-quality concern of AGT-2936: the worker retried every exception. Only
    /// transient I/O is retried; a defect becomes a typed repair at once.
    /// </summary>
    [Fact]
    public async Task Non_transient_evidence_failure_is_a_typed_repair_without_retry()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-evidence-defect");
        var entry = JournalEntry(card, stack);
        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        var worker = EvidenceWorker(stack);
        var retries = 0;
        worker.RetryDelayOverride = _ =>
        {
            retries++;
            return TimeSpan.FromMinutes(30);
        };
        worker.BeforeFencedProjection = () => throw new InvalidOperationException("projection defect");

        await worker.ProcessAsync(EvidenceRequest(card, stack, entry), CancellationToken.None);

        var failed = RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Entry!;
        Assert.Equal(1, failed.EvidenceFailures);
        Assert.Equal("review-evidence-projection-failed", failed.RepairReason);
        Assert.Equal(0, retries);
        var reconciler = new RemoteReviewSettlementReconciler(
            stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(), stack.Resume,
            NullLogger<RemoteReviewSettlementReconciler>.Instance);
        Assert.Equal(RemoteReviewSettlementReconcileStatus.Repair,
            reconciler.Reconcile(stack.Scanner.FindJob(card.Id, _watchPath)!));
    }

    /// <summary>
    /// Code-quality concern of AGT-2936: the journaled delivery decision took the
    /// raw report spelling. It carries the outcome the authority settles, and a
    /// journal with any other spelling is a typed repair.
    /// </summary>
    [Fact]
    public void Journaled_delivery_carries_the_authority_outcome_not_the_report_spelling()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-outcome");
        var report = JournalEntry(card, stack).Report with { Outcome = " pass" };
        var entry = JournalEntry(card, stack) with
        {
            Report = report,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Delivery = Settlement(card, shouldIntegrate: true),
        };

        RemoteReviewSettlementJournal.Write(card.FolderPath, entry);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready,
            RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);

        foreach (var spelling in new[] { " pass", nameof(ReviewTerminalOutcome.ProductFailure) })
        {
            RemoteReviewSettlementJournal.Write(card.FolderPath, entry with
            {
                Delivery = entry.Delivery! with { Outcome = spelling },
            });
            Assert.Equal(RemoteReviewSettlementReadStatus.Repair,
                RemoteReviewSettlementJournal.Read(card.FolderPath, card.ReviewAttemptId).Status);
        }
    }

    /// <summary>
    /// Code-quality concern of AGT-2936: the resume service and the reconciler
    /// each restored the delivery sidecar. Both now use one policy.
    /// </summary>
    [Fact]
    public void Journaled_delivery_restores_the_sidecar_only_for_an_auto_review_card_without_one()
    {
        var stack = Build();
        var card = SeedPassedDelivery(stack, "journal-restore");
        var entry = JournalEntry(card, stack) with { Delivery = Settlement(card, shouldIntegrate: true) };
        var task = stack.Scanner.FindJob(card.Id, _watchPath)!;

        Assert.False(RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task with { State = TaskStates.HumanReview }, entry));
        Assert.Null(RemoteDeliverySettlementStore.Read(card.FolderPath));
        Assert.False(RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task, entry with { Delivery = null }));
        Assert.True(RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task, entry));
        Assert.True(RemoteDeliverySettlementStore.MatchesAttempt(
            RemoteDeliverySettlementStore.Read(card.FolderPath), card.ReviewAttemptId));
        Assert.False(RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task, entry));
    }

    private static RemoteReviewEvidenceProjectionWorker EvidenceWorker(Stack stack)
        => new(
            new RemoteReviewEvidenceProjectionQueue(),
            stack.Scanner,
            stack.Authority,
            new RemotePipelineReviewEvidenceProjector(
                stack.Pipeline,
                stack.Timeline,
                new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance),
                new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance,
                    new ConfigurationBuilder().Build())),
            NullLogger<RemoteReviewEvidenceProjectionWorker>.Instance);

    private static RemoteReviewEvidenceProjectionRequest EvidenceRequest(
        SeededCard card,
        Stack stack,
        RemoteReviewSettlementEntry entry)
        => new(
            card.ReviewAttemptId, card.TaskKey, stack.Authority.GetReview(card.ReviewAttemptId)!, entry.Report,
            RemoteReviewReportEvidence.EvidenceFileName(card.ReviewAttemptId), entry.ReportSha256,
            DateTime.UtcNow, DateTime.UtcNow);

    private static int TimelineLength(string folder)
    {
        var path = Path.Combine(folder, "logs", "timeline.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }

    private static RemoteReviewSettlementReconciler Reconciler(Stack stack) => new(
        stack.Scanner, stack.Authority, new RemoteReviewEvidenceProjectionQueue(),
        stack.Resume, NullLogger<RemoteReviewSettlementReconciler>.Instance,
        stack.Mutations, stack.Transitions, stack.Timeline);

    private static RemoteReviewSettlementEntry FindingRoundEntry(SeededCard card, Stack stack)
    {
        var original = JournalEntry(card, stack);
        var report = original.Report with
        {
            Outcome = "ProductFailure",
            Verdicts = [new Contract.ReviewVerdictDto(
                "code-quality", "block", "ProductFailure", "A concrete defect remains.")],
        };
        return original with
        {
            Report = report,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Continuation = new RemoteReviewContinuation(
                RemoteReviewContinuationKind.FindingRound,
                "Fix the concrete code-quality defect.", ["code-quality"], 1,
                new CardRoundBudgetState(1, 4),
                new DeliveredReviewRound(card.ReviewAttemptId, ["code-quality"], []),
                ReviewRoundBudgetLedger.Empty),
        };
    }

    private static void AssertOneFindingRound(Stack stack, TaskInfo task, SeededCard card)
    {
        var prompt = File.ReadAllText(Path.Combine(task.FolderPath, "prompt.md"));
        Assert.Equal(1, prompt.Split($"<!-- review-finding-round:{card.ReviewAttemptId} -->").Length - 1);
        Assert.Equal(card.ReviewAttemptId,
            ReviewConcernRoundStore.Read(task.FolderPath)!.ReviewAttemptId);
        Assert.Equal(card.ReviewAttemptId,
            Assert.Single(ReviewRoundBudgetStore.Read(task.FolderPath).Rounds).AttemptId);
        Assert.Single(Directory.GetFiles(Path.Combine(task.FolderPath,
            "orchestrator-follow-up-history"), $"*{card.ReviewAttemptId}*"));
        Assert.Single(stack.Timeline.ReadAll(task.FolderPath), item =>
            item.Kind == TimelineEventKinds.QualityLoopReopened
            && item.Details?.GetValueOrDefault("reviewAttemptId") == card.ReviewAttemptId);
    }

    private static RemoteReviewSettlementEntry JournalEntry(SeededCard card, Stack stack)
    {
        var review = stack.Authority.GetReview(card.ReviewAttemptId)!;
        var report = new Contract.ReviewReportRequest(
            "reviewer", "instance", "lease", review.LastFence, "review-settle-" + card.Id,
            "Pass", null, "All aspects passed.",
            new Contract.ReviewWorkspaceProofDto(review.Subject.RepositoryId, card.DeliverySha,
                card.DeliverySha, new string('a', 40), false, false, "workspace", "namespace"),
            new Contract.ReviewEnvironmentDto("review-host", "reviewer", "instance", "linux", "x64",
                "10.0", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [], review.AuthorityEpoch);
        return new RemoteReviewSettlementEntry
        {
            AttemptId = card.ReviewAttemptId,
            TaskKey = card.TaskKey,
            IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Report = report,
            ReceivedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Drill (b): the gate died after its merge had been published by a later
    /// gate. The delivery is an ancestor of <c>origin/develop</c> and the
    /// integration record says <c>integrated</c>, so the completion step must be
    /// re-runnable from that record alone - and must not merge anything again.
    /// </summary>
    [Fact]
    public async Task A_delivery_published_by_a_later_gate_completes_on_the_next_pass_without_remerging()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "agt-2854");
        // The later gate merged and pushed this delivery along with its own.
        Git(_repo, "merge", "-q", "--no-ff", "-m", "chore: later gate published agt-2854", "task/agt-2854");
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
        var publishedTip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        Assert.Equal(
            IntegrationStatuses.Integrated,
            Status(restarted, card.Id).Status);

        var classified = await restarted.Orchestrator.ProcessCardAsync(
            _root, Project, card.Id, _watchPath, CancellationToken.None);
        Assert.Equal(PostProcessingCardStatus.Deferred, classified.Status);
        Assert.Equal(PostProcessingCardResult.AwaitingIntegrationCompletion, classified.Reason);

        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(1, report.Completed);
        Assert.Equal(0, report.Integrated);
        Assert.Equal(0, report.Failed);
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, moved.State);
        // Nothing was merged a second time.
        Assert.Equal(publishedTip, Git(_repo, "rev-parse", "develop"));
        Assert.Equal(IntegrationStatuses.Integrated, Status(restarted, card.Id).Status);
    }

    /// <summary>
    /// The card the resume must keep its hands off: the review has not settled,
    /// so the canonical executor still owns it and nothing may be integrated or
    /// moved on its behalf.
    /// </summary>
    [Fact]
    public async Task A_card_whose_review_has_not_settled_is_left_to_its_executor()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "open-review", settleReview: false);
        var tip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(0, report.Integrated);
        Assert.Equal(0, report.Completed);
        Assert.Equal(0, report.Failed);
        Assert.Equal(
            TaskStates.AutoReview,
            restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
        Assert.Equal(tip, Git(_repo, "rev-parse", "develop"));
    }

    /// <summary>
    /// AGT-3083: the settlement reconciler ends every 30-second pass with this
    /// sweep, one integration read per card in the lane. With the shared origin
    /// store the whole pass costs at most one Git configuration capture.
    /// </summary>
    [Fact]
    public async Task A_resume_pass_over_many_cards_on_one_repository_captures_the_config_at_most_once()
    {
        var seeded = Build();
        var cards = Enumerable.Range(0, 6)
            .Select(i => SeedPassedDelivery(seeded, "origin-pass-" + i, settleReview: false))
            .ToArray();
        var captures = 0;
        var store = new GitOriginStore(root =>
        {
            Interlocked.Increment(ref captures);
            return GitConfigSignature.Capture(root);
        });

        var restarted = Build(originStore: store);
        var first = await restarted.Resume.RunOnceAsync("origin-store");
        var second = await restarted.Resume.RunOnceAsync("origin-store");

        Assert.Equal(0, first.Failed);
        Assert.Equal(0, second.Failed);
        Assert.True(Volatile.Read(ref captures) <= 1, $"expected at most one capture, got {captures}");
        foreach (var card in cards)
            Assert.Equal(TaskStates.AutoReview, restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
    }

    /// <summary>
    /// The safety belt on the sidecar: a passed review whose delivery is not on
    /// the branch and whose settlement record is missing is never integrated on
    /// the strength of "Pass" alone. The aspect verdicts that decided the
    /// build/test gate are not reconstructible from the attempt, so guessing
    /// here would admit exactly the delivery the gate exists to refuse.
    /// </summary>
    [Fact]
    public async Task A_passed_delivery_without_a_settlement_record_is_not_integrated_on_trust()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "no-record");
        var tip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(0, report.Integrated);
        Assert.Equal(0, report.Completed);
        Assert.Equal(tip, Git(_repo, "rev-parse", "develop"));
        Assert.Equal(
            TaskStates.AutoReview,
            restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
    }

    /// <summary>
    /// A refused delivery gate still owes the card its transition: without it
    /// the card strands in <c>4-auto-review</c> exactly like AGT-2855, only with
    /// a failure nobody can see.
    /// </summary>
    [Fact]
    public async Task A_refused_delivery_gate_still_moves_the_card_out_of_auto_review()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "gate-refused");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: false));
        var tip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(1, report.Completed);
        Assert.Equal(0, report.Integrated);
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, moved.State);
        Assert.Equal(tip, Git(_repo, "rev-parse", "develop"));
        var step = restarted.Pipeline.Read(moved.FolderPath)!.Steps
            .Last(item => item.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(AcceptedIntegrationFailureCodes.DeliveryGateFailed, step.FailureCode);
    }

    /// <summary>
    /// Idempotence of the whole sweep: running it twice must not merge twice,
    /// move twice, or report a failure the second time around. The boot sweep
    /// and the per-deferral pass both call it, so they will overlap.
    /// </summary>
    [Fact]
    public async Task Running_the_sweep_twice_changes_nothing_the_second_time()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "twice");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));

        var restarted = Build();
        await restarted.Resume.RunOnceAsync("restart-drill");
        var tipAfterFirst = Git(_repo, "rev-parse", "develop");

        var second = await Build().Resume.RunOnceAsync("restart-drill");

        Assert.Equal(0, second.Integrated);
        Assert.Equal(0, second.Completed);
        Assert.Equal(0, second.Failed);
        Assert.Equal(tipAfterFirst, Git(_repo, "rev-parse", "develop"));
    }

    [Fact]
    public async Task Spent_integration_recovery_budget_parks_with_the_exact_reason()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "spent-recovery-budget");
        RemoteDeliverySettlementStore.Write(
            card.FolderPath,
            Settlement(card, shouldIntegrate: true) with
            {
                Stage = RemoteDeliverySettlementStage.IntegrationSettled,
                IntegrationOutcome = MergeIntoIntegrationOutcome.AgentRoundRequired.ToString(),
                IntegrationDetail = "automatic recovery budget used: 2/2",
            });

        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(1, report.Completed);
        var parked = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, parked.State);
        Assert.Equal("automatic recovery budget used: 2/2", parked.ParkedBlocker?.Reason);
        Assert.True(parked.ParkedBlocker?.RequiresDecisionCard);
    }

    /// <summary>
    /// AGT-2849 owns the branch repair; until it has judged an interrupted gate,
    /// the merge on the branch may be exactly the one about to be rolled back.
    /// The resume therefore keeps out while a gate journal is still open.
    /// </summary>
    [Fact]
    public async Task An_open_integration_gate_journal_keeps_the_resume_out()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "gate-open");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));
        IntegrationGateJournal.Open(card.FolderPath, new IntegrationGateJournalEntry
        {
            Project = Project,
            JobId = card.Id,
            RepoRoot = _repo,
            IntegrationBranch = "develop",
            PreMergeTip = Git(_repo, "rev-parse", "develop"),
            StartedAt = DateTimeOffset.UtcNow,
        });
        var tip = Git(_repo, "rev-parse", "develop");

        var restarted = Build();
        var report = await restarted.Resume.RunOnceAsync("restart-drill");

        Assert.Equal(0, report.Integrated);
        Assert.Equal(0, report.Completed);
        Assert.Equal(0, report.Failed);
        Assert.Equal(tip, Git(_repo, "rev-parse", "develop"));
        Assert.Equal(
            TaskStates.AutoReview,
            restarted.Scanner.FindJob(card.Id, _watchPath)!.State);
    }

    /// <summary>
    /// The seam the incident actually ran through: the post-processing pass that
    /// would have deferred this card (and, five attempts later, logged
    /// deferral-exhausted) settles it instead, and closes its own lifecycle as
    /// completed rather than as a verdict that never landed.
    /// </summary>
    [Fact]
    public async Task The_post_processing_pass_that_would_have_deferred_a_passed_card_settles_it_instead()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "worker-seam");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));

        var restarted = Build();
        var request = new AutoReviewPostProcessingRequest(
            Project, card.Id, _watchPath, DateTime.UtcNow, "deferral-retry", Attempt: 4);

        // What the classifier says on its own: the executor is not what is
        // missing, the integration is.
        var classified = await restarted.Orchestrator.ProcessCardAsync(
            _root, Project, card.Id, _watchPath, CancellationToken.None);
        Assert.Equal(PostProcessingCardStatus.Deferred, classified.Status);
        Assert.Equal(PostProcessingCardResult.AwaitingDeliveryIntegration, classified.Reason);

        var settledOutcome = await restarted.Worker.ResumeDeliveryIfOwedAsync(
            request, classified, CancellationToken.None);

        Assert.Equal(PostProcessingCardStatus.Decided, settledOutcome.Status);
        Assert.StartsWith(
            AutoReviewPostProcessingWorker.DeliveryResumedReason,
            settledOutcome.Reason,
            StringComparison.Ordinal);
        var moved = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, moved.State);
        Assert.True(IsOnIntegrationLane(card.DeliverySha), "the delivery did not reach the integration lane");
    }

    /// <summary>
    /// AGT-2995, the QS-106 shape: the integration failed with a typed Error,
    /// the card was parked in Human Review, the cause was repaired, and an
    /// operator moved the card back to Auto Review. The post-processing pass
    /// must re-run the integration decision - not complete the old transition
    /// and park the card again with the same stale verdict.
    /// </summary>
    [Fact]
    public async Task An_operator_move_out_of_an_integration_park_reruns_the_integration()
    {
        var seeded = Build();
        var card = SeedPassedDelivery(seeded, "operator-move");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));

        // origin/develop and the integration lane diverge: the first
        // integration cannot synchronize its branch. Since AGT-2996 the lane,
        // not the checkout's develop, is what follows origin; a local-only
        // develop commit no longer blocks anything, an unpublished lane merge
        // that origin moved past does.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", "-b", "develop", _origin, other);
        Git(other, "config", "user.email", "other@example.com");
        Git(other, "config", "user.name", "Other");
        File.WriteAllText(Path.Combine(other, "remote.txt"), "remote\n");
        Git(other, "add", "-A");
        Git(other, "commit", "-q", "-m", "chore: remote-only develop work");
        Git(other, "push", "-q", "origin", "develop");
        Git(_repo, "checkout", "-q", "-b", "unpublished-lane", "develop");
        File.WriteAllText(Path.Combine(_repo, "local.txt"), "local\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "chore: unpublished lane work");
        Git(_repo, "update-ref", IntegrationLane, Git(_repo, "rev-parse", "HEAD"));
        Git(_repo, "checkout", "-q", "develop");

        var first = await seeded.Resume.RunOnceAsync("delivery");
        Assert.Equal(1, first.Integrated);
        var parked = seeded.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, parked.State);
        Assert.Equal("integration: branch-sync-failed", parked.ParkedBlocker?.Reason);
        Assert.Equal(
            RemoteDeliverySettlementStage.LaneSettled,
            RemoteDeliverySettlementStore.Read(parked.FolderPath)!.Stage);

        // The operator repairs the branch and moves the card out of the park.
        Git(_repo, "fetch", "-q", "origin");
        Git(_repo, "update-ref", IntegrationLane, "refs/remotes/origin/develop");
        var moved = await seeded.Transitions.MoveAsync(
            card.Id, TaskStates.AutoReview, _watchPath, cause: "operator");
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        var restarted = Build();
        var classified = await restarted.Orchestrator.ProcessCardAsync(
            _root, Project, card.Id, _watchPath, CancellationToken.None);
        Assert.Equal(PostProcessingCardStatus.Deferred, classified.Status);
        Assert.Equal(PostProcessingCardResult.AwaitingDeliveryIntegration, classified.Reason);

        // The direct resume path does not inspect the request's queue timestamp.
        var request = new AutoReviewPostProcessingRequest(
            Project, card.Id, _watchPath, DateTime.UnixEpoch, "deferral-retry", Attempt: 1);
        var settledOutcome = await restarted.Worker.ResumeDeliveryIfOwedAsync(
            request, classified, CancellationToken.None);

        Assert.Equal(PostProcessingCardStatus.Decided, settledOutcome.Status);
        Assert.EndsWith(AutoReviewResumePolicy.Reasons.OperatorReentry, settledOutcome.Reason, StringComparison.Ordinal);
        var settled = restarted.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, settled.State);
        Assert.True(IsOnIntegrationLane(card.DeliverySha), "the delivery did not reach the integration lane");
        Assert.False(
            settled.ParkedBlocker?.Reason?.StartsWith(RemoteDeliveryParkReason.Prefix, StringComparison.Ordinal) ?? false,
            $"stale park reason re-applied: {settled.ParkedBlocker?.Reason}");
        var record = RemoteDeliverySettlementStore.Read(settled.FolderPath)!;
        Assert.Equal(nameof(MergeIntoIntegrationOutcome.Merged), record.IntegrationOutcome);
        Assert.Null(record.IntegrationDetail);
        // Still the one review the subject earned: nothing was re-reviewed.
        Assert.Single(restarted.Authority.GetTaskProjection(card.TaskKey).ReviewAttempts);
    }

    [Fact]
    public async Task Infrastructure_requeue_reintegrates_passed_review_without_operator_action_or_cached_failure()
    {
        var calls = 0;
        var stack = Build(_ => Task.FromResult(++calls == 1
            ? MergeIntoIntegrationResult.Failed(
                AcceptedIntegrationFailureCodes.IntegrationError, "host timeout")
            : MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Merged, mergedSha: new string('a', 40))));
        var card = SeedPassedDelivery(stack, "infrastructure-requeue");
        RemoteDeliverySettlementStore.Write(card.FolderPath, Settlement(card, shouldIntegrate: true));

        Assert.Equal(1, (await stack.Resume.RunOnceAsync("initial")).Integrated);
        var parked = stack.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, parked.State);
        Assert.Equal("Error", RemoteDeliverySettlementStore.Read(parked.FolderPath)!.IntegrationOutcome);

        Assert.True(RemoteDeliverySettlementStore.RequeueInfrastructure(parked.FolderPath));
        var move = await stack.Transitions.MoveAsync(
            card.Id, TaskStates.AutoReview, _watchPath, cause: TimelineActors.System);
        Assert.Equal(MoveJobStatus.Success, move.Status);

        var retry = await stack.Resume.RunOnceAsync("infrastructure-requeue");

        Assert.Equal(1, retry.Integrated);
        Assert.Equal(2, calls);
        var completed = stack.Scanner.FindJob(card.Id, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, completed.State);
        var settlement = RemoteDeliverySettlementStore.Read(completed.FolderPath)!;
        Assert.Equal(nameof(MergeIntoIntegrationOutcome.Merged), settlement.IntegrationOutcome);
        Assert.False(settlement.DiscardCompletedReplay);
        Assert.Single(stack.Authority.GetTaskProjection(card.TaskKey).ReviewAttempts);
    }

    private static RemoteDeliverySettlementRecord Settlement(SeededCard card, bool shouldIntegrate) => new()
    {
        TaskKey = card.TaskKey,
        ReviewAttemptId = card.ReviewAttemptId,
        Outcome = nameof(ReviewTerminalOutcome.Pass),
        ShouldIntegrate = shouldIntegrate,
        BuildTestGate = shouldIntegrate
            ? nameof(RemoteBuildTestGateClass.NotApplicable)
            : nameof(RemoteBuildTestGateClass.Failed),
        GateReason = shouldIntegrate
            ? "The Remote Review plan has no applicable build/test gate."
            : "At least one applicable Remote Review build/test gate did not pass.",
        IntegrationBranch = "develop",
        IntegrationStrategy = IntegrationStrategies.DirectMerge,
        PipelineType = PipelineTypes.Task,
        DeliveredAtUtc = DateTimeOffset.UtcNow,
        Stage = RemoteDeliverySettlementStage.IntegrationPending,
        RecordedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// AGT-2996: an integration merges onto the Studio-owned lane; the
    /// checkout's <c>develop</c> follows only once the push worker published
    /// it, and this drill never pushes (<see cref="AutoPushStrategies.Never"/>).
    /// </summary>
    private static readonly string IntegrationLane = GitService.IntegrationLaneRef("develop");

    private bool IsOnIntegrationLane(string sha)
        => Git(_repo, ["merge-base", "--is-ancestor", sha, IntegrationLane], allowFailure: true) == 0;

    private TaskIntegrationStatus Status(Stack stack, string id)
        => stack.Integration
            .BuildLookup([stack.Scanner.FindJob(id, _watchPath)!])
            .Values
            .Single();

    /// <summary>
    /// A card in <c>4-auto-review</c> whose work sits on its own task branch and
    /// whose canonical ReviewAttempt has settled Pass - the durable state the
    /// backend had for AGT-2855 at 08:39.
    /// </summary>
    private SeededCard SeedPassedDelivery(
        Stack stack,
        string id,
        bool settleReview = true,
        string lane = TaskStates.AutoReview,
        ReviewTerminalOutcome reviewOutcome = ReviewTerminalOutcome.Pass)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + id);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var taskKey = "AGT-" + id;
        var folder = Path.Combine(_watchPath, lane, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = taskKey,
                    title = id,
                    state = lane,
                    order = 1,
                    agent = "codex",
                    cliType = "codex",
                    mode = TaskModes.Coding,
                    projectName = Project,
                    ownerClientId = DefaultClientIdentity.Id,
                    commit = CommitRecord(deliverySha),
                    commits = new[] { CommitRecord(deliverySha) },
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: Success\n");
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);

        var run = stack.Authority.AcquireRun(
            taskKey, "repo_" + id, null, "runner-a", "coding-host", 60, "run-" + id).RunAttempt!;
        Assert.True(stack.Authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete-" + id),
            Outcome = "done",
            ResultSha = deliverySha,
        }).Accepted);
        var review = stack.Authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "repo_" + id, deliverySha, run.AttemptId, "req", "policy", [],
            "review-create-" + id)).ReviewAttempt!;

        if (settleReview)
        {
            var claimed = stack.Authority.ClaimReview(
                review.AttemptId, "reviewer", "review-host", 60, "review-claim-" + id).ReviewAttempt!;
            var settled = stack.Authority.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-settle-" + id),
                deliverySha,
                reviewOutcome,
                Reason: "All aspects passed."));
            Assert.True(settled.Accepted);
            Assert.Equal(reviewOutcome, settled.ReviewAttempt!.Outcome);
        }

        return new SeededCard(id, taskKey, folder, deliverySha, review.AttemptId);
    }

    /// <summary>
    /// A complete, freshly constructed service stack. Calling this a second time
    /// is the restart: the durable workspace, the authority file and the git
    /// repository are shared; nothing else is.
    /// </summary>
    private Stack Build(
        Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>>? integrate = null,
        GitOriginStore? originStore = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
        }).Build();

        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance,
            originStore: originStore);
        var authority = new AttemptAuthorityService(
            configuration, NullLogger<AttemptAuthorityService>.Instance);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline,
            attemptAuthority: authority);
        var escalation = new HumanReviewEscalation(
            states, transitions, _root, NullLogger<HumanReviewEscalation>.Instance, scanner);
        var runner = new MergeIntoDevelopRunner(
            git,
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            integrationWorktrees: new IntegrationWorktreeProvider(git));
        var coordinator = new RemoteDeliveryIntegrationCoordinator(
            integrate ?? (request => runner.RunAsync(
                request.Project,
                request.JobId,
                request.JobFolderPath,
                request.WatchPath,
                request.IntegrationBranch,
                CancellationToken.None,
                request.IntegrationStrategy,
                request.PipelineType)),
            NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance,
            recordFailure: (request, failureCode, summary, detail) =>
            {
                var now = DateTime.UtcNow;
                pipeline.RecordStep(request.JobFolderPath, new PipelineStepExecution
                {
                    StepId = PipelineCatalogue.MergeIntoDevelopStepId,
                    Kind = StepKind.Tool,
                    Status = PipelineStepStatus.Failed,
                    StartedAt = now,
                    CompletedAt = now,
                    Verdict = failureCode,
                    VerdictSummary = summary,
                    Reason = detail,
                    FailureCode = failureCode,
                });
            });
        var resume = new AutoReviewDeliveryResumeService(
            scanner,
            authority,
            integration,
            settings,
            coordinator,
            transitions,
            escalation,
            NullLogger<AutoReviewDeliveryResumeService>.Instance);

        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, configuration);
        scanner.SetIndexCache(indexCache);
        var prompts = new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner,
            states,
            new AgentStudio.TaskAccess.TaskAccessService(
                scanner,
                mutations,
                states,
                transitions,
                indexCache,
                NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance),
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance),
            prompts,
            new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance),
            new AutoReviewStatusSnapshot(),
            configuration,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            attemptAuthority: authority,
            integrationStatus: integration);
        orchestrator.CliRunner = (_, _, _, _, _) => Task.FromResult("");
        var worker = new AutoReviewPostProcessingWorker(
            new AutoReviewPostProcessingQueue(),
            orchestrator,
            scanner,
            mutations,
            configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance,
            reviewExecutorRegistry: null,
            deliveryResume: resume);

        return new Stack(scanner, authority, integration, pipeline, timeline, resume, orchestrator, worker,
            transitions, mutations);
    }

    private static object CommitRecord(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    private sealed class CollectingLogger<T>(List<string> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries) entries.Add(formatter(state, exception));
        }
    }

    private sealed record SeededCard(
        string Id,
        string TaskKey,
        string FolderPath,
        string DeliverySha,
        string ReviewAttemptId);

    private sealed record Stack(
        TaskScannerService Scanner,
        AttemptAuthorityService Authority,
        TaskIntegrationStatusService Integration,
        PipelineExecutionLog Pipeline,
        TimelineLog Timeline,
        AutoReviewDeliveryResumeService Resume,
        ReviewDecisionOrchestrator Orchestrator,
        AutoReviewPostProcessingWorker Worker,
        TaskTransitionService Transitions,
        TaskMutationService Mutations);

    private static string Git(string cwd, params string[] args)
    {
        var (exitCode, stdout, stderr) = RunGit(cwd, args);
        Assert.True(exitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    private static int Git(string cwd, string[] args, bool allowFailure)
    {
        var (exitCode, _, stderr) = RunGit(cwd, args);
        Assert.True(allowFailure || exitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return exitCode;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string cwd, string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Restart drill cleanup is best-effort."); }
    }
}
