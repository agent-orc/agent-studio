

namespace AgentStudio.Tokens;

/// <summary>
/// Phase-4 bus-backed read path for the workspace tokens timeline
/// (<c>GET /api/workspace/tokens</c>). For every (project, watchPath)
/// pair, queries the bus for that project's <c>kind=token-usage</c>
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

    public BusBackedWorkspaceTimelineReader(AgentMessageBusStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Build the workspace timeline view across every supplied project.
    /// Returns an empty timeline when the workspace root is not configured.
    /// </summary>
    public TokenTimeline Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return BuildFromStore(_store, workspaceRoot: "(unconfigured)", projects, windowHours, bucketMinutes, nowUtc);
        }
        return BuildFromStore(_store, workspace!, projects, windowHours, bucketMinutes, nowUtc);
    }

    /// <summary>Build a timeline for exact UTC calendar boundaries.</summary>
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

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }
}
