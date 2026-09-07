namespace AgentStudio.Watcher;

/// <summary>
/// What the Watcher is doing right now. Exposed over HTTP and pushed onto the
/// bus as the heartbeat an independent health monitor reads.
/// </summary>
public sealed record WatcherSnapshot
{
    public bool Enabled { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? LastRunFailedAtUtc { get; init; }
    /// <summary>Sweeps completed since the process started.</summary>
    public long Sweeps { get; init; }
    public int SignalsCollected { get; init; }
    public int FindingsDetected { get; init; }
    public int OpenCases { get; init; }
    public int PendingProposals { get; init; }
    /// <summary>Persistent cases waiting because the contingent is exhausted.</summary>
    public int BacklogCases { get; init; }
    public int ProposalsCreatedLastRun { get; init; }
    public int CommentsAppendedLastRun { get; init; }
    public int ModelCallsLastRun { get; init; }
    public int ResolvedLastRun { get; init; }
    public long LastRunElapsedMs { get; init; }
    /// <summary>Set when the last sweep threw; cleared by the next clean sweep.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// True when the last sweep is older than the misses threshold times the
    /// configured interval. The Watcher does not judge its own health; this is
    /// the fact an independent monitor evaluates.
    /// </summary>
    public bool IsStale(DateTime nowUtc, TimeSpan interval, int missesBeforeUnavailable)
    {
        if (!Enabled) return false;
        if (LastRunAtUtc is null) return false;
        return nowUtc - LastRunAtUtc.Value > interval * Math.Max(1, missesBeforeUnavailable);
    }
}
