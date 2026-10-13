namespace AgentStudio.Tokens;

/// <summary>
/// The projection stage behind <see cref="ITokenAggregator"/>. Each surface
/// composes the same pipeline: read the merged project ledger
/// (<see cref="ProjectTokenLedger"/>), price it (<see cref="TokenPricing"/>),
/// and fold it with a pure fold. Nothing here resolves model identity or
/// prices a call itself.
/// </summary>
public sealed class TokenAggregationService : ITokenAggregator
{
    private readonly BusAggregationCache _bus;
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly ProjectTokenLedger _ledger;
    private readonly JobStatsMetadataCache _jobs;
    private readonly BusAdHocUsageReader _adHoc;
    private readonly TokenSummaryCacheStore _summaryCache;
    private readonly ITokenPricing _pricing;
    private readonly TokenPricingDriftMonitor? _pricingDrift;

    public TokenAggregationService(
        BusAggregationCache bus,
        AgentMessageBusStore store,
        IConfiguration config,
        ProjectTokenLedger ledger,
        JobStatsMetadataCache jobs,
        BusAdHocUsageReader adHoc,
        TokenSummaryCacheStore summaryCache,
        ITokenPricing pricing,
        TokenPricingDriftMonitor? pricingDrift = null)
    {
        _bus = bus;
        _store = store;
        _config = config;
        _ledger = ledger;
        _jobs = jobs;
        _adHoc = adHoc;
        _summaryCache = summaryCache;
        _pricing = pricing;
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
    {
        var snapshot = _ledger.LoadSnapshot(projectName, watchPath);
        return ProjectTokenUsageFold.Summary(projectName, snapshot.Entries, _jobs.JobsById(watchPath), nowUtc) with
        {
            Freshness = snapshot.Freshness,
        };
    }

    public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
    {
        var snapshot = _ledger.LoadSnapshot(projectName, watchPath);
        return ProjectTokenUsageFold.Heatmap(projectName, snapshot.Entries, _jobs.JobsById(watchPath), days, nowUtc) with
        {
            Freshness = snapshot.Freshness,
        };
    }

    public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit)
        => ProjectTokenJobFold.ExpensiveJobs(
            _ledger.LoadSnapshot(projectName, watchPath).Entries, _jobs.JobsById(watchPath), limit);

    public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        return ProjectTokenJobFold.JobDetail(
            projectName, _ledger.LoadSnapshot(projectName, watchPath).Entries, _jobs.JobsById(watchPath), jobId);
    }

    public TokenSummary LifetimeSummary(string projectName, string watchPath)
    {
        var summary = ProjectLifetimeSummary(projectName, watchPath);
        _pricingDrift?.Observe(summary);
        return summary;
    }

    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects)
    {
        var summaries = projects
            .Select(project => (project.Name, ProjectLifetimeSummary(project.Name, project.WatchPath)))
            .ToList();
        foreach (var (_, summary) in summaries)
            _pricingDrift?.Observe(summary);
        var aggregate = TokenSummaryFold.ForWorkspace(summaries);
        _summaryCache.Write(aggregate);
        return aggregate;
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
        return TaskTokenSummaryFold.PerJob(PricedLedger(resolvedProject, watchPath));
    }

    /// <summary>
    /// One merged ledger per project, folded into the trailing window. Per-project
    /// read health rolls up into <see cref="TokenTimeline.Freshness"/> so a failed
    /// source reads as partial instead of an unexplained zero.
    /// </summary>
    public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null)
    {
        var (windowStart, windowEnd, b) = WorkspaceTimelineFold.TrailingWindow(windowHours, bucketMinutes, nowUtc);
        var perProject = new List<(string Project, IReadOnlyList<PricedTokenEntry> Ledger)>();
        var freshness = new List<ProjectTokenDataFreshness>();
        foreach (var (name, watchPath) in projects)
        {
            var snapshot = _ledger.LoadSnapshot(name, watchPath);
            perProject.Add((name, _pricing.Price(snapshot.Entries)));
            freshness.Add(snapshot.Freshness);
        }

        return WorkspaceTimelineFold.Build(perProject, windowStart, windowEnd, b) with
        {
            Freshness = ProjectTokenDataFreshness.Combine(freshness),
        };
    }

    /// <summary>
    /// Exact UTC calendar interval. This range view reads the token bus only;
    /// it does not merge task receipts.
    /// </summary>
    public TokenTimeline WorkspaceTimelineRange(IEnumerable<(string Name, string WatchPath)> projects, DateTime fromUtc, DateTime toUtc, int bucketMinutes)
    {
        var workspaceRoot = _config["TaskRepository"] ?? "(unconfigured)";
        var perProject = projects
            .Select(project => (project.Name, _pricing.Price(
                BusTokenEntryConverter.LoadTokenUsageEntries(_store, workspaceRoot, project.Name))))
            .ToList();
        return WorkspaceTimelineFold.Build(perProject, fromUtc, toUtc, bucketMinutes);
    }

    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null)
    {
        var (records, source) = _adHoc.Read(since);
        var priced = records
            .Select(record => (record, _pricing.Estimate(record.Model, record.InputTokens, record.OutputTokens,
                record.CacheReadTokens, record.CacheCreationTokens, record.Ts)))
            .ToList();
        return AdHocUsageFold.Aggregate(priced, source);
    }

    private TokenSummary ProjectLifetimeSummary(string projectName, string watchPath)
        => TokenSummaryFold.ForProject(projectName, PricedLedger(projectName, watchPath));

    private IReadOnlyList<PricedTokenEntry> PricedLedger(string projectName, string watchPath)
        => _pricing.Price(_ledger.LoadSnapshot(projectName, watchPath).Entries);

    /// <summary>
    /// Project name for a watch path from the <c>WatchedPaths</c> config, the
    /// same lookup <see cref="TaskScannerService"/> uses.
    /// </summary>
    private string? ResolveProjectName(string watchPath)
    {
        foreach (var child in _config.GetSection("WatchedPaths").GetChildren())
        {
            var path = child["Path"];
            var name = child["Name"];
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(path, watchPath, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }
}
