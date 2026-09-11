using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2706 settled as <c>ProductFailure</c> on 2026-09-06 with every aspect at
/// <c>pass</c> and a single <c>documentation-impact</c> aspect at
/// <c>concerns</c>. These tests pin the table-driven fix.
/// </summary>
public sealed class ReviewGradingPolicyTests
{
    [Theory]
    [InlineData(new[] { "pass", "pass", "pass" }, ReviewGrade.Pass)]
    [InlineData(new[] { "pass", "concerns", "pass" }, ReviewGrade.PassWithConcerns)]
    [InlineData(new[] { "concerns", "concerns" }, ReviewGrade.PassWithConcerns)]
    [InlineData(new[] { "pass", "block", "pass" }, ReviewGrade.ProductFailure)]
    [InlineData(new[] { "pass", "concerns", "block" }, ReviewGrade.ProductFailure)]
    [InlineData(new[] { "fail" }, ReviewGrade.ProductFailure)]
    [InlineData(new[] { "blocked" }, ReviewGrade.ProductFailure)]
    [InlineData(new string[0], ReviewGrade.Pass)]
    public void Aspect_mapping_is_table_driven(string[] statuses, ReviewGrade expected)
        => Assert.Equal(expected, ReviewGradingPolicy.Grade(statuses));

    [Fact]
    public void Agt_2706_regression_every_aspect_pass_with_one_documentation_concern_does_not_fail_the_review()
    {
        var grade = ReviewGradingPolicy.Grade([
            "pass", "pass", "concerns", "pass",
        ]);

        Assert.Equal(ReviewGrade.PassWithConcerns, grade);
        Assert.NotEqual(ReviewGrade.ProductFailure, grade);
    }

    [Theory]
    [InlineData("block", true)]
    [InlineData("BLOCKED", true)]
    [InlineData("Fail", true)]
    [InlineData("pass", false)]
    [InlineData("concerns", false)]
    [InlineData(null, false)]
    public void IsBlockingToken_matches_case_insensitively(string? status, bool expected)
        => Assert.Equal(expected, ReviewGradingPolicy.IsBlockingToken(status));

    [Theory]
    [InlineData("concerns", true)]
    [InlineData("Concern", true)]
    [InlineData("pass", false)]
    [InlineData("block", false)]
    public void IsConcernToken_matches_case_insensitively(string? status, bool expected)
        => Assert.Equal(expected, ReviewGradingPolicy.IsConcernToken(status));

    [Fact]
    public void Semantic_block_without_cited_evidence_and_exact_gap_is_downgraded()
    {
        var request = new ReviewReportRequest(
            "executor", "instance", "lease", 1, "report", "ProductFailure",
            "ReviewFinding", "Documentation is allegedly missing.",
            new ReviewWorkspaceProofDto("repo", new string('a', 40), new string('a', 40),
                new string('b', 40), false, false, "workspace", "namespace"),
            new ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", ".NET",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [],
            [new ReviewVerdictDto(
                "documentation-impact",
                "block",
                "RemoteAspectVerdict",
                "Required documentation was not evidenced.")]);

        var normalized = ReviewVerdictCitationPolicy.NormalizeReport(
            request,
            ["documentation-impact"]);
        var verdict = Assert.Single(normalized.Verdicts);

        Assert.Equal("concerns", verdict.Status);
        Assert.Equal(ReviewVerdictCitationPolicy.BlockWithoutCitation, verdict.Classification);
        Assert.Equal("Pass", normalized.Outcome);
        Assert.Equal(ReviewVerdictCitationPolicy.BlockWithoutCitation, normalized.FailureClassification);
        Assert.Equal(ReviewGrade.PassWithConcerns,
            ReviewGradingPolicy.Grade(normalized.Verdicts.Select(item => item.Status)));
    }

    [Fact]
    public void Semantic_block_with_checked_evidence_and_named_gap_remains_blocking()
    {
        var verdict = new ReviewVerdictDto(
            "documentation-impact",
            "block",
            "RemoteAspectVerdict",
            "The review domain contract is absent.",
            "docs/system/domains/README.md; unified diff",
            "docs/system/domains/review.md");

        var normalized = ReviewVerdictCitationPolicy.Normalize(verdict, semanticAspect: true);

        Assert.Equal("block", normalized.Status);
        Assert.Equal(ReviewGrade.ProductFailure, ReviewGradingPolicy.Grade([normalized.Status]));
    }
}
