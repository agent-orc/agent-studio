

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the per-model price math so a future price update has an exact
/// place to update both the catalog and its assertions in lockstep.
/// </summary>
public class TokenPricingTests
{
    private readonly ITokenPriceProvider _provider = new TokenEconomyPriceProvider();

    [Fact]
    public void PublishedTokenEconomyPackage_IsConfiguredVersion()
    {
        var assembly = typeof(TokenEconomy.ModelPriceCatalog).Assembly;
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single()
            .InformationalVersion;

        Assert.Equal("TokenEconomy", assembly.GetName().Name);
        Assert.StartsWith("0.3.3", informationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredProvider_IsTokenEconomyAdapter()
    {
        Assert.IsType<TokenEconomyPriceProvider>(TokenPricing.Provider);
    }

    [Fact]
    public void Estimate_Gpt56SolHistoricalRun_UsesCatalogPriceAtTimestamp()
    {
        var at = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);
        var expectedPrice = TokenPricing.Catalog["gpt-5.6-sol"].History
            .Where(price => price.ValidFrom <= at)
            .MaxBy(price => price.ValidFrom)!;

        var c = _provider.Estimate("gpt-5.6-sol", 1_000_000, 100_000, 0, 0, at);

        Assert.True(c.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.Resolved, c.Status);
        Assert.True(c.Total > 0m);
        Assert.NotNull(c.PriceBasis);
        Assert.Equal(expectedPrice.ValidFrom, c.PriceBasis.ValidFrom);
        Assert.Equal(expectedPrice.InputPerMTok, c.PriceBasis.InputPerMillion);
        Assert.Equal(expectedPrice.OutputPerMTok, c.PriceBasis.OutputPerMillion);
    }
    [Fact]
    public void Estimate_OpusPrices_MatchAnthropicListed()
    {
        // Opus 4.7: $5/M input, $25/M output. 1M input + 200K output =
        // $5 + $5 = $10.
        var at = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);
        var c = _provider.Estimate("claude-opus-4-7",
            inputTokens: 1_000_000, outputTokens: 200_000,
            cacheReadTokens: 0, cacheCreationTokens: 0, recordedAt: at);
        var expectedPrice = TokenPricing.Catalog["claude-opus-4-7"].History
            .Where(price => price.ValidFrom <= at)
            .MaxBy(price => price.ValidFrom)!;
        Assert.True(c.ModelKnown);
        Assert.Equal(5.00m,  c.InputUsd);
        Assert.Equal(5.00m,  c.OutputUsd);
        Assert.Equal(10.00m, c.Total);
        Assert.NotNull(c.PriceBasis);
        Assert.Equal(5m, c.PriceBasis.InputPerMillion);
        Assert.Equal(25m, c.PriceBasis.OutputPerMillion);
        Assert.Equal(0.5m, c.PriceBasis.CacheReadPerMillion);
        Assert.Equal(6.25m, c.PriceBasis.CacheWritePerMillion);
        Assert.Equal(expectedPrice.Source, c.PriceBasis.Source);
        Assert.Equal(expectedPrice.ValidFrom, c.PriceBasis.ValidFrom);
    }

    [Fact]
    public void Estimate_SonnetPrices_AreThreeFifteen()
    {
        // 1M input + 1M output on Sonnet 4.6 = $3 + $15 = $18.
        var c = TokenPricing.Estimate("claude-sonnet-4-6", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(18.00m, c.Total);
    }

    [Fact]
    public void Estimate_HaikuPrices_AreOneFive()
    {
        // 100K input + 50K output on Haiku 4.5 = $0.10 + $0.25 = $0.35.
        var c = TokenPricing.Estimate("claude-haiku-4-5", 100_000, 50_000, 0, 0);
        Assert.Equal(0.10m, c.InputUsd);
        Assert.Equal(0.25m, c.OutputUsd);
        Assert.Equal(0.35m, c.Total);
    }

    [Fact]
    public void Estimate_CacheReadIsTenPercentOfInput()
    {
        // Sonnet $3/M input -> cache read $0.30/M.
        // 10M cache reads -> $3.
        var c = TokenPricing.Estimate("claude-sonnet-4-6", 0, 0,
            cacheReadTokens: 10_000_000, cacheCreationTokens: 0);
        Assert.Equal(3.00m, c.CacheReadUsd);
        Assert.Equal(3.00m, c.Total);
    }

    [Fact]
    public void Estimate_CacheWriteIs125PercentOfInput()
    {
        // Opus $5/M input -> cache write $6.25/M.
        // 1M cache creation -> $6.25.
        var c = TokenPricing.Estimate("claude-opus-4-7", 0, 0, 0, 1_000_000);
        Assert.Equal(6.25m, c.CacheWriteUsd);
    }

    [Fact]
    public void Estimate_Gpt5CodexWithoutPublishedPrice_IsExplicitlyUnknown()
    {
        var c = _provider.Estimate("gpt-5-codex", 1_000_000, 100_000, 1_000_000, 1_000_000);
        Assert.False(c.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.NoPriceForDate, c.Status);
        Assert.Equal(0m, c.Total);
        Assert.Null(c.PriceBasis);
    }

    [Fact]
    public void Estimate_UnknownModel_ReturnsZeroAndModelKnownFalse()
    {
        var c = _provider.Estimate("gpt-5.unknown", 1_000_000, 100_000, 0, 0);
        Assert.False(c.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.UnknownModel, c.Status);
        Assert.Equal(0m, c.Total);
        Assert.Null(c.PriceBasis);
    }

    [Fact]
    public void Estimate_NullOrEmptyModel_ReturnsZeroAndModelKnownFalse()
    {
        Assert.False(TokenPricing.Estimate(null, 1, 1, 0, 0).ModelKnown);
        Assert.False(TokenPricing.Estimate("", 1, 1, 0, 0).ModelKnown);
        Assert.False(TokenPricing.Estimate("   ", 1, 1, 0, 0).ModelKnown);
    }

    [Fact]
    public void Estimate_ModelLookupIsCaseInsensitive()
    {
        var lower = TokenPricing.Estimate("claude-opus-4-7", 1_000_000, 0, 0, 0);
        var upper = TokenPricing.Estimate("CLAUDE-OPUS-4-7", 1_000_000, 0, 0, 0);
        Assert.Equal(lower.Total, upper.Total);
    }

    [Fact]
    public void ModelMetadataPricing_IsPassThroughFromTokenEconomyCatalog()
    {
        foreach (var entry in ModelMetadataRegistry.All.Where(m => m.InputPricePerMillion is not null))
        {
            Assert.True(TokenPricing.Catalog.ContainsKey(entry.Id), entry.Id);
            Assert.NotNull(ModelMetadataRegistry.ContextWindowFor(entry.Id));
        }
    }

    [Fact]
    public void Estimate_UsesPriceValidAtRecordedRunTime()
    {
        var transition = TokenPricing.Catalog["claude-sonnet-5"].History.Max(p => p.ValidFrom);
        var before = _provider.Estimate("claude-sonnet-5", 1_000_000, 0, 0, 0, transition.AddTicks(-1));
        var after = _provider.Estimate("claude-sonnet-5", 1_000_000, 0, 0, 0, transition);

        Assert.True(before.ModelKnown);
        Assert.True(after.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.Resolved, before.Status);
        Assert.Equal(TokenEconomy.PriceStatus.Resolved, after.Status);
        Assert.NotEqual(before.Total, after.Total);
        Assert.NotEqual(before.PriceBasis!.ValidFrom, after.PriceBasis!.ValidFrom);
        Assert.False(string.IsNullOrWhiteSpace(before.PriceBasis.Source));
        Assert.False(string.IsNullOrWhiteSpace(after.PriceBasis.Source));
    }

    [Fact]
    public void Estimate_NormalizesRegisteredAliases()
    {
        var dashed = TokenPricing.Estimate("claude-opus-4-7", 1_000_000, 0, 0, 0);
        var dotted = TokenPricing.Estimate("claude-opus-4.7", 1_000_000, 0, 0, 0);
        Assert.True(dotted.ModelKnown);
        Assert.Equal(dashed.Total, dotted.Total);
        Assert.Equal(ModelIds.ClaudeOpus47, dotted.ModelId);
    }

    [Fact]
    public void Estimate_Gpt55HistoricalRun_UsesCatalogPriceAtTimestamp()
    {
        var at = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var expectedPrice = TokenPricing.Catalog["gpt-5.5"].History
            .Where(price => price.ValidFrom <= at)
            .MaxBy(price => price.ValidFrom)!;

        var c = _provider.Estimate("gpt-5.5", 1_000_000, 100_000, 0, 0, at);

        Assert.True(c.ModelKnown);
        Assert.Equal(TokenEconomy.PriceStatus.Resolved, c.Status);
        Assert.True(c.Total > 0m);
        Assert.NotNull(c.PriceBasis);
        Assert.Equal(expectedPrice.ValidFrom, c.PriceBasis.ValidFrom);
        Assert.Equal(expectedPrice.InputPerMTok, c.PriceBasis.InputPerMillion);
        Assert.Equal(expectedPrice.OutputPerMTok, c.PriceBasis.OutputPerMillion);
    }

    [Fact]
    public void Estimate_Gpt55DisplayNameCasing_ResolvesLikeCanonicalId()
    {
        // The persisted receipt bug (AGT-2752): a receipt recorded the catalog
        // display name "GPT-5.5" instead of the canonical "gpt-5.5". TokenEconomy's
        // own case/dot-dash-insensitive lookup must still resolve it.
        var at = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var canonical = _provider.Estimate("gpt-5.5", 1_000_000, 100_000, 0, 0, at);
        var displayName = _provider.Estimate("GPT-5.5", 1_000_000, 100_000, 0, 0, at);

        Assert.True(displayName.ModelKnown);
        Assert.Equal(canonical.Total, displayName.Total);
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("GPT-5.5")]
    [InlineData("gpt-5.5-2026-04-23")]
    public void CanonicalModelId_ResolvesIdAliasAndDisplayNameToTheSameId(string value)
    {
        Assert.Equal("gpt-5.5", TokenPricing.CanonicalModelId(value));
    }

    [Fact]
    public void CanonicalModelId_UnknownModel_ReturnsNull()
    {
        Assert.Null(TokenPricing.CanonicalModelId("not-a-real-model"));
    }

    [Fact]
    public void CanonicalModelId_NullOrBlank_ReturnsNull()
    {
        Assert.Null(TokenPricing.CanonicalModelId(null));
        Assert.Null(TokenPricing.CanonicalModelId("   "));
    }
}
