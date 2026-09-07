using AgentStudio.AdHoc;
using AgentStudio.Runner;

namespace AgentStudio.Tokens;

public sealed record TokenPricingDiagnosticModel(
    string ModelId,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal PricedCostUsd,
    bool ModelInCatalog,
    bool AllRecordedUsagePriced,
    string ResolutionStatus,
    bool HasRecordedTokens,
    IReadOnlyList<string> Sources);

public sealed record TokenPricingDiagnosticsResponse(
    string Provider,
    string CheckedAt,
    int CatalogModelCount,
    int RecordedModelCount,
    int PricedModelCount,
    int UnpricedModelCount,
    int UnknownModelCount,
    string CoverageStatus,
    IReadOnlyList<string> CoverageWarnings,
    IReadOnlyList<TokenPricingDiagnosticModel> Models,
    IReadOnlyList<TokenPricingDiagnosticModel> UnpricedModels,
    IReadOnlyList<TokenPricingDiagnosticModel> UnknownModels);

/// <summary>
/// Pure coverage projection for the operator-facing pricing diagnostic. It
/// reports catalog absence separately from a known model with no price for a
/// recorded date, and retains zero-token calls so historical gaps stay visible.
/// </summary>
public static class TokenPricingDiagnostics
{
    public static TokenPricingDiagnosticsResponse Build(
        TokenSummaryAggregate workspace,
        AdHocUsageAggregate? adHoc = null,
        DateTime? checkedAt = null)
    {
        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in workspace.ByModel)
        {
            Add(
                buckets,
                model.Model,
                model.Calls,
                model.InputTokens,
                model.OutputTokens,
                model.CacheReadTokens,
                model.CacheCreationTokens,
                model.EstimatedApiCostUsd,
                model.ModelPriced,
                "project-lifetime");
        }

        if (adHoc is not null)
        {
            foreach (var model in adHoc.ByModel)
            {
                var canonicalId = TokenPricing.NormalizeModelId(model.Model);
                Add(
                    buckets,
                    canonicalId,
                    model.Calls,
                    model.InputTokens,
                    model.OutputTokens,
                    model.CacheReadTokens,
                    model.CacheCreationTokens,
                    model.EstimatedApiCostUsd,
                    model.ModelPriced,
                    "ad-hoc");
            }
        }

        var models = buckets.Values
            .Select(bucket => bucket.ToDiagnostic())
            .OrderByDescending(model => model.InputTokens + model.OutputTokens)
            .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unpriced = models.Where(model => !model.AllRecordedUsagePriced).ToList();
        var unknown = models.Where(model => !model.ModelInCatalog).ToList();

        return new TokenPricingDiagnosticsResponse(
            Provider: "TokenEconomy",
            CheckedAt: (checkedAt ?? DateTime.UtcNow).ToUniversalTime().ToString("o"),
            CatalogModelCount: TokenPricing.Catalog.Count,
            RecordedModelCount: models.Count,
            PricedModelCount: models.Count - unpriced.Count,
            UnpricedModelCount: unpriced.Count,
            UnknownModelCount: unknown.Count,
            CoverageStatus: workspace.CoverageStatus,
            CoverageWarnings: workspace.CoverageWarnings ?? [],
            Models: models,
            UnpricedModels: unpriced,
            UnknownModels: unknown);
    }

    private static void Add(
        IDictionary<string, Bucket> buckets,
        string? model,
        int calls,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        decimal pricedCostUsd,
        bool modelPriced,
        string source)
    {
        var canonicalId = TokenPricing.NormalizeModelId(model);
        var key = string.IsNullOrWhiteSpace(canonicalId) ? "(unknown)" : canonicalId;
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(key);
            buckets[key] = bucket;
        }

        bucket.Calls += calls;
        bucket.InputTokens += inputTokens;
        bucket.OutputTokens += outputTokens;
        bucket.CacheReadTokens += cacheReadTokens;
        bucket.CacheCreationTokens += cacheCreationTokens;
        bucket.PricedCostUsd += pricedCostUsd;
        bucket.ModelInCatalog &= TokenPricing.Catalog.ContainsKey(key);
        bucket.AllRecordedUsagePriced &= modelPriced;
        bucket.Sources.Add(source);
    }

    private sealed class Bucket
    {
        public Bucket(string modelId) => ModelId = modelId;

        public string ModelId { get; }
        public int Calls { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public decimal PricedCostUsd { get; set; }
        public bool ModelInCatalog { get; set; } = true;
        public bool AllRecordedUsagePriced { get; set; } = true;
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public TokenPricingDiagnosticModel ToDiagnostic()
            => new(
                ModelId,
                Calls,
                InputTokens,
                OutputTokens,
                CacheReadTokens,
                CacheCreationTokens,
                PricedCostUsd,
                ModelInCatalog,
                AllRecordedUsagePriced,
                AllRecordedUsagePriced
                    ? nameof(TokenEconomy.PriceStatus.Resolved)
                    : ModelInCatalog
                        ? nameof(TokenEconomy.PriceStatus.NoPriceForDate)
                        : nameof(TokenEconomy.PriceStatus.UnknownModel),
                InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens > 0,
                Sources.Order(StringComparer.Ordinal).ToList());
    }
}
