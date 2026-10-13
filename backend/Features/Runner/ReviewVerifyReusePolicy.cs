namespace AgentStudio.Runner;

/// <summary>
/// Decides which terminal ReviewAttempt a freshly planned successor may take
/// passed verify results from (AGT-2841). A ReviewInfra outcome says nothing
/// about the build, test and lint commands that already passed, so the
/// successor names the failed attempt - and only that one. An older attempt of
/// the same chain is never named: a command it passed may have failed in the
/// direct predecessor, and that failure must run again. The executor carries a
/// result forward by re-storing it under the attempt that reused it, re-checks
/// the Result-SHA, the command line and its own toolchain before it reuses
/// anything, and executes whatever failed or did not run.
/// </summary>
public static class ReviewVerifyReusePolicy
{
    /// <summary>
    /// The failed attempt when it ended ReviewInfra. Empty otherwise: a product
    /// failure, a cancellation or an inconclusive review gets a plan that runs
    /// every command.
    /// </summary>
    public static IReadOnlyList<string> SourceAttemptIds(ReviewAttemptDto failed)
        => failed.Outcome == ReviewTerminalOutcome.InfrastructureFailure ? [failed.AttemptId] : [];
}
