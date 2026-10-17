using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// 17.10.2026: with ReviewReport on Engine, every DeferToBatch decision
/// completed without batch admission, because the applier returned for a
/// decision without a target lane before it enqueued. These cases drive the
/// real applier and the recovery loop against the pilot fixture.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Engine_defer_to_batch_admits_the_review_at_its_report_time()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var deferred = SettleDeferredReview(factory, "DOC-ENGINE-DEFER", "docs/engine-defer.md");
        // The engine decides later; the queue must keep the review time.
        _clock = _clock.AddMinutes(45);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var applied = await DecideDeferToBatchAsync(orchestration, deferred);

        Assert.Equal("completed", applied.Status);
        AssertLane(deferred.Member.Key, TaskStates.AutoReview);
        var pending = factory.Services.GetRequiredService<BatchGateStore>()
            .ReadPending(deferred.Member.ReviewAttemptId);
        Assert.NotNull(pending);
        Assert.Equal(deferred.ReportedAt, pending!.Subject.ReviewCompletedAtUtc);
        Assert.NotEqual(_clock, pending.Subject.ReviewCompletedAtUtc);
        Assert.Equal(deferred.Member.ReviewAttemptId, Ownership(deferred.Member.Key)!.ReviewAttemptId);
        Assert.Null(EngineDecisionWaitMarker.Read(
            Path.Combine(_watchPath, TaskStates.AutoReview, deferred.Member.Key)));
    }

    [Fact]
    public async Task Engine_defer_to_batch_runs_the_per_task_gate_when_the_queue_refuses_the_review()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        // Code in a documentation-only batch scope is refused at admission.
        var deferred = SettleDeferredReview(factory, "CODE-ENGINE-DEFER", "backend/EngineDefer.cs");
        _gate.Fallback = request => Red(request.ExpectedSha);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var applied = await DecideDeferToBatchAsync(orchestration, deferred);

        Assert.Equal("completed", applied.Status);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(deferred.Member.Key, TaskStates.Escalated);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.Null(Ownership(deferred.Member.Key));
        // The wait marker moved with the card; it must not outlive the decision.
        Assert.Null(EngineDecisionWaitMarker.Read(
            Path.Combine(_watchPath, TaskStates.Escalated, deferred.Member.Key)));
    }

    [Fact]
    public async Task Engine_defer_to_batch_stays_applying_until_its_per_task_gate_returns()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        // The test owns the retry order of the applying occurrence.
        await factory.Services.GetServices<IHostedService>()
            .OfType<MonolithOrchestrationRecoveryService>().Single().StopAsync(CancellationToken.None);
        EnableBatchGate(factory, closeSize: 4);
        var deferred = SettleDeferredReview(factory, "CODE-ENGINE-RETRY", "backend/EngineRetry.cs");
        _gate.Fallback = _ => throw new InvalidOperationException("The gate host was lost.");
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DecideDeferToBatchAsync(orchestration, deferred));

        // A current review without an owner is never completed; recovery retries it.
        var applying = Assert.Single(orchestration.List(ProjectName, "applying"));
        AssertLane(deferred.Member.Key, TaskStates.AutoReview);
        _gate.Fallback = request => Red(request.ExpectedSha);
        await orchestration.RecoverApplyingAsync(CancellationToken.None);

        Assert.Equal("completed", orchestration.Get(applying.RunId)!.Status);
        Assert.Equal(2, _gate.FallbackRuns);
        AssertLane(deferred.Member.Key, TaskStates.Escalated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_defer_to_batch_that_owes_no_admission_completes_with_a_recorded_reason(
        bool cardMoved)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var deferred = SettleDeferredReview(factory, "DOC-ENGINE-OWED", "docs/engine-owed.md");
        if (cardMoved)
            await MoveToEscalatedAsync(factory, deferred.Member.Key);
        else
            Supersede(factory.Services.GetRequiredService<AttemptAuthorityService>(), deferred.Member.Key);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        var applied = await DecideDeferToBatchAsync(orchestration, deferred);

        Assert.Equal("completed", applied.Status);
        AssertLane(deferred.Member.Key, cardMoved ? TaskStates.Escalated : TaskStates.AutoReview);
        Assert.Null(factory.Services.GetRequiredService<BatchGateStore>()
            .ReadPending(deferred.Member.ReviewAttemptId));
        Assert.Null(Ownership(deferred.Member.Key));
        Assert.Equal(0, _gate.FallbackRuns);
        var reason = Assert.Single(factory.Services.GetRequiredService<OrchestratorLog>().Read(_watchPath),
            entry => entry.Topic == "engine-batch-admission:" + applied.RunId);
        Assert.Contains(cardMoved ? "left " + TaskStates.AutoReview : "no longer the card's current review",
            reason.Reasoning);
    }

    [Fact]
    public async Task Recovery_rearms_only_the_engine_batch_decisions_that_lost_their_admission()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var journal = PreFixJournal(factory);
        var stuck = SettleDeferredReview(factory, "DOC-REARM", "docs/rearm.md");
        var moved = SettleDeferredReview(factory, "DOC-REARM-MOVED", "docs/rearm-moved.md");
        var superseded = SettleDeferredReview(factory, "DOC-REARM-STALE", "docs/rearm-stale.md");
        var shadow = SettleDeferredReview(factory, "DOC-REARM-SHADOW", "docs/rearm-shadow.md");
        var redecided = SettleDeferredReview(factory, "DOC-REARM-NEWER", "docs/rearm-newer.md");
        foreach (var decided in new[] { stuck, moved, superseded, redecided })
            Assert.Equal("completed", (await DecideDeferToBatchAsync(journal, decided)).Status);
        Assert.Equal("completed", (await DecideDeferToBatchAsync(journal, shadow, shadow: true)).Status);
        // A newer report of the same review is still being decided.
        journal.Create(ProjectName, redecided.Member.Key, Contract.OrchestrationStage.ReviewDecision,
            "{\"reviewOutcome\":\"Pass\"}", $"review-report:{redecided.Member.ReviewAttemptId}:newer",
            TaskStates.AutoReview, redecided.Member.ReviewAttemptId, redecided.Review.LastFence,
            redecided.Review.AuthorityEpoch, profile: "ReviewReport");
        await MoveToEscalatedAsync(factory, moved.Member.Key);
        Supersede(factory.Services.GetRequiredService<AttemptAuthorityService>(), superseded.Member.Key);
        _clock = _clock.AddHours(3);
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(
            factory.Services, journal);

        Assert.Equal(1, await recovery.RearmDeferredBatchAdmissionsAsync());
        Assert.Equal(0, await recovery.RearmDeferredBatchAdmissionsAsync());

        var pending = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.Equal(stuck.Member.ReviewAttemptId, pending.ReviewAttemptId);
        Assert.Equal(stuck.ReportedAt, pending.Subject.ReviewCompletedAtUtc);
        Assert.Equal(stuck.Member.ReviewAttemptId, Ownership(stuck.Member.Key)!.ReviewAttemptId);
        foreach (var skipped in new[] { moved, superseded, shadow, redecided })
            Assert.Null(Ownership(skipped.Member.Key));
        AssertLane(stuck.Member.Key, TaskStates.AutoReview);
        AssertLane(moved.Member.Key, TaskStates.Escalated);
        Assert.Equal(0, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Recovery_clears_wait_markers_of_finished_occurrences_and_keeps_live_ones()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var journal = PreFixJournal(factory);
        var done = await MarkedOccurrenceAsync(factory, journal, "MARK-DONE", "{\"transition\":{}}");
        var held = await MarkedOccurrenceAsync(factory, journal, "MARK-HELD",
            "{\"activeChoice\":{\"action\":\"ask\",\"target\":\"operator:ai\"}}");
        var cancelled = await MarkedOccurrenceAsync(factory, journal, "MARK-CANCELLED", output: null);
        var waiting = await MarkedOccurrenceAsync(factory, journal, "MARK-WAITING", output: null,
            claimable: true);
        EngineDecisionWaitMarker.Clear(waiting.Folder, waiting.RunId);
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(
            factory.Services, journal);

        recovery.RecoverOnce();

        Assert.Null(EngineDecisionWaitMarker.Read(done.Folder));
        Assert.Null(EngineDecisionWaitMarker.Read(cancelled.Folder));
        // An active decision holds its card on purpose.
        Assert.Equal(held.RunId, EngineDecisionWaitMarker.Read(held.Folder));
        Assert.Equal(waiting.RunId, EngineDecisionWaitMarker.Read(waiting.Folder));

        // A marker written for an occurrence that completed in the meantime is removed again.
        recovery.MarkWaiting(done.Folder, done.RunId);
        Assert.Null(EngineDecisionWaitMarker.Read(done.Folder));
        recovery.MarkWaiting(waiting.Folder, waiting.RunId);
        Assert.Equal(waiting.RunId, EngineDecisionWaitMarker.Read(waiting.Folder));
    }

    private sealed record DeferredReview(SeededMember Member, ReviewAttemptDto Review,
        DateTimeOffset ReportedAt);

    private sealed record MarkedOccurrence(string Folder, string RunId);

    private DeferredReview SettleDeferredReview(WebApplicationFactory<Program> factory, string key,
        string changedFile)
    {
        var member = SeedMember(factory, key, changedFile, settleReview: false);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var report = review.Reports.Last(item => item.AuthorityStatus == AttemptWriteStatus.Accepted);
        return new DeferredReview(member, review,
            new DateTimeOffset(DateTime.SpecifyKind(report.ReceivedAt, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The occurrence the Engine-mode report path prepares, with its wait
    /// marker, completed by an engine that decided DeferToBatch.
    /// </summary>
    private async Task<Contract.OrchestrationRunDto> DecideDeferToBatchAsync(
        MonolithOrchestrationStore orchestration, DeferredReview deferred, bool shadow = false)
    {
        var review = deferred.Review;
        var report = review.Reports.Last(item => item.AuthorityStatus == AttemptWriteStatus.Accepted);
        var run = orchestration.Create(ProjectName, deferred.Member.Key,
            Contract.OrchestrationStage.ReviewDecision,
            JsonSerializer.Serialize(new { reviewOutcome = "Pass", reviewAttemptId = review.AttemptId }),
            $"review-report:{review.AttemptId}:{report.IdempotencyKey}", TaskStates.AutoReview,
            review.AttemptId, review.LastFence, review.AuthorityEpoch, profile: "ReviewReport",
            shadow: shadow);
        if (!shadow)
            EngineDecisionWaitMarker.Write(CardFolder(deferred.Member.Key), run.RunId);
        var claim = orchestration.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
            [Contract.OrchestrationStage.ReviewDecision]));
        Assert.Equal(run.RunId, claim.Run!.RunId);
        var decision = TransitionDecisionPolicy.Create(TransitionAction.DeferToBatch,
            "The batch gate owns full verification and integration.", deferred.Member.Key,
            review.AttemptId, 0, 4);
        return await orchestration.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest(
            "engine", "instance", claim.Lease!.LeaseId, claim.Lease.Fence,
            Contract.OrchestrationStage.ReviewDecision, Contract.OrchestrationAction.Continue,
            JsonSerializer.Serialize(new { transition = decision }), "complete:" + run.RunId),
            CancellationToken.None);
    }

    /// <summary>
    /// A card with a wait marker that names one occurrence of a separate journal:
    /// completed with <paramref name="output"/>, cancelled before activation, or
    /// still waiting for an engine claim.
    /// </summary>
    private async Task<MarkedOccurrence> MarkedOccurrenceAsync(WebApplicationFactory<Program> factory,
        MonolithOrchestrationStore journal, string key, string? output, bool claimable = false)
    {
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state = TaskStates.AutoReview, order = 1, agent = "claude",
            kind = TaskKinds.Task,
        }));
        var run = journal.Create(ProjectName, key, Contract.OrchestrationStage.ReviewDecision, "{}",
            $"review-report:review_{key}:report", TaskStates.AutoReview, "review_" + key, 1, 1,
            claimable: claimable || output is not null, profile: "ReviewReport");
        if (output is not null)
        {
            var claim = journal.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
                [Contract.OrchestrationStage.ReviewDecision]));
            Assert.Equal(run.RunId, claim.Run!.RunId);
            await journal.CompleteAsync(run.RunId, new Contract.CompleteOrchestrationStageRequest(
                "engine", "instance", claim.Lease!.LeaseId, claim.Lease.Fence,
                Contract.OrchestrationStage.ReviewDecision, Contract.OrchestrationAction.Continue,
                output, "complete:" + run.RunId), CancellationToken.None);
        }
        else if (!claimable)
            journal.CancelUnactivated(run.RunId, "review-settlement-rejected");
        EngineDecisionWaitMarker.Write(folder, run.RunId);
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        return new MarkedOccurrence(folder, run.RunId);
    }

    private string CardFolder(string key)
        => TaskStates.All.Select(state => Path.Combine(_watchPath, state, key)).Single(Directory.Exists);

    private async Task MoveToEscalatedAsync(WebApplicationFactory<Program> factory, string key)
    {
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            key, TaskStates.Escalated, _watchPath, CancellationToken.None, cause: "operator",
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
    }

    /// <summary>
    /// A separate occurrence journal whose applier predates the fix: it completes
    /// a decision and applies nothing, as DeferToBatch did until 17.10.2026.
    /// </summary>
    private MonolithOrchestrationStore PreFixJournal(WebApplicationFactory<Program> factory)
        => new(Path.Combine(_workspace, "pre-fix-occurrences"),
            factory.Services.GetRequiredService<TimeProvider>(), new UnappliedDecisions());

    private sealed class UnappliedDecisions : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
            => Task.CompletedTask;
    }
}
