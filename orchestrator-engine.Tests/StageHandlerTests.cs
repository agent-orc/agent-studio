using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class StageHandlerTests
{
    [Theory]
    [InlineData("""{"agentOutcome":"done"}""", OrchestrationAction.Continue)]
    [InlineData("""{"agentOutcome":"needs-input"}""", OrchestrationAction.Reissue)]
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
        Assert.Equal(expected, ReviewDecisionPolicy.Decide(outcome, null, null));
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

    [Fact]
    public async Task Council_reissues_named_critical_findings()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"reviewFindings":[{"severity":"critical","summary":"unsafe"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Fact]
    public async Task Council_reissues_normalized_remote_review_concerns()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"verdicts":[{"aspect":"requirements","status":"concerns"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Fact]
    public async Task Council_does_not_reissue_a_block_downgraded_for_missing_citation()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""
                {"verdicts":[{
                  "aspect":"documentation-impact",
                  "status":"concerns",
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

    private static OrchestrationRunDto Run(string payload)
        => new(
            "run-1",
            "project-1",
            "task-1",
            1,
            "leased",
            OrchestrationStage.ReviewDecision,
            payload,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            []);
}
