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
    {
        var key = modelId?.Trim() ?? "";
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var cost = Source.ComputeCost(
            key,
            new EconomyPricing.TokenUsage(
                inputTokens,
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
