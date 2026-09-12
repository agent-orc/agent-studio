namespace AgentStudio.Tokens;

/// <summary>
/// Attributes token calls to the latest quota-admission boundary for the same
/// task and reports the calls whose selected route had benchmark candidates.
/// </summary>
public sealed class BetterCandidateUsageReportService
{
    private readonly OrchestratorLog _log;
    private readonly BusBackedProjectTokenUsageReader _tokenUsage;

    public BetterCandidateUsageReportService(
        OrchestratorLog log,
        BusBackedProjectTokenUsageReader tokenUsage)
    {
        _log = log;
        _tokenUsage = tokenUsage;
    }

    public IReadOnlyList<BetterCandidateUsageLine> Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var lines = new List<BetterCandidateUsageLine>();
        foreach (var (name, watchPath) in projects)
        {
            var admissions = _log.Read(watchPath)
                .Where(IsAdmissionBoundary)
                .ToList();
            var calls = _tokenUsage.LoadSnapshot(name, watchPath).Entries;
            lines.AddRange(Aggregate(name, admissions, calls, windowStart, windowEnd));
        }
        return lines
            .OrderBy(line => line.WeekStart)
            .ThenBy(line => line.Project, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Pure fold used by regression tests.</summary>
    internal static IReadOnlyList<BetterCandidateUsageLine> Aggregate(
        string project,
        IReadOnlyList<OrchestratorLogEntry> admissions,
        IReadOnlyList<OrchestratorLogEntry> tokenCalls,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var byJob = admissions
            .Where(IsAdmissionBoundary)
            .GroupBy(entry => entry.JobId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.Ts).ToList(),
                StringComparer.Ordinal);
        var groups = new Dictionary<DateOnly, UsageBucket>();

        foreach (var call in tokenCalls)
        {
            var usage = call.TokenUsage;
            var at = call.Ts.ToUniversalTime();
            if (usage is null || string.IsNullOrWhiteSpace(call.JobId)
                || at < windowStart || at >= windowEnd
                || !byJob.TryGetValue(call.JobId, out var jobAdmissions))
            {
                continue;
            }

            var admission = jobAdmissions.LastOrDefault(entry => entry.Ts.ToUniversalTime() <= at);
            var note = admission?.BetterCandidates;
            if (note is null || note.Candidates.Count == 0
                || !SameModel(note.CurrentModel, usage.Model))
            {
                continue;
            }

            var week = StartOfWeek(at);
            if (!groups.TryGetValue(week, out var bucket))
            {
                bucket = new UsageBucket();
                groups[week] = bucket;
            }

            bucket.Calls++;
            bucket.Tokens += (long)usage.InputTokens + usage.OutputTokens
                + usage.CacheReadTokens + usage.CacheCreationTokens;
            var estimate = TokenPricing.Estimate(
                usage.Model,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheCreationTokens,
                at);
            if (estimate.ModelKnown)
            {
                bucket.CostUsd = (bucket.CostUsd ?? 0m) + estimate.Total;
                bucket.HasPricedCall = true;
            }
            else
            {
                bucket.HasUnpricedCall = true;
            }
        }

        return groups
            .OrderBy(pair => pair.Key)
            .Select(pair => new BetterCandidateUsageLine(
                Project: project,
                WeekStart: pair.Key.ToString("yyyy-MM-dd"),
                WeekEnd: pair.Key.AddDays(7).ToString("yyyy-MM-dd"),
                Calls: pair.Value.Calls,
                Tokens: pair.Value.Tokens,
                CostUsd: pair.Value.CostUsd,
                AllModelsPriced: pair.Value.HasPricedCall && !pair.Value.HasUnpricedCall))
            .ToList();
    }

    private static bool IsAdmissionBoundary(OrchestratorLogEntry entry)
        => string.Equals(entry.Topic, OrchestratorLogTopics.LoadDistribution, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(entry.JobId);

    private static bool SameModel(string expected, string? actual)
        => string.Equals(
            ModelMetadataRegistry.NormalizeId(expected),
            ModelMetadataRegistry.NormalizeId(actual),
            StringComparison.OrdinalIgnoreCase);

    private static DateOnly StartOfWeek(DateTime timestamp)
    {
        var date = DateOnly.FromDateTime(timestamp);
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysFromMonday);
    }

    private sealed class UsageBucket
    {
        public int Calls;
        public long Tokens;
        public decimal? CostUsd;
        public bool HasPricedCall;
        public bool HasUnpricedCall;
    }
}

/// <summary>
/// One project/week line for spend incurred while the latest admission
/// decision carried at least one better benchmark candidate.
/// </summary>
public sealed record BetterCandidateUsageLine(
    string Project,
    string WeekStart,
    string WeekEnd,
    int Calls,
    long Tokens,
    decimal? CostUsd,
    bool AllModelsPriced);
