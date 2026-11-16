namespace AgentStudio.Tokens;

/// <summary>
/// The projection stage behind <see cref="ITokenAggregator"/>. Each surface
/// composes the same pipeline: read the merged project ledger
/// (<see cref="ProjectTokenLedger"/>), price it (<see cref="ITokenPricing"/>),
/// and fold it with a pure fold. Nothing here resolves model identity or
/// prices a call itself.
/// </summary>
public sealed class TokenAggregationService : ITokenAggregator
{
    private readonly BusAggregationCache _bus;
    private readonly IConfiguration _config;
    private readonly ProjectTokenLedger _ledger;
    private readonly JobStatsMetadataCache _jobs;
    private readonly BusAdHocUsageReader _adHoc;
    private readonly TokenSummaryCacheStore _summaryCache;
    private readonly ITokenPricing _pricing;
    private readonly TokenPricingDriftMonitor? _pricingDrift;
    private readonly AgentMessageBusStore? _workspaceBus;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;
    private readonly TaskScannerService? _scanner;

    public TokenAggregationService(
        BusAggregationCache bus,
        IConfiguration config,
        ProjectTokenLedger ledger,
        JobStatsMetadataCache jobs,
        BusAdHocUsageReader adHoc,
        TokenSummaryCacheStore summaryCache,
        ITokenPricing pricing,
        TokenPricingDriftMonitor? pricingDrift = null,
        AgentMessageBusStore? workspaceBus = null,
        AgentStudio.Registry.ProjectRegistry? projects = null,
        TaskScannerService? scanner = null)
    {
        _bus = bus;
        _config = config;
        _ledger = ledger;
        _jobs = jobs;
        _adHoc = adHoc;
        _summaryCache = summaryCache;
        _pricing = pricing;
        _pricingDrift = pricingDrift;
        _workspaceBus = workspaceBus;
        _projects = projects;
        _scanner = scanner;
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
        var workspaceRows = ReadWorkspaceUsage(DateTime.MinValue);
        var project = _projects?.List().FirstOrDefault(p => string.Equals(p.DisplayName, projectName, StringComparison.OrdinalIgnoreCase));
        var entries = project is null ? snapshot.Entries : workspaceRows?.MergeInto(project.Id, snapshot.Entries) ?? snapshot.Entries;
        return ProjectTokenUsageFold.Summary(projectName, entries, _jobs.JobsById(watchPath), nowUtc) with
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
        var summary = ProjectLifetimeSummary(projectName, watchPath, ReadWorkspaceUsage(DateTime.MinValue));
        _pricingDrift?.Observe(summary);
        return summary;
    }

    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects)
    {
        var selected = projects.ToList();
        var workspaceRows = ReadWorkspaceUsage(DateTime.MinValue);
        var summaries = selected
            .Select(project => (project.Name, ProjectLifetimeSummary(project.Name, project.WatchPath, workspaceRows)))
            .ToList();
        if (workspaceRows is { Unattributed.Count: > 0 } && CanShowUnattributed(selected))
            summaries.Add(("Unattributed one-shot jobs",
                TokenSummaryFold.ForProject("Unattributed one-shot jobs", _pricing.Price(workspaceRows.Unattributed))));
        foreach (var (_, summary) in summaries)
            _pricingDrift?.Observe(summary);
        var aggregate = TokenSummaryFold.ForWorkspace(summaries) with { Projects = selected.Count };
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
        var selected = projects.ToList();
        var workspaceUsage = ReadWorkspaceUsage(windowStart);
        var perProject = new List<(string Project, IReadOnlyList<PricedTokenEntry> Ledger)>();
        var freshness = new List<ProjectTokenDataFreshness>();
        foreach (var (name, watchPath) in selected)
        {
            var snapshot = _ledger.LoadSnapshot(name, watchPath);
            var project = _projects?.List().FirstOrDefault(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            var entries = project is null ? snapshot.Entries : workspaceUsage?.MergeInto(project.Id, snapshot.Entries) ?? snapshot.Entries;
            perProject.Add((name, _pricing.Price(InWindow(entries, windowStart, windowEnd))));
            freshness.Add(snapshot.Freshness);
        }

        if (workspaceUsage is not null && CanShowUnattributed(selected))
        {
            var unattributed = InWindow(workspaceUsage.Unattributed, windowStart, windowEnd);
            if (unattributed.Count > 0) perProject.Add((UnattributedProject, _pricing.Price(unattributed)));
        }

        return WorkspaceTimelineFold.Build(perProject, windowStart, windowEnd, b) with
        {
            Freshness = ProjectTokenDataFreshness.Combine(freshness),
            OneShotSources = OneShotSources(perProject),
        };
    }

    /// <summary>
    /// Exact UTC calendar interval over the merged project ledger (bus rows, task
    /// receipts, persona and chat fallback receipts), the same snapshot the
    /// trailing window and the usage cockpit cost read (AGT-3798).
    /// </summary>
    public TokenTimeline WorkspaceTimelineRange(IEnumerable<(string Name, string WatchPath)> projects, DateTime fromUtc, DateTime toUtc, int bucketMinutes,
        WorkspaceBusUsage? workspaceUsage = null)
    {
        var perProject = new List<(string Project, IReadOnlyList<PricedTokenEntry> Ledger)>();
        var freshness = new List<ProjectTokenDataFreshness>();
        foreach (var (name, watchPath) in projects)
        {
            var snapshot = _ledger.LoadSnapshot(name, watchPath);
            var entries = workspaceUsage?.MergeInto(name, snapshot.Entries) ?? snapshot.Entries;
            perProject.Add((name, _pricing.Price(InWindow(entries, fromUtc, toUtc))));
            freshness.Add(snapshot.Freshness);
        }
        if (workspaceUsage is not null)
        {
            var unattributed = InWindow(workspaceUsage.Unattributed, fromUtc, toUtc);
            if (unattributed.Count > 0) perProject.Add((UnattributedProject, _pricing.Price(unattributed)));
            freshness.Add(new ProjectTokenDataFreshness { Sources = [WorkspaceBusUsage.Source] });
        }

        return WorkspaceTimelineFold.Build(perProject, fromUtc, toUtc, bucketMinutes) with
        {
            Freshness = ProjectTokenDataFreshness.Combine(freshness),
            OneShotSources = OneShotSources(perProject),
        };
    }

    /// <summary>Row name of projectless workspace usage, as in the usage cockpit cost.</summary>
    public const string UnattributedProject = "Unattributed one-shot jobs";

    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null)
    {
        var (records, source) = _adHoc.Read(since);
        var priced = records
            .Select(record => (record, _pricing.Estimate(record.Model, record.InputTokens, record.OutputTokens,
                record.CacheReadTokens, record.CacheCreationTokens, record.Ts)))
            .ToList();
        return AdHocUsageFold.Aggregate(priced, source);
    }

    private TokenSummary ProjectLifetimeSummary(string projectName, string watchPath, WorkspaceBusUsage? workspaceRows = null)
    {
        var snapshot = _ledger.LoadSnapshot(projectName, watchPath);
        var project = _projects?.List().FirstOrDefault(p => string.Equals(p.DisplayName, projectName, StringComparison.OrdinalIgnoreCase));
        var entries = project is null ? snapshot.Entries : workspaceRows?.MergeInto(project.Id, snapshot.Entries) ?? snapshot.Entries;
        return TokenSummaryFold.ForProject(projectName, _pricing.Price(entries));
    }

    private WorkspaceBusUsage? ReadWorkspaceUsage(DateTime sinceUtc)
    {
        var root = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root) || _workspaceBus is null || _projects is null || _scanner is null) return null;
        var allProjects = _projects.List();
        return WorkspaceBusUsage.Read(_workspaceBus, root, allProjects, _scanner, sinceUtc);
    }

    private bool CanShowUnattributed(IReadOnlyList<(string Name, string WatchPath)> selected)
        => _scanner is not null && _scanner.GetWatchPaths().All(project => selected.Any(s =>
            string.Equals(s.Name, project.Name, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<TokenTimelineOneShotSource> OneShotSources(
        IEnumerable<(string Project, IReadOnlyList<PricedTokenEntry> Ledger)> projects)
        => projects.SelectMany(project => project.Ledger)
            .Where(row => row.Usage is not null && TokenUsageKindPolicy.ForLedger(
                row.Entry.ParticipantId, row.Entry.Topic, true) == TokenUsageKinds.Enrichment)
            .GroupBy(row => row.Entry.Topic?.Trim() is { Length: > 0 } topic ? topic : "unknown",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new TokenTimelineOneShotSource(group.Key, group.Count(),
                group.Sum(row => (long)row.Usage!.InputTokens + row.Usage.OutputTokens
                    + row.Usage.CacheReadTokens + row.Usage.CacheCreationTokens),
                group.Sum(row => row.Cost?.ModelKnown == true ? row.Cost.Total : 0)))
            .OrderByDescending(row => row.CostUsd).ToList();

    private IReadOnlyList<PricedTokenEntry> PricedLedger(string projectName, string watchPath)
        => _pricing.Price(_ledger.LoadSnapshot(projectName, watchPath).Entries);

    private static IReadOnlyList<OrchestratorLogEntry> InWindow(
        IReadOnlyList<OrchestratorLogEntry> entries, DateTime start, DateTime end)
        => entries.Where(entry => entry.Ts.ToUniversalTime() >= start
            && entry.Ts.ToUniversalTime() < end).ToList();

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
