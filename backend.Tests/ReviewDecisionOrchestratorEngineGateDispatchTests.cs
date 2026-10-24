using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3218: a passed engine gate decision keeps the card in Auto Review and
/// clears its wait marker; the gate handler's next pass reads the completed
/// Continue, dispatches the aspect review and records the
/// <c>engine_gate_dispatched</c> receipt that the applier canary checks.
/// </summary>
public partial class ReviewDecisionOrchestratorCompletionGateTests
{
    [Fact]
    public async Task Engine_gate_decision_is_dispatched_by_the_handlers_next_pass_with_its_receipt()
    {
        const string slug = "engine-gate-dispatch";
        SeedReviewJobWithDone(slug, status: "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n");
        var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
        var orchestration = new MonolithOrchestrationStore(directory: null, new FakeTimeProvider(),
            new KeepsLaneAndClearsMarker(Path.Combine(_watchPath, TaskStates.AutoReview, slug)));
        var gate = new FakeBuildTestGateRunner(new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 10,
            "build passed", "build gate passed", true, false));
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, gate,
            configure: config => config["Orchestration:Transitions:GateResult"] = "Engine",
            orchestration: orchestration);

        // First pass: the gate passed, and the engine owns the decision.
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        var run = Assert.Single(orchestration.List(Project, "pending"));
        Assert.Equal(Contract.OrchestrationStage.GateDispatch, run.CurrentStage);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Assert.Equal(run.RunId, EngineDecisionWaitMarker.Read(folder));
        Assert.Equal(0, aspect.Invocations);

        var claim = orchestration.Claim(new Contract.OrchestrationClaimRequest("engine", "instance",
            [Contract.OrchestrationStage.GateDispatch]));
        var transition = TransitionDecisionPolicy.Create(TransitionAction.Continue, "The gate passed.", slug,
            run.RunId, 0, 4);
        Assert.Equal("completed", (await orchestration.CompleteAsync(run.RunId,
            new Contract.CompleteOrchestrationStageRequest("engine", "instance", claim.Lease!.LeaseId,
                claim.Lease.Fence, Contract.OrchestrationStage.GateDispatch, Contract.OrchestrationAction.Continue,
                System.Text.Json.JsonSerializer.Serialize(new { transition }), "complete:" + run.RunId),
            CancellationToken.None)).Status);
        Assert.False(EngineGateDispatchReceipt.Recorded(timeline, folder, run.RunId));

        // Second pass: the handler finds the completed Continue and dispatches the aspect review.
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        Assert.True(aspect.Invocations > 0, "the second pass dispatches the aspect review");
        var landed = TaskStates.All.Select(state => Path.Combine(_watchPath, state, slug)).Single(Directory.Exists);
        Assert.True(EngineGateDispatchReceipt.Recorded(timeline, landed, run.RunId));
        Assert.Single(orchestration.List(Project, null));
    }

    /// <summary>What the real applier does for a passed gate without a lane: it keeps the lane and clears the marker.</summary>
    private sealed class KeepsLaneAndClearsMarker(string folder) : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            Contract.CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            EngineDecisionWaitMarker.Clear(folder, occurrence.Run.RunId);
            return Task.CompletedTask;
        }
    }
}
