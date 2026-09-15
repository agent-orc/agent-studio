using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Drives the <see cref="ReviewDecisionOrchestrator"/> tick against a temp
/// workspace. The fast-model CLI is stubbed: tests inject the response the
/// orchestrator should "receive", then assert the lane transition, the
/// chat-log line, the decision-journal entry, and (for escalate) the
/// human-decision intake creation.
///
/// ADR-0025 routing: tasks land in <c>4-auto-review</c>; reissue moves
/// to <c>2-ready</c> at order 0 (the runner picks it next without
/// displacing a currently active job); accept-as-done and escalate
/// accept promotes to <c>5-human-review</c>, while escalation parks the
/// original card in <c>5e-escalated</c>.
/// </summary>
public class ReviewDecisionOrchestratorTests : IDisposable
{
    [Fact]
    public void RecentLogTruncation_PreservesTerminalAndDropsEarlyFailure()
    {
        var log = "Build FAILED.\n" + new string('x', 7_000) +
                  "\n67/67 tests passed.\n[[TASK_DONE]]\n[taskboard] CLI exited: status=completed, exitCode=0";

        var recent = ReviewDecisionOrchestrator.TruncateTail(log, 6_000);

        Assert.DoesNotContain("Build FAILED", recent);
        Assert.Contains("67/67 tests passed", recent);
        Assert.Contains("[[TASK_DONE]]", recent);
        Assert.Contains("exitCode=0", recent);
    }

    [Fact]
    public void ReviewBasis_SelectsLastSuccessfulRun_NotLaterLaunchFailure()
    {
        var runs = new[]
        {
            new RunRecord
            {
                Index = 1, Status = "completed", HeadShaBefore = "base", HeadShaAfter = "success",
            },
            new RunRecord
            {
                Index = 2, Status = "failed", HeadShaBefore = "success", HeadShaAfter = "success",
            },
        };

        var selected = ReviewDecisionOrchestrator.SelectAuthoritativeReviewRuns(runs);

        var run = Assert.Single(selected);
        Assert.Equal(1, run.Index);
        Assert.Equal("success", run.HeadShaAfter);
    }

    [Fact]
    public void LaunchFailure_AfterGradedSuccess_PreservesHumanReviewBasis()
    {
        var runs = new[]
        {
            new RunRecord { Index = 1, Status = "completed" },
            new RunRecord { Index = 2, Status = "failed" },
        };

        Assert.True(ProjectRunner.HasSuccessfulGradedRun(
            new[] { "code-review:grade-a" }, runs));
        Assert.False(ProjectRunner.HasSuccessfulGradedRun(
            Array.Empty<string>(), runs));
        Assert.False(ProjectRunner.HasSuccessfulGradedRun(
            new[] { "code-review:grade-a" }, new[] { new RunRecord { Status = "failed" } }));
    }

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);
    private const string Project = "demo";

    public ReviewDecisionOrchestratorTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Reissue_LandsInReadyTop_NotProgress_AndJournalsRecord()
    {
        // Race-fix: the pre-fix path moved the reissue straight to
        // 3-progress while the runner-pickup tick observed an empty
        // lane and grabbed the next queued job. The reissue now parks
        // in 2-ready at order 0 so the runner picks it next without
        // displacing whoever is currently running.
        SeedReviewJobWithNeedsInput("fix-layout", "which column is primary?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=Roadmap names option A.]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "fix-layout")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "fix-layout")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "fix-layout")));

        // Order 0 puts the reissue ahead of any fresh ready jobs that
        // typically use order >= 10.
        Assert.Equal(0, ReadJobOrder(TaskStates.Ready, "fix-layout"));

        // UI hint: reissue tag is stamped so the kanban can render the
        // card distinctly from a plain queued task.
        var tags = ReadJobTags(TaskStates.Ready, "fix-layout");
        Assert.Contains(ReviewDecisionOrchestrator.ReissueTagId, tags);

        var log = ReadCliLog(TaskStates.Ready, "fix-layout");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[reissue]", log);
        Assert.Contains("Roadmap names option A.", log);

        var followUp = Path.Combine(_watchPath, TaskStates.Ready, "fix-layout", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUp));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Equal("fix-layout", record.JobId);
        Assert.Contains("option A", record.Reason);
        Assert.False(string.IsNullOrEmpty(record.FollowUp));
    }

    [Fact]
    public async Task Reissue_SecondNormalizedIdenticalPrompt_IsDiagnosisFirstAndTimelineRecordsGuard()
    {
        const string slug = "repeat-guard";
        const string question = "which column is primary?";
        const string decision =
            "[[ORCHESTRATOR_DECISION: action=reissue; reason=Roadmap names option A.]]\n[[TASK_DONE]]";
        SeedReviewJobWithNeedsInput(slug, question);

        await BuildOrchestrator(decision).TickOnceAsync(_workspace, CancellationToken.None);

        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        var firstPrompt = File.ReadAllText(Path.Combine(readyFolder, "orchestrator-follow-up.md"));

        var reviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.Move(readyFolder, reviewFolder);
        TaskJsonFile.UpdateField(
            reviewFolder, "state", TaskStates.AutoReview, NullLogger.Instance);
        File.AppendAllText(
            Path.Combine(reviewFolder, "logs", "cli-output.log"),
            $"[12:01:00.000] [stdout] [[TASK_NEEDS_INPUT: {question}]]{Environment.NewLine}");

        await BuildOrchestrator(decision).TickOnceAsync(_workspace, CancellationToken.None);

        var secondPrompt = File.ReadAllText(
            Path.Combine(readyFolder, "orchestrator-follow-up.md"));
        Assert.NotEqual(firstPrompt, secondPrompt);
        Assert.Contains("## Reissue repeat guard: diagnosis first", secondPrompt);
        Assert.Contains("Name the exact failed check, blocking aspect, or missing evidence", secondPrompt);

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(2, records.Count);
        Assert.Contains("## Reissue repeat guard: diagnosis first", records[^1].FollowUp);

        var events = ReadTimeline(TaskStates.Ready, slug);
        var guard = Assert.Single(events, e =>
            e.Kind == TimelineEventKinds.OrchestratorSteered
            && e.Details?.GetValueOrDefault("cause") == "reissue-prompt-repeat-guard");
        Assert.Equal(TimelineActors.QualityLoop, guard.Actor);
        Assert.Equal("diagnosis-first-enrichment", guard.Details?["action"]);
        Assert.Equal("1", guard.Details?["matchingPriorPrompts"]);
    }

    [Fact]
    public async Task Reissue_WithAnotherJobActiveInProgress_DoesNotDisplaceIt()
    {
        // Regression for the 2026-05-11 race: while auto-review verdicts
        // run (~1.5 min for aspect runs), the runner-pickup tick can
        // observe an empty 3-progress and grab the next ready job. If
        // the verdict then routed the reissue into 3-progress directly,
        // both jobs ended up in the active lane and the running one was
        // silently parked. The fix routes reissues to 2-ready at order 0
        // so the lane invariant "at most one job in 3-progress" holds
        // across the entire verdict window.
        SeedProgressJob("currently-running");
        SeedReviewJobWithNeedsInput("reissued-task", "which column is primary?");

        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=ok]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Active job stays where it was; reissue lands in ready, not progress.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "currently-running")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "reissued-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "reissued-task")));

        // Order 0 means the runner picks the reissue as the very next
        // task once 'currently-running' finishes.
        Assert.Equal(0, ReadJobOrder(TaskStates.Ready, "reissued-task"));
    }

    [Fact]
    public async Task Canonical_remote_review_attempt_is_not_run_in_task_server_checkout()
    {
        const string slug = "remote-result";
        SeedReviewJobWithDone(slug);
        var authorityConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        var authority = new AttemptAuthorityService(
            authorityConfig, NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun(
            slug, Project, null, "remote-runner", "remote-host", 60, "claim-remote").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "complete-remote"),
            Outcome = "done",
            ResultSha = "589c462f",
        });
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            slug, Project, "589c462f", run.AttemptId, "requirements", "policy", [], "create-review"));
        Assert.Equal(AttemptWriteStatus.Accepted, review.Status);

        var calls = 0;
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept; reason=must not run locally.]]",
            onCall: () => calls++,
            attemptAuthority: authority);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        Assert.Empty(ReviewDecisionLog.ReadAll(_workspace, Project));
        Assert.Equal(review.AttemptId, authority.GetTaskProjection(slug).CurrentReviewAttempt!.AttemptId);
    }

    [Fact]
    public async Task Escalate_FlipsOriginalToEscalated_WritesSupervisorBanner_NoWrapperCard()
    {
        SeedReviewJobWithNeedsInput("auth-rewrite", "use OAuth or magic-link?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs strategic call.]]",
            reviewCli: "custom-review-cli");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // ADR-0049: an orchestrator that cannot decide unattended flips the
        // *original* card to 5e-escalated (the retired 1b-needs-human-review
        // lane is gone); the legacy auto-review folder must no longer hold the job.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "auth-rewrite")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "auth-rewrite")));

        var log = ReadCliLog(TaskStates.Escalated, "auth-rewrite");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("strategic call", log);
        Assert.Contains(TaskStates.Escalated, log);

        // ADR-0049: no sibling human-decision-needed-<slug> wrapper card is
        // spawned - the wrapper-card pattern (ASS-30) is the bug this ADR ends.
        var intake = Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-auth-rewrite");
        Assert.False(Directory.Exists(intake));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.Escalated, "auth-rewrite");
        Assert.Contains(outcomes, o =>
            o.Outcome == PostProcessingOutcomes.NeedsHumanInput &&
            o.Performer == PostProcessingPerformers.SupportingAgent &&
            o.PerformerCliType == "custom-review-cli");
    }

    [Fact]
    public async Task NeedsInput_MalformedDecision_EscalatesRatherThanAcceptingOrLooping()
    {
        SeedReviewJobWithNeedsInput("malformed-decision", "which provider should I use?");
        var calls = 0;
        var orchestrator = BuildOrchestrator(
            cliResponse: "I choose provider A, but forgot the required sentinel.",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "malformed-decision")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "malformed-decision")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "malformed-decision")));

        var log = ReadCliLog(TaskStates.Escalated, "malformed-decision");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("no parseable [[ORCHESTRATOR_DECISION]]", log);

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
        Assert.Equal(ReviewDecisionKind.Escalate, records[0].Kind);
        Assert.DoesNotContain(records, r => r.Kind == ReviewDecisionKind.AcceptAsDone);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Single(ReviewDecisionLog.ReadAll(_workspace, Project));
    }

    [Fact]
    public async Task AcceptAsDone_OperatorBannerLine_FiresOnlyAfterLaneMoveSucceeds()
    {
        // Regression for 2026-05-15: the operator saw "Orchestrator decided
        // accept" while the task was still in 3-progress (or 4-auto-review).
        // The orchestrator chat-log line that drives the workspace banner
        // (and the activity-log decision row) MUST land in the post-move
        // folder, never in 4-auto-review. We assert that here by spying on
        // the chat log and recording the TaskInfo.FolderPath at write time.
        SeedReviewJobWithNeedsInput("banner-timing", "anything?");
        var spy = new RecordingChatLog();
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract.]]",
            chatLogOverride: spy);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var decisionWrites = spy.Calls
            .Where(c => c.Kind == OrchestratorMessageKind.Decision)
            .ToList();
        Assert.NotEmpty(decisionWrites);
        foreach (var call in decisionWrites)
        {
            Assert.Contains(TaskStates.HumanReview, call.FolderPath);
            Assert.DoesNotContain(TaskStates.AutoReview, call.FolderPath);
        }
    }

    [Fact]
    public async Task AcceptAsDone_LaneMoveFails_NoBannerLineWritten_NoJournalRecord()
    {
        // The complementary half: if the lane move fails (we simulate by
        // placing a file where the destination directory would be),
        // no operator-facing decision line goes out and no journal entry
        // records the accept. The banner must not claim "moved to human
        // review" when the folder is still in 4-auto-review.
        SeedReviewJobWithNeedsInput("blocked-move", "anything?");
        // Pre-existing directories are deduped by MoveJob; a file at the
        // target path still forces the move to fail.
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "blocked-move"), "not a directory");

        var spy = new RecordingChatLog();
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract.]]",
            chatLogOverride: spy);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Source folder must still be in 4-auto-review (move was blocked).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "blocked-move")));

        // No banner-worthy chat-log line went out.
        Assert.DoesNotContain(spy.Calls, c => c.Kind == OrchestratorMessageKind.Decision);

        // No accept recorded in the decision journal -> the next tick will
        // retry the move instead of leaving the operator with a misleading
        // "accepted" notification.
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task AcceptAsDone_PromotesToHumanReview_NotDirectlyToCompleted_AndJournalsRecord()
    {
        SeedReviewJobWithNeedsInput("doc-edit", "should I add screenshots?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract; question is courtesy.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // ADR-0025 contract: accept-as-done routes to 5-human-review,
        // never directly to 6-completed. The user always confirms.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "doc-edit")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "doc-edit")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "doc-edit")));

        // Provenance: the orchestrator advanced this task toward Completed
        // (accept-as-done), so the orchestrator-moved tag must be stamped.
        // A human accepting in the UI never stamps this tag.
        var tags = ReadJobTags(TaskStates.HumanReview, "doc-edit");
        Assert.Contains(ReviewDecisionOrchestrator.OrchestratorMovedTagId, tags);

        var log = ReadCliLog(TaskStates.HumanReview, "doc-edit");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[decision]", log);
        // Operator-friendly copy includes the task title and the destination
        // lane in human terms (the slug-only form was the source of the
        // "container-displayin Agent" rendering bug).
        Assert.Contains("Auto-review accepted", log);
        Assert.Contains("doc-edit title", log);
        Assert.Contains("5-human-review", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.HumanReview, "doc-edit");
        Assert.Contains(outcomes, o =>
            o.Outcome == PostProcessingOutcomes.PassToHumanReview &&
            o.Performer == PostProcessingPerformers.SupportingAgent &&
            o.PerformerCliType == CliTypes.Codex);
    }

    [Fact]
    public async Task DoesNotReprocess_OnceOrchestratorLineIsPresent()
    {
        SeedReviewJobWithNeedsInput("already-answered", "anything?");
        // Append an orchestrator follow-up so the parser treats the chain as resolved.
        var logPath = TaskPathLog(TaskStates.AutoReview, "already-answered");
        File.AppendAllText(logPath,
            $"\n[12:00:30.000] [orchestrator] [reissue] previously answered{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=should not run]]",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "already-answered")));
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Theory]
    [InlineData(ReviewDecisionKind.Reissue, TaskStates.Ready)]
    [InlineData(ReviewDecisionKind.Escalate, TaskStates.Escalated)]
    [InlineData(ReviewDecisionKind.AcceptAsDone, TaskStates.HumanReview)]
    public async Task StaleWithVerdict_BackfillsDueLaneMove_ForEachVerdictType(
        ReviewDecisionKind verdict, string expectedLane)
    {
        // The move-after-verdict bug: the verdict path resolved the agent
        // sentinel (its [orchestrator]/[supervisor] follow-up line) then
        // warned-but-continued on a failed MoveJob, leaving the card parked in
        // 4-auto-review WITH a journalled verdict. Every other guard skips it
        // (sentinel resolved; IsStaleWithoutVerdict only covers the no-verdict
        // case), so it hangs. The backfill nudges the due move after the grace
        // window: reissue -> 2-ready, escalate -> 5e-escalated, accept -> 5-human-review.
        var slug = $"stuck-{verdict}".ToLowerInvariant();
        SeedResolvedReviewCardPastGrace(slug);
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-25),
            JobId: slug,
            Project: Project,
            Kind: verdict,
            Reason: "verdict recorded but the lane move never completed",
            Prompt: "(seed)",
            Response: "(seed)",
            FollowUp: string.Empty));

        var calls = 0;
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=should not run]]",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Deterministic backfill: no fast-model call.
        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, expectedLane, slug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));

        // Idempotent: the backfill performs only the move, it does not append a
        // second verdict record (the original is the source of truth).
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
        Assert.Equal(verdict, records[0].Kind);
    }

    [Fact]
    public async Task StaleWithVerdict_DoesNotFire_BeforeGraceWindow()
    {
        // A card that has only just had its verdict recorded (log mtime fresh)
        // must NOT be force-moved: a verdict path may still be completing its own
        // move on the same tick. Only a card stuck past the grace window is stale.
        SeedResolvedReviewCardPastGrace("fresh-verdict");
        // Re-stamp the log to "now" so the grace window has not elapsed.
        File.SetLastWriteTimeUtc(TaskPathLog(TaskStates.AutoReview, "fresh-verdict"), DateTime.UtcNow);
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: "fresh-verdict",
            Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "just recorded",
            Prompt: "(seed)",
            Response: "(seed)",
            FollowUp: string.Empty));

        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=noop]]");
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Untouched: still in 4-auto-review (the grace window protects it).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "fresh-verdict")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "fresh-verdict")));
    }

    [Fact]
    public async Task StaleWithVerdict_SkippedVerdict_IsNotForceMoved()
    {
        // A Skipped record (no decision sentinel parsed) does NOT resolve the
        // agent sentinel, so the normal sentinel path re-processes the card. The
        // backfill must not treat Skipped as a due move and yank it out of lane.
        SeedResolvedReviewCardPastGrace("skipped-card");
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-25),
            JobId: "skipped-card",
            Project: Project,
            Kind: ReviewDecisionKind.Skipped,
            Reason: "no sentinel parsed",
            Prompt: "(seed)",
            Response: "(seed)",
            FollowUp: string.Empty));

        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=noop]]");
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "skipped-card")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "skipped-card")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "skipped-card")));
    }

    [Fact]
    public async Task UnworkedCardWithNoCoreRun_BouncesToReady_NotArchive()
    {
        // ASS-693 / ASS-716: an epic decomposition placed sub-tasks directly in
        // 4-auto-review. A card that never ran a core agent run (no
        // cli-output.log) has nothing to review; left there, a sweep wiped it to
        // 7-archive unworked. The sweep-guard bounces it to 2-ready instead -
        // deterministically, with no fast-model call - so the pickup loop runs it.
        var calls = 0;
        SeedReviewJobWithoutCoreRun("unworked-subtask");
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "unworked-subtask")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "unworked-subtask")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "unworked-subtask")));

        // Deterministic bounce: no fast-model decision call was made.
        Assert.Equal(0, calls);

        // An operator-facing note explains the bounce on the moved card.
        var log = ReadCliLog(TaskStates.Ready, "unworked-subtask");
        Assert.Contains("no core run", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Equal("unworked-subtask", record.JobId);
    }

    [Fact]
    public async Task UnworkedCardBounce_IsIdempotent_AcrossTicks()
    {
        // Re-bill safety: a successful bounce takes the card out of
        // 4-auto-review, so a second tick finds nothing to do.
        SeedReviewJobWithoutCoreRun("unworked-once");
        var orchestrator = BuildOrchestrator(cliResponse: "");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "unworked-once")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "unworked-once")));
        Assert.Single(ReviewDecisionLog.ReadAll(_workspace, Project));
    }

    [Fact]
    public async Task StaleWithVerdict_BackfillIsIdempotent_AcrossTicks()
    {
        // Re-bill safety / move-retry semantics: the backfill appends no new
        // verdict record and a successful move takes the card out of the lane,
        // so a second tick is a clean no-op. The same guard is what retries the
        // move on a later tick when an earlier move did not stick (move-lock).
        SeedResolvedReviewCardPastGrace("idempotent-stuck");
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-25),
            JobId: "idempotent-stuck",
            Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "verdict recorded but the lane move never completed",
            Prompt: "(seed)",
            Response: "(seed)",
            FollowUp: string.Empty));

        var orchestrator = BuildOrchestrator(cliResponse: "[[ORCHESTRATOR_DECISION: action=reissue; reason=noop]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Moved exactly once to 5e-escalated; second tick finds nothing to do.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "idempotent-stuck")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "idempotent-stuck")));

        // No new verdict records were appended by the backfill (only the seed).
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
    }

    [Fact]
    public async Task NoOp_WithRealPrompt_ReissuesWithSharpenedFraming_WithoutSpendingFastModel()
    {
        SeedReviewJobWithNoOp("flesh-out-readme",
            title: "Add product overview to README",
            promptBody: "# Add product overview\n\nWrite a clear two-paragraph product overview at the top of README.md describing the kanban board, the agent loop, and the watch-path model.\n");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // No fast-model call should have been made.
        Assert.Equal(0, calls);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "flesh-out-readme")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "flesh-out-readme")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "flesh-out-readme")));
        Assert.Equal(0, ReadJobOrder(TaskStates.Ready, "flesh-out-readme"));

        var log = ReadCliLog(TaskStates.Ready, "flesh-out-readme");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("[reissue]", log);
        Assert.Contains("NOOP recovery", log);

        var followUpPath = Path.Combine(_watchPath, TaskStates.Ready, "flesh-out-readme", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUpPath));
        var followUp = File.ReadAllText(followUpPath);
        Assert.Contains("Do not reply 'task done'", followUp);
        Assert.Contains("Add product overview", followUp);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
    }

    [Fact]
    public async Task NoOp_AfterNoProgressReissue_EscalatesToHumanReview()
    {
        SeedReviewJobWithDoubleNoProgressNoOp("double-noop",
            title: "Implement no-op recovery guard",
            promptBody: "# Implement guard\n\nAdd a deterministic guard for repeated Codex NOOP recovery loops.\n");

        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-2),
            JobId: "double-noop",
            Project: Project,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "Agent emitted [[TASK_NOOP]] but the task description is real; reissuing with sharpened framing.",
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: "(test seed)"));

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "double-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "double-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "double-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "double-noop")));

        var log = ReadCliLog(TaskStates.Escalated, "double-noop");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("Escalated: 2 consecutive NOOPs without progress", log);

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(2, records.Count);
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
        Assert.Contains("Escalated: 2 consecutive NOOPs without progress", records[^1].Reason);
    }

    [Fact]
    public async Task NoOp_WithEmptyPrompt_PromotesToHumanReview_NoWrapperCard()
    {
        SeedReviewJobWithNoOp("placeholder-task",
            title: "TODO: fill in",
            promptBody: "# TODO\n\nplaceholder\n");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "placeholder-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "placeholder-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "placeholder-task")));

        var log = ReadCliLog(TaskStates.Escalated, "placeholder-task");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("empty or placeholder", log);

        // ADR-0049: escalation no longer spawns a human-decision-needed card.
        var intake = Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-placeholder-task");
        Assert.False(Directory.Exists(intake));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
    }

    [Fact]
    public async Task NoOp_AfterReissueBudgetExhausted_EscalatesInsteadOfReissuing()
    {
        SeedReviewJobWithNoOp("repeated-noop",
            title: "Implement caching layer",
            promptBody: "# Implement caching\n\nAdd an LRU cache in front of the TaskScannerService.GetJobs call to avoid the O(N) disk scan on every poll.\n");

        // Pre-populate the journal with two prior reissues for this job
        // (default budget = 2). Any further reissue should escalate.
        for (var i = 0; i < 2; i++)
        {
            ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow.AddMinutes(-10 + i),
                JobId: "repeated-noop",
                Project: Project,
                Kind: ReviewDecisionKind.Reissue,
                Reason: "prior reissue",
                Prompt: "(test seed)",
                Response: "(test seed)",
                FollowUp: "(test seed)"));
        }

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        // Budget-exhausted NOOP must escalate to 5e-escalated.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "repeated-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "repeated-noop")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "repeated-noop")));

        // ADR-0049: no wrapper card is spawned for the escalation.
        var intake = Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-repeated-noop");
        Assert.False(Directory.Exists(intake));

        var log = ReadCliLog(TaskStates.Escalated, "repeated-noop");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("prior orchestrator reissue", log);

        // Three records: two seed reissues, plus this tick's escalate.
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(3, records.Count);
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
    }

    [Fact]
    public async Task NoCompletionSignal_ModelAccept_FallsBackToDeterministicReissue()
    {
        // Requirement 4 (deterministic completion): a run landed in
        // 4-auto-review with NO terminal sentinel - only heuristic
        // "done"-ish prose. The fast-model fallback may choose reissue or
        // escalate, but it must not override the missing-sentinel gate with a
        // softer accept-as-done decision.
        SeedReviewJobWithoutSentinel("ghosted-completion",
            title: "Add pagination to the task list endpoint",
            promptBody: "# Add pagination\n\nAdd cursor-based pagination to GET /api/tasks so the kanban can lazy-load lanes with hundreds of cards.\n");

        var calls = 0;
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Implementation evidence is complete despite the missing sentinel.]]\n[[TASK_DONE]]",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ghosted-completion")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "ghosted-completion")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "ghosted-completion")));

        var log = ReadCliLog(TaskStates.Ready, "ghosted-completion");
        Assert.Contains("[orchestrator]", log);
        Assert.Contains("no completion signal", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Equal("(deterministic no-completion-signal branch)", record.Prompt);
        Assert.Contains("terminal sentinel", record.Reason);
    }

    [Fact]
    public async Task NoCompletionSignal_MalformedFallback_ReissuesDemandingSentinel()
    {
        // The LLM fallback must not create a new dead-end when its own output
        // is malformed. Fall back to the deterministic reissue path and demand
        // a terminal sentinel from the run agent.
        SeedReviewJobWithoutSentinel("ghosted-malformed",
            title: "Add pagination to the task list endpoint",
            promptBody: "# Add pagination\n\nAdd cursor-based pagination to GET /api/tasks so the kanban can lazy-load lanes with hundreds of cards.\n",
            commits: new[] { ("abc1234", "feat: add cursor model"), ("def5678", "test: cover task pagination") });

        var calls = 0;
        var orchestrator = BuildOrchestrator(
            cliResponse: "I think this should be accepted, but I forgot the required decision sentinel.",
            onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ghosted-malformed")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "ghosted-malformed")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "ghosted-malformed")));
        Assert.Equal(0, ReadJobOrder(TaskStates.Ready, "ghosted-malformed"));

        var followUpPath = Path.Combine(_watchPath, TaskStates.Ready, "ghosted-malformed", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUpPath));
        var followUp = File.ReadAllText(followUpPath);
        Assert.Contains("terminal sentinel", followUp);
        Assert.Contains("[[TASK_DONE]]", followUp);
        Assert.Contains("Commits already made for this task", followUp);
        Assert.Contains("abc1234 feat: add cursor model", followUp);
        Assert.Contains("def5678 test: cover task pagination", followUp);

        var historyDir = Path.Combine(_watchPath, TaskStates.Ready, "ghosted-malformed", "orchestrator-follow-up-history");
        var historyPath = Assert.Single(Directory.GetFiles(historyDir, "*.md"));
        var history = File.ReadAllText(historyPath);
        Assert.Contains("- priorCommits:", history);
        Assert.Contains("abc1234 feat: add cursor model", history);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Equal("(deterministic no-completion-signal branch)", record.Prompt);
        Assert.Contains("abc1234 feat: add cursor model", record.FollowUp);
    }

    [Fact]
    public async Task NoCompletionSignal_AfterReissueBudgetExhausted_EscalatesNeverAcceptsAsDone()
    {
        // The acceptance criterion's terminating case: once the shared
        // reissue budget is spent and the deterministic signal still never
        // arrived, the orchestrator escalates to human review - it must
        // NEVER fall back to accept-as-done.
        SeedReviewJobWithoutSentinel("stubborn-no-signal",
            title: "Wire up the metrics exporter",
            promptBody: "# Metrics exporter\n\nExpose Prometheus counters for run starts, completions, and escalations from the orchestrator loop.\n");

        // Default budget = 2; two prior reissues exhaust it.
        AppendReissueDecision("stubborn-no-signal", "prior reissue 1");
        AppendReissueDecision("stubborn-no-signal", "prior reissue 2");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "stubborn-no-signal")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "stubborn-no-signal")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "stubborn-no-signal")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "stubborn-no-signal")));

        var log = ReadCliLog(TaskStates.Escalated, "stubborn-no-signal");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("deterministic completion signal", log);

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(3, records.Count);
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
        // Never accept-as-done on a missing deterministic signal.
        Assert.DoesNotContain(records, r => r.Kind == ReviewDecisionKind.AcceptAsDone);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // The lane move is the idempotency guard: once in human review, a
        // later tick must not create another "needs your attention" intake or
        // append a duplicate escalation verdict.
        records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(3, records.Count);
        var intake = Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-stubborn-no-signal");
        Assert.False(Directory.Exists(intake));
    }

    [Fact]
    public async Task NoCompletionSignal_WithWithheldCommitCandidates_ParksWithGateAndCountInTheReason()
    {
        // AGT-2828 / WEB-21: the run ended without a sentinel because the commit
        // candidate gate withheld its finished delivery. Before this fix the card
        // landed in 5e-escalated with cause no-completion-signal and an EMPTY
        // parked reason - the operator had no way to know 14 files were waiting
        // uncommitted in the worktree.
        SeedReviewJobWithoutSentinel("withheld-evidence",
            title: "Capture board evidence",
            promptBody: "# Capture board evidence\n\nTake 12 screenshots of the kanban board in both themes and record them under the project's asset path.\n");
        AppendReissueDecision("withheld-evidence", "prior reissue 1");
        AppendReissueDecision("withheld-evidence", "prior reissue 2");

        var candidates = Enumerable.Range(1, 12)
            .Select(i => new WithheldCommitCandidate
            {
                Path = $"docs/assets/shot-{i:00}.png",
                Reason = "binary-surprise",
                SizeBytes = 2048,
                Binary = true,
            })
            .Append(new WithheldCommitCandidate { Path = "docs/report.md", Reason = "gate-warn" })
            .Append(new WithheldCommitCandidate { Path = "status.md", Reason = "gate-warn" })
            .ToArray();
        WithheldCommitCandidateStore.Write(
            Path.Combine(_watchPath, TaskStates.AutoReview, "withheld-evidence"),
            new WithheldCommitCandidateRecord
            {
                Decision = CommitGateDecisions.Warn,
                Operation = "worktree-run",
                TaskId = "withheld-evidence",
                Branch = "task/withheld-evidence",
                RepositoryRoot = _watchPath,
                InspectedAtUtc = DateTime.UtcNow,
                NothingCommitted = true,
                Candidates = candidates,
            });

        var orchestrator = BuildOrchestrator(cliResponse: "");
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var escalated = Path.Combine(_watchPath, TaskStates.Escalated, "withheld-evidence");
        Assert.True(Directory.Exists(escalated));

        // The parked marker the board reads: no longer an empty reason, and it
        // names the gate and the count.
        var parked = ParkedBlockerMarker.TryRead(escalated);
        Assert.NotNull(parked);
        Assert.Equal(HumanReviewEscalationCategories.NoCompletionSignal, parked!.BlockerType);
        Assert.Contains("commit candidate gate (warn) withheld 14 file(s)", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("uncommitted", parked.Reason, StringComparison.Ordinal);

        // And the card says WHICH files, plus the action that lands them.
        var log = ReadCliLog(TaskStates.Escalated, "withheld-evidence");
        Assert.Contains("`docs/assets/shot-01.png` - binary-surprise", log);
        Assert.Contains("`docs/report.md`", log);
        Assert.Contains("/api/tasks/withheld-evidence/git/withheld-candidates/commit", log);

        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Equal(ReviewDecisionKind.Escalate, records[^1].Kind);
        Assert.Contains("withheld 14 file(s)", records[^1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCompletionSignal_WithoutWithheldCandidates_StillCarriesItsOwnReasonIntoThePark()
    {
        // Negative guard for the same wiring: the park reason must be non-empty
        // and correctly typed even when no commit candidates were withheld, so
        // the fix cannot regress into "only gate parks are explainable".
        SeedReviewJobWithoutSentinel("plain-no-signal",
            title: "Wire up the metrics exporter",
            promptBody: "# Metrics exporter\n\nExpose Prometheus counters for run starts, completions, and escalations from the orchestrator loop.\n");
        AppendReissueDecision("plain-no-signal", "prior reissue 1");
        AppendReissueDecision("plain-no-signal", "prior reissue 2");

        var orchestrator = BuildOrchestrator(cliResponse: "");
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var parked = ParkedBlockerMarker.TryRead(
            Path.Combine(_watchPath, TaskStates.Escalated, "plain-no-signal"));
        Assert.NotNull(parked);
        Assert.Equal(HumanReviewEscalationCategories.NoCompletionSignal, parked!.BlockerType);
        Assert.False(string.IsNullOrWhiteSpace(parked.Reason));
        Assert.DoesNotContain("commit candidate gate", parked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCompletionSignal_WithEmptyPrompt_EscalatesImmediately()
    {
        // A placeholder/empty prompt cannot be driven to a sentinel by
        // re-running, so escalate straight to human review rather than
        // burning the reissue budget.
        SeedReviewJobWithoutSentinel("empty-scope-no-signal",
            title: "TODO",
            promptBody: "# TODO\n");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "empty-scope-no-signal")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "empty-scope-no-signal")));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
    }

    [Fact]
    public async Task NoCompletionSignal_DoesNotReprocess_OnceEscalateLineIsPresent()
    {
        // Negative guard: a sentinel-less job the orchestrator already
        // escalated (a [supervisor] follow-up line is present, nothing ran
        // after it) must not be re-detected as a fresh no-signal case.
        SeedReviewJobWithoutSentinel("already-escalated-no-signal",
            title: "Refactor the watcher",
            promptBody: "# Refactor\n\nSplit the watch-path scanner into per-project workers so a slow project cannot stall the others.\n",
            includeRunnerActiveClearedMarker: false);
        var logPath = TaskPathLog(TaskStates.AutoReview, "already-escalated-no-signal");
        File.AppendAllText(logPath,
            $"[12:05:00.000] [supervisor] [escalate] Orchestrator could not obtain a deterministic completion signal.{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "already-escalated-no-signal")));
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task Blocked_PromotesToHumanReview_NoWrapperCard_WithoutSpendingFastModel()
    {
        SeedReviewJobWithBlocked("bug-commit-hangs", "awaiting user decision A/B/C");
        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // BLOCKED is deterministic: no fast-model call.
        Assert.Equal(0, calls);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "bug-commit-hangs")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "bug-commit-hangs")));

        // ADR-0049: no human-decision-needed wrapper card is spawned.
        var intake = Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-bug-commit-hangs");
        Assert.False(Directory.Exists(intake));

        var log = ReadCliLog(TaskStates.Escalated, "bug-commit-hangs");
        Assert.Contains("[supervisor]", log);
        Assert.Contains("[escalate]", log);
        Assert.Contains("BLOCKED", log);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.Contains("awaiting user decision A/B/C", record.Reason);
    }

    [Fact]
    public async Task Blocked_PlaceholderReason_EscalatesWithLastAgentParagraph()
    {
        SeedReviewJobWithBlocked("missing-sdk", "<short reason>");
        var logPath = TaskPathLog(TaskStates.AutoReview, "missing-sdk");
        File.WriteAllText(logPath,
            $"[12:00:00.000] [stdout] The required Contoso SDK is unavailable from every configured feed.{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_BLOCKED:<short reason>]]{Environment.NewLine}");
        var orchestrator = BuildOrchestrator(cliResponse: "");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.Contains("Contoso SDK is unavailable", record.Reason);
        Assert.DoesNotContain("<short reason>", record.Reason);
    }

    [Fact]
    public async Task Blocked_DoesNotReprocess_OnceSupervisorEscalateLineIsPresent()
    {
        SeedReviewJobWithBlocked("already-escalated", "needs human");
        // Append a supervisor escalate so the parser treats the chain as resolved.
        var logPath = TaskPathLog(TaskStates.AutoReview, "already-escalated");
        File.AppendAllText(logPath,
            $"[12:00:30.000] [supervisor] [escalate] previously escalated{Environment.NewLine}");

        var calls = 0;
        var orchestrator = BuildOrchestrator(cliResponse: "", onCall: () => calls++);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task BootSweep_ProcessesPreExistingAutoReviewItem_Immediately()
    {
        // The audit case: a BLOCKED job that landed in 4-auto-review while
        // the backend was offline and is now stuck. The boot sweep
        // (one-shot call to TickOnceAsync before the recurring loop
        // starts) must pick it up on the very first tick rather than
        // waiting for the 30-second interval.
        SeedReviewJobWithBlocked("audit-stuck-blocked", "awaiting user input");

        var orchestrator = BuildOrchestrator(cliResponse: "");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.Equal("audit-stuck-blocked", record.JobId);
    }

    [Fact]
    public void BuildOrchestratorVerdictLookup_ReturnsLatestKindPerJob()
    {
        // Two journal entries for the same job: the latest one wins.
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            JobId: "job-a", Project: Project,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "first try", Prompt: "p", Response: "r", FollowUp: "f"));
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: "job-a", Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "gave up", Prompt: "p", Response: "r", FollowUp: ""));
        // A separate job with one acceptAsDone record.
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: "job-b", Project: Project,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: "looks good", Prompt: "p", Response: "r", FollowUp: ""));

        var jobs = new[]
        {
            new TaskInfo { Id = "job-a", TaskKey = $"{_watchPath}::job-a", ProjectName = Project, WatchPath = _watchPath, State = TaskStates.HumanReview },
            new TaskInfo { Id = "job-b", TaskKey = $"{_watchPath}::job-b", ProjectName = Project, WatchPath = _watchPath, State = TaskStates.HumanReview },
            new TaskInfo { Id = "job-c", TaskKey = $"{_watchPath}::job-c", ProjectName = Project, WatchPath = _watchPath, State = TaskStates.AutoReview },
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();

        var lookup = TaskEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, config);

        Assert.Equal("escalate", lookup[$"{_watchPath}::job-a"]);
        Assert.Equal("accept",   lookup[$"{_watchPath}::job-b"]);
        Assert.False(lookup.ContainsKey($"{_watchPath}::job-c"));
    }

    [Fact]
    public void BuildOrchestratorVerdictLookup_DropsVerdictOlderThanLatestClaim()
    {
        var oldDecisionAt = DateTime.UtcNow.AddMinutes(-5);
        var newClaimAt = DateTime.UtcNow.AddMinutes(-1);
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: oldDecisionAt,
            JobId: "job-a", Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "old cycle", Prompt: "p", Response: "r", FollowUp: ""));

        var job = new TaskInfo
        {
            Id = "job-a",
            TaskKey = $"{_watchPath}::job-a",
            ProjectName = Project,
            WatchPath = _watchPath,
            State = TaskStates.Progress,
            Provenance = new TaskProvenance
            {
                Transitions =
                [
                    new TaskProvenanceTransition { Lane = TaskStates.AutoReview, AtUtc = oldDecisionAt.AddMinutes(-1) },
                    new TaskProvenanceTransition { Lane = TaskStates.Progress, AtUtc = newClaimAt },
                ],
            },
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();

        var lookup = TaskEndpointHelpers.BuildOrchestratorVerdictLookup([job], config);

        Assert.False(lookup.ContainsKey(job.TaskKey));
    }

    [Fact]
    public void CardProjection_DropsOutcomeIssueOlderThanLatestClaim()
    {
        var claimAt = DateTime.UtcNow;
        var stale = new TaskInfo
        {
            Id = "job-a",
            TaskKey = $"{_watchPath}::job-a",
            ProjectName = Project,
            WatchPath = _watchPath,
            State = TaskStates.Progress,
            OutcomeIssue = new TaskOutcomeIssue
            {
                Kind = "environment-blocker",
                LastSeenAt = claimAt.AddMinutes(-2),
            },
            Provenance = new TaskProvenance
            {
                Transitions =
                [
                    new TaskProvenanceTransition { Lane = TaskStates.Progress, AtUtc = claimAt },
                ],
            },
        };
        var current = stale with
        {
            OutcomeIssue = stale.OutcomeIssue with { LastSeenAt = claimAt.AddMinutes(1) },
        };

        Assert.True(TaskEndpointHelpers.IsOutcomeIssueFromSupersededAttempt(stale));
        Assert.False(TaskEndpointHelpers.IsOutcomeIssueFromSupersededAttempt(current));
    }

    [Fact]
    public void BuildDiffSummary_EmptyHeadCommitWithPriorNonEmptyCommits_ReportsRealChangeset()
    {
        // Regression for the 2026-05-11 false positive: an empty
        // crash-recovery commit landed on HEAD on top of three real
        // commits with ~7 files / ~400 lines of work. The aspect runner
        // was reading only HEAD (via TaskInfo.Commit) and reporting
        // "Files changed: 0", which led every aspect reviewer to BLOCK
        // with "no work landed". The aggregator-backed summary must
        // surface the union across all run-window commits so the LLM
        // sees the real changeset.
        var t = DateTime.UtcNow;
        var emptyAutoCommit = new TaskCommitInfo
        {
            Sha = "empty-head",
            ShortSha = "emptyh",
            Message = "chore(crash-recovery): collect leftover state",
            FilesChanged = 0,
            At = t.AddMinutes(1)
        };
        var aggregate = new TaskCommitsAggregate
        {
            Count = 3,
            TotalFilesChanged = 7,
            TotalAdded = 380,
            TotalRemoved = 60,
            Commits = new List<TaskCommitRecord>
            {
                new() { Sha = "empty-head", ShortSha = "emptyh", AuthorDateUtc = t.AddMinutes(1), Subject = "chore(crash-recovery): collect leftover state", FilesChanged = 0, Added = 0, Removed = 0 },
                new() { Sha = "real-two",   ShortSha = "rl2",    AuthorDateUtc = t,                Subject = "feat: token aggregation phase 2",            FilesChanged = 3, Added = 180, Removed = 25 },
                new() { Sha = "real-one",   ShortSha = "rl1",    AuthorDateUtc = t.AddMinutes(-5), Subject = "refactor: single source of truth",            FilesChanged = 4, Added = 200, Removed = 35 }
            }
        };

        var summary = ReviewDecisionOrchestrator.BuildDiffSummary(aggregate, emptyAutoCommit);

        // The aggregate-level header must surface the real numbers; an LLM
        // reading the prompt must NOT see "Files changed: 0".
        Assert.Contains("Commits attributed to this task: 3", summary);
        Assert.Contains("Total files changed", summary);
        Assert.Contains("7", summary);
        Assert.Contains("380", summary);
        Assert.Contains("60", summary);

        // Every commit (including the empty recovery one) is enumerated so
        // the reviewer can see context, but the empty one no longer
        // dominates the prompt.
        Assert.Contains("rl2", summary);
        Assert.Contains("rl1", summary);
        Assert.Contains("feat: token aggregation phase 2", summary);
        Assert.Contains("refactor: single source of truth", summary);

        // Critical assertion: the false-positive substring from the
        // pre-fix view must not appear.
        Assert.DoesNotContain("Files changed: 0\r", summary);
        Assert.DoesNotContain("Files changed: 0\n", summary);
    }

    [Fact]
    public void BuildDiffSummary_TrulyNoCommits_StatesItExplicitly()
    {
        // No runs and no auto-commit: the aspect runner must be told
        // unambiguously that nothing landed rather than receiving a
        // misleading "Commit: " line or an empty string.
        var summary = ReviewDecisionOrchestrator.BuildDiffSummary(
            new TaskCommitsAggregate { Count = 0, Commits = [] },
            legacyAutoCommit: null);

        Assert.Contains("No commits attributed to this task", summary);
    }

    [Fact]
    public void BuildBranchDiffSummary_SteerFollowupEmptyWorkingDiff_ShowsBranchCommits()
    {
        // AGT-2022 test 3: a steer follow-up run leaves an empty working diff, but
        // the task branch still carries its commits vs the base branch. The aspect
        // reviewers must see that branch range so they never false-BLOCK the task
        // as "deliverables missing".
        var commits = new List<GitCommitInfo>
        {
            new("sha2222222", "sha2", DateTime.UtcNow, "dev", "feat: wire the endpoint", 3, 40, 5),
            new("sha1111111", "sha1", DateTime.UtcNow.AddMinutes(-10), "dev", "feat: add the service", 2, 120, 0),
        };

        var summary = ReviewDecisionOrchestrator.BuildBranchDiffSummary("develop", "task/AGT-2022", commits);

        Assert.Contains("task/AGT-2022", summary);
        Assert.Contains("develop", summary);
        Assert.Contains("2 commit(s) ahead", summary);
        Assert.Contains("Total files changed: 5", summary);
        Assert.Contains("+160/-5", summary);
        Assert.Contains("feat: wire the endpoint", summary);
        Assert.Contains("feat: add the service", summary);
        Assert.Contains("Do NOT treat an empty working diff as missing work", summary);
    }

    [Fact]
    public void SelectGradeDiff_EmptyWorkingDiff_FallsBackToBranchRange_AndLabelsIt()
    {
        // AGT-2022 fallback selection: post-squash/merge or steer follow-up leaves
        // the run-window working diff empty. The grade must fall back to the
        // task-branch-vs-base range so it never mis-grades a real, landed change as
        // an empty diff, and the label must name the fallback source.
        var branchSummary = "Task branch `task/AGT-2022` vs base `main`: 2 commit(s) ahead.";
        var (diff, label) = ReviewDecisionOrchestrator.SelectGradeDiff(
            workingDiff: "   ", scopeLabel: "abc1234", branchDiffFactory: () => branchSummary);

        Assert.Equal(branchSummary, diff);
        Assert.Equal("abc1234 (branch range vs base)", label);
    }

    [Fact]
    public void SelectGradeDiff_NonEmptyWorkingDiff_WinsAndNeverConsultsBranchRange()
    {
        // The guard that keeps a normal run cheap and correct: a non-empty working
        // diff is graded on exactly what the run changed, and the git-backed branch
        // range is never resolved (the factory must not be called).
        var (diff, label) = ReviewDecisionOrchestrator.SelectGradeDiff(
            workingDiff: "diff --git a/x b/x\n+real change",
            scopeLabel: "HEAD",
            branchDiffFactory: () => throw new InvalidOperationException("branch range must not be resolved for a non-empty working diff"));

        Assert.Contains("+real change", diff);
        Assert.Equal("HEAD", label);
    }

    [Fact]
    public void SelectGradeDiff_EmptyWorkingDiff_NoBranchRange_StaysEmpty()
    {
        // Genuinely empty deliverable: the working diff is blank AND no branch
        // range exists (git unwired / no task branch / empty range). The diff
        // selection stays empty and keeps the original scope label - the results/
        // inventory and card-mode framing carry the read-only case in the prompt,
        // not this fallback.
        var (diff, label) = ReviewDecisionOrchestrator.SelectGradeDiff(
            workingDiff: "", scopeLabel: "HEAD", branchDiffFactory: () => null);

        Assert.Equal("", diff);
        Assert.Equal("HEAD", label);
    }

    [Fact]
    public void ComposeAspectDiffSummary_AppendsBranchRange_WhenPresent()
    {
        // The aspect diff summary always carries the branch range appended to the
        // run-window summary so an empty run-window view still shows the real
        // change set (AGT-2022).
        var composed = ReviewDecisionOrchestrator.ComposeAspectDiffSummary(
            baseSummary: "No commits attributed to this task.",
            branchSummary: "Task branch `task/AGT-2022` vs base `main`: 2 commit(s) ahead.");

        Assert.Contains("No commits attributed to this task.", composed);
        Assert.Contains("Task branch `task/AGT-2022` vs base `main`", composed);
        // The two evidence blocks are kept as separate paragraphs.
        Assert.Contains("task.\n\nTask branch", composed);
    }

    [Fact]
    public void ComposeAspectDiffSummary_NoBranchRange_ReturnsBaseUnchanged()
    {
        // Sequential runs never create a task branch, and an empty range yields
        // no summary: the base run-window summary passes through untouched.
        const string baseSummary = "Commits attributed to this task: 1\nTotal files changed: 3";
        Assert.Equal(baseSummary, ReviewDecisionOrchestrator.ComposeAspectDiffSummary(baseSummary, null));
        Assert.Equal(baseSummary, ReviewDecisionOrchestrator.ComposeAspectDiffSummary(baseSummary, "   "));
    }

    [Fact]
    public void BuildDiffSummary_AggregateEmptyButLegacyCommitPresent_FallsBackToLegacyView()
    {
        // Defensive fallback path: the aggregator could not be wired
        // (test / missing deps). With a legacy auto-commit on hand we
        // still emit the old single-commit view so the prompt is not
        // empty.
        var legacy = new TaskCommitInfo
        {
            Sha = "abc123",
            ShortSha = "abc123",
            Message = "feat: legacy single-commit\n\nbody",
            FilesChanged = 4,
            At = DateTime.UtcNow
        };
        var summary = ReviewDecisionOrchestrator.BuildDiffSummary(
            new TaskCommitsAggregate { Count = 0, Commits = [] }, legacy);

        Assert.Contains("abc123", summary);
        Assert.Contains("feat: legacy single-commit", summary);
        Assert.Contains("Files changed: 4", summary);
    }

    [Fact]
    public void EnrichLineStats_PersistedChainCommitsWithoutLines_BackfillsRealChangeset()
    {
        // ASS-770 regression. The run-window SHA range produced no commits, so
        // the aggregator surfaced the work only from the persisted task.json
        // chain - which stores a file count but hardcodes +0/-0. Pre-fix the
        // aspect reviewer saw "7 files, +0/-0" and false-BLOCKed a real,
        // tested commit (a9af3aa: +771/-28 over 7 files). EnrichLineStats must
        // re-derive the genuine line counts per SHA and rebuild the totals.
        var t = DateTime.UtcNow;
        var aggregate = new TaskCommitsAggregate
        {
            Count = 2,
            TotalFilesChanged = 7,
            TotalAdded = 0,
            TotalRemoved = 0,
            Commits = new List<TaskCommitRecord>
            {
                new() { Sha = "a9af3aa", ShortSha = "a9af3aa", AuthorDateUtc = t,                Subject = "feat(orchestrator): post-core gate", FilesChanged = 5, Added = 0, Removed = 0 },
                new() { Sha = "e4bb834", ShortSha = "e4bb834", AuthorDateUtc = t.AddMinutes(-3), Subject = "feat(models): thinking level",        FilesChanged = 2, Added = 0, Removed = 0 }
            }
        };

        var stats = new Dictionary<string, (int, int, int)>
        {
            ["a9af3aa"] = (5, 600, 20),
            ["e4bb834"] = (2, 171, 8)
        };
        var enriched = ReviewDecisionOrchestrator.EnrichLineStats(aggregate, sha => stats[sha]);

        Assert.Equal(771, enriched.TotalAdded);
        Assert.Equal(28, enriched.TotalRemoved);
        Assert.Equal(7, enriched.TotalFilesChanged);

        var summary = ReviewDecisionOrchestrator.BuildDiffSummary(enriched, legacyAutoCommit: null);
        Assert.Contains("Total lines added: 771", summary);
        Assert.Contains("Total lines removed: 28", summary);
        Assert.Contains("+600, -20", summary);
        // The false-positive cue must be gone: no per-commit +0/-0 line.
        Assert.DoesNotContain("+0, -0", summary);
        Assert.DoesNotContain("could not be computed", summary);
    }

    [Fact]
    public void EnrichLineStats_RangeCommitsAlreadyCarryLines_LeftUntouched()
    {
        // A commit that came from the SHA-range path already has +/- counts.
        // The lookup must not be consulted for it (and a thrown lookup must
        // not corrupt the result), so the aggregate passes through unchanged.
        var aggregate = new TaskCommitsAggregate
        {
            Count = 1,
            TotalFilesChanged = 3,
            TotalAdded = 120,
            TotalRemoved = 15,
            Commits = new List<TaskCommitRecord>
            {
                new() { Sha = "rng1", ShortSha = "rng1", Subject = "feat: real", FilesChanged = 3, Added = 120, Removed = 15 }
            }
        };

        var enriched = ReviewDecisionOrchestrator.EnrichLineStats(
            aggregate, _ => throw new InvalidOperationException("lookup must not be called"));

        Assert.Equal(120, enriched.TotalAdded);
        Assert.Equal(15, enriched.TotalRemoved);
        Assert.Equal(3, enriched.TotalFilesChanged);
    }

    [Fact]
    public void EnrichLineStats_LookupUnavailable_KeepsFilesAndTriggersDefensiveNote()
    {
        // Worst case: commits exist with a file count but git cannot re-derive
        // line stats (lookup returns all-zero - e.g. an unresolvable worktree).
        // We must NOT invent numbers; the file count stays, and BuildDiffSummary
        // appends the defensive note so the reviewer does not read +0/-0 as
        // "corrupted / no work".
        var aggregate = new TaskCommitsAggregate
        {
            Count = 1,
            TotalFilesChanged = 4,
            TotalAdded = 0,
            TotalRemoved = 0,
            Commits = new List<TaskCommitRecord>
            {
                new() { Sha = "deadbee", ShortSha = "deadbee", Subject = "feat: work", FilesChanged = 4, Added = 0, Removed = 0 }
            }
        };

        var enriched = ReviewDecisionOrchestrator.EnrichLineStats(aggregate, _ => (0, 0, 0));
        Assert.Equal(4, enriched.TotalFilesChanged);

        var summary = ReviewDecisionOrchestrator.BuildDiffSummary(enriched, legacyAutoCommit: null);
        Assert.Contains("do NOT treat the zero line totals as missing, empty, or corrupted", summary);
        Assert.Contains("4 files", summary);
    }

    [Fact]
    public void IsPromptUsable_SeparatesRealPromptsFromPlaceholders()
    {
        // Heuristic guard: any future change to the placeholder rules
        // must keep these classifications stable so the NOOP branch logic
        // continues to route correctly.
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("", "Real body with twenty plus characters of detail."));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("Real title", ""));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("TODO: fill in", "# TODO\n\nplaceholder\n"));
        Assert.False(ReviewDecisionOrchestrator.IsPromptUsable("Real title", "# Heading only\n"));
        Assert.True(ReviewDecisionOrchestrator.IsPromptUsable("Add caching layer",
            "# Add caching\n\nWrap GetJobs in an LRU cache to avoid the disk scan on every poll.\n"));
    }

    [Fact]
    public async Task TaskDone_AllAspectsPass_PromotesToHumanReview_WithoutConcernTags_AndWritesAspectMds()
    {
        SeedReviewJobWithDone("clean-job");
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Lane: 4-auto-review -> 5-human-review (accept-as-done).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "clean-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "clean-job")));

        // All four aspect MDs must exist with frontmatter status=pass.
        foreach (var aspect in new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" })
        {
            var path = Path.Combine(_watchPath, TaskStates.HumanReview, "clean-job", $"aspect-{aspect}.md");
            Assert.True(File.Exists(path), $"missing aspect MD: {aspect}");
            Assert.Equal(AspectStatus.Pass, AspectVerdictParsing.ReadStatusFromReport(File.ReadAllText(path)));
        }

        // Job tags must NOT contain any *:concerns chips.
        var tags = ReadJobTags(TaskStates.HumanReview, "clean-job");
        Assert.DoesNotContain(tags, t => t.EndsWith(":concerns", StringComparison.OrdinalIgnoreCase));

        // Provenance: the orchestrator advanced this task toward Completed
        // (accept-as-done), so the orchestrator-moved tag must be stamped.
        Assert.Contains(ReviewDecisionOrchestrator.OrchestratorMovedTagId, tags);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.HumanReview, "clean-job");
        Assert.Contains(outcomes, o => o.Outcome == PostProcessingOutcomes.PassToHumanReview);
        Assert.Contains(outcomes, o =>
            o.StepId == PipelineCatalogue.OrchestratorDecisionStepId &&
            o.Performer == PostProcessingPerformers.Orchestrator &&
            o.PerformerCliType == null);

        // Decision-journal records the accept-as-done with a multi-aspect reason.
        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);
        Assert.Contains("Multi-aspect", record.Reason);
    }

    [Fact]
    public async Task ResearchDone_WithPrimaryHtml_UsesLightweightFlow_AndSkipsAspects()
    {
        const string slug = "research-report";
        SeedReviewJobWithDone(slug, mode: TaskModes.Research);
        var results = Path.Combine(_watchPath, TaskStates.AutoReview, slug, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(
            Path.Combine(results, "report.html"),
            "<!doctype html><html lang=\"en\"><title>Research report</title><body>Ready.</body></html>");
        var aspectCalls = 0;
        var orchestrator = BuildOrchestratorWithAspects(_ =>
        {
            aspectCalls++;
            return "[[ASPECT_VERDICT: status=pass; summary=should not run]]\n[[TASK_DONE]]";
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Assert.True(Directory.Exists(moved));
        Assert.Equal(0, aspectCalls);
        Assert.Empty(Directory.EnumerateFiles(moved, "aspect-*.md"));
        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);
        Assert.Contains("results/report.html", record.Reason);
    }

    [Fact]
    public async Task ResearchDone_WithInvalidPrimaryHtml_ReissuesWithoutRunningAspects()
    {
        const string slug = "research-invalid-report";
        SeedReviewJobWithDone(slug, mode: TaskModes.Research);
        var results = Path.Combine(_watchPath, TaskStates.AutoReview, slug, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "report.html"), "not an HTML document");
        var aspectCalls = 0;
        var orchestrator = BuildOrchestratorWithAspects(_ =>
        {
            aspectCalls++;
            return "[[ASPECT_VERDICT: status=pass; summary=should not run]]\n[[TASK_DONE]]";
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var moved = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(moved));
        Assert.Equal(0, aspectCalls);
        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Contains("valid HTML document", record.Reason);
    }

    [Fact]
    public async Task TaskDone_OneAspectConcerns_PromotesToHumanReview_AndAddsConcernsTag()
    {
        SeedReviewJobWithDone("concerns-job");
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper duplicated.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "concerns-job")));

        var tags = ReadJobTags(TaskStates.HumanReview, "concerns-job");
        Assert.Contains("quality:concerns", tags);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.HumanReview, "concerns-job");
        Assert.Contains(outcomes, o => o.Outcome == PostProcessingOutcomes.FindingsAdded);
        Assert.Contains(outcomes, o => o.Outcome == PostProcessingOutcomes.PassToHumanReview);

        // The aspect MD itself records the concern.
        var qualityMd = File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "concerns-job", "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(qualityMd));
        Assert.Contains("Helper duplicated.", qualityMd);

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, record.Kind);
        Assert.Contains("concerns", record.Reason);
    }

    [Fact]
    public async Task TaskDone_PostProcessingEvidence_AttributesDeterministicDecisionWithoutInventedCli()
    {
        SeedReviewJobWithDone("codex-main-deterministic-post", agent: CliTypes.Codex);
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var taskJson = File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "codex-main-deterministic-post", "task.json"));
        Assert.Contains("\"agent\": \"codex\"", taskJson);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.HumanReview, "codex-main-deterministic-post");
        Assert.Contains(outcomes, o =>
            o.Outcome == PostProcessingOutcomes.PassToHumanReview &&
            o.Performer == PostProcessingPerformers.Orchestrator &&
            o.PerformerCliType == null);
    }

    [Fact]
    public async Task TaskDone_FollowUpAllPass_RemovesStaleConcernTagsFromEarlierPass()
    {
        // Regression ("concern tags bleiben kleben"): an earlier auto-review
        // pass left requirement:concerns + quality:concerns on the card. A
        // later pass that now accepts cleanly must STRIP those stale concern
        // tags - merge-only left them stuck even though nothing is open.
        // Non-aspect tags (here a plain registry tag) must survive.
        SeedReviewJobWithDone("stale-concerns-job",
            initialTags: new[] { "requirement:concerns", "quality:concerns", "area-backend" });
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "stale-concerns-job")));

        var tags = ReadJobTags(TaskStates.HumanReview, "stale-concerns-job");
        Assert.DoesNotContain(tags, t => t.EndsWith(":concerns", StringComparison.OrdinalIgnoreCase));
        // The unrelated registry tag and the orchestrator provenance tag stay.
        Assert.Contains("area-backend", tags);
        Assert.Contains(ReviewDecisionOrchestrator.OrchestratorMovedTagId, tags);
    }

    [Fact]
    public async Task TaskDone_FollowUpReducedConcerns_KeepsCurrentDropsStale()
    {
        // A later pass narrows the open concerns from two namespaces to one:
        // the now-clean requirement:concerns must drop while the still-open
        // quality:concerns is kept (reconcile, not blanket-strip).
        SeedReviewJobWithDone("reduced-concerns-job",
            initialTags: new[] { "requirement:concerns", "quality:concerns" });
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper duplicated.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var tags = ReadJobTags(TaskStates.HumanReview, "reduced-concerns-job");
        Assert.Contains("quality:concerns", tags);
        Assert.DoesNotContain("requirement:concerns", tags);
    }

    [Fact]
    public void Backfill_AcceptedCleanCard_StripsStaleConcernTags()
    {
        // AC2 migration: a card already parked in 5-human-review carries stale
        // concern tags from before the reconcile fix. Its latest decision is a
        // clean accept (no concerns recorded) and no outcome issue -> the boot
        // sweep strips the concern chips but leaves the unrelated registry tag.
        SeedHumanReviewCard("backfill-clean", new[] { "requirement:concerns", "quality:concerns", "area-backend" });
        AppendAcceptDecision("backfill-clean", "Multi-aspect: all aspects pass");
        var orchestrator = BuildOrchestratorWithAspects(_ => string.Empty);

        orchestrator.BackfillStaleConcernTags(_workspace, CancellationToken.None);

        var tags = ReadJobTags(TaskStates.HumanReview, "backfill-clean");
        Assert.DoesNotContain(tags, t => t.EndsWith(":concerns", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("area-backend", tags);
    }

    [Fact]
    public void Backfill_AcceptWithConcerns_KeepsRecordedConcernsDropsStale()
    {
        // accept-with-concerns is legitimate: the decision recorded exactly
        // quality:concerns. The sweep must keep that and only strip the stale
        // requirement:concerns left over from an earlier pass.
        SeedHumanReviewCard("backfill-partial", new[] { "requirement:concerns", "quality:concerns" });
        AppendAcceptDecision("backfill-partial", "Multi-aspect: accept with concerns (quality:concerns)");
        var orchestrator = BuildOrchestratorWithAspects(_ => string.Empty);

        orchestrator.BackfillStaleConcernTags(_workspace, CancellationToken.None);

        var tags = ReadJobTags(TaskStates.HumanReview, "backfill-partial");
        Assert.Contains("quality:concerns", tags);
        Assert.DoesNotContain("requirement:concerns", tags);
    }

    [Fact]
    public void Backfill_AcceptCardWithActiveOutcomeIssue_PreservesConcernTags()
    {
        // Negative gate: an active runner-outcome issue (missing sentinel) means
        // something is still open, so the sweep must NOT touch the concern tags.
        SeedHumanReviewCard("backfill-outcome", new[] { "quality:concerns" });
        File.WriteAllText(
            Path.Combine(_watchPath, TaskStates.HumanReview, "backfill-outcome", "logs", "cli-output.log"),
            $"[12:00:00.000] [orchestrator] missing-terminal-sentinel{Environment.NewLine}");
        AppendAcceptDecision("backfill-outcome", "Multi-aspect: all aspects pass");
        var orchestrator = BuildOrchestratorWithAspects(_ => string.Empty);

        orchestrator.BackfillStaleConcernTags(_workspace, CancellationToken.None);

        var tags = ReadJobTags(TaskStates.HumanReview, "backfill-outcome");
        Assert.Contains("quality:concerns", tags);
    }

    [Fact]
    public void OutcomeBackfill_AcceptedHumanReviewCard_ClearsStaleClassifierUnknownChip()
    {
        // ASS-775: a 5-human-review card the orchestrator accepted still derives a
        // classifier-unknown chip because the accept note never reached the log.
        // The backfill writes a reconcile note so the read path drops the chip.
        SeedHumanReviewCardWithLog("accepted-stale",
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}");
        AppendAcceptDecision("accepted-stale", "all aspects pass");
        var orchestrator = BuildOrchestratorWithAspects(_ => string.Empty);

        Assert.Equal("classifier-unknown", OutcomeKind(TaskStates.HumanReview, "accepted-stale"));

        orchestrator.BackfillStaleAcceptedOutcomeIssues(_workspace, CancellationToken.None);

        var log = ReadCliLog(TaskStates.HumanReview, "accepted-stale");
        Assert.Contains("reconciled on accept", log, StringComparison.OrdinalIgnoreCase);
        Assert.Null(OutcomeKind(TaskStates.HumanReview, "accepted-stale"));
    }

    [Fact]
    public void OutcomeBackfill_IsIdempotent_AppendsReconcileNoteOnce()
    {
        SeedHumanReviewCardWithLog("idempotent-stale",
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}");
        AppendAcceptDecision("idempotent-stale", "all aspects pass");

        // The backfill runs once per boot. Use a fresh orchestrator for each
        // call so the second pass sees the first pass's appended note through a
        // cold scanner cache (TaskIndexCache is keyed on task.json mtime and does
        // not invalidate on a cli-output.log append within one process).
        BuildOrchestratorWithAspects(_ => string.Empty)
            .BackfillStaleAcceptedOutcomeIssues(_workspace, CancellationToken.None);
        BuildOrchestratorWithAspects(_ => string.Empty)
            .BackfillStaleAcceptedOutcomeIssues(_workspace, CancellationToken.None);

        var log = ReadCliLog(TaskStates.HumanReview, "idempotent-stale");
        var occurrences = log.Split("reconciled on accept", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void OutcomeBackfill_EscalatedCard_PreservesChip()
    {
        // A 5-human-review card whose latest verdict is NOT accept (here: a reissue
        // record, i.e. not accepted) must keep its outcome chip - the gate only
        // touches accepted cards.
        SeedHumanReviewCardWithLog("escalated-open",
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}");
        AppendReissueDecision("escalated-open", "needs another pass");
        var orchestrator = BuildOrchestratorWithAspects(_ => string.Empty);

        orchestrator.BackfillStaleAcceptedOutcomeIssues(_workspace, CancellationToken.None);

        var log = ReadCliLog(TaskStates.HumanReview, "escalated-open");
        Assert.DoesNotContain("reconciled on accept", log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("classifier-unknown", OutcomeKind(TaskStates.HumanReview, "escalated-open"));
    }

    [Fact]
    public void OutcomeReconciliationRule_SuppressesContradictingWarnOnlyWhenAccepted()
    {
        var classifierUnknown = new TaskOutcomeIssue { Kind = "classifier-unknown", Severity = "Warn" };
        var blocker = new TaskOutcomeIssue { Kind = "environment-blocker", Severity = "High" };

        Assert.True(TaskOutcomeIssueReconciliation.IsVerdictContradicting(classifierUnknown));
        Assert.False(TaskOutcomeIssueReconciliation.IsVerdictContradicting(blocker));
        Assert.False(TaskOutcomeIssueReconciliation.IsVerdictContradicting(null));

        Assert.True(TaskOutcomeIssueReconciliation.ShouldSuppress(classifierUnknown, verdictAccepted: true));
        Assert.False(TaskOutcomeIssueReconciliation.ShouldSuppress(classifierUnknown, verdictAccepted: false));
        Assert.False(TaskOutcomeIssueReconciliation.ShouldSuppress(blocker, verdictAccepted: true));
    }

    [Fact]
    public async Task TaskDone_OneAspectBlocks_ReissuesToReadyTop_WithFollowUpFile()
    {
        SeedReviewJobWithDone("blocked-job");
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=block; summary=Acceptance criterion 2 missing.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // A blocking aspect parks the job in 2-ready at order 0 (never
        // straight to 3-progress - that's the race the lane-write rule
        // forbids).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "blocked-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "blocked-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "blocked-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "blocked-job")));
        Assert.Equal(0, ReadJobOrder(TaskStates.Ready, "blocked-job"));

        // Follow-up file lists the per-aspect findings.
        var followUp = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Ready, "blocked-job", "orchestrator-follow-up.md"));
        Assert.Contains("requirement-fit", followUp);
        Assert.Contains("Acceptance criterion 2 missing.", followUp);

        // Aspect MDs travel with the folder to 2-ready.
        Assert.True(File.Exists(Path.Combine(_watchPath, TaskStates.Ready, "blocked-job", "aspect-requirement-fit.md")));

        var record = ReadOnlyDecisionRecord();
        Assert.Equal(ReviewDecisionKind.Reissue, record.Kind);
        Assert.Contains("Multi-aspect block", record.Reason);

        var outcomes = ReadPostProcessingOutcomes(TaskStates.Ready, "blocked-job");
        Assert.Contains(outcomes, o =>
            o.Outcome == PostProcessingOutcomes.NeedsFollowUpTask &&
            o.Performer == PostProcessingPerformers.Orchestrator &&
            o.PerformerCliType == null);
    }

    [Fact]
    public async Task TaskDone_AspectVerdictInfraCrash_EscalatesEnvironmental_WithoutBudgetBurn()
    {
        // AGT-2021: the reviewing CLI dies on both the run and the single
        // environmental retry, so no aspect produces a verdict. This is an
        // INFRASTRUCTURE crash, not the card's unfinished work. The card must be
        // escalated flagged environmental (InfraCrash), NEVER reissued or accepted,
        // and the escalation must NOT burn the reissue budget (an Escalate record,
        // never a Reissue).
        SeedReviewJobWithDone("infra-crash-job");
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: _ => string.Empty);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Lane: moved to 5e-escalated, never accepted (5-human-review) or
        // reissued (2-ready).
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, "infra-crash-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "infra-crash-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "infra-crash-job")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "infra-crash-job")));

        // Decision journal: exactly one record, an Escalate (chain-ending, so no
        // budget is charged) - and crucially NOT a Reissue.
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        var record = Assert.Single(records);
        Assert.Equal(ReviewDecisionKind.Escalate, record.Kind);
        Assert.DoesNotContain(records, r => r.Kind == ReviewDecisionKind.Reissue);
        Assert.Contains(ReviewDecisionOrchestrator.AspectInfraCrashReasonPrefix, record.Reason);
        // No reissue in the chain -> the card's reissue budget is untouched.
        Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, "infra-crash-job"));

        // Outcome evidence is a post-processing failure (infra), not a work
        // deficit reissue.
        var outcomes = ReadPostProcessingOutcomes(TaskStates.Escalated, "infra-crash-job");
        Assert.Contains(outcomes, o =>
            o.Outcome == PostProcessingOutcomes.FailedPostProcessing &&
            o.Performer == PostProcessingPerformers.Orchestrator &&
            o.PerformerCliType == null);

        // Timeline carries the environmental + InfraCrash flags.
        var events = ReadTimeline(TaskStates.Escalated, "infra-crash-job");
        var escalate = Assert.Single(
            events.Where(e => e.Kind == TimelineEventKinds.OrchestratorEscalated).ToList());
        Assert.Equal("true", escalate.Details?["environmental"]);
        Assert.Equal("InfraCrash", escalate.Details?["issueKind"]);
    }

    [Fact]
    public async Task TaskDone_AllAspectsPass_EmitsOrchestratorVerdictAcceptedTimelineEvent()
    {
        // ASS-566: the positive terminal of the completion loop must reach
        // the unified per-task ledger (ADR-0049) so the Overview/Timeline
        // surfaces can show "orchestrator accepted" without re-deriving it
        // from the decision journal.
        SeedReviewJobWithDone("accept-timeline");
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // The event lands in the POST-MOVE folder (5-human-review), so it
        // travels with the card.
        var events = ReadTimeline(TaskStates.HumanReview, "accept-timeline");
        var accept = Assert.Single(
            events.Where(e => e.Kind == TimelineEventKinds.OrchestratorVerdictAccepted).ToList());
        Assert.Equal(TimelineActors.Orchestrator, accept.Actor);
        Assert.Equal("accept", accept.Details?["verdict"]);
        // A clean accept never emits a reopen.
        Assert.DoesNotContain(events, e => e.Kind == TimelineEventKinds.QualityLoopReopened);
    }

    [Fact]
    public async Task TaskDone_OneAspectBlocks_EmitsQualityLoopReopenedTimelineEvent_WithGapAndAttempt()
    {
        // ASS-566: a "go again" verdict must be a first-class timeline event
        // carrying the gap (what was missing) and the attempt counter, so the
        // FE can render "Attempt N of M - reopened: <reason>".
        SeedReviewJobWithDone("reopen-timeline");
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=block; summary=Acceptance criterion 2 missing.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // Lands in the POST-MOVE folder (2-ready, where the reissue parks).
        var events = ReadTimeline(TaskStates.Ready, "reopen-timeline");
        var reopen = Assert.Single(
            events.Where(e => e.Kind == TimelineEventKinds.QualityLoopReopened).ToList());
        Assert.Equal(TimelineActors.QualityLoop, reopen.Actor);
        Assert.Equal("multi-aspect-block", reopen.Details?["cause"]);
        // First reopen: the initial run was attempt 1, so the upcoming attempt
        // is 2 of (budget 2 + 1) = 3.
        Assert.Equal("2", reopen.Details?["attempt"]);
        Assert.Equal("3", reopen.Details?["maxAttempts"]);
        // The specific gap is fed forward into the next attempt.
        Assert.Contains("Acceptance criterion 2 missing.", reopen.Details?["gap"]);
        Assert.DoesNotContain(events, e => e.Kind == TimelineEventKinds.OrchestratorVerdictAccepted);
    }

    [Fact]
    public async Task Escalate_EmitsOrchestratorEscalatedTimelineEvent()
    {
        // ASS-566: handing the wheel to a human is the third terminal of the
        // completion loop and must also be visible on the timeline.
        SeedReviewJobWithNeedsInput("escalate-timeline", "use OAuth or magic-link?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs strategic call.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // ADR-0049: the escalate event travels with the original card, which
        // is now in 5e-escalated.
        var events = ReadTimeline(TaskStates.Escalated, "escalate-timeline");
        var escalate = Assert.Single(
            events.Where(e => e.Kind == TimelineEventKinds.OrchestratorEscalated).ToList());
        Assert.Equal(TimelineActors.Orchestrator, escalate.Actor);
        Assert.Equal("needs-input-escalate", escalate.Details?["cause"]);
        Assert.Contains("strategic call", escalate.Details?["reason"]);
    }

    [Fact]
    public async Task TaskDone_WithRunnerActiveClearedMarker_StillRunsAspects()
    {
        SeedReviewJobWithDone("marker-job", includeRunnerActiveClearedMarker: true);
        var calls = 0;
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: _ =>
        {
            calls++;
            return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(4, calls);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "marker-job")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "marker-job")));
    }

    [Fact]
    public async Task TickOnce_RecordsPendingCountForHeaderStatus()
    {
        SeedReviewJobWithDone("status-job", includeRunnerActiveClearedMarker: true);
        var statusSnapshot = new AutoReviewStatusSnapshot();
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]",
            statusSnapshot: statusSnapshot);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        var status = statusSnapshot.Read();
        Assert.Equal(1, status.Pending);
        Assert.Equal(4, status.AspectsRun);
    }

    [Fact]
    public async Task TaskDone_AspectPipelineDisabled_LeavesJobInAutoReview()
    {
        // Kill switch: an empty AspectRunners list disables the multi-aspect
        // pipeline and the orchestrator falls back to the legacy "do nothing
        // for clean DONE" behaviour.
        SeedReviewJobWithDone("pipeline-off");
        var calls = 0;
        var orchestrator = BuildOrchestratorWithAspects(
            aspectStub: _ => { calls++; return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"; },
            aspectRunners: Array.Empty<string>());

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "pipeline-off")));
        Assert.False(File.Exists(ReviewDecisionLog.DecisionsFile(_workspace, Project)));
    }

    [Fact]
    public async Task TaskDone_OnceProcessed_DoesNotReprocessOnNextTick()
    {
        // The orchestrator appends an [orchestrator] line to cli-output.log
        // when it acts on the DONE; FindUnresolvedDone must then return null
        // so a second tick does not re-bill the aspect calls.
        SeedReviewJobWithDone("idempotent-job");
        var calls = 0;
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: _ =>
        {
            calls++;
            return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        var firstCalls = calls;
        Assert.Equal(4, firstCalls);

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.Equal(firstCalls, calls);
    }

    [Fact]
    public async Task OperatorRequeue_WithResolvedOldSentinel_ForcesFreshAspectAssessment()
    {
        const string slug = "operator-fresh-assessment";
        SeedResolvedReviewCardPastGrace(slug);
        // This failure text belongs to the consumed pre-requeue run. Without a
        // log freshness boundary the completion gate reissues immediately and
        // no aspect runs, reproducing the overnight ping-pong shape.
        File.AppendAllText(
            Path.Combine(_watchPath, TaskStates.AutoReview, slug, "logs", "cli-output.log"),
            $"[12:00:40.000] [stdout] Build FAILED before operator recovery.{Environment.NewLine}");
        for (var i = 0; i < 2; i++)
        {
            ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow.AddMinutes(-3 + i),
                JobId: slug,
                Project: Project,
                Kind: ReviewDecisionKind.Reissue,
                Reason: "old spent attempt",
                Prompt: string.Empty,
                Response: string.Empty,
                FollowUp: string.Empty)
            {
                AttemptEpoch = 0,
            });
        }
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow.AddMinutes(-1),
            JobId: slug,
            Project: Project,
            Kind: ReviewDecisionKind.Escalate,
            Reason: "old escalation",
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty)
        {
            AttemptEpoch = 0,
        });

        var requeueConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        new OperatorReviewRequeueService(
            requeueConfig,
            NullLogger<OperatorReviewRequeueService>.Instance,
            _timeline)
            .Apply(
                folder,
                slug,
                Project,
                TaskStates.Escalated,
                TaskStates.AutoReview,
                "Host recovered; run the complete assessment again.",
                TimelineActors.Human("operator@example.com"));

        var calls = 0;
        var orchestrator = BuildOrchestratorWithAspects(aspectStub: _ =>
        {
            calls++;
            return "[[ASPECT_VERDICT: status=pass; summary=fresh pass]]\n[[TASK_DONE]]";
        });

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.Equal(4, calls);
        var reviewedFolder = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Assert.True(Directory.Exists(reviewedFolder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        Assert.All(
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            aspect => Assert.True(File.Exists(Path.Combine(reviewedFolder, $"aspect-{aspect}.md"))));

        var records = ReviewDecisionLog.ReadAll(_workspace, Project)
            .Where(r => r.JobId == slug)
            .ToList();
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, records[^1].Kind);
        Assert.Equal(1, records[^1].AttemptEpoch);
        Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, slug));
    }

    private void SeedHumanReviewCard(string slug, IReadOnlyList<string> tags)
    {
        var dir = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var tagsJson = "[" + string.Join(",", tags.Select(t => $"\"{t}\"")) + "]";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.HumanReview}\",\"order\":1,\"agent\":\"claude\",\"tags\":{tagsJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] done{Environment.NewLine}");
    }

    private void SeedHumanReviewCardWithLog(string slug, string log)
    {
        var dir = Path.Combine(_watchPath, TaskStates.HumanReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.HumanReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), log);
    }

    private string? OutcomeKind(string state, string slug)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.FindJob(slug, _watchPath)?.OutcomeIssue?.Kind;
    }

    private void AppendAcceptDecision(string slug, string reason)
    {
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: slug,
            Project: Project,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: reason,
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty));
    }

    private void SeedReviewJobWithDone(
        string slug,
        bool includeRunnerActiveClearedMarker = false,
        IReadOnlyList<string>? initialTags = null,
        string agent = CliTypes.Claude,
        string? mode = null)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var tagsJson = initialTags is { Count: > 0 }
            ? ",\"tags\":[" + string.Join(",", initialTags.Select(t => $"\"{t}\"")) + "]"
            : string.Empty;
        var modeJson = string.IsNullOrWhiteSpace(mode)
            ? string.Empty
            : $",\"mode\":\"{mode}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"{agent}\",\"cliType\":\"{agent}\"{modeJson}{tagsJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        var suffix = includeRunnerActiveClearedMarker
            ? $"[12:00:02.000] [orchestrator] [decision] Runner active state cleared: job moved out of 3-progress externally (3-progress -> 4-auto-review){Environment.NewLine}"
            : string.Empty;
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}" +
            suffix);
    }

    /// <summary>
    /// Seed a 4-auto-review card whose agent sentinel is already resolved by a
    /// trailing [orchestrator] follow-up line (the verdict-recorded state) and
    /// whose log mtime is aged 30 min past the stale-verdict grace window. This
    /// is the move-after-verdict failure shape: a verdict was journalled but the
    /// lane move never completed, leaving the card stuck.
    /// </summary>
    private void SeedResolvedReviewCardPastGrace(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        var logPath = Path.Combine(dir, "logs", "cli-output.log");
        File.WriteAllText(logPath,
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_DONE]]{Environment.NewLine}" +
            $"[12:00:30.000] [orchestrator] [decision] verdict recorded{Environment.NewLine}");
        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow.AddMinutes(-30));
    }

    private int ReadJobOrder(string state, string slug)
    {
        var jobJsonPath = Path.Combine(_watchPath, state, slug, "task.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jobJsonPath));
        if (json.RootElement.TryGetProperty("order", out var orderEl) &&
            orderEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return orderEl.GetInt32();
        }
        return -1;
    }

    private List<string> ReadJobTags(string state, string slug)
    {
        var jobJsonPath = Path.Combine(_watchPath, state, slug, "task.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jobJsonPath));
        var tags = new List<string>();
        if (json.RootElement.TryGetProperty("tags", out var tagsEl) &&
            tagsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var t in tagsEl.EnumerateArray())
            {
                if (t.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) tags.Add(s!);
                }
            }
        }
        return tags;
    }

    private List<PostProcessingOutcomeRecord> ReadPostProcessingOutcomes(string state, string slug)
    {
        var path = Path.Combine(_watchPath, state, slug, PostProcessingOutcomeLog.FileName);
        Assert.True(File.Exists(path), $"missing {PostProcessingOutcomeLog.FileName} for {slug}");
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<PostProcessingOutcomeRecord>(line, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!)
            .ToList();
    }

    private ReviewDecisionOrchestrator BuildOrchestratorWithAspects(
        Func<string, string> aspectStub,
        IReadOnlyList<string>? aspectRunners = null,
        AutoReviewStatusSnapshot? statusSnapshot = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["ReviewDecisionOrchestrator:Enabled"] = "true",
            ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
            ["ReviewDecisionOrchestrator:AspectsEnabled"] = "true"
        };
        if (aspectRunners != null)
        {
            for (var i = 0; i < aspectRunners.Count; i++)
            {
                dict[$"ReviewDecisionOrchestrator:AspectRunners:{i}"] = aspectRunners[i];
            }
            if (aspectRunners.Count == 0)
            {
                // Force the section to exist as an empty array so the kill
                // switch path is tested. Configuration "exists" when any
                // child key is present; we sentinel-add and remove via a
                // placeholder so the section binds to an empty list.
                dict["ReviewDecisionOrchestrator:AspectRunners:0"] = string.Empty;
            }
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        aspectRunner.CliRunner = (aspectId, _, _, _, _, _) => Task.FromResult(aspectStub(aspectId));
        // No real wall-clock wait on the AGT-2021 environmental retry path.
        aspectRunner.VerdictRetryBackoff = _ => TimeSpan.Zero;
        var taskAccess = BuildTaskAccess(scanner, stateMachine, config);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner, statusSnapshot ?? new AutoReviewStatusSnapshot(), config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            timeline: _timeline);
        return orchestrator;
    }

    private static AgentStudio.TaskAccess.TaskAccessService BuildTaskAccess(
        TaskScannerService scanner,
        TaskStateMachine stateMachine,
        IConfiguration config)
    {
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(scanner, stateMachine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        return new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
    }

    private void SeedProgressJob(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.Progress}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
    }

    private void SeedReviewJobWithoutCoreRun(string slug)
    {
        // A card placed in 4-auto-review with task.json + prompt.md but NO
        // logs/cli-output.log: the decomposition-bug fingerprint (a freshly
        // created sub-task that never ran a core agent run). 0 commits, no run.
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
    }

    private void SeedReviewJobWithBlocked(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_BLOCKED: {reason}]]{Environment.NewLine}");
    }

    private void SeedReviewJobWithoutSentinel(
        string slug,
        string title,
        string promptBody,
        bool includeRunnerActiveClearedMarker = true,
        IReadOnlyList<(string ShortSha, string Message)>? commits = null)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var commitJson = commits == null || commits.Count == 0
            ? string.Empty
            : ",\"commits\":[" + string.Join(",", commits.Select(c =>
                $"{{\"sha\":\"{c.ShortSha}\",\"shortSha\":\"{c.ShortSha}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(c.Message)},\"filesChanged\":1,\"files\":[],\"at\":\"2026-06-01T00:00:00Z\"}}")) + "]";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":{System.Text.Json.JsonSerializer.Serialize(title)},\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"{commitJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        var suffix = includeRunnerActiveClearedMarker
            ? $"[12:00:02.000] [orchestrator] [decision] Runner active state cleared: job moved out of 3-progress externally (3-progress -> 4-auto-review){Environment.NewLine}"
            : string.Empty;
        // No terminal sentinel anywhere - just prose plus the technical
        // "Runner active state cleared" bookkeeping marker that does not
        // resolve anything. This is the heuristic-done / Unknown-completed
        // shape that requirement 4 must not treat as silently completed.
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] I think the work is finished, everything looks good.{Environment.NewLine}" +
            suffix);
    }

    private void AppendReissueDecision(string slug, string reason)
    {
        ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: slug,
            Project: Project,
            Kind: ReviewDecisionKind.Reissue,
            Reason: reason,
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty));
    }

    private void SeedReviewJobWithNoOp(string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":{System.Text.Json.JsonSerializer.Serialize(title)},\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NOOP]]{Environment.NewLine}");
    }

    private void SeedReviewJobWithDoubleNoProgressNoOp(string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":{System.Text.Json.JsonSerializer.Serialize(title)},\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"codex\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] {{\"type\":\"thread.started\",\"thread_id\":\"test-session\"}}{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] {{\"type\":\"turn.started\"}}{Environment.NewLine}" +
            $"[12:00:02.000] [stdout] {{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":\"[[TASK_NOOP]]\"}}}}{Environment.NewLine}" +
            $"[12:00:02.001] [stdout] [[TASK_NOOP]]{Environment.NewLine}" +
            $"[12:00:30.000] [orchestrator] [reissue] Decision: reissue (NOOP recovery). Reason: Agent emitted [[TASK_NOOP]] but the task description is real; reissuing with sharpened framing.{Environment.NewLine}" +
            $"[12:01:00.000] [stdout] {{\"type\":\"turn.started\"}}{Environment.NewLine}" +
            $"[12:01:01.000] [stdout] {{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":\"[[TASK_NOOP]]\"}}}}{Environment.NewLine}" +
            $"[12:01:01.001] [stdout] [[TASK_NOOP]]{Environment.NewLine}");
    }

    private string TaskPathLog(string state, string slug) =>
        Path.Combine(_watchPath, state, slug, "logs", "cli-output.log");

    private string ReadCliLog(string state, string slug) =>
        File.ReadAllText(TaskPathLog(state, slug));

    private List<TimelineEvent> ReadTimeline(string state, string slug) =>
        _timeline.ReadAll(Path.Combine(_watchPath, state, slug));

    private void SeedReviewJobWithNeedsInput(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NEEDS_INPUT: {reason}]]{Environment.NewLine}");
    }

    private ReviewDecisionRecord ReadOnlyDecisionRecord()
    {
        var records = ReviewDecisionLog.ReadAll(_workspace, Project);
        Assert.Single(records);
        return records[0];
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(
        string cliResponse,
        Action? onCall = null,
        OrchestratorChatLog? chatLogOverride = null,
        string? reviewCli = null,
        AttemptAuthorityService? attemptAuthority = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["ReviewDecisionOrchestrator:Enabled"] = "true",
                ["ReviewDecisionOrchestrator:CallsPerHour"] = "100",
                ["ReviewDecisionOrchestrator:Cli"] = reviewCli ?? CliTypes.Codex,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = chatLogOverride ?? new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var statusSnapshot = new AutoReviewStatusSnapshot();
        var taskAccess = BuildTaskAccess(scanner, stateMachine, config);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner, statusSnapshot, config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            timeline: _timeline,
            attemptAuthority: attemptAuthority);
        orchestrator.CliRunner = (cli, model, prompt, timeout, ct) =>
        {
            onCall?.Invoke();
            return Task.FromResult(cliResponse);
        };
        return orchestrator;
    }

    // --- branch-diff fallback: real git, end to end -------------------------

    [Fact]
    public void BranchDiffFallback_SteerFollowupCleanWorkingTree_SurfacesRealBranchCommits()
    {
        // AGT-2022 test scenario 3, end to end against a real repository: a steer
        // follow-up run leaves the working tree clean (`git diff` is empty), yet
        // the task branch still carries its commits vs the base branch. This test
        // closes the gap the code-review named - the git-backed fallback was
        // exercised only through its pure renderer. Here we drive the exact
        // GitService primitives TryBuildBranchDiffSummary depends on
        // (BranchExists / ResolveIntegrationBranch / GetCommitsInRangeAtRoot)
        // against a seeded repo, prove the working diff really is empty, and
        // assert the branch range still surfaces the real, landed change set so
        // the reviewer never false-BLOCKs the task as "deliverables missing".
        var repo = SeedBranchDiffRepo("steer-followup", commitCount: 2);
        var git = BuildGitServiceForRepo(repo);

        // Precondition of the fallback: the current run's working diff is empty.
        var (workingDiff, _, _) = RunGit(repo, "diff HEAD");
        Assert.True(string.IsNullOrWhiteSpace(workingDiff),
            "seed must leave a clean working tree so this exercises the empty-working-diff fallback");

        // The real primitives the orchestrator's fallback resolves lazily.
        const string taskBranch = "task/steer-followup";
        Assert.True(git.BranchExists(repo, taskBranch));
        var baseBranch = git.ResolveIntegrationBranch(repo, "develop");
        Assert.Equal("develop", baseBranch);

        var commits = git.GetCommitsInRangeAtRoot(repo, baseBranch, taskBranch);
        Assert.Equal(2, commits.Count);

        // The renderer fed by real git output produces the evidence block that
        // rides into every aspect / review prompt.
        var summary = ReviewDecisionOrchestrator.BuildBranchDiffSummary(baseBranch, taskBranch, commits);

        Assert.Contains("task/steer-followup", summary);
        Assert.Contains("develop", summary);
        Assert.Contains("2 commit(s) ahead", summary);
        Assert.Contains("feat: wire the endpoint", summary);
        Assert.Contains("feat: add the service", summary);
        // The line counts are real git shortstat numbers, not hand-authored.
        Assert.Contains("lines +", summary);
        Assert.Contains("Do NOT treat an empty working diff as missing work", summary);
    }

    [Fact]
    public void BranchDiffFallback_NoTaskBranch_YieldsNoRange()
    {
        // Sequential runs never create a task branch: the fallback must resolve
        // no range (BranchExists false, empty commit list) so a genuinely empty
        // deliverable is not dressed up with a phantom branch diff.
        var repo = SeedBranchDiffRepo("no-branch", commitCount: 0, createTaskBranch: false);
        var git = BuildGitServiceForRepo(repo);

        Assert.False(git.BranchExists(repo, "task/no-branch"));
        Assert.Empty(git.GetCommitsInRangeAtRoot(repo, "develop", "task/no-branch"));
    }

    /// <summary>
    /// Seed a throwaway repo shaped like a live task worktree: <c>main</c> seed
    /// commit, a <c>develop</c> integration branch, and (optionally) a
    /// <c>task/&lt;name&gt;</c> branch carrying <paramref name="commitCount"/>
    /// committed changes with a clean working tree - the steer-follow-up state
    /// where the run-window working diff is empty but the branch holds the work.
    /// </summary>
    private string SeedBranchDiffRepo(string name, int commitCount, bool createTaskBranch = true)
    {
        var repo = Path.Combine(_workspace, "branchdiff-" + name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        RunGit(repo, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, "checkout -q -b develop");

        if (createTaskBranch)
        {
            RunGit(repo, $"checkout -q -b task/{name}");
            // Newest-first order in the summary means the last commit ("wire the
            // endpoint") is the higher one; keep the two assertion subjects fixed.
            var subjects = new[] { "feat: add the service", "feat: wire the endpoint" };
            for (var i = 0; i < commitCount; i++)
            {
                File.WriteAllText(Path.Combine(repo, $"work{i}.txt"),
                    string.Join("\n", Enumerable.Range(0, (i + 1) * 5).Select(n => $"line {n}")) + "\n");
                RunGit(repo, "add -A");
                var subject = i < subjects.Length ? subjects[i] : $"feat: work {i}";
                RunGit(repo, $"commit -q -m \"{subject}\"");
            }
        }
        return repo;
    }

    private static GitService BuildGitServiceForRepo(string repo)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}

/// <summary>
/// Test double that records every <see cref="OrchestratorChatLog.Append"/>
/// call and the <see cref="TaskInfo.FolderPath"/> at write time. Used to
/// assert the firing-order rule: operator-facing decision notifications
/// must only fire after the lane move has succeeded, never while the job
/// is still in the source lane.
/// </summary>
internal sealed class RecordingChatLog : OrchestratorChatLog
{
    public RecordingChatLog() : base(NullLogger<OrchestratorChatLog>.Instance) { }

    public List<RecordedCall> Calls { get; } = new();

    public override bool Append(TaskInfo info, OrchestratorMessageKind kind, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        Calls.Add(new RecordedCall(kind, info?.FolderPath ?? string.Empty, text));
        return base.Append(info!, kind, text, liveBuffer);
    }

    internal record RecordedCall(OrchestratorMessageKind Kind, string FolderPath, string Text);
}
