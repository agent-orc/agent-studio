namespace AgentStudio.Runner;

/// <summary>
/// Pure decision rule for the completed-push backstop (AGT-2761): given what
/// git already knows about a commit's reachability, decide whether the
/// backstop should attempt a push, wait out a backoff window, or leave the
/// commit alone permanently. No I/O; <see cref="TaskTransitionService"/>
/// resolves the ancestor sets and applies the resulting side effects.
/// </summary>
public static class CompletedPushSelectionPolicy
{
    /// <summary>
    /// Wait applied after the 1st, 2nd, and 3rd non-fast-forward rejection.
    /// The 4th rejection (<see cref="MaxRejectionAttempts"/>) goes straight to
    /// <see cref="CommitPushStatuses.Rejected"/> instead of scheduling a
    /// further wait, so a permanently diverged remote stops generating retries
    /// rather than backing off indefinitely.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RejectionBackoff =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>Rejection count at which a commit becomes permanently terminal.</summary>
    public const int MaxRejectionAttempts = 4;

    public enum Action
    {
        /// <summary>Already an ancestor of the remote target branch; nothing to do.</summary>
        AlreadyRemote,
        /// <summary>Not reachable from the card's integrated result or the remote; permanently skipped.</summary>
        Superseded,
        /// <summary>Previously rejected as non-fast-forward through the full backoff; permanently skipped.</summary>
        Terminal,
        /// <summary>Backing off after a non-fast-forward rejection; not yet due for retry.</summary>
        WaitingBackoff,
        /// <summary>Eligible: attempt the push.</summary>
        Attempt,
    }

    /// <summary>
    /// Classifies one commit. <paramref name="isAncestorOfIntegration"/> is
    /// null when no integration signal is available (no computed integration
    /// verdict and no reviewed-result SHA) - the pre-fix behaviour of
    /// attempting every pending commit unconditionally applies in that case,
    /// since there is nothing to prove the commit is superseded.
    /// </summary>
    public static Action Classify(
        TaskCommitInfo commit,
        bool isAncestorOfRemote,
        bool? isAncestorOfIntegration,
        DateTimeOffset nowUtc)
    {
        if (isAncestorOfRemote) return Action.AlreadyRemote;
        if (string.Equals(commit.PushStatus, CommitPushStatuses.Rejected, StringComparison.Ordinal))
            return Action.Terminal;
        if (string.Equals(commit.PushStatus, CommitPushStatuses.Superseded, StringComparison.Ordinal))
            return Action.Superseded;
        if (isAncestorOfIntegration == false) return Action.Superseded;
        if (commit.PushNextRetryAtUtc is { } next && next > nowUtc.UtcDateTime) return Action.WaitingBackoff;
        return Action.Attempt;
    }

    /// <summary>
    /// The bookkeeping to persist after a non-fast-forward rejection. Returns
    /// a terminal <see cref="CommitPushOutcome"/> once <see cref="MaxRejectionAttempts"/>
    /// is reached; otherwise a backoff outcome scheduling the next retry.
    /// </summary>
    public static CommitPushOutcome NextRejectionOutcome(int priorAttempts, DateTimeOffset nowUtc, string? error)
    {
        var attempts = priorAttempts + 1;
        if (attempts >= MaxRejectionAttempts)
            return CommitPushOutcome.Rejected(attempts, error);

        var delay = RejectionBackoff[attempts - 1];
        return CommitPushOutcome.Backoff(attempts, nowUtc.UtcDateTime + delay, error);
    }
}

/// <summary>
/// Outcome of one card's completed-push attempt, aggregated by
/// <see cref="CompletedPushBackstopHostedService"/> into its one-line-per-cycle
/// summary.
/// </summary>
public sealed record CompletedPushCommitsResult(
    int Pushed,
    int SkippedSuperseded,
    int Rejected,
    bool CardSkippedIntegrated)
{
    public static readonly CompletedPushCommitsResult Empty = new(0, 0, 0, false);
}
