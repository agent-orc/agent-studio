using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeepReviewDecisionTests
{
    private static DeepReviewGrounds Facts(string risk = "routine", string[]? paths = null,
        int lines = 20, int blocks = 0, int rounds = 0, int gates = 0,
        string quota = "available")
        => new("AGT-1", "0123456789abcdef", risk, paths ?? ["docs/readme.md"], lines,
            "gpt-6-sol", "medium", blocks, rounds, 4, gates, quota);

    [Theory]
    [InlineData("routine", "docs/readme.md", 20, 0, 0, "available", false, "medium")]
    [InlineData("routine", "backend/Security/Auth.cs", 20, 0, 0, "available", true, "xhigh")]
    [InlineData("critical", "docs/readme.md", 20, 0, 0, "available", true, "xhigh")]
    [InlineData("routine", "docs/readme.md", 600, 0, 0, "available", true, "medium")]
    [InlineData("routine", "docs/readme.md", 20, 1, 0, "available", true, "medium")]
    [InlineData("routine", "docs/readme.md", 20, 0, 1, "available", true, "medium")]
    [InlineData("routine", "docs/readme.md", 600, 0, 0, "capped", false, "medium")]
    [InlineData("critical", "docs/readme.md", 20, 0, 0, "capped", true, "xhigh")]
    public void Selection_uses_risk_history_size_and_quota(string risk, string path,
        int lines, int blocks, int gates, string quota, bool selected, string effort)
    {
        var decision = DeepReviewPolicy.Decide(
            Facts(risk, [path], lines, blocks, gates: gates, quota: quota), DeepReviewPolicy.Active);
        Assert.Equal(selected, decision.Selected);
        Assert.Equal(selected, decision.Dispatched);
        Assert.Equal(effort, decision.ThinkingLevel);
    }

    [Fact]
    public void Shadow_receipt_is_stable_and_does_not_dispatch()
    {
        var facts = Facts(paths: ["backend/Security/Auth.cs"]);
        var first = DeepReviewPolicy.Decide(facts, DeepReviewPolicy.Shadow, "rule-1");
        Assert.Equal(first.ReceiptId,
            DeepReviewPolicy.Decide(facts, DeepReviewPolicy.Shadow, "rule-1").ReceiptId);
        Assert.True(first.Selected);
        Assert.False(first.Dispatched);
        Assert.Contains("critical-touched-area", first.Grounds);
        Assert.NotEqual(first.ReceiptId, DeepReviewPolicy.Decide(
            facts with { PreviousBlocks = 1 }, DeepReviewPolicy.Shadow, "rule-1").ReceiptId);
    }

    [Fact]
    public void Rule_cannot_waive_a_critical_floor_and_three_delivered_rounds_stop_the_loop()
    {
        var critical = DeepReviewPolicy.Decide(Facts(risk: "critical"), DeepReviewPolicy.Active,
            ruleSelection: false, ruleModel: "gpt-6-luna", ruleThinkingLevel: "medium");
        Assert.True(critical.Dispatched);
        Assert.Equal("gpt-6-sol", critical.Model);
        Assert.Equal("xhigh", critical.ThinkingLevel);
        Assert.Equal("medium", DeepReviewPolicy.Decide(Facts(lines: 600), DeepReviewPolicy.Active,
            ruleThinkingLevel: "xhigh").ThinkingLevel);
        var capped = DeepReviewPolicy.Decide(Facts(risk: "critical", rounds: 3), DeepReviewPolicy.Active);
        Assert.False(capped.Dispatched);
        Assert.Contains("deep-round-limit", capped.Grounds);
    }

    [Fact]
    public void Aspect_and_deep_review_citing_the_same_defect_create_one_fix_item()
    {
        var findings = ReviewFindingMergePolicy.Merge([
            new ReviewFollowUpFinding("code-quality", "concerns", "Possible race", "backend/A.cs", "Missing fence"),
            new ReviewFollowUpFinding("deep-review", "block", "Unsafe concurrent write", "backend/A.cs", "Missing fence"),
            new ReviewFollowUpFinding("tests-and-evidence", "block", "No test", "backend/A.Tests.cs", "Missing test"),
        ]);
        Assert.Equal(2, findings.Count);
        Assert.Equal("deep-review", findings[0].Aspect);
        Assert.Equal(ReviewFollowUpAction.ReviewFindingRound,
            ReviewFollowUpPolicy.Decide(findings, 0, 1).Action);
    }

    [Fact]
    public void Third_deep_block_escalates_instead_of_opening_another_fix_round()
    {
        var finding = new ReviewFollowUpFinding("deep-review", "block", "Missing fence",
            "backend/Lease.cs", "Missing fence");
        var key = ReviewFindingMergePolicy.Fingerprint(finding);
        var prior = new ReviewRoundBudgetLedger([
            new DeliveredReviewRound("one", ["deep-review"], [], BlockingFindingKeys: [key]),
            new DeliveredReviewRound("two", ["deep-review"], [], BlockingFindingKeys: [key]),
        ]);
        var decision = ReviewBudgetPolicy.Decide(new ReviewBudgetFacts(
            ReviewOutcomeKinds.ProductFailure,
            [finding],
            0, 1, new CardRoundBudgetState(2, 4), prior, "three", 4, 2, 1, 2));
        Assert.True(decision.Escalate);
        Assert.False(decision.ShouldDegrade);

        var different = ReviewBudgetPolicy.Decide(new ReviewBudgetFacts(
            ReviewOutcomeKinds.ProductFailure,
            [finding with { Finding = "Different defect" }],
            0, 1, new CardRoundBudgetState(2, 4), prior, "three", 4, 2, 1, 2));
        Assert.False(different.Escalate);
    }

    [Fact]
    public void Rules_editor_accepts_only_bounded_deep_review_choices()
    {
        var rule = new DecisionRuleV1("deep-size", DecisionRuleTriggers.DeepReview,
            "review.changed-lines", "500", [DecisionActionKinds.Prepare], "A1",
            "gpt-6-sol", "signal", ThinkingLevel: "medium");
        var models = DecisionRuleModelCatalogueSource.For("codex");
        Assert.True(DecisionRulePolicy.Validate([rule], false, models).Accepted);
        Assert.False(DecisionRulePolicy.Validate([rule with
        {
            CandidateActions = [DecisionActionKinds.Halt],
        }], false, models).Accepted);
        Assert.False(DecisionRulePolicy.Validate([rule with
        {
            ThinkingLevel = "low",
        }], false, models).Accepted);
    }

    [Fact]
    public void Remote_review_plan_carries_shadow_receipt_and_active_command()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deep-review-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "prompt.md"), "Preserve the authorization boundary.");
            var task = new TaskInfo { Id = "AGT-1", ProjectName = "demo", FolderPath = folder,
                Tags = ["p0"] };
            var prompts = new AgentStudio.Prompts.RuntimePromptService(new ConfigurationBuilder().Build(),
                NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance);
            var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
            var shadow = new RemoteReviewPlanBuilder(aspects, new ConfigurationBuilder().Build())
                .Build(task, null, null, "develop", "0123456789abcdef", ["backend/Auth.cs"]);
            Assert.True(shadow.DeepReviewDecision!.Selected);
            Assert.False(shadow.DeepReviewDecision.Dispatched);
            Assert.DoesNotContain(shadow.Commands, command => command.Aspect == "deep-review");

            var activeConfig = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DeepReview:Mode"] = "active" }).Build();
            var active = new RemoteReviewPlanBuilder(aspects, activeConfig)
                .Build(task, null, null, "develop", "0123456789abcdef", ["backend/Auth.cs"]);
            var command = Assert.Single(active.Commands, item => item.Aspect == "deep-review");
            Assert.Equal("gpt-6-sol", command.Model);
            Assert.Equal("xhigh", command.ThinkingLevel);
            Assert.Equal(ReviewCommandKinds.AgentAspect, command.ExecutionKind);
            Assert.Contains($"Decision receipt: {active.DeepReviewDecision!.ReceiptId}", command.Prompt);
            Assert.Contains("Review the immutable delivery for concrete defects", command.Prompt);
            Assert.Contains("deep-review", active.RequiredAspects);
            Assert.Equal("active", active.DeepReviewDecision!.Mode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
