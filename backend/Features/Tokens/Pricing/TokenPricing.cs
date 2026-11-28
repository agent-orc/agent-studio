namespace AgentStudio.Tokens;

/// <summary>Prices normalized ledger entries at their recorded time.</summary>
public sealed class TokenPricingService(ITokenPriceProvider provider) : ITokenPricing
{
    public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
        => provider.Estimate(ModelIdentity.Resolve(modelId)?.ModelId ?? modelId,
            inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, recordedAt);

    public TokenCostEstimate EstimateRequest(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, long promptTokens, DateTime recordedAt)
        => provider.EstimateRequest(ModelIdentity.Resolve(modelId)?.ModelId ?? modelId,
            inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, promptTokens, recordedAt);
}

/// <summary>Projects the legacy receipt ledger through the shared pricing seam.</summary>
public static class TokenLedgerPricing
{
    public static IReadOnlyList<PricedTokenEntry> Price(this ITokenPricing pricing, IReadOnlyList<OrchestratorLogEntry> ledger)
        => ledger.Select(entry => new PricedTokenEntry(entry, entry.TokenUsage is { } usage
            ? pricing.EstimateReceipt(usage, entry.Ts)
            : null)).ToList();
}
