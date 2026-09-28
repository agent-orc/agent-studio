

namespace AgentStudio.Runner;

/// <summary>
/// Workspace-wide token-usage timeline. Walks every watched project's
/// orchestrator log, drops entries that fall outside the requested
/// window, and folds the surviving token-usage records into
/// (project, time-bucket) cells. The frontend renders the result as a
/// stacked timeline at <c>#/workspace/tokens</c>.
///
/// <para>
/// Same data source as <see cref="TokenSummaryService"/> (the
/// orchestrator JSONL log). The aggregator deliberately reuses the
/// per-call <see cref="OrchestratorTokenUsage"/> records rather than
/// recomputing from raw CLI output - the orchestrator log already
/// carries every LLM call's token counts plus the timestamp we need
/// to bucket on.
/// </para>
/// </summary>
public class WorkspaceTokensTimelineService
{
    private static readonly int[] AllowedWindowHours = [1, 6, 24, 168];
    private static readonly int[] AllowedBucketMinutes = [5, 15, 60];

    public const int DefaultWindowHours = 24;
    public const int DefaultBucketMinutes = 60;

    /// <summary>
    /// Categorisation for the workspace timeline is participant-driven
    /// (<c>agent:</c> / <c>support:</c> / <c>orchestrator:</c> prefixes), so
    /// no per-job title lookup is needed here - the bus-native entries carry
    /// their participant. An empty job map is enough for
    /// <see cref="ProjectTokenUsageService.Categorize"/> to run its
    /// participant branch without a disk walk.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TaskInfo> NoJobs =
        new Dictionary<string, TaskInfo>(StringComparer.Ordinal);

    private readonly OrchestratorLog _log;
    private readonly BusBackedWorkspaceTimelineReader? _busReader;

    public WorkspaceTokensTimelineService(OrchestratorLog log, BusBackedWorkspaceTimelineReader? busReader = null)
    {
        _log = log;
        _busReader = busReader;
    }

    /// <summary>
    /// Build a timeline view across every supplied (project, watch path)
    /// pair. Reads each project's orchestrator log once.
    /// </summary>
    public TokenTimeline Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        if (_busReader != null)
            return _busReader.Build(projects, windowHours, bucketMinutes, nowUtc);

        var w = ResolveWindowHours(windowHours);
        var b = ResolveBucketMinutes(bucketMinutes);
        var now = nowUtc ?? DateTime.UtcNow;
        var windowEnd = AlignDown(now, b);
        var windowStart = windowEnd.AddHours(-w);

        var perProjectEntries = new List<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)>();
        foreach (var (name, watchPath) in projects)
        {
            var entries = _log.Read(watchPath);
            perProjectEntries.Add((name, entries));
        }

        return BuildFromEntries(perProjectEntries, windowStart, windowEnd, b);
    }

    /// <summary>
    /// Pure overload: bucket pre-loaded entries. Used by the unit tests
    /// to avoid filesystem round-trips.
    /// </summary>
    public static TokenTimeline BuildFromEntries(
        IReadOnlyList<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)> perProject,
        DateTime windowStart,
        DateTime windowEnd,
        int bucketMinutes)
    {
        var b = ResolveBucketMinutes(bucketMinutes);
        var bucketSpan = TimeSpan.FromMinutes(b);
        var bucketCount = Math.Max(0, (int)Math.Round((windowEnd - windowStart).TotalMinutes / b));

        // (project, bucketStart) -> Bucket
        var cellMap = new Dictionary<(string, DateTime), Bucket>();
        var projectTotals = new Dictionary<string, ProjectTotal>(StringComparer.Ordinal);
        // (project, model id, host) -> usage row (AGT-2986)
        var modelMap = new Dictionary<(string Project, string Model, string Host), ModelTotal>();

        foreach (var (project, entries) in perProject)
        {
            if (!projectTotals.ContainsKey(project))
                projectTotals[project] = new ProjectTotal(project);

            foreach (var entry in entries)
            {
                var u = entry.TokenUsage;
                if (u == null) continue;
                var ts = entry.Ts.ToUniversalTime();
                if (ts < windowStart || ts >= windowEnd) continue;

                var bucketStart = AlignDown(ts, b);
                var key = (project, bucketStart);
                if (!cellMap.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket(project, bucketStart, bucketStart + bucketSpan);
                    cellMap[key] = bucket;
                }
                var entryTotal = (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
                var category = ProjectTokenUsageService.Categorize(entry, NoJobs);

                bucket.Calls++;
                bucket.Input += u.InputTokens;
                bucket.Output += u.OutputTokens;
                bucket.CacheRead += u.CacheReadTokens;
                bucket.CacheWrite += u.CacheCreationTokens;
                bucket.AddCategory(category, entryTotal);

                var cost = TokenPricing.Estimate(u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens, ts);
                if (cost.ModelKnown)
                {
                    bucket.Dollars = (bucket.Dollars ?? 0m) + cost.Total;
                    bucket.HasPricedCall = true;
                }
                else
                {
                    bucket.HasUnpricedCall = true;
                }

                var modelId = TokenModelDisplay.StoredId(u.Model) ?? UnknownModel;
                var host = TokenUsageHost.Resolve(u.Host, entry.ParticipantId);
                if (!modelMap.TryGetValue((project, modelId, host), out var modelTotal))
                {
                    modelTotal = new ModelTotal(project, modelId, host);
                    modelMap[(project, modelId, host)] = modelTotal;
                }
                modelTotal.Add(u, entryTotal, cost);
                if (!string.IsNullOrWhiteSpace(u.CliType)) modelTotal.CliTypes.Add(u.CliType.Trim().ToLowerInvariant());

                var pt = projectTotals[project];
                pt.Calls++;
                pt.AddHost(host, entryTotal);
                pt.Input += u.InputTokens;
                pt.Output += u.OutputTokens;
                pt.CacheRead += u.CacheReadTokens;
                pt.CacheWrite += u.CacheCreationTokens;
                pt.AddCategory(category, entryTotal);
                if (cost.ModelKnown)
                {
                    pt.Dollars = (pt.Dollars ?? 0m) + cost.Total;
                    pt.HasPricedCall = true;
                }
                else
                {
                    pt.HasUnpricedCall = true;
                }
                if (pt.PeakBucketTotal < (bucket.Input + bucket.Output + bucket.CacheRead + bucket.CacheWrite))
                {
                    pt.PeakBucketTotal = bucket.Input + bucket.Output + bucket.CacheRead + bucket.CacheWrite;
                    pt.PeakBucketStart = bucketStart;
                }
                if (pt.LastActivity == null || ts > pt.LastActivity)
                    pt.LastActivity = ts;
            }
        }

        var cells = new List<TokenTimelineCell>(cellMap.Count);
        foreach (var bucket in cellMap.Values
            .OrderBy(b1 => b1.BucketStart)
            .ThenBy(b1 => b1.Project, StringComparer.Ordinal))
        {
            var total = bucket.Input + bucket.Output + bucket.CacheRead + bucket.CacheWrite;
            cells.Add(new TokenTimelineCell(
                Project: bucket.Project,
                BucketStart: bucket.BucketStart.ToString("o"),
                BucketEnd: bucket.BucketEnd.ToString("o"),
                Calls: bucket.Calls,
                Input: bucket.Input,
                Output: bucket.Output,
                CacheRead: bucket.CacheRead,
                CacheWrite: bucket.CacheWrite,
                Total: total,
                Dollars: bucket.Dollars,
                AllModelsPriced: bucket.HasPricedCall && !bucket.HasUnpricedCall,
                AgentTokens: bucket.AgentTokens,
                SupportingTokens: bucket.SupportingTokens,
                OrchestratorTokens: bucket.OrchestratorTokens));
        }

        var projectsOut = projectTotals.Values
            .OrderByDescending(p => p.Input + p.Output + p.CacheRead + p.CacheWrite)
            .Select(p => new TokenTimelineProject(
                Project: p.Project,
                Calls: p.Calls,
                Input: p.Input,
                Output: p.Output,
                CacheRead: p.CacheRead,
                CacheWrite: p.CacheWrite,
                Total: p.Input + p.Output + p.CacheRead + p.CacheWrite,
                Dollars: p.Dollars,
                AllModelsPriced: p.HasPricedCall && !p.HasUnpricedCall,
                PeakBucketStart: p.PeakBucketStart?.ToString("o"),
                PeakBucketTotal: p.PeakBucketTotal,
                LastActivity: p.LastActivity?.ToString("o"),
                AgentTokens: p.AgentTokens,
                SupportingTokens: p.SupportingTokens,
                OrchestratorTokens: p.OrchestratorTokens)
            {
                Hosts = p.Hosts.Values
                    .OrderByDescending(h => h.Total)
                    .ThenBy(h => h.Host, StringComparer.Ordinal)
                    .Select(h => new TokenTimelineHostShare(h.Host, h.Calls, h.Total))
                    .ToList(),
            })
            .ToList();

        var modelsOut = modelMap.Values
            .OrderByDescending(m => m.Total)
            .ThenBy(m => m.Project, StringComparer.Ordinal)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ThenBy(m => m.Host, StringComparer.Ordinal)
            .Select(m => new TokenTimelineModelUsage(
                Project: m.Project,
                Model: m.Model,
                // Label resolved per response from the registry; the id is
                // the stored value and an unknown id renders as itself.
                ModelLabel: m.Model == UnknownModel ? UnknownModel : TokenModelDisplay.Label(m.Model) ?? m.Model,
                Host: m.Host,
                CliTypes: m.CliTypes.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                Calls: m.Calls,
                Input: m.Input,
                Output: m.Output,
                CacheRead: m.CacheRead,
                CacheWrite: m.CacheWrite,
                Total: m.Total,
                Dollars: m.Dollars,
                AllModelsPriced: m.HasPricedCall && !m.HasUnpricedCall))
            .ToList();

        return new TokenTimeline(
            WindowStart: windowStart.ToString("o"),
            WindowEnd: windowEnd.ToString("o"),
            WindowHours: (int)Math.Round((windowEnd - windowStart).TotalHours),
            BucketMinutes: b,
            BucketCount: bucketCount,
            Cells: cells,
            Projects: projectsOut,
            FetchedAt: DateTime.UtcNow.ToString("o"),
            Disclaimer: TokenSummaryService.DefaultDisclaimer)
        {
            Models = modelsOut,
        };
    }

    /// <summary>Model key for ledger rows that recorded no model.</summary>
    public const string UnknownModel = "(unknown)";

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

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }

    private sealed class Bucket
    {
        public string Project { get; }
        public DateTime BucketStart { get; }
        public DateTime BucketEnd { get; }
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public decimal? Dollars;
        public bool HasPricedCall;
        public bool HasUnpricedCall;
        public long AgentTokens;
        public long SupportingTokens;
        public long OrchestratorTokens;

        public Bucket(string project, DateTime start, DateTime end)
        {
            Project = project;
            BucketStart = start;
            BucketEnd = end;
        }

        public void AddCategory(string category, long amount)
        {
            switch (category)
            {
                case ProjectTokenCategory.Job: AgentTokens += amount; break;
                case ProjectTokenCategory.Supporting: SupportingTokens += amount; break;
                case ProjectTokenCategory.Orchestrator: OrchestratorTokens += amount; break;
            }
        }
    }

    private sealed class ModelTotal
    {
        public string Project { get; }
        public string Model { get; }
        public string Host { get; }
        public HashSet<string> CliTypes { get; } = new(StringComparer.Ordinal);
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public long Total;
        public decimal? Dollars;
        public bool HasPricedCall;
        public bool HasUnpricedCall;

        public ModelTotal(string project, string model, string host)
        {
            Project = project;
            Model = model;
            Host = host;
        }

        public void Add(OrchestratorTokenUsage u, long entryTotal, TokenCostEstimate cost)
        {
            Calls++;
            Input += u.InputTokens;
            Output += u.OutputTokens;
            CacheRead += u.CacheReadTokens;
            CacheWrite += u.CacheCreationTokens;
            Total += entryTotal;
            if (cost.ModelKnown)
            {
                Dollars = (Dollars ?? 0m) + cost.Total;
                HasPricedCall = true;
            }
            else
            {
                HasUnpricedCall = true;
            }
        }
    }

    private sealed class HostTotal
    {
        public string Host { get; }
        public int Calls;
        public long Total;

        public HostTotal(string host) => Host = host;
    }

    private sealed class ProjectTotal
    {
        public Dictionary<string, HostTotal> Hosts { get; } = new(StringComparer.Ordinal);

        public void AddHost(string host, long amount)
        {
            if (!Hosts.TryGetValue(host, out var total))
            {
                total = new HostTotal(host);
                Hosts[host] = total;
            }
            total.Calls++;
            total.Total += amount;
        }

        public string Project { get; }
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public decimal? Dollars;
        public bool HasPricedCall;
        public bool HasUnpricedCall;
        public DateTime? PeakBucketStart;
        public long PeakBucketTotal;
        public DateTime? LastActivity;
        public long AgentTokens;
        public long SupportingTokens;
        public long OrchestratorTokens;

        public ProjectTotal(string project)
        {
            Project = project;
        }

        public void AddCategory(string category, long amount)
        {
            switch (category)
            {
                case ProjectTokenCategory.Job: AgentTokens += amount; break;
                case ProjectTokenCategory.Supporting: SupportingTokens += amount; break;
                case ProjectTokenCategory.Orchestrator: OrchestratorTokens += amount; break;
            }
        }
    }
}

/// <summary>
/// Workspace-wide token timeline response. One entry per
/// (project, time bucket) cell that had at least one orchestrator LLM
/// call inside the window.
/// </summary>
public sealed record TokenTimeline(
    string WindowStart,
    string WindowEnd,
    int WindowHours,
    int BucketMinutes,
    int BucketCount,
    IReadOnlyList<TokenTimelineCell> Cells,
    IReadOnlyList<TokenTimelineProject> Projects,
    string FetchedAt,
    string Disclaimer)
{
    /// <summary>
    /// Separate project/week lines for calls that ran after an admission
    /// decision with a better benchmark candidate for the selected route.
    /// </summary>
    public IReadOnlyList<BetterCandidateUsageLine> BetterCandidateUsage { get; init; } = [];

    /// <summary>
    /// Per (project, model id, executing host) usage over the window
    /// (AGT-2986). Sums reconcile to the per-project totals.
    /// </summary>
    public IReadOnlyList<TokenTimelineModelUsage> Models { get; init; } = [];

    /// <summary>Read health of the merged usage ledger behind this response.</summary>
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
}

/// <summary>
/// One (project, model, host) row. <see cref="Model"/> is the stored id;
/// <see cref="ModelLabel"/> is resolved from the registry for this response
/// and equals the id when the registry does not know it. <see cref="Host"/>
/// is a remote runner id, <c>local</c>, or <c>remote-unrecorded</c> for
/// remote receipts written before host attribution.
/// </summary>
public sealed record TokenTimelineModelUsage(
    string Project,
    string Model,
    string ModelLabel,
    string Host,
    IReadOnlyList<string> CliTypes,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced);

/// <summary>Share of one project's window total executed on one host.</summary>
public sealed record TokenTimelineHostShare(string Host, int Calls, long Total);

/// <summary>
/// One (project, bucket) cell. <see cref="AllModelsPriced"/> is false
/// when at least one call in the bucket used a model that is not in
/// <see cref="TokenPricing.Catalog"/>; <see cref="Dollars"/> in that
/// case covers only the priced subset. <see cref="AgentTokens"/> +
/// <see cref="SupportingTokens"/> + <see cref="OrchestratorTokens"/> add
/// up to <see cref="Total"/>; the split lets the UI show the orchestrator
/// share separately (AGT-2038).
/// </summary>
public sealed record TokenTimelineCell(
    string Project,
    string BucketStart,
    string BucketEnd,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced,
    long AgentTokens,
    long SupportingTokens,
    long OrchestratorTokens);

/// <summary>
/// Per-project rollup over the full window. Drives the legend and the
/// summary table under the chart. <see cref="AgentTokens"/> +
/// <see cref="SupportingTokens"/> + <see cref="OrchestratorTokens"/> add
/// up to <see cref="Total"/> so the table can carry a Total / davon Agent
/// / davon Orchestrator split (AGT-2038). <see cref="LastActivity"/> now
/// reflects the newest real activity of any kind - an agent run counts,
/// not only the last orchestrator call.
/// </summary>
public sealed record TokenTimelineProject(
    string Project,
    int Calls,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Total,
    decimal? Dollars,
    bool AllModelsPriced,
    string? PeakBucketStart,
    long PeakBucketTotal,
    string? LastActivity,
    long AgentTokens,
    long SupportingTokens,
    long OrchestratorTokens)
{
    /// <summary>Executing hosts of this project's window total, largest first (AGT-2986).</summary>
    public IReadOnlyList<TokenTimelineHostShare> Hosts { get; init; } = [];
}
