using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: attempt timestamps are inert policy inputs; nothing compares them to now.

/// <summary>
/// AGT-2841: which failed attempts a freshly planned review successor may take
/// passed verify results from. Only a ReviewInfra chain of the same
/// Result-SHA qualifies.
/// </summary>
public sealed class ReviewVerifyReusePolicyTests
{
    private const string Sha = "1111111111111111111111111111111111111111";

    [Theory]
    [InlineData(ReviewTerminalOutcome.InfrastructureFailure, true)]
    [InlineData(ReviewTerminalOutcome.ProductFailure, false)]
    [InlineData(ReviewTerminalOutcome.Inconclusive, false)]
    [InlineData(ReviewTerminalOutcome.Cancellation, false)]
    [InlineData(ReviewTerminalOutcome.IntegrationBranchDefect, false)]
    public void Only_a_review_infra_outcome_names_its_attempt(ReviewTerminalOutcome outcome, bool reusable)
    {
        var failed = Review("review-1", outcome);

        Assert.Equal(reusable ? ["review-1"] : [], ReviewVerifyReusePolicy.SourceAttemptIds(failed, _ => null));
    }

    [Fact]
    public void A_retry_chain_names_every_review_infra_attempt_of_the_same_result()
    {
        var reviews = new[]
        {
            Review("review-1", ReviewTerminalOutcome.InfrastructureFailure),
            Review("review-2", ReviewTerminalOutcome.InfrastructureFailure, source: "review-1"),
            Review("review-3", ReviewTerminalOutcome.InfrastructureFailure, source: "review-2"),
        }.ToDictionary(review => review.AttemptId);

        Assert.Equal(
            ["review-3", "review-2", "review-1"],
            ReviewVerifyReusePolicy.SourceAttemptIds(reviews["review-3"], reviews.GetValueOrDefault));
    }

    [Theory]
    [InlineData("product failure predecessor")]
    [InlineData("other result sha")]
    [InlineData("unknown predecessor")]
    public void The_chain_stops_at_a_predecessor_that_cannot_contribute(string variant)
    {
        var predecessor = variant switch
        {
            "product failure predecessor" => Review("review-1", ReviewTerminalOutcome.ProductFailure),
            "other result sha" => Review(
                "review-1", ReviewTerminalOutcome.InfrastructureFailure, sha: new string('2', 40)),
            "unknown predecessor" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        var failed = Review("review-2", ReviewTerminalOutcome.InfrastructureFailure, source: "review-1");

        Assert.Equal(
            ["review-2"],
            ReviewVerifyReusePolicy.SourceAttemptIds(failed, id => id == "review-1" ? predecessor : null));
    }

    [Fact]
    public void A_cyclic_link_does_not_loop()
    {
        var reviews = new[]
        {
            Review("review-1", ReviewTerminalOutcome.InfrastructureFailure, source: "review-2"),
            Review("review-2", ReviewTerminalOutcome.InfrastructureFailure, source: "review-1"),
        }.ToDictionary(review => review.AttemptId);

        Assert.Equal(
            ["review-2", "review-1"],
            ReviewVerifyReusePolicy.SourceAttemptIds(reviews["review-2"], reviews.GetValueOrDefault));
    }

    private static ReviewAttemptDto Review(
        string attemptId,
        ReviewTerminalOutcome outcome,
        string? source = null,
        string sha = Sha)
    {
        var at = new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc);
        return new ReviewAttemptDto(
            attemptId,
            "AGT-2841",
            "repo",
            "run-1",
            source,
            new ReviewSubjectDto("subject-1", "repo", sha, "run-1", "requirements", "policy", [], at),
            AttemptLifecycleState.Completed,
            null,
            1,
            1,
            at,
            at,
            outcome,
            null,
            sha,
            null,
            []);
    }
}
