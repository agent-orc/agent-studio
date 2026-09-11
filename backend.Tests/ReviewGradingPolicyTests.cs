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
}
