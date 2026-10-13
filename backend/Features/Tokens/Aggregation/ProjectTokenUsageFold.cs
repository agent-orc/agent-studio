namespace AgentStudio.Tokens;

/// <summary>
/// Pure folds behind the project Token Usage panel: lifetime and rolling
/// 24h / 7d totals with the Job / Supporting / Orchestrator split, and the
/// per-job by per-day heatmap. Linear in ledger entries; the job lookup is
/// passed in, so a fold never touches disk (locked by
/// <c>ProjectTokenUsageEndpointPerfTests</c>).
/// </summary>
public static class ProjectTokenUsageFold
{
    /// <summary>Default heatmap window. 30 days matches the timeline default.</summary>
    public const int DefaultHeatmapDays = 30;
    /// <summary>Heatmap window cap. Keeps the matrix small enough to draw without virtualisation.</summary>
    public const int MaxHeatmapDays = 90;

    /// <summary>
    /// Lifetime + rolling 24h / 7d totals with the Job / Supporting / Orchestrator
    /// split. Returns an "empty" payload (HasData = false) when the
    /// project has not produced a single token-using orchestrator entry
    /// yet, so the panel can render its hide-when-empty branch (Hard
    /// rules in the prompt).
    /// </summary>
    public static ProjectTokenUsageSummary Summary(
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

            var category = ProjectTokenCategory.Of(entry, jobsById);
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
            Disclaimer = TokenCostDisclaimer.Text,
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
    public static ProjectTokenHeatmap Heatmap(
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
            var category = ProjectTokenCategory.Of(entry, jobsById);
            perJobCategory[jobId!] = ProjectTokenCategory.Prefer(perJobCategory.GetValueOrDefault(jobId!), category);
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
                    Category = perJobCategory.GetValueOrDefault(jobId) ?? ProjectTokenCategory.Of(jobId, jobsById),
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

    public static int ResolveDays(int requested) =>
        requested <= 0 ? DefaultHeatmapDays : Math.Min(requested, MaxHeatmapDays);

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
}
