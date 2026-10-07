using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2903: GPT-6 runs are priced from the TokenEconomy 0.3.6 dated snapshots,
/// and cached input is counted once at the cache-read rate (AGT-2882).
/// </summary>
public sealed class Gpt6TokenPricingTests
{
    private static readonly DateTime AfterRelease = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    // model, input, cached, output per MTok (operator facts, 2026-09-25)
    [InlineData("gpt-6-sol", 2.0, 0.20, 10.0)]
    [InlineData("gpt-6-luna", 0.10, 0.01, 0.50)]
    public void Gpt6_prices_resolve_from_TokenEconomy(string model, double input, double cached, double output)
    {
        var estimate = TokenPricing.Estimate(model, 1_000_000, 1_000_000, 1_000_000, 0, AfterRelease);

        Assert.True(estimate.ModelKnown);
        Assert.Equal((decimal)input, estimate.PriceBasis!.InputPerMillion);
        Assert.Equal((decimal)cached, estimate.PriceBasis.CacheReadPerMillion);
        Assert.Equal((decimal)output, estimate.PriceBasis.OutputPerMillion);
        Assert.Equal((decimal)(input + cached + output), estimate.Total);
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
        var estimate = TokenPricing.Estimate(
            "gpt-6-sol", usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, 0, AfterRelease);

        Assert.Equal(200_000, usage.InputTokens);
        Assert.Equal(800_000, usage.CacheReadTokens);
        // 0.2M x $2 + 0.8M x $0.20 + 0.1M x $10 = 0.40 + 0.16 + 1.00
        Assert.Equal(1.56m, estimate.Total);
    }
}
