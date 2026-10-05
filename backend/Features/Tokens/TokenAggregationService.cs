

namespace AgentStudio.Tokens;

/// <summary>
/// Implementation of <see cref="ITokenAggregator"/>. Project and task-card
/// surfaces read a deduplicated union of the historical bus and current
/// durable task receipts (see <c>docs/system/domains/tokens.md</c>). The
/// pure-function folds still live on the legacy services
/// (<see cref="TokenSummaryService"/>, <see cref="WorkspaceTokensTimelineService"/>,
/// <see cref="ProjectTokenUsageService"/>) so the math is identical to the
/// pre-Phase-4 reader; the readers only swap the input source.
/// </summary>
/// <remarks>
/// <para>
/// Each bus-backed reader has a Phase-5 parity test
/// (<c>TokenSummaryBusParityTests</c>,
/// <c>WorkspaceTokensTimelineBusParityTests</c>,
/// <c>ProjectTokenUsageBusParityTests</c>) that drives both the legacy
/// reader and the bus reader over a fixed data set and asserts numeric
/// equality. The legacy services stay registered for historical-data
/// fallback and for the parity-test fixture, but
/// <see cref="ITokenAggregator"/> consumers never hit them directly.
/// </para>
/// </remarks>
public sealed class TokenAggregationService : ITokenAggregator
{
    private readonly BusAggregationCache _bus;
    private readonly IConfiguration _config;
    private readonly TokenSummaryCacheStore _summaryCache;
    private readonly BusBackedAdHocUsageReader _busAdHoc;
    private readonly BusBackedWorkspaceTimelineReader _busTimeline;
    private readonly BusBackedProjectTokenUsageReader _busProjectUsage;
    private readonly TokenPricingDriftMonitor? _pricingDrift;

    public TokenAggregationService(
        BusAggregationCache bus,
        IConfiguration config,
        TokenSummaryCacheStore summaryCache,
        BusBackedAdHocUsageReader busAdHoc,
        BusBackedWorkspaceTimelineReader busTimeline,
        BusBackedProjectTokenUsageReader busProjectUsage,
        TokenPricingDriftMonitor? pricingDrift = null)
    {
        _bus = bus;
        _config = config;
        _summaryCache = summaryCache;
        _busAdHoc = busAdHoc;
        _busTimeline = busTimeline;
        _busProjectUsage = busProjectUsage;
        _pricingDrift = pricingDrift;
    }

    public TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return new TokenAggregateResponse(
                Project: project,
                TotalMessages: 0,
                Since: since,
                Until: until,
                ByModel: Array.Empty<TokenAggregateBucket>(),
                ByParticipant: Array.Empty<TokenAggregateBucket>(),
                ByDay: Array.Empty<TokenAggregateBucket>(),
                Totals: new TokenAggregateTotals(0, 0, 0, 0, 0, null));
        }
        return _bus.Aggregate(workspace!, project, since, until, ct);
    }

    public ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null)
        => _busProjectUsage.BuildSummary(projectName, watchPath, nowUtc);

    public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
        => _busProjectUsage.BuildHeatmap(projectName, watchPath, days, nowUtc);

    public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit)
        => _busProjectUsage.BuildExpensiveJobs(projectName, watchPath, limit);

    public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId)
        => _busProjectUsage.BuildJobDetail(projectName, watchPath, jobId);

    public TokenSummary LifetimeSummary(string projectName, string watchPath)
    {
        var summary = _busProjectUsage.BuildLifetimeSummary(projectName, watchPath);
        _pricingDrift?.Observe(summary);
        return summary;
    }

    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects)
    {
        var summaries = projects
            .Select(project => (project.Name, _busProjectUsage.BuildLifetimeSummary(project.Name, project.WatchPath)))
            .ToList();
        foreach (var (_, summary) in summaries)
            _pricingDrift?.Observe(summary);
        return TokenSummaryService.AggregateSummaries(summaries, _summaryCache);
    }

    public TokenSummaryAggregate? CachedWorkspaceAggregate()
        => _summaryCache.Read();

    public Dictionary<string, TaskTokenSummary> WorkspacePerJob(string projectName, string watchPath)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(watchPath))
            return new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
        var resolvedProject = !string.IsNullOrWhiteSpace(projectName)
            ? projectName
            : ResolveProjectName(watchPath!) ?? Path.GetFileName(watchPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return _busProjectUsage.BuildPerJob(resolvedProject, watchPath);
    }

    public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null)
        => _busTimeline.Build(projects, windowHours, bucketMinutes, nowUtc);

    public TokenTimeline WorkspaceTimelineRange(IEnumerable<(string Name, string WatchPath)> projects, DateTime fromUtc, DateTime toUtc, int bucketMinutes)
        => _busTimeline.BuildRange(projects, fromUtc, toUtc, bucketMinutes);

    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null)
        => _busAdHoc.Aggregate(since);

    /// <summary>
    /// Best-effort lookup of the project slug for a watch path. Reads the
    /// <c>WatchedPaths</c> config the same way <see cref="TaskScannerService"/>
    /// does; falls back to the folder name if nothing matches.
    /// </summary>
    private string? ResolveProjectName(string watchPath)
    {
        var section = _config.GetSection("WatchedPaths");
        foreach (var child in section.GetChildren())
        {
            var path = child["Path"];
            var name = child["Name"];
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(path, watchPath, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }
}
