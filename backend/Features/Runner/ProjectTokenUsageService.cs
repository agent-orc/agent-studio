

namespace AgentStudio.Runner;

/// <summary>
/// Project-scoped token-usage rollups for slice 8 of the quality-system
/// mockup (docs/mockups/quality-system/, "Token Usage" surface). Runtime
/// reads are bus-backed; the legacy fallback reads the project's
/// <c>orchestrator.jsonl</c> through <see cref="OrchestratorLog"/> exactly
/// once per call and folds the surviving entries into:
/// <list type="bullet">
///   <item><b>Summary</b>: lifetime + last-24h totals plus a category
///   split (Job / Supporting / Orchestrator) per <c>taxonomy.md</c>.</item>
///   <item><b>Heatmap</b>: rows = jobs (most expensive first), columns =
///   days. One cell per (job, day) with at least one priced or unpriced
///   call.</item>
///   <item><b>Expensive jobs</b>: top N jobs by total tokens.</item>
///   <item><b>Per-job drill-down</b>: per-call rows with token deltas
///   vs. the previous call.</item>
/// </list>
/// <para>
/// Categorisation rule (visibility, not enforcement - Critical
/// Boundaries in the README): an orchestrator-log entry's category is
/// derived from its <see cref="OrchestratorLogEntry.JobId"/> and the
/// matching job's title prefix. Orchestrator entries with no JobId fall
/// into the orchestrator bucket; entries whose JobId matches a job
/// whose title starts with one of <see cref="SupportingJobTitlePrefixes"/>
/// are supporting (security audits, drift analyses, etc.). Everything
/// else is a regular job.
/// </para>
/// <para>
/// Performance: O(N) over orchestrator-log entries plus O(M) over the
/// project's job folders. Both happen at most once per request; no
/// per-job disk I/O. Locked by
/// <see cref="AgentStudio.Tests.ProjectTokenUsageEndpointPerfTests"/>.
/// </para>
/// </summary>
public static class ProjectTokenUsageService
{
    /// <summary>
    /// Title prefixes that mark a job as a "supporting" loop in the
    /// Token-Usage split. These are the job kinds taxonomy.md explicitly
    /// names as supporting analysis loops (audits, councils, drift, etc.);
    /// new prefixes should be added here as the queue grows new
    /// orchestrator-spawned job categories.
    /// </summary>
    public static readonly string[] SupportingJobTitlePrefixes =
    [
        "Security audit",
        "Drift analysis",
        "Output pattern analysis",
        "Task check",
        "Traceability check",
        "Council review",
        "Source map",
        "Screenshot critique",
    ];

    /// <summary>Default heatmap window. 30 days matches the timeline default.</summary>
    public const int DefaultHeatmapDays = 30;
    /// <summary>Heatmap window cap. Keeps the matrix small enough to draw without virtualisation.</summary>
    public const int MaxHeatmapDays = 90;
    /// <summary>Default top-N for the expensive-jobs list.</summary>
    public const int DefaultExpensiveLimit = 10;
    /// <summary>Hard cap for the expensive-jobs list.</summary>
    public const int MaxExpensiveLimit = 50;

    /// <summary>
    /// Lifetime + rolling 24h / 7d totals with the Job / Supporting / Orchestrator
    /// split. Returns an "empty" payload (HasData = false) when the
    /// project has not produced a single token-using orchestrator entry
    /// yet, so the panel can render its hide-when-empty branch (Hard
    /// rules in the prompt).
    /// </summary>
    public static ProjectTokenUsageSummary BuildSummaryFromEntries(
        string projectName,
        IReadOnlyList<OrchestratorLogEntry> entries,
        IReadOnlyDictionary<string, TaskInfo> jobsById,
        DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var since24h = now.AddHours(-24);
        var since7d = now.AddDays(-7);

        var lifetime = new CategoryBucket();
        var last24h = new CategoryBucket();
        var last7d = new CategoryBucket();
        long lifetimeTotal = 0;
        long last24hTotal = 0;
        long last7dTotal = 0;
        DateTime? firstAt = null;
        DateTime? lastAt = null;
        int callsLifetime = 0;
        int callsLast24h = 0;
        int callsLast7d = 0;

        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            var ts = entry.Ts.ToUniversalTime();
            var total = (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
            if (total <= 0) continue;

            var category = Categorize(entry, jobsById);
            lifetime.Add(category, total);
            lifetimeTotal += total;
            callsLifetime++;
            if (firstAt == null || ts < firstAt) firstAt = ts;
            if (lastAt == null || ts > lastAt) lastAt = ts;

            if (ts >= since24h && ts <= now)
            {
                last24h.Add(category, total);
                last24hTotal += total;
                callsLast24h++;
            }
            if (ts >= since7d && ts <= now)
            {
                last7d.Add(category, total);
                last7dTotal += total;
                callsLast7d++;
            }
        }

        return new ProjectTokenUsageSummary
        {
            Project = projectName,
            HasData = callsLifetime > 0,
            LifetimeTotalTokens = lifetimeTotal,
            LifetimeJobTokens = lifetime.Job,
            LifetimeSupportingTokens = lifetime.Supporting,
            LifetimeOrchestratorTokens = lifetime.Orchestrator,
            LifetimeCalls = callsLifetime,
            Last24hTotalTokens = last24hTotal,
            Last24hJobTokens = last24h.Job,
            Last24hSupportingTokens = last24h.Supporting,
            Last24hOrchestratorTokens = last24h.Orchestrator,
            Last24hCalls = callsLast24h,
            Last7dTotalTokens = last7dTotal,
            Last7dJobTokens = last7d.Job,
            Last7dSupportingTokens = last7d.Supporting,
            Last7dOrchestratorTokens = last7d.Orchestrator,
            Last7dCalls = callsLast7d,
            FirstActivity = firstAt?.ToString("o"),
            LastActivity = lastAt?.ToString("o"),
            FetchedAt = DateTime.UtcNow.ToString("o"),
            Disclaimer = TokenSummaryService.DefaultDisclaimer,
        };
    }

    /// <summary>
    /// Per-job × per-day heatmap. Days are aligned to UTC midnight; the
    /// returned <see cref="ProjectTokenHeatmap.Days"/> are oldest →
    /// newest. <see cref="ProjectTokenHeatmap.Jobs"/> are ordered by
    /// total tokens descending so the "hot rows" land at the top of the
    /// rendered matrix (the rendering contract: rows = jobs most
    /// expensive first).
    /// </summary>
    public static ProjectTokenHeatmap BuildHeatmapFromEntries(
        string projectName,
        IReadOnlyList<OrchestratorLogEntry> entries,
        IReadOnlyDictionary<string, TaskInfo> jobsById,
        int days,
        DateTime? nowUtc = null)
    {
        var d = ResolveDays(days);
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var endDay = AlignDay(now);
        var startDay = endDay.AddDays(-(d - 1));

        var dayList = new List<string>(d);
        for (var i = 0; i < d; i++)
        {
            dayList.Add(endDay.AddDays(-(d - 1 - i)).ToString("yyyy-MM-dd"));
        }

        // (jobId, day) → token total
        var perJobDay = new Dictionary<(string JobId, string Day), long>();
        var perJobTotal = new Dictionary<string, long>(StringComparer.Ordinal);
        var perJobCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        var perJobLastActivity = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var perJobCategory = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            var jobId = entry.JobId;
            if (string.IsNullOrWhiteSpace(jobId)) continue;
            var ts = entry.Ts.ToUniversalTime();
            if (ts < startDay || ts > endDay.AddDays(1)) continue;
            var total = (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
            if (total <= 0) continue;

            var dayKey = AlignDay(ts).ToString("yyyy-MM-dd");
            var key = (jobId!, dayKey);
            perJobDay.TryGetValue(key, out var prior);
            perJobDay[key] = prior + total;
            perJobTotal.TryGetValue(jobId!, out var pt);
            perJobTotal[jobId!] = pt + total;
            perJobCalls.TryGetValue(jobId!, out var pc);
            perJobCalls[jobId!] = pc + 1;
            if (!perJobLastActivity.TryGetValue(jobId!, out var lastAt) || ts > lastAt)
                perJobLastActivity[jobId!] = ts;
            var category = Categorize(entry, jobsById);
            perJobCategory[jobId!] = PreferCategory(perJobCategory.GetValueOrDefault(jobId!), category);
        }

        var jobRows = perJobTotal
            .OrderByDescending(p => p.Value)
            .Select(p =>
            {
                var jobId = p.Key;
                jobsById.TryGetValue(jobId, out var info);
                var cells = new List<ProjectTokenHeatmapCell>(d);
                for (var i = 0; i < d; i++)
                {
                    var day = dayList[i];
                    perJobDay.TryGetValue((jobId, day), out var cellTotal);
                    cells.Add(new ProjectTokenHeatmapCell
                    {
                        Day = day,
                        Total = cellTotal,
                    });
                }
                return new ProjectTokenHeatmapJob
                {
                    JobId = jobId,
                    Title = info?.Title ?? jobId,
                    State = info?.State,
                    Category = perJobCategory.GetValueOrDefault(jobId) ?? Categorize(jobId, jobsById),
                    Total = p.Value,
                    Calls = perJobCalls.GetValueOrDefault(jobId),
                    LastActivity = perJobLastActivity.GetValueOrDefault(jobId).ToString("o"),
                    Cells = cells,
                };
            })
            .ToList();

        return new ProjectTokenHeatmap
        {
            Project = projectName,
            Days = dayList,
            Jobs = jobRows,
            HasData = jobRows.Count > 0,
            FetchedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>
    /// Top N jobs by total tokens. Used by the panel's expensive-jobs
    /// list. Always returns "real" jobs first - a JobId that no longer
    /// resolves to a folder still appears with its raw id as the title
    /// so deleted-job spend stays visible (Critical Boundaries: Token
    /// usage is accountability).
    /// </summary>
    public static IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobsFromEntries(
        IReadOnlyList<OrchestratorLogEntry> entries,
        IReadOnlyDictionary<string, TaskInfo> jobsById,
        int limit)
    {
        var lim = ResolveExpensiveLimit(limit);
        var perJobTotal = new Dictionary<string, TaskAccumulator>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            var jobId = entry.JobId;
            if (string.IsNullOrWhiteSpace(jobId)) continue;
            var total = (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
            if (total <= 0) continue;
            if (!perJobTotal.TryGetValue(jobId!, out var acc))
            {
                acc = new TaskAccumulator();
                perJobTotal[jobId!] = acc;
            }
            acc.Total += total;
            acc.Calls++;
            var ts = entry.Ts.ToUniversalTime();
            if (ts > acc.LastActivity) acc.LastActivity = ts;
            acc.Category = PreferCategory(acc.Category, Categorize(entry, jobsById));
            var displayModel = ModelIdentity.Label(u.Model);
            if (!string.IsNullOrWhiteSpace(displayModel))
            {
                if (ts > acc.LastAnyModelAt)
                {
                    acc.LastAnyModelAt = ts;
                    acc.LastAnyModel = displayModel;
                }
                if (TokenParticipants.IsAgent(entry.ParticipantId) && ts > acc.LastAgentModelAt)
                {
                    acc.LastAgentModelAt = ts;
                    acc.LastAgentModel = displayModel;
                }
            }
        }

        return perJobTotal
            .OrderByDescending(p => p.Value.Total)
            .Take(lim)
            .Select(p =>
            {
                var jobId = p.Key;
                jobsById.TryGetValue(jobId, out var info);
                return new ProjectExpensiveJob
                {
                    JobId = jobId,
                    Title = info?.Title ?? jobId,
                    State = info?.State,
                    Category = p.Value.Category ?? Categorize(jobId, jobsById),
                    TotalTokens = p.Value.Total,
                    Calls = p.Value.Calls,
                    LastActivity = p.Value.LastActivity == default ? null : p.Value.LastActivity.ToString("o"),
                    LastModel = p.Value.LastAgentModel ?? p.Value.LastAnyModel,
                };
            })
            .ToList();
    }

    /// <summary>
    /// Per-run drill-down: every orchestrator call attributed to one
    /// job, ordered oldest first, with total-token delta vs. the prior
    /// call. The frontend's drill-down panel renders this as a list
    /// (per the prompt's "split by run, by category, with deltas").
    /// </summary>
    public static ProjectJobTokenDetail? BuildJobDetailFromEntries(
        string projectName,
        IReadOnlyList<OrchestratorLogEntry> entries,
        IReadOnlyDictionary<string, TaskInfo> jobsById,
        string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var rows = new List<ProjectJobTokenRun>();
        long runningTotal = 0;
        long lastTotal = 0;
        long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
        DateTime? firstAt = null;
        DateTime? lastAt = null;
        string? lastModel = null;
        string? category = null;
        int index = 0;

        foreach (var entry in entries.OrderBy(e => e.Ts))
        {
            if (!string.Equals(entry.JobId, jobId, StringComparison.Ordinal)) continue;
            var u = entry.TokenUsage;
            if (u == null) continue;
            var rowTotal = (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheCreationTokens;
            if (rowTotal <= 0) continue;

            var ts = entry.Ts.ToUniversalTime();
            var delta = rows.Count == 0 ? (long?)null : rowTotal - lastTotal;
            var displayModel = ModelIdentity.Label(u.Model);
            rows.Add(new ProjectJobTokenRun
            {
                Index = index++,
                Ts = ts.ToString("o"),
                Model = displayModel,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CacheReadTokens = u.CacheReadTokens,
                CacheCreationTokens = u.CacheCreationTokens,
                Total = rowTotal,
                DeltaVsPrev = delta,
                Topic = entry.Topic,
                Summary = entry.Summary,
            });
            runningTotal += rowTotal;
            input += u.InputTokens;
            output += u.OutputTokens;
            cacheRead += u.CacheReadTokens;
            cacheCreate += u.CacheCreationTokens;
            lastTotal = rowTotal;
            if (firstAt == null) firstAt = ts;
            lastAt = ts;
            category = PreferCategory(category, Categorize(entry, jobsById));
            if (!string.IsNullOrWhiteSpace(displayModel))
            {
                if (TokenParticipants.IsAgent(entry.ParticipantId)) lastModel = displayModel;
                else lastModel ??= displayModel;
            }
        }

        if (rows.Count == 0) return null;

        jobsById.TryGetValue(jobId, out var info);
        return new ProjectJobTokenDetail
        {
            Project = projectName,
            JobId = jobId,
            Title = info?.Title ?? jobId,
            State = info?.State,
            Category = category ?? Categorize(jobId, jobsById),
            TotalTokens = runningTotal,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreate,
            Calls = rows.Count,
            FirstActivity = firstAt?.ToString("o"),
            LastActivity = lastAt?.ToString("o"),
            LastModel = lastModel,
            Runs = rows,
            FetchedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>
    /// Public test hook so the perf test can inject a pre-baked job
    /// lookup without going through the scanner's disk walk twice.
    /// </summary>
    public static string Categorize(string? jobId, IReadOnlyDictionary<string, TaskInfo> jobsById)
        => Categorize(new OrchestratorLogEntry { JobId = jobId }, jobsById);

    public static string Categorize(OrchestratorLogEntry entry, IReadOnlyDictionary<string, TaskInfo> jobsById)
    {
        if (TokenParticipants.IsAgent(entry.ParticipantId)) return ProjectTokenCategory.Job;
        if (TokenParticipants.IsSupporting(entry.ParticipantId)) return ProjectTokenCategory.Supporting;
        if (TokenParticipants.IsOrchestrator(entry.ParticipantId)) return ProjectTokenCategory.Orchestrator;

        var jobId = entry.JobId;
        if (string.IsNullOrWhiteSpace(jobId)) return ProjectTokenCategory.Orchestrator;
        if (jobsById.TryGetValue(jobId!, out var info))
        {
            var title = info.Title ?? "";
            foreach (var prefix in SupportingJobTitlePrefixes)
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ProjectTokenCategory.Supporting;
            }
        }
        return ProjectTokenCategory.Job;
    }

    private static string PreferCategory(string? current, string candidate)
    {
        if (string.IsNullOrWhiteSpace(current)) return candidate;
        return CategoryRank(candidate) > CategoryRank(current) ? candidate : current;
    }

    private static int CategoryRank(string category) => category switch
    {
        ProjectTokenCategory.Job => 3,
        ProjectTokenCategory.Supporting => 2,
        ProjectTokenCategory.Orchestrator => 1,
        _ => 0
    };

    public static int ResolveDays(int requested) =>
        requested <= 0 ? DefaultHeatmapDays : Math.Min(requested, MaxHeatmapDays);

    public static int ResolveExpensiveLimit(int requested) =>
        requested <= 0 ? DefaultExpensiveLimit : Math.Min(requested, MaxExpensiveLimit);

    private static DateTime AlignDay(DateTime ts)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class CategoryBucket
    {
        public long Job;
        public long Supporting;
        public long Orchestrator;

        public void Add(string category, long amount)
        {
            switch (category)
            {
                case ProjectTokenCategory.Job: Job += amount; break;
                case ProjectTokenCategory.Supporting: Supporting += amount; break;
                case ProjectTokenCategory.Orchestrator: Orchestrator += amount; break;
            }
        }
    }

    private sealed class TaskAccumulator
    {
        public long Total;
        public int Calls;
        public DateTime LastActivity;
        public string? LastAnyModel;
        public string? LastAgentModel;
        public DateTime LastAnyModelAt;
        public DateTime LastAgentModelAt;
        public string? Category;
    }
}

/// <summary>String constants for the Token Usage category split.</summary>
public static class ProjectTokenCategory
{
    public const string Job = "job";
    public const string Supporting = "supporting";
    public const string Orchestrator = "orchestrator";
}

public sealed record ProjectTokenUsageSummary
{
    public string Project { get; init; } = "";
    public bool HasData { get; init; }
    public long LifetimeTotalTokens { get; init; }
    public long LifetimeJobTokens { get; init; }
    public long LifetimeSupportingTokens { get; init; }
    public long LifetimeOrchestratorTokens { get; init; }
    public int LifetimeCalls { get; init; }
    public long Last24hTotalTokens { get; init; }
    public long Last24hJobTokens { get; init; }
    public long Last24hSupportingTokens { get; init; }
    public long Last24hOrchestratorTokens { get; init; }
    public int Last24hCalls { get; init; }
    public long Last7dTotalTokens { get; init; }
    public long Last7dJobTokens { get; init; }
    public long Last7dSupportingTokens { get; init; }
    public long Last7dOrchestratorTokens { get; init; }
    public int Last7dCalls { get; init; }
    public string? FirstActivity { get; init; }
    public string? LastActivity { get; init; }
    public string FetchedAt { get; init; } = "";
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
    public string Disclaimer { get; init; } = "";
}

public sealed record ProjectTokenHeatmap
{
    public string Project { get; init; } = "";
    public IReadOnlyList<string> Days { get; init; } = [];
    public IReadOnlyList<ProjectTokenHeatmapJob> Jobs { get; init; } = [];
    public bool HasData { get; init; }
    public string FetchedAt { get; init; } = "";
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
}

/// <summary>
/// Read-health metadata for project token surfaces. <see cref="AsOf"/> is the
/// newest usage timestamp successfully read, not the HTTP fetch time.
/// </summary>
public sealed record ProjectTokenDataFreshness
{
    public static ProjectTokenDataFreshness Empty { get; } = new();

    /// <summary><c>complete</c>, <c>partial</c>, or <c>unavailable</c>.</summary>
    public string Status { get; init; } = "complete";
    public string? AsOf { get; init; }
    public string? Warning { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
}

public sealed record ProjectTokenHeatmapJob
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long Total { get; init; }
    public int Calls { get; init; }
    public string? LastActivity { get; init; }
    public IReadOnlyList<ProjectTokenHeatmapCell> Cells { get; init; } = [];
}

public sealed record ProjectTokenHeatmapCell
{
    public string Day { get; init; } = "";
    public long Total { get; init; }
}

public sealed record ProjectExpensiveJob
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long TotalTokens { get; init; }
    public int Calls { get; init; }
    public string? LastActivity { get; init; }
    public string? LastModel { get; init; }
}

public sealed record ProjectJobTokenDetail
{
    public string Project { get; init; } = "";
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public int Calls { get; init; }
    public string? FirstActivity { get; init; }
    public string? LastActivity { get; init; }
    public string? LastModel { get; init; }
    public IReadOnlyList<ProjectJobTokenRun> Runs { get; init; } = [];
    public string FetchedAt { get; init; } = "";
}

public sealed record ProjectJobTokenRun
{
    public int Index { get; init; }
    public string Ts { get; init; } = "";
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long Total { get; init; }
    public long? DeltaVsPrev { get; init; }
    public string? Topic { get; init; }
    public string? Summary { get; init; }
}
