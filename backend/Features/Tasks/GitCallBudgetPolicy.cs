namespace AgentStudio.Tasks;

/// <summary>
/// Decides how long the next <c>git</c> invocation of one file-history lookup
/// may run.
///
/// <para>A lookup is not one git call. Reading the history of a file walks every
/// commit that touched it and spends three to four calls per commit
/// (<c>cat-file</c>, <c>show</c>, <c>show -s</c>), and the workspace variant adds
/// one per candidate lane path. Capping each call on its own therefore bounds
/// nothing: the request's worst case is the cap times a commit count the request
/// does not know in advance. On a loaded host that sum ran past the HTTP client's
/// own deadline, so the client gave up first and the server logged the request as
/// aborted rather than answering - the shape
/// <c>TaskFileHistoryEndpointsTests.CodeFileHistory_UsesProjectRepositoryWhenScopedToCode</c>
/// kept failing in (AGT-2867).</para>
///
/// <para>The fix is one deadline for the whole lookup, which makes the two clocks
/// ordered instead of racing: the server's budget is strictly the smaller, so a
/// slow host produces a reported git failure with a real status code, never a
/// client abort.</para>
/// </summary>
internal static class GitCallBudgetPolicy
{
    /// <summary>
    /// How long all git calls of a single lookup may take together. Well under
    /// the two minutes a caller is expected to allow, so the server always
    /// answers first.
    /// </summary>
    internal static readonly TimeSpan DefaultTotalBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling for any one call, so a single wedged <c>git</c> cannot consume the
    /// whole lookup's budget and starve the calls behind it.
    /// </summary>
    internal static readonly TimeSpan DefaultPerCallCap = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The timeout the next call gets: whatever is left of the total budget,
    /// never more than the per-call cap. <see cref="TimeSpan.Zero"/> means the
    /// budget is spent and the call must not be started at all, which keeps an
    /// exhausted lookup from queueing yet another process.
    /// </summary>
    internal static TimeSpan NextCallTimeout(
        TimeSpan elapsed,
        TimeSpan totalBudget,
        TimeSpan perCallCap)
    {
        var remaining = totalBudget - elapsed;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < perCallCap ? remaining : perCallCap;
    }
}

/// <summary>
/// The running total behind <see cref="GitCallBudgetPolicy"/>: one instance per
/// lookup, created at the entry point and passed down to every git call so they
/// all draw on the same deadline.
/// </summary>
internal sealed class GitCallBudget
{
    private readonly System.Diagnostics.Stopwatch _spent = System.Diagnostics.Stopwatch.StartNew();
    private readonly TimeSpan _totalBudget;
    private readonly TimeSpan _perCallCap;

    internal GitCallBudget(TimeSpan? totalBudget = null, TimeSpan? perCallCap = null)
    {
        _totalBudget = totalBudget ?? GitCallBudgetPolicy.DefaultTotalBudget;
        _perCallCap = perCallCap ?? GitCallBudgetPolicy.DefaultPerCallCap;
    }

    /// <summary>Timeout for the call about to be started; zero when spent.</summary>
    internal TimeSpan NextCallTimeout() =>
        GitCallBudgetPolicy.NextCallTimeout(_spent.Elapsed, _totalBudget, _perCallCap);

    /// <summary>
    /// True once no further git call may start. Callers report this instead of
    /// the answer they would otherwise infer from a missing git result: a lookup
    /// that ran out of budget before it could resolve the repository must not be
    /// reported as "not a git repository".
    /// </summary>
    internal bool IsExhausted => NextCallTimeout() <= TimeSpan.Zero;
}
