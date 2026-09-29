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
        Assert.Equal(
            0,
            Git(_repo, ["merge-base", "--is-ancestor", card.DeliverySha, "develop"], allowFailure: true));
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
        var changed = entry.Report with { Summary = "Different verdict detail." };
        // The accepted key owns the journal: a changed payload under it, and any
        // other key, is answered by the authority and never rebinds the journal.
        foreach (var candidate in new[]
                 {
                     entry with { Report = changed, ReportSha256 = RemoteReviewSettlementJournal.Hash(changed) },
                     entry with
                     {
                         IdempotencyKey = "other-key",
                         Report = changed with { IdempotencyKey = "other-key" },
                         ReportSha256 = RemoteReviewSettlementJournal.Hash(changed with { IdempotencyKey = "other-key" }),
                     },
                 })
        {
            var refused = RemoteReviewSettlementJournal.PrepareAndSettle(card.FolderPath, candidate, acceptedKey,
                () =>
                {
                    settleCalls++;
                    return new AttemptWriteResult(AttemptWriteStatus.Invalid, card.ReviewAttemptId);
                });
            Assert.Equal(AttemptWriteStatus.Invalid, refused.Status);
        }
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
        Assert.Equal(
            0,
            Git(_repo, ["merge-base", "--is-ancestor", card.DeliverySha, "develop"], allowFailure: true));
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
    private SeededCard SeedPassedDelivery(Stack stack, string id, bool settleReview = true)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + id);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var taskKey = "AGT-" + id;
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = taskKey,
                    title = id,
                    state = TaskStates.AutoReview,
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
                ReviewTerminalOutcome.Pass,
                Reason: "All aspects passed."));
            Assert.True(settled.Accepted);
            Assert.Equal(ReviewTerminalOutcome.Pass, settled.ReviewAttempt!.Outcome);
        }

        return new SeededCard(id, taskKey, folder, deliverySha, review.AttemptId);
    }

    /// <summary>
    /// A complete, freshly constructed service stack. Calling this a second time
    /// is the restart: the durable workspace, the authority file and the git
    /// repository are shared; nothing else is.
    /// </summary>
    private Stack Build()
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
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
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
            request => runner.RunAsync(
                request.Project,
                request.JobId,
                request.JobFolderPath,
                request.WatchPath,
                request.IntegrationBranch,
                CancellationToken.None,
                request.IntegrationStrategy,
                request.PipelineType),
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

        return new Stack(scanner, authority, integration, pipeline, timeline, resume, orchestrator, worker);
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
        AutoReviewPostProcessingWorker Worker);

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
