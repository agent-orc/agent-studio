namespace AgentStudio.Tokens;

/// <summary>
/// Pure fold of priced ad-hoc one-shot calls (TitleGen, SummaryGen, ...)
/// into totals and per-source, per-day, and per-model breakdowns. The cost
/// is a list-price comparison, not a bill.
/// </summary>
public static class AdHocUsageFold
{
    public const string Disclaimer =
        "Theoretical API cost based on Anthropic's published per-million-token rates. " +
        "Your actual usage is billed through the Claude CLI subscription you signed in with " +
        "(Pro / Max / Team / Enterprise), so the dollar number is a comparison, not a bill.";

    public static AdHocUsageAggregate Aggregate(
        IReadOnlyList<(AdHocUsageRecord Record, TokenCostEstimate Cost)> calls,
        string logPath)
    {
        long totalIn = 0, totalOut = 0, totalCacheR = 0, totalCacheW = 0;
        int totalCalls = 0;
        decimal totalCost = 0;
        bool allPriced = calls.Count > 0;

        var bySource = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        var byDay = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        foreach (var (r, cost) in calls)
        {
            totalCalls++;
            totalIn += r.InputTokens;
            totalOut += r.OutputTokens;
            totalCacheR += r.CacheReadTokens;
            totalCacheW += r.CacheCreationTokens;

            totalCost += cost.Total;
            if (!cost.ModelKnown) allPriced = false;

            Add(bySource, string.IsNullOrWhiteSpace(r.Source) ? AdHocUsageSources.Unknown : r.Source, r, cost);
            Add(byModel, ModelIdentity.Resolve(r.Model)?.ModelId ?? "(unknown)", r, cost);
            Add(byDay, r.Ts.ToUniversalTime().ToString("yyyy-MM-dd"), r, cost);
        }

        // Sources with equal call counts sort by key, independent of arrival order.
        var sourceList = bySource
            .OrderByDescending(kv => kv.Value.Calls)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AdHocUsageBySource(
                Source: kv.Key,
                Calls: kv.Value.Calls,
                InputTokens: kv.Value.Input,
                OutputTokens: kv.Value.Output,
                CacheReadTokens: kv.Value.CacheRead,
                CacheCreationTokens: kv.Value.CacheCreate,
                EstimatedApiCostUsd: kv.Value.Cost))
            .ToList();

        var dayList = byDay
            .OrderByDescending(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AdHocUsageByDay(
                Date: kv.Key,
                Calls: kv.Value.Calls,
                InputTokens: kv.Value.Input,
                OutputTokens: kv.Value.Output,
                CacheReadTokens: kv.Value.CacheRead,
                CacheCreationTokens: kv.Value.CacheCreate,
                EstimatedApiCostUsd: kv.Value.Cost))
            .ToList();

        var modelList = byModel
            .OrderByDescending(kv => kv.Value.Input + kv.Value.Output)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var priced = kv.Value.AllPriced;
                return new AdHocUsageByModel(
                    Model: ModelIdentity.Resolve(kv.Key)?.DisplayLabel ?? kv.Key,
                    Calls: kv.Value.Calls,
                    InputTokens: kv.Value.Input,
                    OutputTokens: kv.Value.Output,
                    CacheReadTokens: kv.Value.CacheRead,
                    CacheCreationTokens: kv.Value.CacheCreate,
                    EstimatedApiCostUsd: kv.Value.Cost,
                    ModelPriced: priced)
                { ModelId = kv.Key, Cli = ModelIdentity.Resolve(kv.Key)?.Cli };
            })
            .ToList();

        return new AdHocUsageAggregate(
            Calls: totalCalls,
            InputTokens: totalIn,
            OutputTokens: totalOut,
            CacheReadTokens: totalCacheR,
            CacheCreationTokens: totalCacheW,
            EstimatedApiCostUsd: totalCost,
            AllModelsPriced: allPriced,
            BySource: sourceList,
            ByDay: dayList,
            ByModel: modelList,
            LogPath: logPath,
            LogSizeBytes: 0,
            LogModifiedAt: null,
            Disclaimer: Disclaimer);
    }

    private static void Add(Dictionary<string, Bucket> map, string key, AdHocUsageRecord r, TokenCostEstimate cost)
    {
        if (!map.TryGetValue(key, out var b))
        {
            b = new Bucket();
            map[key] = b;
        }
        b.Calls++;
        b.AllPriced &= cost.ModelKnown;
        b.Input += r.InputTokens;
        b.Output += r.OutputTokens;
        b.CacheRead += r.CacheReadTokens;
        b.CacheCreate += r.CacheCreationTokens;
        b.Cost += cost.Total;
    }

    private sealed class Bucket
    {
        public bool AllPriced = true;
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreate;
        public decimal Cost;
    }
}
