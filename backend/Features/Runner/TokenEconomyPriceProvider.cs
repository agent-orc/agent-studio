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
        var key = TokenPricing.NormalizeModelId(modelId);
        var billableInputTokens = TokenPricing.BillableInputTokens(key, inputTokens, cacheReadTokens);
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var cost = Source.ComputeCost(
            key,
            new EconomyPricing.TokenUsage(
                billableInputTokens,
                outputTokens,
                cacheReadTokens,
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
                PriceBasis: null);
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
            basis);
    }
}
