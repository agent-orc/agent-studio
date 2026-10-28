using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class IntegrationResultPolicyTests
{
    [Theory]
    [InlineData(IntegrationOutcomeKinds.Integrated, 1, IntegrationResultAction.HumanReview, "5-human-review")]
    [InlineData(IntegrationOutcomeKinds.AlreadyIntegrated, 1, IntegrationResultAction.HumanReview, "5-human-review")]
    [InlineData(IntegrationOutcomeKinds.StaleBase, 1, IntegrationResultAction.Requeue, null)]
    [InlineData(IntegrationOutcomeKinds.StaleBase, 2, IntegrationResultAction.Requeue, null)]
    [InlineData(IntegrationOutcomeKinds.StaleBase, 3, IntegrationResultAction.Escalate, "5e-escalated")]
    [InlineData(IntegrationOutcomeKinds.BlockedFailingArea, 1, IntegrationResultAction.Requeue, null)]
    [InlineData(IntegrationOutcomeKinds.BlockedFailingArea, 99, IntegrationResultAction.Requeue, null)]
    [InlineData(IntegrationOutcomeKinds.Conflict, 1, IntegrationResultAction.Escalate, "5e-escalated")]
    [InlineData(IntegrationOutcomeKinds.Failed, 1, IntegrationResultAction.Escalate, "5e-escalated")]
    [InlineData("unknown", 1, IntegrationResultAction.Escalate, "5e-escalated")]
    public void Integration_outcome_selects_one_lane(
        string outcome, int attempts, IntegrationResultAction action, string? lane)
    {
        var decision = IntegrationResultPolicy.Decide(outcome, attempts);

        Assert.Equal(action, decision.Action);
        Assert.Equal(lane, decision.TaskState);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    private const string Registered = "https://example.invalid/team/product.git";
    private const string ResultRef = "refs/heads/agent-studio/results/run-1/1";
    private const string ResultSha = "1111111111111111111111111111111111111111";

    [Theory]
    [InlineData(Registered, "run-1", Registered, ResultRef, ResultSha, IntegrationSource.Publishable)]
    [InlineData(Registered, "run-1", null, ResultRef, ResultSha, IntegrationSource.Publishable)]
    [InlineData(Registered, "run-1", "https://EXAMPLE.invalid/team/product.git/", ResultRef, ResultSha, IntegrationSource.Publishable)]
    // Source bundle only: the run recorded a result SHA but no fenced ref.
    [InlineData(Registered, "run-1", Registered, null, ResultSha, IntegrationSource.ResultMissing)]
    [InlineData(Registered, "run-1", Registered, ResultRef, null, IntegrationSource.ResultMissing)]
    [InlineData(Registered, null, Registered, ResultRef, ResultSha, IntegrationSource.ResultMissing)]
    [InlineData(null, "run-1", Registered, ResultRef, ResultSha, IntegrationSource.ResultMissing)]
    [InlineData(Registered, "run-1", "https://example.invalid/fork/product.git", ResultRef, ResultSha, IntegrationSource.RepositoryMismatch)]
    [InlineData(Registered, "run-1", "http://example.invalid/team/product.git", ResultRef, ResultSha, IntegrationSource.RepositoryMismatch)]
    // A source-bundle-only result is missing before its remote is even compared.
    [InlineData(Registered, "run-1", "https://example.invalid/fork/product.git", null, ResultSha, IntegrationSource.ResultMissing)]
    public void Accepted_delivery_source_is_classified_from_the_fenced_result_and_the_registered_remote(
        string? registered, string? sourceRunId, string? runRepository, string? resultRef, string? resultSha,
        IntegrationSource expected)
    {
        Assert.Equal(expected,
            IntegrationSourcePolicy.Classify(registered, sourceRunId, runRepository, resultRef, resultSha));
    }

    [Theory]
    [InlineData(TransitionAction.AcceptAndIntegrate, true, IntegrationSource.Publishable, "integrating", null)]
    [InlineData(TransitionAction.AcceptAndIntegrate, true, IntegrationSource.ResultMissing, "escalated", "5e-escalated")]
    [InlineData(TransitionAction.AcceptAndIntegrate, true, IntegrationSource.RepositoryMismatch, "escalated", "5e-escalated")]
    [InlineData(TransitionAction.AcceptAndIntegrate, false, IntegrationSource.Publishable, "completed", "5-human-review")]
    [InlineData(TransitionAction.AcceptAndIntegrate, false, IntegrationSource.ResultMissing, "completed", "5-human-review")]
    [InlineData(TransitionAction.HumanReview, true, IntegrationSource.Publishable, "completed", "5-human-review")]
    [InlineData(TransitionAction.HumanReview, true, IntegrationSource.ResultMissing, "completed", "5-human-review")]
    [InlineData(TransitionAction.DegradeWithFollowUp, true, IntegrationSource.Publishable, "completed", "5-human-review")]
    public void Accepted_delivery_waits_in_auto_review_only_under_reviewed_publication(
        TransitionAction accepted, bool reviewedPublication, IntegrationSource source, string status, string? lane)
    {
        var decision = OrchestrationSettlementPolicy.Decide(
            OrchestrationAction.Continue,
            [OrchestrationStage.ReviewDecision],
            OrchestrationStage.ReviewDecision,
            0, 2, "4-auto-review", 7, 7, null, null, accepted, reviewedPublication, source);

        Assert.True(decision.IsTerminal);
        Assert.Equal(status, decision.RunStatus);
        Assert.Equal(lane, decision.TaskState);
    }

    [Fact]
    public void A_fix_round_never_waits_for_integration()
    {
        var transition = TransitionDecisionPolicy.Create(TransitionAction.FindingRound, "block", "task", "review",
            0, 4, continuationBase: new ContinuationBase("refs/heads/result", new string('a', 40), "review"));

        var decision = OrchestrationSettlementPolicy.Decide(
            OrchestrationAction.Reissue,
            [OrchestrationStage.ReviewDecision],
            OrchestrationStage.ReviewDecision,
            0, 2, "4-auto-review", 7, 7, null, transition, TransitionAction.FindingRound, true,
            IntegrationSource.ResultMissing);

        Assert.Equal("reissued", decision.RunStatus);
        Assert.Equal("2-ready", decision.TaskState);
        Assert.NotNull(decision.Transition?.ContinuationBase);
    }
}
