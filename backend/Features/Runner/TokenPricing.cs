using EconomyPricing = TokenEconomy;

namespace AgentStudio.Runner;

/// <summary>
/// Studio-facing projection of TokenEconomy's historical pricing result.
/// Decimal fields remain non-null for wire compatibility; <see cref="ModelKnown"/>
/// is the mandatory guard and is false for both unknown models and dates for
/// which the catalog has no price. Consumers must then render "unknown".
/// </summary>
public sealed record TokenCostEstimate(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheReadUsd,
    decimal CacheWriteUsd,
    decimal Total,
    string ModelId,
    bool ModelKnown,
    EconomyPricing.PriceStatus Status,
    TokenPriceBasis? PriceBasis);

/// <summary>The exact historical TokenEconomy catalog entry used for a calculation.</summary>
public sealed record TokenPriceBasis(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion,
    string Currency,
    DateTime ValidFrom,
    string? Source,
    string? Note,
    bool Unconfirmed);

/// <summary>
/// Pricing seam owned by Studio. Provider packages implement this without
/// changing aggregators or API consumers.
/// </summary>
public interface ITokenPriceProvider
{
    TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null);
}

/// <summary>
/// The only Studio pricing entry point. Model catalog, aliases, rates, cache
/// policy, and price history all come from TokenEconomy.
/// </summary>
public static class TokenPricing
{
    private static readonly EconomyPricing.ModelPriceCatalog Source = EconomyPricing.ModelPriceCatalog.Default;

    /// <summary>
    /// Configured pricing provider. Internal visibility lets focused tests pin
    /// the package adapter without exposing provider selection through the API.
    /// </summary>
    internal static ITokenPriceProvider Provider { get; } = new TokenEconomyPriceProvider();

    /// <summary>Read-only TokenEconomy catalog projection retained for catalog consumers.</summary>
    public static IReadOnlyDictionary<string, EconomyPricing.ModelListing> Catalog { get; } =
        Source.Listings.ToDictionary(x => x.ModelId, StringComparer.OrdinalIgnoreCase);

    public static TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        DateTime? recordedAt = null)
    {
        return Provider.Estimate(AgentStudio.Tokens.ModelIdentity.Resolve(modelId)?.ModelId ?? modelId,
            inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, recordedAt);
    }
}
