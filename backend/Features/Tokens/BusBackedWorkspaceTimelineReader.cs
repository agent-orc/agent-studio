

namespace AgentStudio.Tokens;

/// <summary>
/// Read path for the workspace tokens timeline
/// (<c>GET /api/workspace/tokens/timeline</c>). In production every project
/// reads the merged usage ledger (bus history plus durable task receipts,
/// see <see cref="BuildFromLedger"/>), so remote runner completions, remote
/// review attempts, and chat turns count alongside local runs (AGT-2986).
/// The bus-only projection below remains for the parity fixtures: for every
/// (project, watchPath) pair it queries the bus for that project's
/// <c>kind=token-usage</c>
/// messages across <b>every</b> participant (coding-agent runs, supporting
/// analysis loops, and orchestrator meta-turns), converts them into
/// transient <see cref="OrchestratorLogEntry"/> records, and folds them
/// through the pure-function bucketing in
/// <see cref="WorkspaceTokensTimelineService.BuildFromEntries"/>, which
/// splits each project's spend into Agent / Supporting / Orchestrator
/// subtotals off the participant prefix.
/// </summary>
/// <remarks>
/// <para>
/// The view used to load <see cref="BusTokenEntryConverter.LoadOrchestratorEntries"/>
/// (orchestrator participant only), so the nightly agent runs - the bulk
/// of the spend - never showed up and the per-project totals read far too
/// small (AGT-2038). It now loads
/// <see cref="BusTokenEntryConverter.LoadTokenUsageEntries"/> so the total
/// per project is the true sum, with the orchestrator share broken out
/// separately.
/// </para>
/// <para>
/// Reusing the shared bucketer keeps window snapping, bucket alignment,
/// per-bucket dollar accounting, and the per-project rollup ordering in
/// one place. The parity test
/// (<c>WorkspaceTokensTimelineBusParityTests</c>) drives both readers over
/// an orchestrator-only data set and asserts numeric equality for every
/// cell and every per-project total.
/// </para>
/// </remarks>
public sealed class BusBackedWorkspaceTimelineReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly BusBackedProjectTokenUsageReader? _ledger;

    public BusBackedWorkspaceTimelineReader(
        AgentMessageBusStore store,
        IConfiguration config,
        BusBackedProjectTokenUsageReader? ledger = null)
    {
        _store = store;
        _config = config;
        _ledger = ledger;
    }

    /// <summary>
    /// Build the workspace timeline view across every supplied project.
    /// With the project ledger wired (production), every project reads the
    /// same deduplicated union of bus history and durable task receipts that
    /// the project and card surfaces read, so remote runner usage is part of
    /// the timeline (AGT-2986). Without it (parity fixtures) the reader
    /// falls back to the bus-only projection.
    /// </summary>
    public TokenTimeline Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        if (_ledger is not null)
            return BuildFromLedger(_ledger, projects, windowHours, bucketMinutes, nowUtc);

        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return BuildFromStore(_store, workspaceRoot: "(unconfigured)", projects, windowHours, bucketMinutes, nowUtc);
        }
        return BuildFromStore(_store, workspace!, projects, windowHours, bucketMinutes, nowUtc);
    }

    /// <summary>
    /// Pure overload used by the parity test. The window math lives in
    /// <see cref="WorkspaceTokensTimelineService.BuildFromEntries"/> so
    /// the bus path cannot disagree with the legacy reader on snapping,
    /// bucket span, or the empty-bucket-count derivation.
    /// </summary>
    public static TokenTimeline BuildFromStore(
        AgentMessageBusStore store,
        string workspaceRoot,
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        var w = WorkspaceTokensTimelineService.ResolveWindowHours(windowHours);
        var b = WorkspaceTokensTimelineService.ResolveBucketMinutes(bucketMinutes);
        var now = nowUtc ?? DateTime.UtcNow;
        var windowEnd = AlignDown(now, b);
        var windowStart = windowEnd.AddHours(-w);

        var perProjectEntries = new List<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)>();
        foreach (var (name, _) in projects)
        {
            // All participants, not just the orchestrator: agent runs and
            // supporting loops are the bulk of a project's spend and must
            // be part of the workspace total (AGT-2038). Participant ids ride
            // along so BuildFromEntries can split Agent/Supporting/Orchestrator.
            var entries = BusTokenEntryConverter.LoadTokenUsageEntries(store, workspaceRoot, name);
            perProjectEntries.Add((name, entries));
        }

        return WorkspaceTokensTimelineService.BuildFromEntries(perProjectEntries, windowStart, windowEnd, b);
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
