using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;
using System.Text.Json;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class StageHandlerTests
{
    [Fact]
    public async Task Operator_sweep_stage_uses_the_shared_policy()
    {
        var payload = new AgentStudio.OrchestrationDecisions.OperatorSweepStagePayload(
            AgentStudio.OrchestrationDecisions.OperatorSweepKinds.FixRounds,
            new AgentStudio.OrchestrationDecisions.OperatorSweepTrigger("review-one", false),
            new AgentStudio.OrchestrationDecisions.OperatorSweepGuardFacts(false, true, false, false,
                new AgentStudio.OrchestrationDecisions.CardRoundBudgetState(0, 4)));
        var run = new OrchestrationRunDto("run", "project", "card", 1, "leased",
            OrchestrationStage.OperatorSweep, System.Text.Json.JsonSerializer.Serialize(payload),
            0, new FrozenTimeProvider(DateTimeOffset.UnixEpoch).GetUtcNow().UtcDateTime,
            new FrozenTimeProvider(DateTimeOffset.UnixEpoch).GetUtcNow().UtcDateTime, null);

        var result = await new OperatorSweepLoop().ExecuteAsync(run, default);

        Assert.Equal(OrchestrationAction.Reissue, result.Action);
        Assert.Contains("sweepDecision", result.OutputJson);
    }
    [Theory]
    [InlineData("""{"agentOutcome":"done"}""", OrchestrationAction.Continue)]
    [InlineData("""{"agentOutcome":"needs-input"}""", OrchestrationAction.Escalate)]
    [InlineData("""{"agentOutcome":"blocked"}""", OrchestrationAction.Escalate)]
    public async Task Review_decision_loop_maps_terminal_facts(
        string payload,
        OrchestrationAction expected)
    {
        var decision = await new ReviewDecisionOrchestratorLoop()
            .ExecuteAsync(Run(payload), default);
        Assert.Equal(expected, decision.Action);
    }

    [Theory]
    [InlineData("Pass", OrchestrationAction.Continue)]
    [InlineData("ProductFailure", OrchestrationAction.Reissue)]
    [InlineData("ReviewInfra", OrchestrationAction.Escalate)]
    [InlineData("unknown", OrchestrationAction.Escalate)]
    public void Review_decision_policy_maps_normalized_remote_review_verdicts(
        string outcome,
        OrchestrationAction expected)
    {
        Assert.Equal(expected, StageDecisionPolicy.ReviewOutcome(outcome, null, null));
    }

    [Fact]
    public async Task Review_decision_output_preserves_the_attempt_envelope()
    {
        var decision = await new ReviewDecisionOrchestratorLoop().ExecuteAsync(
            Run("""
                {
                  "runAttemptId":"run-42",
                  "reviewSubjectId":"subject-42",
                  "reviewAttemptId":"review-42",
                  "reviewOutcome":"Pass"
                }
                """),
            default);

        Assert.Equal(OrchestrationAction.Continue, decision.Action);
        Assert.Contains("run-42", decision.OutputJson, StringComparison.Ordinal);
        Assert.Contains("subject-42", decision.OutputJson, StringComparison.Ordinal);
        Assert.Contains("review-42", decision.OutputJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskStates.AutoReview, TransitionAction.CreateReview)]
    [InlineData(TaskStates.Ready, TransitionAction.ReturnToReady)]
    [InlineData(TaskStates.Escalated, TransitionAction.Escalate)]
    public async Task Coding_completion_writes_a_typed_library_decision(
        string target, TransitionAction expected)
    {
        var result = await new ReviewDecisionOrchestratorLoop().ExecuteAsync(
            Run(JsonSerializer.Serialize(new { agentOutcome = "done", runAttemptId = "attempt-1",
                targetState = target })), default);
        using var output = JsonDocument.Parse(result.OutputJson);
        var transition = output.RootElement.GetProperty("transition")
            .Deserialize<TransitionDecision>()!;
        Assert.Equal(expected, transition.Action);
        Assert.Equal(StageDecisionPolicy.ToStageAction(expected), result.Action);
        Assert.Equal("attempt-1", transition.Receipt.AttemptId);
    }

    [Fact]
    public async Task Council_reissues_named_critical_findings()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"reviewFindings":[{"severity":"critical","summary":"unsafe"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Fact]
    public async Task Council_keeps_unactionable_remote_review_concerns()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"verdicts":[{"aspect":"requirements","status":"concerns"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Continue, decision.Action);
    }

    [Fact]
    public async Task Council_does_not_reissue_a_block_without_citation()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""
                {"verdicts":[{
                  "aspect":"documentation-impact",
                  "status":"block",
                  "classification":"block-without-citation"
                }]}
                """),
            default);

        Assert.Equal(OrchestrationAction.Continue, decision.Action);
        Assert.Contains("\"findingCount\":1", decision.OutputJson, StringComparison.Ordinal);
        Assert.Contains("\"blockerCount\":0", decision.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_dispatch_reissues_a_failed_gate()
    {
        var decision = await new GateDispatchLoop().ExecuteAsync(
            Run("""{"gates":[{"status":"failed"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Gate_dispatch_escalates_infrastructure_and_intervention_failures(
        bool infrastructureFailure, bool interventionRequired)
    {
        var result = await new GateDispatchLoop().ExecuteAsync(Run(JsonSerializer.Serialize(new
        {
            monolithGateResult = true,
            gateAttemptId = "gate-attempt",
            automaticRoundsUsed = 0,
            maximumCardRounds = 4,
            infrastructureFailure,
            interventionRequired,
            gates = new[] { new { status = "failed" } },
        })), default);
        using var output = JsonDocument.Parse(result.OutputJson);
        var transition = output.RootElement.GetProperty("transition")
            .Deserialize<TransitionDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(OrchestrationAction.Escalate, result.Action);
        Assert.Equal(TransitionAction.Escalate, transition?.Action);
    }

    [Fact]
    public async Task Batch_gate_release_chooses_a_typed_human_review_transition()
    {
        var result = await new GateDispatchLoop().ExecuteAsync(Run("""
            {"monolithGateResult":true,"gateAttemptId":"review-1","targetState":"5-human-review",
             "gates":[{"status":"passed"}]}
            """), default);
        using var output = JsonDocument.Parse(result.OutputJson);
        var transition = output.RootElement.GetProperty("transition")
            .Deserialize<TransitionDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(OrchestrationAction.Continue, result.Action);
        Assert.Equal(TransitionAction.Continue, transition?.Action);
        Assert.Equal("review-1", transition?.Receipt.AttemptId);
        Assert.Equal($"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}",
            output.RootElement.GetProperty("decider").GetString());
        Assert.Equal(TaskStates.HumanReview,
            TransitionDecisionPolicy.TargetLane(transition?.Action, TaskStates.HumanReview, result.Action));
    }

    private static OrchestrationRunDto Run(string payload, TimeProvider? clock = null)
    {
        var now = (clock ?? new FrozenTimeProvider(DateTimeOffset.UnixEpoch)).GetUtcNow().UtcDateTime;
        return new(
            "run-1",
            "project-1",
            "task-1",
            1,
            "leased",
            OrchestrationStage.ReviewDecision,
            payload,
            0,
            now,
            now,
            null,
            []);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
