using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2903: GPT-6 runs are priced from the TokenEconomy 0.3.6 dated snapshots,
/// and cached input is counted once at the cache-read rate (AGT-2882).
/// </summary>
public sealed class Gpt6TokenPricingTests
{
    // clock-independent: pricing uses the supplied historical timestamp and never compares it with now.
    private static readonly DateTime AfterRelease = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("claude-fable-5-2")]
    [InlineData("claude-fable-5.2")]
    public void Fable52_UsesDatedPublishedPrices(string model)
    {
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var estimate = TokenTestServices.Pricing().Estimate(model, 1_000_000, 1_000_000, 1_000_000, 1_000_000, at);
        Assert.True(estimate.ModelKnown);
        Assert.Equal(8m, estimate.PriceBasis!.InputPerMillion);
        Assert.Equal(0.20m, estimate.PriceBasis.CacheReadPerMillion);
        Assert.Equal(40m, estimate.PriceBasis.OutputPerMillion);
        Assert.Equal(10m, estimate.PriceBasis.CacheWritePerMillion);
        Assert.Equal(58.20m, estimate.Total);
        var beforeRelease = TokenTestServices.Pricing().Estimate(model, 1, 1, 0, 0, at.AddDays(-9));
        Assert.Equal(TokenEconomy.PriceStatus.NoPriceForDate, beforeRelease.Status);
    }

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
