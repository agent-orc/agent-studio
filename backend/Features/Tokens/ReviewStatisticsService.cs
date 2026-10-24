using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

/// <summary>Cached, read-only review projection from the canonical project ledger.</summary>
public sealed class ReviewStatisticsService(
    ProjectTokenLedger ledger, JobStatsMetadataCache jobs, ITokenPricing pricing,
    TaskIndexCache index, TimeProvider clock)
{
    private readonly GenerationSingleFlightCache<ReviewStatistics> _cache = new(clock);

    public ReviewStatistics Get(string project, string watchPath, int days,
        string? model, string? level, string? host)
    {
        var key = string.Join('\n', project, watchPath, days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            model ?? "", level ?? "", host ?? "");
        return _cache.GetOrCreateVersioned(key,
            index.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(ProjectTokenLedger.SnapshotTtlSeconds), () => Build(project, watchPath, days, model, level, host));
    }

    private ReviewStatistics Build(string project, string watchPath, int days,
        string? model, string? level, string? host)
    {
        var snapshot = ledger.LoadSnapshot(project, watchPath);
        var taskMap = jobs.JobsById(watchPath);
        var cardTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ReviewUsageRow>();
        foreach (var entry in pricing.Price(snapshot.Entries))
        {
            var log = entry.Entry;
            var usage = entry.Usage;
            if (usage is null || string.IsNullOrWhiteSpace(log.JobId)) continue;
            var total = (long)usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheCreationTokens;
            if (total <= 0) continue;
            var cardKey = project + "/" + log.JobId;
            cardTotals[cardKey] = cardTotals.GetValueOrDefault(cardKey) + total;
            var step = ReviewStatisticsFold.Step(log.ParticipantId);
            if (step is null) continue;
            rows.Add(new ReviewUsageRow(project, log.JobId!,
                ReviewStatisticsFold.Attempt(log.ParticipantId, log.Ts), step, log.Ts,
                string.IsNullOrWhiteSpace(usage.Model) ? "model unknown" : usage.Model!,
                string.IsNullOrWhiteSpace(usage.ThinkingLevel) ? "level unknown" : usage.ThinkingLevel!,
                TokenUsageHost.Resolve(usage.Host, log.ParticipantId), total,
                entry.Cost?.ModelKnown == true ? entry.Cost.Total : null));
        }
        foreach (var task in taskMap.Values)
        {
            if (task.State is "6-completed" or "7-archive") accepted.Add(project + "/" + task.Id);
        }
        var result = ReviewStatisticsFold.Build(rows, cardTotals, accepted,
            clock.GetUtcNow().UtcDateTime, days, model, level, host);
        return result with
        {
            Warning = string.Join(" ", new[] { snapshot.Freshness.Warning, result.Warning }
                .Where(text => !string.IsNullOrWhiteSpace(text))) is { Length: > 0 } warning ? warning : null,
        };
    }
}
