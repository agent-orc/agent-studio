using EconomyPricing = TokenEconomy;

namespace AgentStudio.Runner;

/// <summary>
/// Adapts the published TokenEconomy package to Studio's stable pricing
/// contract. Package-specific types and cost projection stay behind
/// <see cref="ITokenPriceProvider"/>.
/// </summary>
public sealed class TokenEconomyPriceProvider : ITokenPriceProvider
{
    private static readonly EconomyPricing.ModelPriceCatalog Source =
        EconomyPricing.ModelPriceCatalog.Default;

    public TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        DateTime? recordedAt = null)
    {
        var key = TokenPricing.CanonicalModelId(modelId);
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var listing = Source.Find(key);
        var normalizedInputTokens = Math.Max(0L, inputTokens);
        var normalizedCacheReadTokens = Math.Max(0L, cacheReadTokens);

        // OpenAI usage reports cached_input_tokens as a subset of input_tokens,
        // while TokenEconomy expects fresh input and cache reads separately.
        // Anthropic and the other adapters already persist separate counters.
        if (string.Equals(listing?.Vendor, "openai", StringComparison.OrdinalIgnoreCase))
        {
            normalizedInputTokens = Math.Max(0L, normalizedInputTokens - normalizedCacheReadTokens);
        }

        var cost = Source.ComputeCost(
            key,
            new EconomyPricing.TokenUsage(
                normalizedInputTokens,
                outputTokens,
                normalizedCacheReadTokens,
                cacheCreationTokens),
            atUtc);

        if (!cost.HasPrice || cost.Total is null || cost.Price is null)
        {
            return new TokenCostEstimate(
                0m,
                0m,
                0m,
                0m,
                0m,
                cost.ModelId ?? key,
                ModelKnown: false,
                cost.Status,
                PriceBasis: null,
                PricedInputTokens: normalizedInputTokens);
        }

        var price = cost.Price;
        var basis = new TokenPriceBasis(
            price.InputPerMTok,
            price.OutputPerMTok,
            price.CacheReadPerMTok ?? price.InputPerMTok,
            price.CacheWritePerMTok ?? price.InputPerMTok,
            price.Currency,
            price.ValidFrom,
            price.Source,
            price.Note,
            price.Unconfirmed);

        return new TokenCostEstimate(
            cost.InputCost,
            cost.OutputCost,
            cost.CacheReadCost,
            cost.CacheWriteCost,
            cost.Total.Value,
            cost.ModelId ?? key,
            ModelKnown: true,
            cost.Status,
            basis,
            PricedInputTokens: normalizedInputTokens);
    }
}
