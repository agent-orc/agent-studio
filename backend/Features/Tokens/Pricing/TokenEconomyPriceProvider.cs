using EconomyPricing = TokenEconomy;

namespace AgentStudio.Tokens;

/// <summary>
/// Adapts the published TokenEconomy package to Studio's stable pricing
/// contract. Package-specific types and cost projection stay behind
/// <see cref="ITokenPriceProvider"/>.
/// </summary>
public sealed class TokenEconomyPriceProvider : ITokenPriceProvider
{
    // Declared before Source: static initializers run in textual order and
    // CreateCatalog reads this date.
    private static readonly DateTime Gpt62Release = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    private static readonly EconomyPricing.ModelPriceCatalog Source = CreateCatalog();

    /// <summary>Codex credits per MTok are the list price times this rate.</summary>
    public const decimal CodexCreditsPerUsd = 25m;

    private static EconomyPricing.ModelPriceCatalog CreateCatalog()
    {
        var published = EconomyPricing.ModelPriceCatalog.Default;
        // Local dated seed until the pinned TokenEconomy includes TE-82
        // (0.3.7 to 0.3.9 carry no GPT-6.2 listing). A published entry wins.
        var seeds = new[]
            {
                Gpt62Listing("gpt-6.2-sol", "GPT-6.2 Sol", 2m, 0.10m, 2.50m, 10m,
                    "Long context (>272K input): $4 input, $0.20 cached input, $5 cache write, $15 output per MTok."),
                Gpt62Listing("gpt-6.2-astra", "GPT-6.2 Astra", 8m, 0.40m, 10m, 40m,
                    "Long context (>272K input): $16 input, $0.80 cached input, $20 cache write, $60 output per MTok."),
            }
            .Where(seed => published.Find(seed.ModelId) is null)
            .ToArray();
        return seeds.Length == 0
            ? published
            : new EconomyPricing.ModelPriceCatalog(published.Listings.Concat(seeds));
    }

    private static EconomyPricing.ModelListing Gpt62Listing(
        string modelId, string displayName,
        decimal input, decimal cacheRead, decimal cacheWrite, decimal output, string longContext)
        => new()
        {
            ModelId = modelId,
            DisplayName = displayName,
            Vendor = "openai",
            ReleaseDate = DateOnly.FromDateTime(Gpt62Release),
            ReleaseDateSource = "https://developers.openai.com/api/docs/changelog",
            History = [new EconomyPricing.ModelPrice
            {
                InputPerMTok = input, CacheReadPerMTok = cacheRead,
                OutputPerMTok = output, CacheWritePerMTok = cacheWrite,
                Currency = "USD", ValidFrom = Gpt62Release,
                Source = "https://developers.openai.com/api/docs/pricing",
                VerifiedOn = new DateOnly(2026, 11, 18),
                Note = "Standard short-context API tariff (<=272K input), 30-minute cache writes. "
                       + longContext
                       + $" Codex credits: {input * CodexCreditsPerUsd:0.##} input, {cacheRead * CodexCreditsPerUsd:0.##} cached input,"
                       + $" {output * CodexCreditsPerUsd:0.##} output per MTok (list price x {CodexCreditsPerUsd:0}).",
            }],
        };

    public static IReadOnlyDictionary<string, EconomyPricing.ModelListing> Catalog { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, EconomyPricing.ModelListing>(
            Source.Listings.ToDictionary(x => x.ModelId, StringComparer.OrdinalIgnoreCase));

    public static string Version { get; } =
        $"TokenEconomy/{typeof(EconomyPricing.ModelPriceCatalog).Assembly.GetName().Version}+studio-2026-11-18";

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
