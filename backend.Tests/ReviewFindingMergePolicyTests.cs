using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewFindingMergePolicyTests
{
    [Fact]
    public void Ordinary_aspects_with_identical_summaries_remain_distinct_in_stage_history()
    {
        var verdicts = new[]
        {
            new ReviewVerdictDto("tests-and-evidence", "block", "quality:block", "Missing coverage"),
            new ReviewVerdictDto("code-quality", "block", "quality:block", "Missing coverage"),
        };
        Assert.Equal(2, ReviewFindingMergePolicy.MergeVerdicts(verdicts).Count);
        var payload = new StagePayload(ReviewOutcome: ReviewOutcomeKinds.ProductFailure,
            ReviewAttemptId: "review-1", Verdicts: verdicts);
        Assert.Equal(["tests-and-evidence", "code-quality"],
            Assert.Single(StageDecisionPolicy.DeliveredHistory([payload]).Rounds).BlockingAspects);
    }

    [Fact]
    public void Cited_deep_finding_merges_with_semantic_aspect_but_preserves_test_gate()
    {
        var findings = ReviewFindingMergePolicy.Merge([
            new ReviewFollowUpFinding("code-quality", "concerns", "Race", "backend/A.cs", "Missing fence"),
            new ReviewFollowUpFinding("deep-review", "block", "Unsafe write", "backend/A.cs", "Missing fence"),
            new ReviewFollowUpFinding("tests-and-evidence", "block", "Missing test", "backend/A.cs", "Missing fence"),
        ]);
        Assert.Equal(["deep-review", "tests-and-evidence"],
            ReviewRoundBudgetPolicy.CountedBlockingAspects(findings));
    }
}
