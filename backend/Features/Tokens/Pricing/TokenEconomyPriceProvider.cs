using EconomyPricing = TokenEconomy;

namespace AgentStudio.Tokens;

/// <summary>
/// Adapts the published TokenEconomy package to Studio's stable pricing
/// contract. Package-specific types and cost projection stay behind
/// <see cref="ITokenPriceProvider"/>.
/// </summary>
public sealed class TokenEconomyPriceProvider : ITokenPriceProvider
{
    private static readonly EconomyPricing.ModelPriceCatalog Source =
        EconomyPricing.ModelPriceCatalog.Default;

    public static IReadOnlyDictionary<string, EconomyPricing.ModelListing> Catalog { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, EconomyPricing.ModelListing>(
            Source.Listings.ToDictionary(x => x.ModelId, StringComparer.OrdinalIgnoreCase));

    public static string Version { get; } =
        $"TokenEconomy/{typeof(EconomyPricing.ModelPriceCatalog).Assembly.GetName().Version}";

    internal static EconomyPricing.ModelListing? FindListing(string modelId) => Source.Find(modelId);

    public TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        DateTime? recordedAt = null)
        => EstimateCore(modelId, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, recordedAt, null);

    public TokenCostEstimate EstimateRequest(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, long promptTokens, DateTime recordedAt)
        => EstimateCore(modelId, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, recordedAt, promptTokens);

    private static TokenCostEstimate EstimateCore(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt, long? promptTokens)
    {
        var key = modelId?.Trim() ?? "";
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var cost = Source.ComputeCost(
            key,
            new EconomyPricing.TokenUsage(
                inputTokens,
                outputTokens,
                cacheReadTokens,
                cacheCreationTokens, PromptTokens: promptTokens),
            atUtc);

        // A small aggregate establishes an upper bound for every constituent
        // request. Larger aggregates do not establish a prompt-size tariff.
        var promptUpperBound = Math.Max(0, inputTokens) + Math.Max(0, cacheReadTokens) + Math.Max(0, cacheCreationTokens);
        if (cost.Status == EconomyPricing.PriceStatus.AmbiguousPromptSize && cost.Price is { } datedPrice
            && datedPrice.PromptSizeTiers.All(tier => promptUpperBound < tier.MinPromptTokens))
            cost = Source.ComputeCost(key, new EconomyPricing.TokenUsage(inputTokens, outputTokens,
                cacheReadTokens, cacheCreationTokens, PromptTokens: promptUpperBound), atUtc);

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
        var tier = cost.AppliedPromptSizeTier;
        var inputRate = tier?.InputPerMTok ?? price.InputPerMTok;
        var basis = new TokenPriceBasis(
            inputRate,
            tier?.OutputPerMTok ?? price.OutputPerMTok,
            tier?.CacheReadPerMTok ?? price.CacheReadPerMTok ?? inputRate,
            tier?.CacheWritePerMTok ?? price.CacheWritePerMTok ?? inputRate,
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
