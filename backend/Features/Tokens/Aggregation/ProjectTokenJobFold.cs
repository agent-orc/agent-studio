namespace AgentStudio.Tokens;

/// <summary>
/// Pure per-job folds behind the project Token Usage panel: the
/// expensive-jobs list and the per-call drill-down of one job.
/// </summary>
public static class ProjectTokenJobFold
{
    /// <summary>Default top-N for the expensive-jobs list.</summary>
    public const int DefaultExpensiveLimit = 10;
    /// <summary>Hard cap for the expensive-jobs list.</summary>
    public const int MaxExpensiveLimit = 50;

    /// <summary>
    /// Top N jobs by total tokens. Used by the panel's expensive-jobs
    /// list. Always returns "real" jobs first - a JobId that no longer
    /// resolves to a folder still appears with its raw id as the title
    /// so deleted-job spend stays visible (Critical Boundaries: Token
    /// usage is accountability).
    /// </summary>
    public static IReadOnlyList<ProjectExpensiveJob> ExpensiveJobs(
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
            acc.Category = ProjectTokenCategory.Prefer(acc.Category, ProjectTokenCategory.Of(entry, jobsById));
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
                    Category = p.Value.Category ?? ProjectTokenCategory.Of(jobId, jobsById),
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
    public static ProjectJobTokenDetail? JobDetail(
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
            category = ProjectTokenCategory.Prefer(category, ProjectTokenCategory.Of(entry, jobsById));
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
            Category = category ?? ProjectTokenCategory.Of(jobId, jobsById),
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

    public static int ResolveExpensiveLimit(int requested) =>
        requested <= 0 ? DefaultExpensiveLimit : Math.Min(requested, MaxExpensiveLimit);

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
