extern alias Monolith;
using System.Text.Json;
using AgentStudio.Orchestration.Decisions;
using AgentStudio.OrchestratorEngine;
using AgentStudio.Runner;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;
using MonolithAdapter = Monolith::AgentStudio.Runner.MonolithDecisionAdapter;

namespace OrchestratorEngine.Tests;

public sealed class DecisionGoldenTests
{
    [Theory]
    [InlineData("pass", TransitionAction.AcceptAndIntegrate)]
    [InlineData("concerns", TransitionAction.ConcernRound)]
    [InlineData("block", TransitionAction.FindingRound)]
    [InlineData("test-block", TransitionAction.FindingRound)]
    [InlineData("budget-spent", TransitionAction.DegradeWithFollowUp)]
    [InlineData("test-budget-spent", TransitionAction.Escalate)]
    [InlineData("concern-budget-spent", TransitionAction.DegradeWithFollowUp)]
    [InlineData("infra", TransitionAction.ReviewInfrastructureRetry)]
    [InlineData("infra-spent", TransitionAction.Escalate)]
    [InlineData("infra-with-block", TransitionAction.ReviewInfrastructureRetry)]
    [InlineData("branch-defect", TransitionAction.AcceptAndIntegrate)]
    [InlineData("batch", TransitionAction.DeferToBatch)]
    [InlineData("missing-build", TransitionAction.HumanReview)]
    public void Every_adapter_matches_the_same_review_table(string scenario, TransitionAction expected)
    {
        var facts = Facts(scenario);
        var decisions = new[] { MonolithAdapter.Review(facts), TaskServerDecisionAdapter.Review(facts), EngineDecisionAdapter.Review(facts) };
        foreach (var decision in decisions)
        {
            Assert.Equal(expected, decision.Action);
            Assert.NotEmpty(decision.Reason);
            Assert.Equal("card-1", decision.Receipt.CardId);
            Assert.Equal("review-1", decision.Receipt.AttemptId);
            Assert.Equal(expected == TransitionAction.DegradeWithFollowUp, decision.Receipt.RequiresLinkedFollowUp);
            Assert.Equal(expected, Assert.Single(decision.Candidates).Action);
            if (expected is TransitionAction.FindingRound or TransitionAction.ConcernRound)
            {
                Assert.Equal(facts.Attempt.ReviewedDelivery, decision.ContinuationBase);
                Assert.NotEmpty(decision.Findings);
            }
        }
        Assert.Equal(JsonSerializer.Serialize(decisions[0]), JsonSerializer.Serialize(decisions[1]));
        Assert.Equal(JsonSerializer.Serialize(decisions[0]), JsonSerializer.Serialize(decisions[2]));
    }

    [Theory]
    [InlineData("fix-rounds", false, false, 0, null, OperatorSweepAction.Act)]
    [InlineData("gate-triage", false, false, 0, "product", OperatorSweepAction.Act)]
    [InlineData("gate-triage", false, false, 0, "infrastructure", OperatorSweepAction.Hold)]
    [InlineData("gate-triage", false, false, 0, null, OperatorSweepAction.WaitForPerson)]
    [InlineData("salvage", false, false, 0, null, OperatorSweepAction.Act)]
    [InlineData("fix-rounds", true, false, 0, null, OperatorSweepAction.Hold)]
    [InlineData("fix-rounds", false, true, 0, null, OperatorSweepAction.Hold)]
    [InlineData("salvage", false, false, 4, null, OperatorSweepAction.WaitForPerson)]
    public void Every_adapter_matches_the_same_sweep_table(string sweep, bool paused, bool active,
        int rounds, string? domain, OperatorSweepAction expected)
    {
        var trigger = new OperatorSweepTrigger("subject-1", false, domain);
        var facts = new OperatorSweepGuardFacts(paused, true, active, false, new(rounds, 4));
        var monolith = MonolithAdapter.Sweep(sweep, trigger, facts);
        Assert.Equal(expected, monolith.Action);
        Assert.Equal(monolith, TaskServerDecisionAdapter.Sweep(sweep, trigger, facts));
        Assert.Equal(monolith, EngineDecisionAdapter.Sweep(sweep, trigger, facts));
        var transitionFacts = new SweepTransitionFacts("card-1", sweep, trigger, facts);
        var transition = MonolithAdapter.Sweep(transitionFacts);
        Assert.Equal("subject-1", transition.Receipt.AttemptId);
        Assert.Equal(rounds, transition.Receipt.RoundsUsed);
        Assert.Equal(JsonSerializer.Serialize(transition), JsonSerializer.Serialize(TaskServerDecisionAdapter.Sweep(transitionFacts)));
        Assert.Equal(JsonSerializer.Serialize(transition), JsonSerializer.Serialize(EngineDecisionAdapter.Sweep(transitionFacts)));
    }

    [Fact]
    public async Task Engine_stage_emits_the_shared_receipt_and_reviewed_base()
    {
        var facts = Facts("block");
        var run = new OrchestrationRunDto("run", "project", "card-1", 1, "leased",
            OrchestrationStage.ReviewDecision, JsonSerializer.Serialize(new { decisionFacts = facts }),
            0, DateTime.UnixEpoch, DateTime.UnixEpoch, null);
        var result = await new ReviewDecisionOrchestratorLoop().ExecuteAsync(run, default);
        Assert.Equal(OrchestrationAction.Reissue, result.Action);
        using var output = JsonDocument.Parse(result.OutputJson);
        var receipt = output.RootElement.GetProperty("transition").Deserialize<TransitionDecision>();
        Assert.Equal(TransitionAction.FindingRound, receipt!.Action);
        Assert.Equal(facts.Attempt.ReviewedDelivery, receipt.ContinuationBase);
    }

    [Theory]
    [InlineData("done", TransitionAction.CreateReview, "4-auto-review")]
    [InlineData("noop", TransitionAction.CreateReview, "4-auto-review")]
    [InlineData("needsinput", TransitionAction.Escalate, "5e-escalated")]
    [InlineData("blocked", TransitionAction.Escalate, "5e-escalated")]
    [InlineData("stopped", TransitionAction.ReturnToReady, "2-ready")]
    [InlineData("environmentfailure", TransitionAction.ReturnToReady, "2-ready")]
    public void Coding_completion_is_identical_through_every_adapter(string outcome, TransitionAction action, string lane)
    {
        var facts = new CodingCompletionFacts("card", "attempt", outcome);
        var monolith = MonolithAdapter.Coding(facts);
        Assert.Equal(action, monolith.Action);
        Assert.Equal(lane, monolith.TargetLane);
        Assert.Equal(JsonSerializer.Serialize(monolith), JsonSerializer.Serialize(TaskServerDecisionAdapter.Coding(facts)));
        Assert.Equal(JsonSerializer.Serialize(monolith), JsonSerializer.Serialize(EngineDecisionAdapter.Coding(facts)));
    }

    [Fact]
    public async Task Council_does_not_reopen_a_concern_after_the_shared_decision_spent_its_budget()
    {
        var facts = Facts("concerns");
        facts = facts with { Rounds = facts.Rounds with { ConcernRoundsUsed = 1 } };
        var decision = EngineDecisionAdapter.Review(facts);
        Assert.Equal(TransitionAction.AcceptAndIntegrate, decision.Action);
        var run = new OrchestrationRunDto("run", "project", "card", 1, "leased", OrchestrationStage.Council,
            """{"verdicts":[{"aspect":"requirement-fit","status":"concerns","summary":"Fix src/a.cs"}]}""",
            1, DateTime.UnixEpoch, DateTime.UnixEpoch, null,
            [new(1, OrchestrationStage.ReviewDecision, OrchestrationAction.Continue,
                JsonSerializer.Serialize(new { transition = decision }), DateTime.UnixEpoch)]);
        var result = await new CouncilLoop().ExecuteAsync(run, default);
        Assert.Equal(OrchestrationAction.Continue, result.Action);
    }

    internal static TransitionFacts Facts(string scenario)
    {
        var testBlock = scenario.StartsWith("test-", StringComparison.Ordinal);
        var block = scenario is "block" or "budget-spent" or "infra-with-block" || testBlock;
        var concern = scenario is "concerns" or "concern-budget-spent";
        var infra = scenario.StartsWith("infra", StringComparison.Ordinal);
        var verdicts = new List<ReviewVerdictDto>
        {
            new("build-tests", "pass", "Verified", "Build passed."),
        };
        if (block || concern)
            verdicts.Add(new(testBlock ? "tests-and-evidence" : "requirement-fit",
                block ? "block" : "concerns", "Finding", "Fix src/a.cs.", Missing: "Implement the missing branch."));
        if (scenario == "missing-build") verdicts.Clear();
        return new(new("card-1", "4-auto-review"),
            new("review-1", true, new("refs/heads/result", new string('a', 40), "review-1")),
            new(infra ? "ReviewInfra" : scenario == "branch-defect" ? "IntegrationBranchDefect" : block ? "ProductFailure" : "Pass",
                verdicts, new([], ["build-tests"], BuildTestDeferredToBatch: scenario == "batch")),
            new(new(scenario.EndsWith("budget-spent", StringComparison.Ordinal) ? 4 : 0, 4), new([])),
            new(), new(HasRetryBudget: scenario != "infra-spent"));
    }
}
