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
        CreateCatalog();

    private static EconomyPricing.ModelPriceCatalog CreateCatalog()
    {
        var published = EconomyPricing.ModelPriceCatalog.Default;
        if (published.Find("claude-fable-5-2") is not null) return published;
        // Local dated seed until the pinned TokenEconomy includes TE-82.
        // Standard API rates; fast mode is $16 input / $80 output per MTok.
        return new EconomyPricing.ModelPriceCatalog(published.Listings.Append(new EconomyPricing.ModelListing
        {
            ModelId = "claude-fable-5-2",
            DisplayName = "Claude Fable 5.2",
            Vendor = "anthropic",
            Aliases = ["claude-fable-5.2"],
            ReleaseDate = new DateOnly(2026, 11, 9),
            ReleaseDateSource = "https://platform.claude.com/docs/en/models/fable-5-2/overview",
            History = [new EconomyPricing.ModelPrice
            {
                InputPerMTok = 8m, CacheReadPerMTok = 0.20m,
                OutputPerMTok = 40m, CacheWritePerMTok = 10m,
                Currency = "USD", ValidFrom = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc),
                Source = "https://platform.claude.com/docs/en/about-claude/pricing",
                VerifiedOn = new DateOnly(2026, 11, 17),
                Note = "Standard API, 5-minute cache writes. Fast mode: $16 input / $80 output per MTok. 1M context, 128K output.",
            }],
        }));
    }

    public static IReadOnlyDictionary<string, EconomyPricing.ModelListing> Catalog { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, EconomyPricing.ModelListing>(
            Source.Listings.ToDictionary(x => x.ModelId, StringComparer.OrdinalIgnoreCase));

    public static string Version { get; } =
        $"TokenEconomy/{typeof(EconomyPricing.ModelPriceCatalog).Assembly.GetName().Version}+studio-2026-11-17";

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
