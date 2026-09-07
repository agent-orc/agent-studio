

namespace AgentStudio.AdHoc;

/// <summary>
/// Reads <see cref="AdHocUsageRecorder"/>'s JSONL log and produces a
/// rolled-up <see cref="AdHocUsageAggregate"/>: totals, per-source
/// breakdown, per-day breakdown, per-model breakdown, and a theoretical
/// USD estimate via the existing <see cref="TokenPricing"/> table.
///
/// <para>
/// The estimate is the same kind of "comparison number, not a bill" the
/// per-project token summary already shows, with the same disclaimer:
/// the user pays via CLI subscription plans, not per-token API rates.
/// </para>
///
/// <para>
/// The pure overload <see cref="Aggregate(IReadOnlyList{AdHocUsageRecord}, string, long, DateTime?)"/>
/// is exposed for tests so the rollup math can be exercised without
/// touching disk.
/// </para>
/// </summary>
public sealed class AdHocUsageService
{
    public const string DefaultDisclaimer =
        "Theoretical API cost based on Anthropic's published per-million-token rates. " +
        "Your actual usage is billed through the Claude CLI subscription you signed in with " +
        "(Pro / Max / Team / Enterprise), so the dollar number is a comparison, not a bill.";

    private readonly AdHocUsageRecorder _recorder;
    private readonly BusBackedAdHocUsageReader? _busReader;

    public AdHocUsageService(AdHocUsageRecorder recorder, BusBackedAdHocUsageReader? busReader = null)
    {
        _recorder = recorder;
        _busReader = busReader;
    }

    /// <summary>
    /// Read the full log and aggregate.
    /// </summary>
    public AdHocUsageAggregate Aggregate(DateTime? since = null)
    {
        if (_busReader != null)
            return _busReader.Aggregate(since);

        var records = _recorder.ReadAll();
        if (since is DateTime cutoff)
            records = records.Where(r => r.Ts >= cutoff).ToList();
        var (size, modified) = _recorder.Stat();
        return Aggregate(records, _recorder.LogPath, size, modified);
    }

    /// <summary>
    /// Pure overload for tests: aggregate the supplied records directly.
    /// </summary>
    public static AdHocUsageAggregate Aggregate(
        IReadOnlyList<AdHocUsageRecord> records,
        string logPath,
        long logSizeBytes,
        DateTime? logModifiedAt)
    {
        long totalIn = 0, totalOut = 0, totalCacheR = 0, totalCacheW = 0;
        int totalCalls = 0;
        decimal totalCost = 0;
        bool allPriced = records.Count > 0;

        var bySource = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        var byDay = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        foreach (var r in records)
        {
            totalCalls++;
            totalIn += r.InputTokens;
            totalOut += r.OutputTokens;
            totalCacheR += r.CacheReadTokens;
            totalCacheW += r.CacheCreationTokens;

            var canonicalModel = TokenPricing.CanonicalModelId(r.Model);
            var modelKey = string.IsNullOrWhiteSpace(canonicalModel) ? "(unknown)" : canonicalModel;
            var cost = TokenPricing.Estimate(modelKey, r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheCreationTokens, r.Ts);
            totalCost += cost.Total;
            if (!cost.ModelKnown) allPriced = false;

            Add(bySource, string.IsNullOrWhiteSpace(r.Source) ? AdHocUsageSources.Unknown : r.Source, r, cost);
            Add(byModel, modelKey, r, cost);
            Add(byDay, r.Ts.ToUniversalTime().ToString("yyyy-MM-dd"), r, cost);
        }

        // Stable tie-break by source key so two sources with equal call counts
        // always sort the same way regardless of insertion order. Without this,
        // the bus-backed reader's order (ULID / arrival order) can diverge from
        // the JSONL reader's order (file-write order) for tied buckets.
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
                    Model: TokenModelDisplay.Label(kv.Key) ?? kv.Key,
                    Calls: kv.Value.Calls,
                    InputTokens: kv.Value.Input,
                    OutputTokens: kv.Value.Output,
                    CacheReadTokens: kv.Value.CacheRead,
                    CacheCreationTokens: kv.Value.CacheCreate,
                    EstimatedApiCostUsd: kv.Value.Cost,
                    ModelPriced: priced,
                    ModelId: kv.Key);
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
            LogSizeBytes: logSizeBytes,
            LogModifiedAt: logModifiedAt,
            Disclaimer: DefaultDisclaimer);
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
