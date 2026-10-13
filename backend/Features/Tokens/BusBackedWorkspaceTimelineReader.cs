namespace AgentStudio.Tokens;

/// <summary>
/// Read path for the workspace tokens timeline
/// (<c>GET /api/workspace/tokens/timeline</c>). Every project reads the
/// merged usage ledger (bus history plus durable task receipts, see
/// <see cref="BuildFromLedger"/>), so remote runner completions, remote
/// review attempts, and chat turns count alongside local runs (AGT-2986).
/// The entries fold through
/// <see cref="WorkspaceTokensTimelineService.BuildFromEntries"/>, which
/// splits each project's spend into Agent / Supporting / Orchestrator
/// subtotals off the participant prefix.
/// </summary>
public sealed class BusBackedWorkspaceTimelineReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly BusBackedProjectTokenUsageReader _ledger;

    public BusBackedWorkspaceTimelineReader(
        AgentMessageBusStore store,
        IConfiguration config,
        BusBackedProjectTokenUsageReader ledger)
    {
        _store = store;
        _config = config;
        _ledger = ledger;
    }

    /// <summary>
    /// Build the workspace timeline view across every supplied project.
    /// Every project reads the same deduplicated union of bus history and
    /// durable task receipts that the project and card surfaces read, so
    /// remote runner usage is part of the timeline (AGT-2986).
    /// </summary>
    public TokenTimeline Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
        => BuildFromLedger(_ledger, projects, windowHours, bucketMinutes, nowUtc);

    /// <summary>
    /// Build a timeline for exact UTC calendar boundaries. This range view
    /// reads the token bus only; it does not merge task receipts.
    /// </summary>
    public TokenTimeline BuildRange(
        IEnumerable<(string Name, string WatchPath)> projects,
        DateTime fromUtc,
        DateTime toUtc,
        int bucketMinutes)
    {
        var workspaceRoot = _config["TaskRepository"] ?? "(unconfigured)";
        var entries = projects.Select(p => (p.Name,
            (IReadOnlyList<OrchestratorLogEntry>)BusTokenEntryConverter.LoadTokenUsageEntries(_store, workspaceRoot, p.Name)))
            .ToList();
        return WorkspaceTokensTimelineService.BuildFromEntries(entries, fromUtc, toUtc, bucketMinutes);
    }

    /// <summary>
    /// Ledger read path: one merged snapshot per project (bus history plus
    /// task receipts, deduplicated by canonical token identity), folded by
    /// the shared bucketer. Per-project read health rolls up into
    /// <see cref="TokenTimeline.Freshness"/> so a failed source reads as
    /// partial instead of an unexplained zero.
    /// </summary>
    internal static TokenTimeline BuildFromLedger(
        BusBackedProjectTokenUsageReader ledger,
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        var (windowStart, windowEnd, b) = ResolveWindow(windowHours, bucketMinutes, nowUtc);
        var perProjectEntries = new List<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)>();
        var freshness = new List<ProjectTokenDataFreshness>();
        foreach (var (name, watchPath) in projects)
        {
            var snapshot = ledger.LoadSnapshot(name, watchPath);
            perProjectEntries.Add((name, snapshot.Entries));
            freshness.Add(snapshot.Freshness);
        }

        return WorkspaceTokensTimelineService.BuildFromEntries(perProjectEntries, windowStart, windowEnd, b) with
        {
            Freshness = CombineFreshness(freshness),
        };
    }

    internal static ProjectTokenDataFreshness CombineFreshness(IReadOnlyList<ProjectTokenDataFreshness> projects)
    {
        if (projects.Count == 0) return ProjectTokenDataFreshness.Empty;
        var status = projects.All(p => p.Status == "unavailable")
            ? "unavailable"
            : projects.Any(p => p.Status != "complete") ? "partial" : "complete";
        var warnings = projects
            .Select(p => p.Warning)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ProjectTokenDataFreshness
        {
            Status = status,
            AsOf = projects
                .Select(p => p.AsOf)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Max(StringComparer.Ordinal),
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            Sources = projects.SelectMany(p => p.Sources).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    private static (DateTime Start, DateTime End, int BucketMinutes) ResolveWindow(
        int windowHours, int bucketMinutes, DateTime? nowUtc)
    {
        var w = WorkspaceTokensTimelineService.ResolveWindowHours(windowHours);
        var b = WorkspaceTokensTimelineService.ResolveBucketMinutes(bucketMinutes);
        var windowEnd = AlignDown(nowUtc ?? DateTime.UtcNow, b);
        return (windowEnd.AddHours(-w), windowEnd, b);
    }

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }
}
