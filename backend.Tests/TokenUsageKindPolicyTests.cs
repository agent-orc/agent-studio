using AgentStudio.Shared;
using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TokenUsageKindPolicyTests
{
    [Theory]
    [InlineData("core-agent-run", StepKind.Core, true, TokenUsageKinds.Coding)]
    [InlineData("core-agent-run", StepKind.Core, false, TokenUsageKinds.FixRound)]
    [InlineData("aspect-code-quality", StepKind.Aspect, false, TokenUsageKinds.ReviewAspect)]
    [InlineData("aspect-concept-fit", StepKind.Aspect, false, TokenUsageKinds.ReviewAspect)]
    [InlineData("post-code-review-grade", StepKind.Orchestrator, false, TokenUsageKinds.ReviewGrade)]
    [InlineData("post-analysis-model-review", StepKind.Analysis, false, TokenUsageKinds.DeepReview)]
    [InlineData("post-second-opinion-review", StepKind.Analysis, false, TokenUsageKinds.DeepReview)]
    [InlineData("post-build-test-gate", StepKind.Tool, false, TokenUsageKinds.Gate)]
    [InlineData("post-merge-into-develop", StepKind.Tool, false, TokenUsageKinds.Gate)]
    [InlineData("pre-prompt-enrichment", StepKind.Module, false, TokenUsageKinds.Enrichment)]
    [InlineData("pre-model-qualification", StepKind.Module, false, TokenUsageKinds.Enrichment)]
    [InlineData("post-orchestrator-decision", StepKind.Orchestrator, false, TokenUsageKinds.OrchestratorDecision)]
    [InlineData("post-failure-intervention", StepKind.Module, false, TokenUsageKinds.OrchestratorDecision)]
    [InlineData("post-drift-code-pattern", StepKind.Drift, false, TokenUsageKinds.Other)]
    [InlineData("post-wiki-learnings", StepKind.Module, false, TokenUsageKinds.Other)]
    public void Steps_classify_by_id_and_kind(string stepId, StepKind kind, bool first, string expected)
        => Assert.Equal(expected, TokenUsageKindPolicy.ForStep(stepId, kind, first));

    [Theory]
    [InlineData("agent:codex", "task-token-receipt", true, TokenUsageKinds.Coding)]
    [InlineData("agent:claude", null, false, TokenUsageKinds.FixRound)]
    [InlineData("agent:task-receipt", "task-token-receipt", true, TokenUsageKinds.Coding)]
    [InlineData("orchestrator:P", "orchestrator-decision", true, TokenUsageKinds.OrchestratorDecision)]
    [InlineData("orchestrator:P", "orchestrator-chat", true, TokenUsageKinds.Chat)]
    [InlineData("support:adhoc", "chat-turn", true, TokenUsageKinds.Chat)]
    [InlineData("support:adhoc", "title-generation", true, TokenUsageKinds.Enrichment)]
    [InlineData("support:adhoc", "prompt-enhancement", true, TokenUsageKinds.Enrichment)]
    [InlineData("support:adhoc", "review-decision", true, TokenUsageKinds.OrchestratorDecision)]
    [InlineData("support:adhoc", "drift-analysis", true, TokenUsageKinds.Other)]
    [InlineData("support:aspect", "aspect-code-quality", true, TokenUsageKinds.ReviewAspect)]
    [InlineData("support:aspect", null, true, TokenUsageKinds.ReviewAspect)]
    [InlineData("orchestrator:P", null, true, TokenUsageKinds.OrchestratorDecision)]
    [InlineData(null, null, true, TokenUsageKinds.Coding)]
    public void Ledger_calls_classify_by_topic_then_participant(
        string? participant, string? topic, bool first, string expected)
        => Assert.Equal(expected, TokenUsageKindPolicy.ForLedger(participant, topic, first));

    [Theory]
    [InlineData(TaskStates.Completed, "integrated", false, CardOutcomes.Integrated)]
    [InlineData(TaskStates.HumanReview, "integrated", false, CardOutcomes.Integrated)]
    [InlineData(TaskStates.Completed, null, false, CardOutcomes.Completed)]
    [InlineData(TaskStates.Archive, null, false, CardOutcomes.Completed)]
    [InlineData(TaskStates.HumanReview, "pending", false, CardOutcomes.Delivered)]
    [InlineData(TaskStates.Escalated, null, false, CardOutcomes.Escalated)]
    [InlineData(TaskStates.FailedPickup, null, false, CardOutcomes.Blocked)]
    [InlineData(TaskStates.CodeNotComplete, null, false, CardOutcomes.Blocked)]
    [InlineData(TaskStates.Progress, null, false, CardOutcomes.InProgress)]
    [InlineData(TaskStates.AutoReview, null, true, CardOutcomes.Blocked)]
    [InlineData(TaskStates.Ready, null, false, CardOutcomes.Open)]
    [InlineData(TaskStates.Ready, null, true, CardOutcomes.Blocked)]
    [InlineData(null, null, false, CardOutcomes.Open)]
    public void Outcome_prefers_integration_evidence_then_lane(
        string? state, string? integration, bool blocker, string expected)
        => Assert.Equal(expected, CardOutcomes.Of(state, integration, blocker));

    [Theory]
    [InlineData(TaskStates.Progress, true)]
    [InlineData(TaskStates.AutoReview, true)]
    [InlineData(TaskStates.HumanReview, false)]
    [InlineData(TaskStates.Ready, false)]
    public void In_flight_lanes_can_still_add_tokens(string state, bool expected)
        => Assert.Equal(expected, CardOutcomes.IsInFlight(state));

    [Fact]
    public void Every_kind_is_listed_once()
        => Assert.Equal(TokenUsageKinds.All.Length, TokenUsageKinds.All.Distinct().Count());
}
