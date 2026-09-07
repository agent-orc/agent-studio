using System.Reflection;
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
    TokenPriceBasis? PriceBasis,
    long PricedInputTokens = 0);

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

    /// <summary>The exact TokenEconomy package version supplying the catalog.</summary>
    public static string CatalogVersion { get; } =
        typeof(EconomyPricing.ModelPriceCatalog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(EconomyPricing.ModelPriceCatalog).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Resolves a persisted model id, alias, or display name to the canonical
    /// id owned by TokenEconomy. The Studio metadata registry is a fallback for
    /// models that have not entered the price catalog yet.
    /// </summary>
    public static string CanonicalModelId(string? recordedModel)
    {
        if (string.IsNullOrWhiteSpace(recordedModel)) return "";

        var recorded = NormalizeDisplayName(recordedModel);
        var listing = Source.Find(recorded)
                      ?? Source.Listings.FirstOrDefault(candidate =>
                          string.Equals(
                              NormalizeDisplayName(candidate.DisplayName),
                              recorded,
                              StringComparison.OrdinalIgnoreCase));
        if (listing is not null) return listing.ModelId;

        return ModelMetadataRegistry.FindByLabelOrAlias(recorded)?.Id ?? recorded;
    }

    /// <summary>
    /// Returns the shared TokenEconomy display name for a recorded model. The
    /// Studio registry remains a fallback for models not represented there.
    /// </summary>
    public static string? ModelDisplayName(string? recordedModel)
    {
        var canonical = CanonicalModelId(recordedModel);
        if (string.IsNullOrWhiteSpace(canonical)) return null;

        return Source.Find(canonical)?.DisplayName
               ?? ModelMetadataRegistry.FindByLabelOrAlias(canonical)?.Label
               ?? canonical;
    }

    /// <summary>True when an id, alias, or display name resolves in TokenEconomy.</summary>
    public static bool ModelInCatalog(string? recordedModel)
        => Source.Find(CanonicalModelId(recordedModel)) is not null;

    public static TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        DateTime? recordedAt = null)
    {
        return Provider.Estimate(modelId, inputTokens, outputTokens, cacheReadTokens,
            cacheCreationTokens, recordedAt);
    }

    private static string NormalizeDisplayName(string? value)
        => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
