using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: attempt timestamps are inert policy inputs; nothing compares them to now.

/// <summary>
/// AGT-2841: which failed attempt a freshly planned review successor may take
/// passed verify results from. Only a ReviewInfra outcome qualifies, and only
/// the failed attempt itself - never an older attempt of the retry chain.
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

        Assert.Equal(reusable ? ["review-1"] : [], ReviewVerifyReusePolicy.SourceAttemptIds(failed));
    }

    [Fact]
    public void A_retry_chain_names_only_the_direct_predecessor()
    {
        // review-1 may have passed a command that failed in review-2; a pass of
        // review-1 must not stand in for that failure in review-3.
        var failed = Review("review-2", ReviewTerminalOutcome.InfrastructureFailure, source: "review-1");

        Assert.Equal(["review-2"], ReviewVerifyReusePolicy.SourceAttemptIds(failed));
    }

    private static ReviewAttemptDto Review(
        string attemptId,
        ReviewTerminalOutcome outcome,
        string? source = null)
    {
        var at = new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc);
        return new ReviewAttemptDto(
            attemptId,
            "AGT-2841",
            "repo",
            "run-1",
            source,
            new ReviewSubjectDto("subject-1", "repo", Sha, "run-1", "requirements", "policy", [], at),
            AttemptLifecycleState.Completed,
            null,
            1,
            1,
            at,
            at,
            outcome,
            null,
            Sha,
            null,
            []);
    }
}
