using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Business-first contract for the lane/pipeline state machine.
///
/// The test names follow State_Trigger_Expectation. Each allowed transition
/// names the product trigger that owns it and asserts the durable task.json
/// result. This deliberately exercises services instead of HTTP endpoints:
/// transport integration is secondary to the domain rule.
/// </summary>
public sealed class TaskLanePipelineBusinessStateMachineTests : IDisposable
{
    private readonly TaskLanePipelineFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public Task Backlog_OperatorPromotion_TaskMovesToPreparation()
        => MoveAndAssertAsync(TaskStates.Backlog, TaskStates.Preparation);

    [Fact]
    public Task Preparation_OrchestratorPrepAccept_TaskMovesToReady()
        => MoveAndAssertAsync(TaskStates.Preparation, TaskStates.Ready);

    [Fact]
    public Task OrchestratorPrep_BackendMigration_TaskReturnsToPreparation()
        => MoveAndAssertAsync(TaskStates.OrchestratorPrep, TaskStates.Preparation);

    [Fact]
    public Task Ready_RunnerClaim_TaskMovesToProgress()
        => MoveAndAssertAsync(
            TaskStates.Ready,
            TaskStates.Progress,
            sourcePhase: LifecyclePhases.IntakePassed);

    [Fact]
    public Task Progress_EnvironmentRetry_TaskReturnsToReady()
        => MoveAndAssertAsync(
            TaskStates.Progress,
            TaskStates.Ready,
            sourcePhase: LifecyclePhases.ExecutionRunning);

    [Fact]
    public async Task Progress_RunnerCompletion_TaskMovesToAutoReviewWithResult()
    {
        const string id = "runner-completion-result";
        _fixture.SeedTask(
            TaskStates.Progress,
            id,
            LifecyclePhases.ExecutionRunning);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.AutoReview,
            _fixture.WatchPath,
            cause: "runner-completion");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        _fixture.AssertTaskTruth(
            outcome.NewFolderPath!,
            id,
            TaskStates.AutoReview,
            LifecyclePhases.PostProcessingRunning);
        var status = File.ReadAllText(Path.Combine(outcome.NewFolderPath!, "status.md"));
        Assert.Contains("<!-- agent-studio:result-scaffold -->", status);
        Assert.Contains("- Result: Partial", status);
        Assert.Contains("- Grade: Not recorded", status);
        Assert.Contains("- Deliverables: Not recorded", status);
        Assert.Contains($"entering `{TaskStates.AutoReview}`", status);
    }

    [Fact]
    public async Task Progress_AgentBlocked_TaskMovesThroughEscalationFunnel()
    {
        const string id = "agent-blocked";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning);
        var funnel = _fixture.CreateEscalation();

        var outcome = await funnel.EscalateAsync(
            id,
            _fixture.WatchPath,
            TaskLanePipelineFixture.Project,
            HumanReviewEscalationCategories.AgentBlocked,
            "The agent reported a blocking dependency.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        _fixture.AssertTaskTruth(
            outcome.NewFolderPath!,
            id,
            TaskStates.Escalated,
            expectedPhase: null);
        var status = File.ReadAllText(Path.Combine(outcome.NewFolderPath!, "status.md"));
        Assert.Contains(HumanReviewEscalationCategories.AgentBlocked, status);
    }

    [Fact]
    public async Task AutoReview_ApiMove_TaskMovesToHumanReviewWithArtifactReferences()
    {
        const string id = "api-move-result";
        _fixture.SeedTask(
            TaskStates.AutoReview,
            id,
            LifecyclePhases.AwaitingReview,
            tags: [TaskLanePipelineFixture.ContractTag, "code-review:grade-b"]);
        var source = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        File.WriteAllText(
            Path.Combine(source.FolderPath, "code-review-grade-2026-07-30.md"),
            "# Code Review\n\nGrade: B\n");
        Directory.CreateDirectory(TaskPaths.ResultsDir(source.FolderPath));
        File.WriteAllText(
            Path.Combine(TaskPaths.ResultsDir(source.FolderPath), "deliverables.md"),
            "# Deliverables\n");
        var transitions = _fixture.CreateTransitions(
            integrationStatus: _fixture.CreateIntegrationStatus());

        var outcome = await transitions.MoveAsync(
            id,
            TaskStates.HumanReview,
            _fixture.WatchPath,
            cause: "human:api",
            reason: "Operator moved the reviewed task through the API.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var status = File.ReadAllText(Path.Combine(outcome.NewFolderPath!, "status.md"));
        Assert.Contains(
            "- Grade: B ([code-review-grade-2026-07-30.md](code-review-grade-2026-07-30.md))",
            status);
        Assert.Contains(
            "- Deliverables: [results/deliverables.md](results/deliverables.md)",
            status);
        Assert.Contains("- Integration: `pending`", status);
    }

    /// <summary>
    /// AGT-2816. AGT-2736 sat parked for three days with <c>Open Items: None</c>
    /// on a card the runtime had just parked for a human. The scaffold is one of
    /// the two places a summary stub is produced, so it names the park instead
    /// of reporting nothing open.
    /// </summary>
    [Fact]
    public async Task HumanReviewMove_ResultScaffold_NamesTheParkInsteadOfReportingNothingOpen()
    {
        const string id = "parked-open-items";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.HumanReview,
            _fixture.WatchPath,
            cause: "human:api",
            reason: "Parked for an operator decision.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var status = File.ReadAllText(Path.Combine(outcome.NewFolderPath!, "status.md"));
        Assert.Contains(ParkedOpenItems.Heading, status);
        Assert.DoesNotContain("None recorded in this synthesized scaffold", status);
        Assert.Contains("- [ ] This card is parked", status);
    }

    [Fact]
    public void AcceptedCards_StartupBackfill_ReceiveOperatorMarkedResultOnce()
    {
        var states = new[]
        {
            TaskStates.HumanReview,
            TaskStates.Completed,
            TaskStates.Archive,
        };
        foreach (var state in states)
        {
            var id = "backfill-" + state;
            _fixture.SeedTask(
                state,
                id,
                tags: [TaskLanePipelineFixture.ContractTag, "code-review:grade-a"]);
            var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
            File.WriteAllText(
                Path.Combine(task.FolderPath, "code-review-grade.md"),
                "# Code Review\n\nGrade: A\n");
            Directory.CreateDirectory(TaskPaths.ResultsDir(task.FolderPath));
            File.WriteAllText(
                Path.Combine(TaskPaths.ResultsDir(task.FolderPath), "deliverables.md"),
                "# Deliverables\n");
        }
        _fixture.SeedTask(TaskStates.HumanReview, "backfill-preserves-existing");
        var preserved = _fixture.Scanner.FindJob(
            "backfill-preserves-existing",
            _fixture.WatchPath)!;
        File.WriteAllText(Path.Combine(preserved.FolderPath, "status.md"), "# Status\n\n- Result: Success\n");

        var first = _fixture.Transitions.BackfillMissingResultDocuments(
            new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));
        var second = _fixture.Transitions.BackfillMissingResultDocuments(
            new DateTime(2026, 7, 30, 12, 1, 0, DateTimeKind.Utc));

        Assert.Equal(4, first.Scanned);
        Assert.Equal(3, first.Repaired);
        Assert.Empty(first.Failures);
        Assert.Equal(0, second.Repaired);
        foreach (var state in states)
        {
            var task = _fixture.Scanner.FindJob("backfill-" + state, _fixture.WatchPath)!;
            var status = File.ReadAllText(Path.Combine(task.FolderPath, "status.md"));
            Assert.Contains("<!-- agent-studio:operator-result-backfill -->", status);
            Assert.Contains(
                "- Provenance: Operator backfill on 2026-07-30T12:00:00Z",
                status);
            Assert.Contains("- Grade: A ([code-review-grade.md](code-review-grade.md))", status);
            Assert.Contains(
                "- Deliverables: [results/deliverables.md](results/deliverables.md)",
                status);
        }
        Assert.Equal(
            "# Status\n\n- Result: Success\n",
            File.ReadAllText(Path.Combine(preserved.FolderPath, "status.md")));
    }

    [Fact]
    public async Task Progress_ResultCannotBeWritten_TaskStaysProgress()
    {
        const string id = "result-write-refused";
        _fixture.SeedTask(
            TaskStates.Progress,
            id,
            LifecyclePhases.ExecutionRunning);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        Directory.CreateDirectory(Path.Combine(task.FolderPath, "status.md"));

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.AutoReview,
            _fixture.WatchPath,
            cause: "runner-completion");

        Assert.Equal(MoveJobStatus.Failure, outcome.Status);
        Assert.Contains("status.md could not be ensured", outcome.Message);
        var current = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        Assert.NotNull(current);
        _fixture.AssertTaskTruth(
            current!.FolderPath,
            id,
            TaskStates.Progress,
            LifecyclePhases.ExecutionRunning);
    }

    [Fact]
    public Task Progress_StalePickupRecovery_TaskMovesToFailedPickup()
        => MoveAndAssertAsync(
            TaskStates.Progress,
            TaskStates.FailedPickup,
            sourcePhase: LifecyclePhases.ExecutionStalled);

    [Fact]
    public Task Progress_RetryBudgetExhausted_TaskMovesToCodeNotComplete()
        => MoveAndAssertAsync(
            TaskStates.Progress,
            TaskStates.CodeNotComplete,
            sourcePhase: LifecyclePhases.ExecutionStalled);

    [Fact]
    public void FailedPickup_OperatorRestore_TaskReturnsToReady()
    {
        const string failedId = "restore-me-pickup-failed-2026-07-29";
        const string restoredId = "restore-me";
        _fixture.SeedTask(TaskStates.FailedPickup, failedId);

        var outcome = _fixture.States.RestoreFromFailedPickup(
            failedId,
            _fixture.WatchPath,
            keepDeadLetterSlug: false);

        Assert.Equal(RestoreFromFailedPickupStatus.Success, outcome.Status);
        Assert.Equal(restoredId, outcome.RestoredSlug);
        var restored = _fixture.Scanner.FindJob(restoredId, _fixture.WatchPath);
        Assert.NotNull(restored);
        _fixture.AssertTaskTruth(
            restored!.FolderPath,
            restoredId,
            TaskStates.Ready,
            expectedPhase: null);
    }

    [Fact]
    public Task CodeNotComplete_OperatorReissue_TaskReturnsToReady()
        => MoveAndAssertAsync(TaskStates.CodeNotComplete, TaskStates.Ready);

    [Fact]
    public Task AutoReview_OrchestratorReissue_TaskReturnsToReady()
        => MoveAndAssertAsync(
            TaskStates.AutoReview,
            TaskStates.Ready,
            sourcePhase: LifecyclePhases.AwaitingReview);

    [Fact]
    public Task AutoReview_ReviewPassed_TaskMovesToHumanReview()
        => MoveAndAssertAsync(
            TaskStates.AutoReview,
            TaskStates.HumanReview,
            sourcePhase: LifecyclePhases.AwaitingReview);

    [Fact]
    public Task AutoReview_ReviewBlocked_TaskMovesToEscalated()
        => MoveAndAssertAsync(
            TaskStates.AutoReview,
            TaskStates.Escalated,
            sourcePhase: LifecyclePhases.PostProcessingBlocked);

    [Fact]
    public Task HumanReview_NoCodeOperatorAccept_TaskMovesToCompleted()
        => MoveAndAssertAsync(
            TaskStates.HumanReview,
            TaskStates.Completed,
            includeCommit: false,
            noBranchExpected: true);

    [Fact]
    public Task HumanReview_OperatorReissue_TaskReturnsToReady()
        => MoveAndAssertAsync(TaskStates.HumanReview, TaskStates.Ready);

    [Fact]
    public async Task HumanReview_LegacyVerdictBackfill_TaskMovesToEscalated()
    {
        const string id = "legacy-verdictless";
        _fixture.SeedTask(TaskStates.HumanReview, id);
        var backfillFunnel = _fixture.CreateEscalation();

        var outcome = await backfillFunnel.EscalateAsync(
            id,
            _fixture.WatchPath,
            TaskLanePipelineFixture.Project,
            HumanReviewEscalationCategories.UnknownLegacy,
            "Parked in human review without a durable verdict.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        _fixture.AssertTaskTruth(
            outcome.NewFolderPath!,
            id,
            TaskStates.Escalated,
            expectedPhase: null);
    }

    [Fact]
    public Task Escalated_OperatorReissue_TaskReturnsToReady()
        => MoveAndAssertAsync(TaskStates.Escalated, TaskStates.Ready);

    [Fact]
    public Task Escalated_NoCodeOperatorAccept_TaskMovesToCompleted()
        => MoveAndAssertAsync(
            TaskStates.Escalated,
            TaskStates.Completed,
            includeCommit: false);

    [Fact]
    public Task Completed_AuditReopen_TaskReturnsToReady()
        => MoveAndAssertAsync(TaskStates.Completed, TaskStates.Ready);

    [Fact]
    public Task Completed_ArchiveSweep_TaskMovesToArchive()
        => MoveAndAssertAsync(TaskStates.Completed, TaskStates.Archive);

    [Fact]
    public Task Archive_AuditReopen_TaskReturnsToReady()
        => MoveAndAssertAsync(TaskStates.Archive, TaskStates.Ready);

    [Fact]
    public async Task Progress_StaleCompletionAfterRecovery_TaskRejectsOldSourceState()
    {
        const string id = "stale-completion";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning);

        var recovered = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.Ready,
            _fixture.WatchPath,
            expectedSourceState: TaskStates.Progress);
        Assert.Equal(MoveJobStatus.Success, recovered.Status);

        var staleCompletion = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.AutoReview,
            _fixture.WatchPath,
            expectedSourceState: TaskStates.Progress);

        Assert.Equal(MoveJobStatus.SourceStateMismatch, staleCompletion.Status);
        var current = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        Assert.NotNull(current);
        _fixture.AssertTaskTruth(
            current!.FolderPath,
            id,
            TaskStates.Ready,
            expectedPhase: null);
    }

    [Fact]
    public async Task Escalated_UnintegratedCodingAccept_TaskStaysEscalated()
    {
        const string id = "unintegrated-escalation";
        _fixture.SeedTask(TaskStates.Escalated, id);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.Completed,
            _fixture.WatchPath);

        Assert.Equal(MoveJobStatus.Failure, outcome.Status);
        Assert.Contains("only be accepted", outcome.Message);
        var current = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        Assert.NotNull(current);
        _fixture.AssertTaskTruth(
            current!.FolderPath,
            id,
            TaskStates.Escalated,
            expectedPhase: null);
    }

    [Fact]
    public async Task Ready_UnknownLaneRequest_TaskStaysReady()
    {
        const string id = "invalid-target";
        _fixture.SeedTask(TaskStates.Ready, id, LifecyclePhases.HumanReady);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            "9-impossible",
            _fixture.WatchPath);

        Assert.Equal(MoveJobStatus.Failure, outcome.Status);
        var current = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        Assert.NotNull(current);
        _fixture.AssertTaskTruth(
            current!.FolderPath,
            id,
            TaskStates.Ready,
            LifecyclePhases.HumanReady);
    }

    [Fact]
    public void FailedPickup_MalformedRestoreSlug_TaskStaysFailedPickup()
    {
        const string id = "manually-renamed-dead-letter";
        _fixture.SeedTask(TaskStates.FailedPickup, id);

        var outcome = _fixture.States.RestoreFromFailedPickup(
            id,
            _fixture.WatchPath,
            keepDeadLetterSlug: false);

        Assert.Equal(RestoreFromFailedPickupStatus.InvalidSlug, outcome.Status);
        var current = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        Assert.NotNull(current);
        _fixture.AssertTaskTruth(
            current!.FolderPath,
            id,
            TaskStates.FailedPickup,
            expectedPhase: null);
    }

    private async Task MoveAndAssertAsync(
        string source,
        string target,
        string? sourcePhase = null,
        string? expectedPhase = null,
        bool includeCommit = true,
        bool noBranchExpected = false)
    {
        const string id = "business-transition";
        _fixture.SeedTask(
            source,
            id,
            sourcePhase,
            includeCommit: includeCommit,
            noBranchExpected: noBranchExpected);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            target,
            _fixture.WatchPath,
            cause: "business-contract-test",
            reason: $"{source} -> {target}");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.NewFolderPath));
        _fixture.AssertTaskTruth(
            outcome.NewFolderPath!,
            id,
            target,
            expectedPhase,
            expectedCommitCount: includeCommit ? 1 : 0);
    }
}

/// <summary>
/// Edge cases where host-local runtime state must never become task authority.
/// The task record keeps the lane, phase, tags, integration branch and commit
/// chain; leases merely fence which runner may advance it.
/// </summary>
public sealed class TaskLanePipelineEdgeCaseTests : IDisposable
{
    private readonly TaskLanePipelineFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Ready_DoubleClaim_OnlyFirstRunnerAdvancesTask()
    {
        const string id = "double-claim";
        const string key = "AGT-DOUBLE-CLAIM";
        _fixture.SeedTask(TaskStates.Ready, id, key: key);
        var now = new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);
        var authority = _fixture.CreateAttemptAuthority(() => now);

        var first = authority.AcquireRun(
            key, "PROJ-002", null, "runner-a", "host-a", 60, "claim-a");
        Assert.Equal(AttemptWriteStatus.Accepted, first.Status);
        var moved = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.Progress,
            _fixture.WatchPath,
            expectedSourceState: TaskStates.Ready);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        var second = authority.AcquireRun(
            key, "PROJ-002", null, "runner-b", "host-b", 60, "claim-b");

        Assert.Equal(AttemptWriteStatus.InvalidState, second.Status);
        _fixture.AssertTaskTruth(
            moved.NewFolderPath!,
            id,
            TaskStates.Progress,
            expectedPhase: null);
    }

    [Theory]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public async Task AutoReview_TerminalTransition_OpenReviewAttemptIsSuperseded(
        string terminalState)
    {
        const string id = "terminal-review-cleanup";
        const string key = "AGT-TERMINAL-REVIEW";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview, key: key);
        var now = new DateTime(2026, 7, 30, 1, 0, 0, DateTimeKind.Utc);
        var authority = _fixture.CreateAttemptAuthority(() => now);
        var run = authority.AcquireRun(
            key, "PROJ-002", null, "runner-a", "host-a", 60, "run-claim").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "run-complete"),
            Outcome = "done",
            ResultSha = "sha-terminal",
        });
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key,
            "PROJ-002",
            "sha-terminal",
            run.AttemptId,
            "requirements",
            "policy",
            [],
            "review-create")).ReviewAttempt!;
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);
        var transitions = _fixture.CreateTransitions(reviewAttemptLifecycle: lifecycle);

        var moved = await transitions.MoveAsync(
            id,
            terminalState,
            _fixture.WatchPath,
            cause: "business-contract-test");

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var superseded = authority.GetReview(review.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Superseded, superseded.State);
        Assert.Equal(ReviewTerminalOutcome.Superseded, superseded.Outcome);
        Assert.Contains(terminalState, superseded.TerminalReason);
        Assert.Equal(
            AttemptWriteStatus.Superseded,
            authority.ClaimReview(
                review.AttemptId,
                "reviewer",
                "review-host",
                60,
                "claim-after-terminal").Status);
        var cleanupEvent = Assert.Single(
            _fixture.Timeline.ReadAll(moved.NewFolderPath!),
            item =>
                item.Kind == TimelineEventKinds.ReviewAttemptSuperseded
                && item.RunId == review.AttemptId);
        Assert.Equal("Superseded", cleanupEvent.Details!["authority"]);
        Assert.Equal(terminalState, cleanupEvent.Details["lane"]);
        Assert.Equal("lane-transition", cleanupEvent.Details["source"]);
    }

    [Fact]
    public void CompletedTask_ClaimGuard_SupersedesOpenReviewAttempt()
    {
        const string id = "terminal-review-claim-guard";
        const string key = "AGT-TERMINAL-CLAIM-GUARD";
        _fixture.SeedTask(TaskStates.Completed, id, key: key);
        var now = new DateTime(2026, 7, 30, 2, 0, 0, DateTimeKind.Utc);
        var authority = _fixture.CreateAttemptAuthority(() => now);
        var review = CreateOpenReviewAttempt(authority, key, "claim-guard");
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        var claim = lifecycle.ClaimNextReview(
            "reviewer",
            "review-host",
            "review-instance",
            60);

        Assert.Equal(AttemptWriteStatus.NotFound, claim.Status);
        var superseded = authority.GetReview(review.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Superseded, superseded.State);
        Assert.Equal(ReviewTerminalOutcome.Superseded, superseded.Outcome);
        Assert.Contains(TaskStates.Completed, superseded.TerminalReason);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        var cleanupEvent = Assert.Single(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item =>
                item.Kind == TimelineEventKinds.ReviewAttemptSuperseded
                && item.RunId == review.AttemptId);
        Assert.Equal("claim-guard", cleanupEvent.Details!["source"]);
    }

    [Fact]
    public async Task Progress_CompletionReviewMint_ClaimCannotSupersedeBeforeAutoReviewLaneLands()
    {
        const string id = "completion-review-mint-race";
        const string key = "AGT-COMPLETION-REVIEW-MINT-RACE";
        const string baseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string resultSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => DateTime.UtcNow);
        var run = authority.AcquireRun(key, "PROJ-002", null, "runner", "host", 60, "claim").RunAttempt!;
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "PROJ-002",
            run.AttemptId,
            baseSha,
            resultSha,
            "refs/agent-studio/results/completion-review-mint-race",
            null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);
        var request = new CreateReviewAttemptRequest(
            key, "PROJ-002", resultSha, run.AttemptId,
            "requirements", "policy", [], "review-create");

        // This is the old interleaving: a claim poll after the mint but before
        // the lane write. The lifecycle boundary refuses to mint in Progress.
        Assert.Equal(AttemptWriteStatus.InvalidState,
            lifecycle.CreateReviewAttemptInAutoReview(
                _fixture.Scanner.FindJob(id, _fixture.WatchPath)!, request).Status);
        Assert.Equal(AttemptWriteStatus.NotFound,
            lifecycle.ClaimNextReview("reviewer", "review-host", "review-instance", 60).Status);

        var transitions = _fixture.CreateTransitions(reviewAttemptLifecycle: lifecycle);
        Assert.Equal(MoveJobStatus.Success,
            (await transitions.MoveAsync(id, TaskStates.AutoReview, _fixture.WatchPath)).Status);
        var created = lifecycle.CreateReviewAttemptInAutoReview(
            _fixture.Scanner.FindJob(id, _fixture.WatchPath)!, request);

        Assert.True(created.Accepted);
        Assert.Equal(AttemptWriteStatus.Accepted,
            lifecycle.ClaimNextReview("reviewer", "review-host", "review-instance", 60).Status);
        Assert.DoesNotContain(_fixture.Timeline.ReadAll(
                _fixture.Scanner.FindJob(id, _fixture.WatchPath)!.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptSuperseded
                    && item.Details is not null
                    && item.Details.TryGetValue("source", out var source)
                    && source == "claim-guard");
    }

    [Fact]
    public void AutoReview_SupersededCurrentReviewWithCompletedEnvelope_RecoveryReissuesAttempt()
    {
        const string id = "superseded-review-recovery";
        const string key = "AGT-SUPERSEDED-REVIEW-RECOVERY";
        const string resultSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => DateTime.UtcNow);
        var run = authority.AcquireRun(key, "PROJ-002", null, "runner", "host", 60, "claim").RunAttempt!;
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "PROJ-002", run.AttemptId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resultSha,
            "refs/agent-studio/results/recovery", null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "complete"),
            Outcome = "done", ResultSha = resultSha, ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        var original = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key, "PROJ-002", resultSha, run.AttemptId, "requirements", "policy", [], "original")).ReviewAttempt!;
        authority.SupersedeOpenReviewAttempts(taskKey => taskKey == key ? "old claim guard race" : null);
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        Assert.Equal(1, lifecycle.SweepSupersededAutoReviewAttempts());
        var replacement = authority.GetTaskProjection(key).CurrentReviewAttempt!;
        Assert.NotEqual(original.AttemptId, replacement.AttemptId);
        Assert.Equal(AttemptLifecycleState.Pending, replacement.State);
        Assert.Equal(run.AttemptId, replacement.SourceRunAttemptId);
        Assert.Equal(0, lifecycle.SweepSupersededAutoReviewAttempts());
    }

    [Fact]
    public void ArchivedTask_BootSweep_SupersedesPersistedOpenReviewAttempt()
    {
        const string id = "terminal-review-boot-sweep";
        const string key = "AGT-TERMINAL-BOOT-SWEEP";
        _fixture.SeedTask(TaskStates.Archive, id, key: key);
        var now = new DateTime(2026, 7, 30, 3, 0, 0, DateTimeKind.Utc);
        var firstProcess = _fixture.CreateAttemptAuthority(() => now);
        var review = CreateOpenReviewAttempt(firstProcess, key, "boot-sweep");
        var restartedAuthority = _fixture.CreateAttemptAuthority(() => now.AddMinutes(1));
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(restartedAuthority);

        var repaired = lifecycle.SweepUnclaimableAttempts();

        Assert.Equal(1, repaired);
        var superseded = restartedAuthority.GetReview(review.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Superseded, superseded.State);
        Assert.Equal(ReviewTerminalOutcome.Superseded, superseded.Outcome);
        Assert.Contains(TaskStates.Archive, superseded.TerminalReason);
        Assert.Equal(0, lifecycle.SweepUnclaimableAttempts());
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        var cleanupEvent = Assert.Single(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item =>
                item.Kind == TimelineEventKinds.ReviewAttemptSuperseded
                && item.RunId == review.AttemptId);
        Assert.Equal("boot-sweep", cleanupEvent.Details!["source"]);
    }

    private static ReviewAttemptDto CreateOpenReviewAttempt(
        AttemptAuthorityService authority,
        string taskKey,
        string suffix)
    {
        var run = authority.AcquireRun(
            taskKey,
            "PROJ-002",
            null,
            "runner-a",
            "host-a",
            60,
            $"run-claim-{suffix}").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                $"run-complete-{suffix}"),
            Outcome = "done",
            ResultSha = $"sha-{suffix}",
        });
        return authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey,
            "PROJ-002",
            $"sha-{suffix}",
            run.AttemptId,
            "requirements",
            "policy",
            [],
            $"review-create-{suffix}")).ReviewAttempt!;
    }

    [Fact]
    public void Progress_LeaseTakeoverByOtherHost_TaskKeepsCommitChain()
    {
        const string id = "runner-takeover";
        const string key = "AGT-TAKEOVER";
        _fixture.SeedTask(
            TaskStates.Progress,
            id,
            LifecyclePhases.ExecutionRunning,
            commits:
            [
                TaskLanePipelineFixture.Commit('a', "runner-a: first work", 1),
                TaskLanePipelineFixture.Commit('b', "runner-b: continued work", 2),
            ],
            key: key);
        var now = new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);
        var firstProcess = _fixture.CreateAttemptAuthority(() => now);
        var first = firstProcess.AcquireRun(
            key, "PROJ-002", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;

        now = now.AddSeconds(31);
        var restartedBackend = _fixture.CreateAttemptAuthority(() => now);
        var takeover = restartedBackend.AcquireRun(
            key,
            "PROJ-002",
            first.AttemptId,
            "runner-b",
            "host-b",
            30,
            "claim-b").RunAttempt!;
        var stale = restartedBackend.AcceptRunWrite(
            new AttemptWriteReference(
                first.AttemptId,
                first.LastFence,
                first.AuthorityEpoch,
                "late-write-a"));

        Assert.True(takeover.LastFence > first.LastFence);
        Assert.Equal("runner-b", takeover.Lease!.ExecutorId);
        Assert.Equal("host-b", takeover.Lease.HostId);
        Assert.Equal(AttemptWriteStatus.Superseded, stale.Status);

        var afterRestart = _fixture.RestartScanner().FindJob(id, _fixture.WatchPath);
        Assert.NotNull(afterRestart);
        Assert.Equal(TaskStates.Progress, afterRestart!.State);
        Assert.Equal(2, afterRestart.Commits.Count);
        Assert.Equal(new string('a', 40), afterRestart.Commits[0].Sha);
        Assert.Equal(new string('b', 40), afterRestart.Commits[1].Sha);
        _fixture.AssertTaskTruth(
            afterRestart.FolderPath,
            id,
            TaskStates.Progress,
            LifecyclePhases.ExecutionRunning,
            expectedCommitCount: 2);
    }

    [Theory]
    [InlineData(TaskStates.Ready, LifecyclePhases.HumanReady)]
    [InlineData(TaskStates.Ready, LifecyclePhases.IntakeRunning)]
    [InlineData(TaskStates.Ready, LifecyclePhases.IntakeBlocked)]
    [InlineData(TaskStates.Ready, LifecyclePhases.IntakePassed)]
    [InlineData(TaskStates.Progress, LifecyclePhases.ExecutionRunning)]
    [InlineData(TaskStates.Progress, LifecyclePhases.ExecutionStalled)]
    [InlineData(TaskStates.Progress, LifecyclePhases.LoopWaiting)]
    [InlineData(TaskStates.Progress, LifecyclePhases.SteerPending)]
    [InlineData(TaskStates.Progress, LifecyclePhases.QuotaWaiting)]
    [InlineData(TaskStates.Progress, LifecyclePhases.PostProcessingRunning)]
    [InlineData(TaskStates.Progress, LifecyclePhases.PostProcessingBlocked)]
    [InlineData(TaskStates.Progress, LifecyclePhases.AwaitingReview)]
    [InlineData(TaskStates.AutoReview, LifecyclePhases.PostProcessingRunning)]
    [InlineData(TaskStates.AutoReview, LifecyclePhases.PostProcessingBlocked)]
    [InlineData(TaskStates.AutoReview, LifecyclePhases.AwaitingReview)]
    public void Phase_BackendRestart_TaskJsonRestoresEveryDurablePhase(
        string state,
        string phase)
    {
        const string id = "phase-restart";
        _fixture.SeedTask(state, id, phase);

        var restarted = _fixture.RestartScanner();
        var recovered = restarted.FindJob(id, _fixture.WatchPath);

        Assert.NotNull(recovered);
        Assert.Equal(state, recovered!.State);
        Assert.Equal(phase, recovered.Phase);
        _fixture.AssertTaskTruth(
            recovered.FolderPath,
            id,
            state,
            phase);
    }

    [Fact]
    public async Task AutoReview_BackendRestart_UnfinishedPostProcessingIsRequeued()
    {
        const string id = "restart-post-processing";
        _fixture.SeedTask(
            TaskStates.Progress,
            id,
            LifecyclePhases.ExecutionRunning);
        var queue = new AutoReviewPostProcessingQueue();
        var transitions = _fixture.CreateTransitions(autoReviewQueue: queue);
        var moved = await transitions.MoveAsync(
            id,
            TaskStates.AutoReview,
            _fixture.WatchPath);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.True(queue.Reader.TryRead(out _));

        var restartedScanner = _fixture.RestartScanner();
        var restartedQueue = new AutoReviewPostProcessingQueue();
        var restartedTransitions = _fixture.CreateTransitions(
            scanner: restartedScanner,
            autoReviewQueue: restartedQueue);
        var recovery = AutoReviewPostProcessingRecoveryService.RunRecoveryScan(
            restartedScanner,
            restartedTransitions,
            NullLogger.Instance);

        Assert.Equal(1, recovery.ReEnqueued);
        Assert.True(restartedQueue.Reader.TryRead(out var request));
        Assert.Equal(id, request.JobId);
        Assert.Equal("startup-recovery", request.Source);
        _fixture.AssertTaskTruth(
            moved.NewFolderPath!,
            id,
            TaskStates.AutoReview,
            LifecyclePhases.PostProcessingRunning);
    }

    [Fact]
    public async Task HumanReview_OutOfBandMergeThenAccept_TaskRecognizesIntegratedWork()
    {
        // AGT-2424 Part C: a merge performed outside the accept request is
        // still Git truth. Accept must derive "integrated" and must not leave a
        // stale integrationpending marker.
        var integratedSha = _fixture.InitializeRepositoryWithIntegratedCommit();
        const string id = "out-of-band-integrated";
        _fixture.SeedTask(
            TaskStates.HumanReview,
            id,
            commits: [TaskLanePipelineFixture.Commit(integratedSha, "merged out of band", 1)],
            tags: [TaskLanePipelineFixture.ContractTag, IntegrationStatuses.PendingTag],
            integrationBranch: "refs/heads/develop");
        var transitions = _fixture.CreateTransitions(
            integrationStatus: _fixture.CreateIntegrationStatus());

        var outcome = await transitions.MoveAsync(
            id,
            TaskStates.Completed,
            _fixture.WatchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        _fixture.AssertTaskTruth(
            outcome.NewFolderPath!,
            id,
            TaskStates.Completed,
            expectedPhase: null,
            expectedTags: [TaskLanePipelineFixture.ContractTag],
            expectedIntegrationBranch: "refs/heads/develop");
        var completed = _fixture.Scanner.FindJob(id, _fixture.WatchPath);
        var status = _fixture.CreateIntegrationStatus().BuildLookup([completed!])[completed!.TaskKey];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
    }

    [Fact]
    public void HumanReview_TwoRunnerCommits_TaskJsonKeepsOneOrderedCommitChain()
    {
        const string id = "two-runner-commits";
        _fixture.SeedTask(TaskStates.HumanReview, id, includeCommit: false);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;

        Assert.True(_fixture.Mutations.SetJobCommitOnFolder(
            task.FolderPath,
            TaskLanePipelineFixture.Commit('a', "runner-a: implementation", 1)));
        Assert.True(_fixture.Mutations.SetJobCommitOnFolder(
            task.FolderPath,
            TaskLanePipelineFixture.Commit('b', "runner-b: takeover continuation", 2)));

        var restarted = _fixture.RestartScanner().FindJob(id, _fixture.WatchPath);
        Assert.NotNull(restarted);
        Assert.Equal(2, restarted!.Commits.Count);
        Assert.Equal(new string('a', 40), restarted.Commits[0].Sha);
        Assert.Equal(new string('b', 40), restarted.Commits[1].Sha);
        Assert.Equal(restarted.Commits[^1].Sha, restarted.Commit!.Sha);
        _fixture.AssertTaskTruth(
            restarted.FolderPath,
            id,
            TaskStates.HumanReview,
            expectedPhase: null,
            expectedCommitCount: 2);
    }
}

/// <summary>
/// Reviewable SOLL skip list for the Status Workbench.
///
/// Each skipped test is intentionally phrased as the desired business rule,
/// not as today's permissive implementation. Removing a Skip therefore means
/// implementing the rule and making its assertions real. Current decisions:
///
/// 1. reject Accept until integration has a decided successful/no-code result;
/// 2. reject direct lane jumps that bypass claim, review or acceptance;
/// 3. fence queued integration against a later reissue;
/// 4. persist runner route, escalation reason and integration outcome on the
///    task instead of requiring logs or volatile projections.
/// </summary>
public sealed class TaskLanePipelineSollSkipTests
{
    [Fact(Skip = "SOLL: Accept without a decided integration result must not move the task to 6-completed.")]
    public void HumanReview_AcceptWithoutIntegrationResult_TaskStaysHumanReview()
    {
    }

    [Fact(Skip = "SOLL: When Accept races the merge, task.json must stay in Human Review until the merge result is durably successful.")]
    public void HumanReview_AcceptRaceWithMerge_TaskStaysHumanReviewUntilMergeWins()
    {
    }

    [Fact(Skip = "SOLL: After a backend restart, pending integration must recover from task.json without prematurely marking the task Completed.")]
    public void HumanReview_BackendRestart_PendingIntegrationRecoversWithoutDelivery()
    {
    }

    [Fact(Skip = "SOLL: Backlog must not bypass preparation, readiness, and review through direct acceptance.")]
    public void Backlog_DirectAccept_TaskIsRejected()
    {
    }

    [Fact(Skip = "SOLL: Ready may move to Progress only through a successful fenced claim.")]
    public void Ready_DirectReviewWithoutClaim_TaskIsRejected()
    {
    }

    [Fact(Skip = "SOLL: Progress must not bypass human review and integration through direct completion.")]
    public void Progress_DirectComplete_TaskIsRejected()
    {
    }

    [Fact(Skip = "SOLL: Auto Review must not move directly to Completed; Human Review is the acceptance gate.")]
    public void AutoReview_DirectComplete_TaskIsRejected()
    {
    }

    [Fact(Skip = "SOLL: Reissue during active integration must supersede the old merge request and fence its later execution.")]
    public void Completed_ReissueWhileIntegrationRuns_QueuedMergeIsRejected()
    {
    }

    [Fact(Skip = "SOLL: task.json must carry the ordered runner and host route with attempt and fence; logs alone are not task truth.")]
    public void Progress_LeaseTakeover_TaskJsonContainsRunnerRoute()
    {
    }

    [Fact(Skip = "SOLL: task.json must carry the escalation category and reason; status.md and the decision log alone are insufficient.")]
    public void Progress_SystemEscalation_TaskJsonContainsCategoryAndReason()
    {
    }

    [Fact(Skip = "SOLL: task.json must carry the decided integration result with branch and SHA; a tag plus a live Git projection is insufficient.")]
    public void Completed_MergeFinished_TaskJsonContainsIntegrationOutcome()
    {
    }

    [Fact(Skip = "SOLL: every task.json.commits[] entry must identify its producing run attempt and runner, not only through commit text.")]
    public void HumanReview_TwoRunnerCommits_TaskJsonContainsPerCommitRunnerAttribution()
    {
    }
}

internal sealed class TaskLanePipelineFixture : IDisposable
{
    public const string Project = "business-state-machine";
    public const string ContractTag = "quality";

    private static readonly JsonSerializerOptions TaskJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public TaskLanePipelineFixture()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "task-lane-business-" + Guid.NewGuid().ToString("N"));
        WatchPath = Path.Combine(Root, "projects", Project);
        RepositoryPath = Path.Combine(Root, "repository");
        Directory.CreateDirectory(WatchPath);
        Directory.CreateDirectory(RepositoryPath);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(WatchPath, state));
        }

        Configuration = BuildConfiguration();
        Scanner = BuildScanner();
        Timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        States = new TaskStateMachine(
            Scanner,
            NullLogger<TaskStateMachine>.Instance,
            timeline: Timeline);
        Mutations = BuildMutations(Scanner);
        Settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            Configuration);
        Settings.SetAutoCommit(Project, false);
        Settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        Git = new GitService(
            NullLogger<GitService>.Instance,
            Scanner,
            Configuration);
        Transitions = CreateTransitions();
    }

    public string Root { get; }
    public string WatchPath { get; }
    public string RepositoryPath { get; }
    public IConfiguration Configuration { get; }
    public TaskScannerService Scanner { get; }
    public TimelineLog Timeline { get; }
    public TaskStateMachine States { get; }
    public TaskMutationService Mutations { get; }
    public ProjectSettingsService Settings { get; }
    public GitService Git { get; }
    public TaskTransitionService Transitions { get; }

    public TaskTransitionService CreateTransitions(
        TaskScannerService? scanner = null,
        IAutoReviewPostProcessingQueue? autoReviewQueue = null,
        TaskIntegrationStatusService? integrationStatus = null,
        ReviewAttemptTaskLifecycleService? reviewAttemptLifecycle = null)
    {
        var selectedScanner = scanner ?? Scanner;
        var selectedStates = ReferenceEquals(selectedScanner, Scanner)
            ? States
            : new TaskStateMachine(
                selectedScanner,
                NullLogger<TaskStateMachine>.Instance,
                timeline: Timeline);
        var selectedMutations = ReferenceEquals(selectedScanner, Scanner)
            ? Mutations
            : BuildMutations(selectedScanner);
        var selectedGit = ReferenceEquals(selectedScanner, Scanner)
            ? Git
            : new GitService(
                NullLogger<GitService>.Instance,
                selectedScanner,
                Configuration);

        return new TaskTransitionService(
            selectedScanner,
            selectedStates,
            selectedMutations,
            selectedGit,
            Settings,
            NullLogger<TaskTransitionService>.Instance,
            autoReviewQueue: autoReviewQueue,
            integrationStatus: integrationStatus,
            timeline: Timeline,
            reviewAttemptLifecycle: reviewAttemptLifecycle);
    }

    public ReviewAttemptTaskLifecycleService CreateReviewAttemptLifecycle(
        AttemptAuthorityService authority)
        => new(
            authority,
            Scanner,
            Timeline,
            NullLogger<ReviewAttemptTaskLifecycleService>.Instance);

    public HumanReviewEscalation CreateEscalation()
        => new(
            States,
            Transitions,
            Root,
            NullLogger<HumanReviewEscalation>.Instance,
            Scanner);

    public TaskIntegrationStatusService CreateIntegrationStatus()
        => new(
            Git,
            Settings,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance);

    public AttemptAuthorityService CreateAttemptAuthority(Func<DateTime> utcNow)
        => new(
            Configuration,
            NullLogger<AttemptAuthorityService>.Instance,
            utcNow);

    public TaskScannerService RestartScanner() => BuildScanner();

    public void SeedTask(
        string state,
        string id,
        string? phase = null,
        bool includeCommit = true,
        IReadOnlyList<TaskCommitInfo>? commits = null,
        IReadOnlyList<string>? tags = null,
        string? key = null,
        string? integrationBranch = null,
        bool noBranchExpected = false)
    {
        var folder = Path.Combine(WatchPath, state, id);
        Directory.CreateDirectory(folder);
        var chain = commits?.ToList()
            ?? (includeCommit ? [Commit('c', "contract commit", 1)] : []);
        var task = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["key"] = key,
            ["title"] = "Business transition contract",
            ["state"] = state,
            ["order"] = 7,
            ["agent"] = "codex",
            ["cliType"] = "codex",
            ["ownerClientId"] = DefaultClientIdentity.Id,
            ["createdAt"] = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc),
            ["enteredLaneAt"] = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc),
            ["tags"] = tags?.ToList() ?? [ContractTag],
            ["integrationBranch"] = integrationBranch,
            ["noBranchExpected"] = noBranchExpected,
            ["commits"] = chain,
            ["commit"] = chain.LastOrDefault(),
        };
        if (phase is not null)
        {
            task["phase"] = phase;
            task["phaseEnteredAt"] =
                new DateTime(2026, 7, 29, 8, 1, 0, DateTimeKind.Utc);
        }
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(task, TaskJsonOptions));
        File.WriteAllText(
            Path.Combine(folder, "prompt.md"),
            "# Business transition contract\n");
        Scanner.InvalidateCache();
    }

    public void AssertTaskTruth(
        string folder,
        string expectedId,
        string expectedState,
        string? expectedPhase,
        int expectedCommitCount = 1,
        IReadOnlyList<string>? expectedTags = null,
        string? expectedIntegrationBranch = null)
    {
        var path = Path.Combine(folder, "task.json");
        Assert.True(File.Exists(path), $"task.json missing at {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var task = document.RootElement;

        Assert.Equal(expectedId, task.GetProperty("id").GetString());
        Assert.Equal(expectedState, task.GetProperty("state").GetString());
        Assert.Equal("Business transition contract", task.GetProperty("title").GetString());
        Assert.Equal("codex", task.GetProperty("agent").GetString());
        Assert.Equal("codex", task.GetProperty("cliType").GetString());
        Assert.Equal(DefaultClientIdentity.Id, task.GetProperty("ownerClientId").GetString());
        Assert.Equal(7, task.GetProperty("order").GetInt32());
        Assert.True(
            task.TryGetProperty("enteredLaneAt", out var enteredLane)
            && enteredLane.ValueKind == JsonValueKind.String
            && enteredLane.TryGetDateTime(out _),
            "task.json must carry the durable lane-entry timestamp");

        var actualPhase = task.TryGetProperty("phase", out var phase)
            && phase.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(phase.GetString())
                ? phase.GetString()
                : null;
        Assert.Equal(expectedPhase, actualPhase);

        var persistedCommits = task.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal(expectedCommitCount, persistedCommits.Count);
        if (expectedCommitCount == 0)
        {
            Assert.True(
                !task.TryGetProperty("commit", out var legacy)
                || legacy.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
        }
        else
        {
            var tail = JsonSerializer.Deserialize<TaskCommitInfo>(
                persistedCommits[^1].GetRawText(),
                TaskJsonOptions);
            var legacy = JsonSerializer.Deserialize<TaskCommitInfo>(
                task.GetProperty("commit").GetRawText(),
                TaskJsonOptions);
            Assert.NotNull(tail);
            Assert.NotNull(legacy);
            Assert.Equal(tail!.Sha, legacy!.Sha);
        }

        var tags = task.GetProperty("tags")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToList();
        Assert.Equal(
            (expectedTags ?? [ContractTag]).OrderBy(value => value, StringComparer.Ordinal),
            tags.OrderBy(value => value, StringComparer.Ordinal));

        var actualIntegrationBranch = task.TryGetProperty("integrationBranch", out var branch)
            && branch.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(branch.GetString())
                ? branch.GetString()
                : null;
        Assert.Equal(expectedIntegrationBranch, actualIntegrationBranch);
    }

    public string InitializeRepositoryWithIntegratedCommit()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "business-tests@example.com");
        RunGit("config", "user.name", "Business Tests");
        File.WriteAllText(Path.Combine(RepositoryPath, "seed.txt"), "seed\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "seed");
        RunGit("checkout", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(RepositoryPath, "integrated.txt"), "integrated\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "feat: integrated out of band");
        return RunGitCapture("rev-parse", "HEAD").Trim();
    }

    public static TaskCommitInfo Commit(
        char shaCharacter,
        string message,
        int minute)
        => Commit(new string(shaCharacter, 40), message, minute);

    public static TaskCommitInfo Commit(
        string sha,
        string message,
        int minute)
        => new()
        {
            Sha = sha,
            ShortSha = sha[..7],
            Message = message,
            FilesChanged = 1,
            Files = [$"file-{minute}.txt"],
            At = new DateTime(2026, 7, 29, 8, minute, 0, DateTimeKind.Utc),
            Attribution = CommitAttributionKinds.Automatic,
            Confidence = 1,
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for test temp data.
        }
    }

    private IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = WatchPath,
                ["WatchPaths:0:RootPath"] = RepositoryPath,
                ["WatchPaths:0:RepositoryPath"] = RepositoryPath,
            })
            .Build();

    private TaskScannerService BuildScanner()
    {
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            Configuration);
        return new TaskScannerService(
            Configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
    }

    private TaskMutationService BuildMutations(TaskScannerService scanner)
        => new(
            scanner,
            new ClientIdentityStore(
                Configuration,
                NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(
                Configuration,
                NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            Timeline);

    private void RunGit(params string[] args)
    {
        var result = RunProcess("git", args);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(" ", args)} failed ({result.ExitCode}): {result.Error}");
    }

    private string RunGitCapture(params string[] args)
    {
        var result = RunProcess("git", args);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(" ", args)} failed ({result.ExitCode}): {result.Error}");
        return result.Output;
    }

    private ProcessResult RunProcess(string fileName, IReadOnlyList<string> args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = RepositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
