namespace AgentStudio.Git;

/// <summary>
/// What a request path is allowed to say about git-derived state: when the
/// background index last completed, and whether a newer run is already known
/// to be needed. Reading it costs a dictionary lookup - no git process, no
/// wait on the indexer.
/// </summary>
/// <param name="GitStateAt">
/// Completion time of the newest snapshot the answer is built from, or null
/// while the index is still warming.
/// </param>
/// <param name="Stale">
/// True when a change has been observed that this snapshot does not contain
/// yet. The board renders the stamp quietly and updates through the existing
/// SignalR push once the index catches up.
/// </param>
public sealed record GitStateStamp(DateTimeOffset? GitStateAt, bool Stale)
{
    /// <summary>Nothing indexed yet: no stamp, and honestly stale.</summary>
    public static GitStateStamp Warming { get; } = new(null, true);

    /// <summary>
    /// Board reads span every registered repository, so the honest stamp is
    /// the OLDEST completed run (the weakest link) and stale if any repository
    /// is stale. A per-repository breakdown lives on the Admin telemetry page;
    /// a card list only needs one truthful "as of".
    /// </summary>
    public static GitStateStamp Merge(IEnumerable<GitStateStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        DateTimeOffset? oldest = null;
        var stale = false;
        var any = false;
        foreach (var stamp in stamps)
        {
            any = true;
            stale |= stamp.Stale;
            if (stamp.GitStateAt is null)
            {
                // A repository that never completed a run has no "as of" to
                // contribute; it is reported through Stale instead. Treating
                // it as time zero would make the stamp permanently useless.
                stale = true;
                continue;
            }
            if (oldest is null || stamp.GitStateAt < oldest) oldest = stamp.GitStateAt;
        }
        return any ? new GitStateStamp(oldest, stale) : Warming;
    }
}
