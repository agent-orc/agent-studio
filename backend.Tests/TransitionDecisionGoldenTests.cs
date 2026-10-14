extern alias TaskServerAssembly;
extern alias EngineAssembly;

using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using Xunit;
using TaskAdapter = TaskServerAssembly::AgentStudio.TaskServer.TaskServerTransitionAdapter;
using EngineAdapter = EngineAssembly::AgentStudio.OrchestratorEngine.EngineTransitionAdapter;

namespace AgentStudio.Tests;

// clock-independent: the fixed stage timestamps are receipt data; the pure decision code never reads the clock.

public sealed class TransitionDecisionGoldenTests
{
    public static IEnumerable<object[]> Reviews()
    {
        yield return Case("pass", Facts("Pass"), TransitionAction.AcceptAndIntegrate);
        yield return Case("unactionable concern", Facts("Pass", "requirements", "concerns"), TransitionAction.AcceptAndIntegrate);
        yield return Case("actionable concern", Facts("Pass", "requirements", "concerns", finding: "Fix src/feature.cs")
            with { Settings = new(MaximumConcernRounds: 1) }, TransitionAction.ConcernRound);
        yield return Case("concern exhausted", Facts("Pass", "requirements", "concerns", finding: "Fix src/feature.cs")
            with { Settings = new(MaximumConcernRounds: 1), Counters = new(ConcernRounds: 1) }, TransitionAction.AcceptAndIntegrate);
        yield return Case("blocking verdict", Facts("ProductFailure", "requirements", "block"), TransitionAction.FindingRound);
        yield return Case("uncited block", Facts("ProductFailure", "requirements", "block",
            classification: ReviewVerdictCitationPolicy.BlockWithoutCitation), TransitionAction.HumanReview);
        yield return Case("test block", Facts("ProductFailure", "build-tests", "block"), TransitionAction.FindingRound);
        yield return Case("budget spent", Facts("ProductFailure", "requirements", "block")
            with { Counters = new(AutomaticRounds: 4) }, TransitionAction.DegradeWithFollowUp);
        yield return Case("recurring block", Facts("ProductFailure", "requirements", "block")
            with { DeliveredRounds = new([new("prior-1", ["requirements"], []),
                new("prior-2", ["requirements"], [])]) }, TransitionAction.DegradeWithFollowUp);
        yield return Case("test budget spent", Facts("ProductFailure", "tests-and-evidence", "block")
            with { Counters = new(AutomaticRounds: 4) }, TransitionAction.Escalate);
        yield return Case("infra retry", Facts("ReviewInfra")
            with { Gate = new(InfrastructureRetryAvailable: true) }, TransitionAction.ReviewInfrastructureRetry);
        yield return Case("infra exhausted", Facts("ReviewInfra"), TransitionAction.Escalate);
        yield return Case("integration branch defect", Facts("IntegrationBranchDefect"), TransitionAction.AcceptAndIntegrate);
        yield return Case("deferred to batch", Facts("Pass")
            with { Gate = new(DeferredToBatch: true) }, TransitionAction.DeferToBatch);
        yield return Case("missing envelope", Facts("Pass")
            with { Attempt = new("attempt", false) }, TransitionAction.HumanReview);
        yield return Case("accepted replay", Facts("Pass")
            with { AcceptedReplay = true }, TransitionAction.Halt);
    }

    private static object[] Case(string name, TransitionFacts facts, TransitionAction expected) => [name, facts, expected];

    [Theory]
    [MemberData(nameof(Reviews))]
    public void All_adapters_produce_identical_actions_and_receipts(string name, TransitionFacts facts, TransitionAction expected)
    {
        var monolith = MonolithTransitionAdapter.Decide(facts);
        var server = TaskAdapter.Decide(facts);
        var engine = EngineAdapter.Decide(facts);
        Assert.Equal(expected, monolith.Action);
        Assert.Equal(JsonSerializer.Serialize(monolith), JsonSerializer.Serialize(server));
        Assert.Equal(JsonSerializer.Serialize(monolith), JsonSerializer.Serialize(engine));
        Assert.Equal("card", monolith.Receipt.CardKey);
        Assert.Equal("attempt", monolith.Receipt.AttemptId);
        Assert.Equal(TransitionDecisionPolicy.Union(expected), Assert.Single(monolith.Candidates).Union);
        if (expected is TransitionAction.FindingRound or TransitionAction.ConcernRound)
        {
            Assert.Equal(facts.Attempt.ReviewedDelivery, monolith.ContinuationBase);
            Assert.NotEmpty(monolith.Findings);
        }
        if (expected == TransitionAction.DegradeWithFollowUp)
        {
            Assert.True(monolith.RequiresLinkedFollowUp);
            Assert.NotEmpty(monolith.Findings);
            Assert.Equal(DecisionAuthority.LinkedFollowUp, monolith.Candidates[0].AuthorityFloor);
        }
    }

    [Fact]
    public void Uncited_block_does_not_reissue_in_any_adapter()
    {
        var facts = Facts("ProductFailure", "requirements", "block",
            classification: ReviewVerdictCitationPolicy.BlockWithoutCitation);
        Assert.Equal(TransitionAction.HumanReview, MonolithTransitionAdapter.Decide(facts).Action);
        Assert.Equal(TransitionAction.HumanReview, TaskAdapter.Decide(facts).Action);
        Assert.Equal(TransitionAction.HumanReview, EngineAdapter.Decide(facts).Action);
    }

    [Theory]
    [InlineData(OperatorSweepKinds.FixRounds, "product", false, 0, OperatorSweepAction.Act)]
    [InlineData(OperatorSweepKinds.GateTriage, "product", false, 0, OperatorSweepAction.Act)]
    [InlineData(OperatorSweepKinds.GateTriage, "infrastructure", false, 0, OperatorSweepAction.Hold)]
    [InlineData(OperatorSweepKinds.GateTriage, null, false, 0, OperatorSweepAction.WaitForPerson)]
    [InlineData(OperatorSweepKinds.Salvage, null, false, 0, OperatorSweepAction.Act)]
    [InlineData(OperatorSweepKinds.Salvage, null, true, 0, OperatorSweepAction.Hold)]
    [InlineData(OperatorSweepKinds.FixRounds, "product", false, 4, OperatorSweepAction.WaitForPerson)]
    public void Sweep_adapters_share_guard_order(string sweep, string? domain, bool paused, int used, OperatorSweepAction expected)
    {
        var facts = new OperatorSweepGuardFacts(paused, true, false, false, new(used, 4));
        var trigger = new OperatorSweepTrigger("subject", false, domain);
        var monolith = MonolithTransitionAdapter.Sweep(sweep, trigger, facts);
        Assert.Equal(expected, monolith.Action);
        Assert.Equal(monolith, TaskAdapter.Sweep(sweep, trigger, facts));
        Assert.Equal(monolith, EngineAdapter.Sweep(sweep, trigger, facts));
    }

    [Theory]
    [InlineData("done", false, "4-auto-review")]
    [InlineData("noop", false, "4-auto-review")]
    [InlineData("needsinput", false, "5e-escalated")]
    [InlineData("stopped", false, "2-ready")]
    [InlineData("done", true, "5e-escalated")]
    [InlineData("invalid", false, "")]
    public void Completion_routing_preserves_the_lease_contract(string outcome, bool rejected, string lane)
        => Assert.Equal(lane, TransitionDecisionPolicy.CompletionLane(outcome, rejected));

    [Theory]
    [MemberData(nameof(Reviews))]
    public async Task Engine_stage_projects_the_same_golden_review_action(string name, TransitionFacts facts, TransitionAction expected)
    {
        if (facts.AcceptedReplay) return; // Admission rejects a replay before a stage is claimed.
        var payload = new StagePayload(
            ReviewOutcome: facts.Review.Outcome, RunAttemptId: "run", ReviewAttemptId: facts.Attempt.Id,
            ResultSha: facts.Attempt.HasSettledEnvelope ? facts.Attempt.ReviewedDelivery?.ResultSha : null,
            ResultRef: facts.Attempt.ReviewedDelivery?.ResultRef,
            Verdicts: facts.Review.Verdicts,
            ReviewPlan: facts.Review.Plan! with { BuildTestDeferredToBatch = facts.Gate.DeferredToBatch },
            FailureClassification: facts.Review.FailureClassification,
            AutomaticRoundsUsed: facts.Counters.AutomaticRounds, MaximumCardRounds: facts.Settings.MaximumRounds,
            ConcernRoundsUsed: facts.Counters.ConcernRounds, MaximumConcernRounds: facts.Settings.MaximumConcernRounds,
            InfrastructureRetryAvailable: facts.Gate.InfrastructureRetryAvailable,
            MaximumReissues: facts.Settings.MaximumReissues, DeliveredRounds: facts.DeliveredRounds);
        var now = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var run = new OrchestrationRunDto("run", "project", facts.Card.Key, 1, "leased",
            OrchestrationStage.ReviewDecision,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            facts.Counters.Reissues, now, now, null, []);
        var result = await new EngineAssembly::AgentStudio.OrchestratorEngine.ReviewDecisionOrchestratorLoop()
            .ExecuteAsync(run, default);
        using var output = JsonDocument.Parse(result.OutputJson);
        var transition = output.RootElement.GetProperty("transition").Deserialize<TransitionDecision>()!;
        Assert.Equal(expected, transition.Action);
        Assert.Equal(StageDecisionPolicy.ToStageAction(expected), result.Action);
        if (expected is TransitionAction.FindingRound or TransitionAction.ConcernRound)
            Assert.Equal(facts.Attempt.ReviewedDelivery, transition.ContinuationBase);
    }

    [Fact]
    public void Frozen_history_counts_only_the_chosen_concern_round()
    {
        var first = new StagePayload(ReviewOutcome: "Pass", ReviewAttemptId: "one",
            Verdicts: [new("requirements", "concerns", "Review", "Fix src/feature.cs")],
            MaximumConcernRounds: 1);
        var second = first with { ReviewAttemptId = "two", ConcernRoundsUsed = 1 };
        Assert.Equal(1, StageDecisionPolicy.ConcernRoundsUsed([first, second]));
    }

    private static TransitionFacts Facts(string outcome, string? aspect = null, string? status = null,
        string? finding = null, string? classification = null)
    {
        ReviewFollowUpFinding[] findings = aspect is null ? [] : [new(aspect, status, "Review finding",
            Finding: finding, Classification: classification)];
        ReviewVerdictDto[] verdicts = aspect is null ? [] : [new(aspect, status!,
            classification ?? "Review", "Review finding", Missing: finding)];
        return new(new("card", "4-auto-review"), new("attempt", true, new("refs/results/delivery", "abc", "attempt")),
            new(outcome, findings, new ReviewPlanDto([], []), verdicts), new(), new(), new());
    }
}
