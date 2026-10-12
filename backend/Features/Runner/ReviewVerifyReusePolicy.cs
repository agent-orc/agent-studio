namespace AgentStudio.Runner;

/// <summary>
/// Decides which terminal ReviewAttempts a freshly planned successor may take
/// passed verify results from (AGT-2841). A ReviewInfra outcome says nothing
/// about the build, test and lint commands that already passed, so the
/// successor names the failed attempt and every earlier ReviewInfra attempt of
/// the same chain it carried results from. The executor still re-checks the
/// Result-SHA, the command line and its own toolchain before it reuses
/// anything, and executes whatever failed or did not run.
/// </summary>
public static class ReviewVerifyReusePolicy
{
    /// <summary>Bound on the walk back through linked attempts; the retry budget is three.</summary>
    public const int MaximumChainLength = 8;

    /// <summary>
    /// The failed attempt followed by its linked ReviewInfra predecessors for
    /// the same Result-SHA. Empty when the failed attempt is not ReviewInfra:
    /// a product failure, a cancellation or an inconclusive review gets a plan
    /// that runs every command.
    /// </summary>
    public static IReadOnlyList<string> SourceAttemptIds(
        ReviewAttemptDto failed,
        Func<string, ReviewAttemptDto?> findReview)
    {
        if (failed.Outcome != ReviewTerminalOutcome.InfrastructureFailure) return [];
        var sources = new List<string> { failed.AttemptId };
        var current = failed;
        while (sources.Count < MaximumChainLength
               && !string.IsNullOrWhiteSpace(current.SourceReviewAttemptId))
        {
            var previous = findReview(current.SourceReviewAttemptId);
            if (previous is not { Outcome: ReviewTerminalOutcome.InfrastructureFailure }
                || !string.Equals(
                    previous.Subject.ExpectedResultSha,
                    failed.Subject.ExpectedResultSha,
                    StringComparison.OrdinalIgnoreCase)
                || sources.Contains(previous.AttemptId, StringComparer.Ordinal))
                break;
            sources.Add(previous.AttemptId);
            current = previous;
        }
        return sources;
    }
}
