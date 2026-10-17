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
/// real applier against the pilot fixture.
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

    private sealed record DeferredReview(SeededMember Member, ReviewAttemptDto Review,
        DateTimeOffset ReportedAt);

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

    private string CardFolder(string key)
        => TaskStates.All.Select(state => Path.Combine(_watchPath, state, key)).Single(Directory.Exists);

    private async Task MoveToEscalatedAsync(WebApplicationFactory<Program> factory, string key)
    {
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            key, TaskStates.Escalated, _watchPath, CancellationToken.None, cause: "operator",
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
    }
}
