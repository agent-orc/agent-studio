namespace AgentStudio.Tokens;

/// <summary>
/// Pure fold from per-project priced usage ledgers to the workspace token
/// timeline: (project, time bucket) cells, per-project totals with the
/// Agent / Supporting / Orchestrator split and executing hosts, and
/// (project, model, host) rows. Entries outside the window are dropped.
/// The frontend renders the result at <c>#/workspace/tokens</c>.
/// </summary>
public static class WorkspaceTimelineFold
{
    private static readonly int[] AllowedWindowHours = [1, 6, 24, 168];
    private static readonly int[] AllowedBucketMinutes = [5, 15, 60];

    public const int DefaultWindowHours = 24;
    public const int DefaultBucketMinutes = 60;

    /// <summary>Model key for ledger rows that recorded no model.</summary>
    public const string UnknownModel = "(unknown)";

    /// <summary>
    /// Category comes from the participant prefix every ledger row carries,
    /// so the timeline needs no job-title lookup.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TaskInfo> NoJobs =
        new Dictionary<string, TaskInfo>(StringComparer.Ordinal);

    /// <summary>
    /// The trailing window ending at the bucket boundary at or before
    /// <paramref name="nowUtc"/>, with window hours and bucket minutes
    /// snapped to the allowed values.
    /// </summary>
    public static (DateTime Start, DateTime End, int BucketMinutes) TrailingWindow(
        int windowHours, int bucketMinutes, DateTime? nowUtc)
    {
        var w = ResolveWindowHours(windowHours);
        var b = ResolveBucketMinutes(bucketMinutes);
        var windowEnd = AlignDown(nowUtc ?? DateTime.UtcNow, b);
        return (windowEnd.AddHours(-w), windowEnd, b);
    }

    public static TokenTimeline Build(
        IReadOnlyList<(string Project, IReadOnlyList<PricedTokenEntry> Ledger)> perProject,
        DateTime windowStart,
        DateTime windowEnd,
        int bucketMinutes)
    {
        var b = ResolveBucketMinutes(bucketMinutes);
        var bucketSpan = TimeSpan.FromMinutes(b);
        var bucketCount = Math.Max(0, (int)Math.Round((windowEnd - windowStart).TotalMinutes / b));

        var cells = new Dictionary<(string, DateTime), TimelineCellTotals>();
        var projects = new Dictionary<string, TimelineProjectTotals>(StringComparer.Ordinal);
        var models = new Dictionary<(string Project, string Model, string Host), TimelineModelTotals>();

        foreach (var (project, ledger) in perProject)
        {
            if (!projects.TryGetValue(project, out var projectTotals))
            {
                projectTotals = new TimelineProjectTotals(project);
                projects[project] = projectTotals;
            }

            foreach (var priced in ledger)
            {
                var usage = priced.Usage;
                if (usage is null || priced.Cost is null) continue;
                var entry = priced.Entry;
                var ts = entry.Ts.ToUniversalTime();
                if (ts < windowStart || ts >= windowEnd) continue;

                var bucketStart = windowStart.AddMinutes(Math.Floor((ts - windowStart).TotalMinutes / b) * b);
                if (!cells.TryGetValue((project, bucketStart), out var cell))
                {
                    cell = new TimelineCellTotals(project, bucketStart, bucketStart + bucketSpan);
                    cells[(project, bucketStart)] = cell;
                }
                var entryTotal = (long)usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheCreationTokens;
                var category = ProjectTokenCategory.Of(entry, NoJobs);
                cell.Add(usage, priced.Cost);
                cell.AddCategory(category, entryTotal);

                var modelId = ModelIdentity.StoredId(usage.Model) ?? UnknownModel;
                var host = TokenUsageHost.Resolve(usage.Host, entry.ParticipantId);
                if (!models.TryGetValue((project, modelId, host), out var model))
                {
                    model = new TimelineModelTotals(project, modelId, host);
                    models[(project, modelId, host)] = model;
                }
                model.Add(usage, priced.Cost);
                if (!string.IsNullOrWhiteSpace(usage.CliType)) model.CliTypes.Add(usage.CliType.Trim().ToLowerInvariant());

                projectTotals.Add(usage, priced.Cost);
                projectTotals.AddHost(host, entryTotal);
                projectTotals.AddCategory(category, entryTotal);
                if (projectTotals.PeakBucketTotal < cell.Total)
                {
                    projectTotals.PeakBucketTotal = cell.Total;
                    projectTotals.PeakBucketStart = bucketStart;
                }
                if (projectTotals.LastActivity == null || ts > projectTotals.LastActivity)
                    projectTotals.LastActivity = ts;
            }
        }

        return new TokenTimeline(
            WindowStart: windowStart.ToString("o"),
            WindowEnd: windowEnd.ToString("o"),
            WindowHours: (int)Math.Round((windowEnd - windowStart).TotalHours),
            BucketMinutes: b,
            BucketCount: bucketCount,
            Cells: CellRows(cells.Values),
            Projects: ProjectRows(projects.Values),
            FetchedAt: DateTime.UtcNow.ToString("o"),
            Disclaimer: TokenCostDisclaimer.Text)
        {
            Models = ModelRows(models.Values),
        };
    }

    public static int ResolveWindowHours(int requested)
    {
        // Snap to the nearest allowed value; default when the caller passed 0 or negative.
        if (requested <= 0) return DefaultWindowHours;
        return AllowedWindowHours.Contains(requested) ? requested : DefaultWindowHours;
    }

    public static int ResolveBucketMinutes(int requested)
    {
        if (requested <= 0) return DefaultBucketMinutes;
        return AllowedBucketMinutes.Contains(requested) ? requested : DefaultBucketMinutes;
    }

    private static List<TokenTimelineCell> CellRows(IEnumerable<TimelineCellTotals> cells)
        => cells
            .OrderBy(cell => cell.BucketStart)
            .ThenBy(cell => cell.Project, StringComparer.Ordinal)
            .Select(cell => new TokenTimelineCell(
                Project: cell.Project,
                BucketStart: cell.BucketStart.ToString("o"),
                BucketEnd: cell.BucketEnd.ToString("o"),
                Calls: cell.Calls,
                Input: cell.Input,
                Output: cell.Output,
                CacheRead: cell.CacheRead,
                CacheWrite: cell.CacheWrite,
                Total: cell.Total,
                Dollars: cell.Dollars,
                AllModelsPriced: cell.AllModelsPriced,
                AgentTokens: cell.AgentTokens,
                SupportingTokens: cell.SupportingTokens,
                OrchestratorTokens: cell.OrchestratorTokens))
            .ToList();

    private static List<TokenTimelineProject> ProjectRows(IEnumerable<TimelineProjectTotals> projects)
        => projects
            .OrderByDescending(project => project.Total)
            .Select(project => new TokenTimelineProject(
                Project: project.Project,
                Calls: project.Calls,
                Input: project.Input,
                Output: project.Output,
                CacheRead: project.CacheRead,
                CacheWrite: project.CacheWrite,
                Total: project.Total,
                Dollars: project.Dollars,
                AllModelsPriced: project.AllModelsPriced,
                PeakBucketStart: project.PeakBucketStart?.ToString("o"),
                PeakBucketTotal: project.PeakBucketTotal,
                LastActivity: project.LastActivity?.ToString("o"),
                AgentTokens: project.AgentTokens,
                SupportingTokens: project.SupportingTokens,
                OrchestratorTokens: project.OrchestratorTokens)
            {
                Hosts = project.Hosts.Values
                    .OrderByDescending(host => host.Total)
                    .ThenBy(host => host.Host, StringComparer.Ordinal)
                    .Select(host => new TokenTimelineHostShare(host.Host, host.Calls, host.Total))
                    .ToList(),
            })
            .ToList();

    private static List<TokenTimelineModelUsage> ModelRows(IEnumerable<TimelineModelTotals> models)
        => models
            .OrderByDescending(model => model.Total)
            .ThenBy(model => model.Project, StringComparer.Ordinal)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .ThenBy(model => model.Host, StringComparer.Ordinal)
            .Select(model => new TokenTimelineModelUsage(
                Project: model.Project,
                Model: model.Model,
                ModelLabel: model.Model == UnknownModel ? UnknownModel : ModelIdentity.Label(model.Model) ?? model.Model,
                Host: model.Host,
                CliTypes: model.CliTypes.OrderBy(cli => cli, StringComparer.Ordinal).ToList(),
                Calls: model.Calls,
                Input: model.Input,
                Output: model.Output,
                CacheRead: model.CacheRead,
                CacheWrite: model.CacheWrite,
                Total: model.Total,
                Dollars: model.Dollars,
                AllModelsPriced: model.AllModelsPriced))
            .ToList();

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }
}
