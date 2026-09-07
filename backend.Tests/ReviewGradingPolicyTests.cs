using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests for the aspect-verdict to review-grade mapping. The
/// AGT-2706 row is the one that mattered on 2026-09-06: every aspect passed,
/// <c>documentation-impact</c> returned <c>concerns</c>, and the card still
/// settled as <c>ProductFailure</c>.
/// </summary>
public sealed class ReviewGradingPolicyTests
{
    public static TheoryData<string[], bool, ReviewGrade, string> Matrix() => new()
    {
        { [], false, ReviewGrade.Pass, ReviewOutcomes.Pass },
        { ["pass"], false, ReviewGrade.Pass, ReviewOutcomes.Pass },
        { ["pass", "pass", "pass"], false, ReviewGrade.Pass, ReviewOutcomes.Pass },
        // AGT-2706.
        { ["pass", "pass", "concerns"], false, ReviewGrade.PassWithConcerns, ReviewOutcomes.PassWithConcerns },
        { ["concerns"], false, ReviewGrade.PassWithConcerns, ReviewOutcomes.PassWithConcerns },
        { ["concern"], false, ReviewGrade.PassWithConcerns, ReviewOutcomes.PassWithConcerns },
        { ["pass", "block"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        { ["pass", "fail"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        { ["blocked"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        // A blocking verdict outranks a concern regardless of order.
        { ["concerns", "block"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        { ["block", "concerns"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        // A real failing verify command refuses the change on its own.
        { ["pass"], true, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        { ["concerns"], true, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
        // Casing and padding come off the wire unnormalized.
        { [" Concerns "], false, ReviewGrade.PassWithConcerns, ReviewOutcomes.PassWithConcerns },
        { ["BLOCK"], false, ReviewGrade.ProductFailure, ReviewOutcomes.ProductFailure },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void AspectVerdicts_GradeAndOutcomeAreTableDriven(
        string[] statuses,
        bool hasBlockingCommandFailure,
        ReviewGrade expectedGrade,
        string expectedOutcome)
    {
        var grade = ReviewGradingPolicy.Grade(statuses, hasBlockingCommandFailure);

        Assert.Equal(expectedGrade, grade);
        Assert.Equal(expectedOutcome, ReviewGradingPolicy.ToOutcome(grade));
        Assert.Equal(expectedOutcome, ReviewGradingPolicy.Outcome(statuses, hasBlockingCommandFailure));
    }

    [Fact]
    public void ConcernsAloneNeverProduceAProductFailure()
    {
        var outcome = ReviewGradingPolicy.Outcome(["pass", "concerns", "pass"]);

        Assert.NotEqual(ReviewOutcomes.ProductFailure, outcome);
        Assert.True(ReviewOutcomes.IsAccepting(outcome));
    }

    [Theory]
    [InlineData(ReviewOutcomes.Pass, true)]
    [InlineData(ReviewOutcomes.PassWithConcerns, true)]
    [InlineData("passwithconcerns", true)]
    [InlineData(ReviewOutcomes.ProductFailure, false)]
    [InlineData(ReviewOutcomes.ReviewInfra, false)]
    [InlineData(null, false)]
    public void IsAccepting_ClearsOnlyTheTwoPassingOutcomes(string? outcome, bool expected)
        => Assert.Equal(expected, ReviewOutcomes.IsAccepting(outcome));

    [Fact]
    public void UnknownVerdictToken_IsNeitherBlockingNorAConcern()
    {
        Assert.False(ReviewGradingPolicy.IsBlockingToken("maybe"));
        Assert.False(ReviewGradingPolicy.IsConcernToken("maybe"));
        Assert.Equal(ReviewGrade.Pass, ReviewGradingPolicy.Grade(["maybe"]));
    }
}
