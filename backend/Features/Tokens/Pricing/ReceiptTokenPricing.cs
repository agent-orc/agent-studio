using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

/// <summary>Receipt-aware pricing: missing cache evidence is not uncached input evidence.</summary>
public static class ReceiptTokenPricing
{
    public static bool MissingCacheSplit(string? model, long input, long cacheRead,
        bool? inputIncludesCached, string? normalization)
        => ProviderUsageNormalization.IsOpenAiModel(model)
           && input > 0 && cacheRead == 0 && inputIncludesCached is null
           && string.IsNullOrWhiteSpace(normalization);

    public static TokenCostEstimate EstimateReceipt(this ITokenPricing pricing,
        OrchestratorTokenUsage usage, DateTime at)
        => pricing.EstimateRecorded(usage.Model, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, usage.CacheCreationTokens, at, usage.InputIncludesCached, usage.UsageNormalization);

    public static bool HasUsableStoredCost(TaskTokenCall call)
        => call.ModelPriced && !MissingCacheSplit(call.Model, call.InputTokens, call.CacheReadTokens,
            call.InputIncludesCached, call.UsageNormalization);

    public static TokenCostEstimate EstimateRecorded(this ITokenPricing pricing, string? model,
        long input, long output, long cacheRead, long cacheWrite, DateTime? at,
        bool? inputIncludesCached, string? normalization)
    {
        var cost = pricing.Estimate(model, input, output, cacheRead, cacheWrite, at);
        return cost.ModelKnown && MissingCacheSplit(model, input, cacheRead, inputIncludesCached, normalization)
            ? cost with
            {
                InputUsd = 0,
                OutputUsd = 0,
                CacheReadUsd = 0,
                CacheWriteUsd = 0,
                Total = 0,
                ModelKnown = false,
                IncompleteReason = "missing-cache-split",
            }
            : cost;
    }
}
