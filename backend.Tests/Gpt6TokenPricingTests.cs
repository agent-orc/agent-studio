using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2903: GPT-6 runs are priced from the TokenEconomy dated snapshots (GPT-6.2
/// from the dated local seed until TE-82, AGT-3958),
/// and cached input is counted once at the cache-read rate (AGT-2882).
/// </summary>
public sealed class Gpt6TokenPricingTests
{
    // clock-independent: pricing uses the supplied historical timestamp and never compares it with now.
    private static readonly DateTime AfterRelease = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    // model, input, cached, output per MTok (operator facts, 2026-09-25)
    [InlineData("gpt-6-sol", 2.0, 0.20, 10.0)]
    [InlineData("gpt-6-luna", 0.10, 0.01, 0.50)]
    [InlineData("gpt-6.1-sol", 2.0, 0.10, 10.0)]
    public void Gpt6_prices_resolve_from_TokenEconomy(string model, double input, double cached, double output)
    {
        var estimate = TokenTestServices.Pricing().Estimate(model, 1_000_000, 1_000_000, 1_000_000, 0, AfterRelease);

        Assert.True(estimate.ModelKnown);
        Assert.Equal((decimal)input, estimate.PriceBasis!.InputPerMillion);
        Assert.Equal((decimal)cached, estimate.PriceBasis.CacheReadPerMillion);
        Assert.Equal((decimal)output, estimate.PriceBasis.OutputPerMillion);
        Assert.Equal((decimal)(input + cached + output), estimate.Total);
    }

    [Theory]
    // model, input, cached input, 30-minute cache write, output per MTok (verified 2026-11-18)
    [InlineData("gpt-6.2-sol", 2.0, 0.10, 2.50, 10.0)]
    [InlineData("gpt-6.2-astra", 8.0, 0.40, 10.0, 40.0)]
    public void Gpt62_UsesDatedPublishedPrices(string model, double input, double cached, double write, double output)
    {
        var released = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var estimate = TokenTestServices.Pricing().Estimate(model, 1_000_000, 1_000_000, 1_000_000, 1_000_000, released);

        Assert.True(estimate.ModelKnown);
        Assert.Equal((decimal)input, estimate.PriceBasis!.InputPerMillion);
        Assert.Equal((decimal)cached, estimate.PriceBasis.CacheReadPerMillion);
        Assert.Equal((decimal)write, estimate.PriceBasis.CacheWritePerMillion);
        Assert.Equal((decimal)output, estimate.PriceBasis.OutputPerMillion);
        Assert.Equal((decimal)(input + cached + write + output), estimate.Total);
        Assert.Equal("https://developers.openai.com/api/docs/pricing", estimate.PriceBasis.Source);
        Assert.Equal(released, estimate.PriceBasis.ValidFrom);

        var beforeRelease = TokenTestServices.Pricing().Estimate(model, 1, 1, 0, 0, released.AddTicks(-1));
        Assert.Equal(TokenEconomy.PriceStatus.NoPriceForDate, beforeRelease.Status);
    }

    [Theory]
    [InlineData("gpt-6.2-sol", "Codex credits: 50 input, 2.5 cached input, 250 output per MTok (list price x 25).")]
    [InlineData("gpt-6.2-astra", "Codex credits: 200 input, 10 cached input, 1000 output per MTok (list price x 25).")]
    public void Gpt62_PriceBasisCarriesTheCodexCreditRate(string model, string credits)
    {
        var estimate = TokenTestServices.Pricing().Estimate(
            model, 1, 0, 0, 0, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains(credits, estimate.PriceBasis!.Note);
        Assert.Equal(25m, AgentStudio.Tokens.TokenEconomyPriceProvider.CodexCreditsPerUsd);
    }

    [Fact]
    public void Gpt61Sol_UsesThePublishedCacheWritePrice()
    {
        var estimate = TokenTestServices.Pricing().Estimate(ModelIds.Gpt61Sol, 0, 0, 0, 1_000_000, AfterRelease);

        Assert.True(estimate.ModelKnown);
        Assert.Equal(2.50m, estimate.PriceBasis!.CacheWritePerMillion);
        Assert.Equal(2.50m, estimate.Total);
    }

    [Fact]
    public void Gpt6_codex_usage_counts_cached_input_once()
    {
        const string stdout = """
            {"type":"turn.completed","usage":{"input_tokens":1000000,"cached_input_tokens":800000,"output_tokens":100000,"reasoning_output_tokens":0}}
            """;
        var started = AfterRelease;

        var result = CodexOneShot.ParseOutput(
            0, stdout, "", "gpt-6-sol", started, started.AddSeconds(1),
            new CodexUsageParser(), new CliModelRegistry());
        var usage = result.Usage!;
        var estimate = TokenTestServices.Pricing().Estimate(
            "gpt-6-sol", usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, 0, AfterRelease);

        Assert.Equal(200_000, usage.InputTokens);
        Assert.Equal(800_000, usage.CacheReadTokens);
        // 0.2M x $2 + 0.8M x $0.20 + 0.1M x $10 = 0.40 + 0.16 + 1.00
        Assert.Equal(1.56m, estimate.Total);
    }
}
