using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2749 addendum (2026-09-07 19:20): a flat 60s aspect budget starved
/// Claude aspect calls and misclassified the resulting timeout as
/// ToolUnavailable. Each toolchain now has its own default and its own
/// configuration key.
/// </summary>
public sealed class ReviewAspectTimeoutPolicyTests
{
    [Theory]
    [InlineData("codex", ReviewAspectTimeoutDefaults.CodexSeconds)]
    [InlineData("claude", ReviewAspectTimeoutDefaults.ClaudeSeconds)]
    [InlineData("gemini", ReviewAspectTimeoutDefaults.GeminiSeconds)]
    [InlineData("some-future-cli", ReviewAspectTimeoutDefaults.FallbackSeconds)]
    [InlineData(null, ReviewAspectTimeoutDefaults.CodexSeconds)]
    public void SecondsFor_UsesThePerToolchainDefault(string? cliType, int expected)
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(expected, ReviewAspectTimeoutPolicy.SecondsFor(cliType, configuration));
    }

    [Fact]
    public void SecondsFor_RaisingOneToolchainDoesNotNarrowAnother()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewDecisionOrchestrator:AspectTimeoutSeconds:claude"] = "600",
            })
            .Build();

        Assert.Equal(600, ReviewAspectTimeoutPolicy.SecondsFor("claude", configuration));
        Assert.Equal(ReviewAspectTimeoutDefaults.CodexSeconds, ReviewAspectTimeoutPolicy.SecondsFor("codex", configuration));
        Assert.Equal(ReviewAspectTimeoutDefaults.GeminiSeconds, ReviewAspectTimeoutPolicy.SecondsFor("gemini", configuration));
    }

    [Fact]
    public void SecondsFor_ClampsToTheSevenThousandTwoHundredSecondCeiling()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewDecisionOrchestrator:AspectTimeoutSeconds:codex"] = "999999",
            })
            .Build();

        Assert.Equal(7200, ReviewAspectTimeoutPolicy.SecondsFor("codex", configuration));
    }

    /// <summary>
    /// AGT-2820: the per-toolchain number is the base of a derivation, not the
    /// budget. Ten cards on <c>claude-opus-5</c> lost every review because the
    /// budget did not move with the model an operator routed them to.
    /// </summary>
    [Fact]
    public void Derive_ScalesTheConfiguredBaseByTheModelAndTheMaterial()
    {
        var configuration = new ConfigurationBuilder().Build();

        var opus = ReviewAspectTimeoutPolicy.Derive(
            "claude", "claude-opus-5", "high", 0, configuration);
        var sonnet = ReviewAspectTimeoutPolicy.Derive(
            "claude", "claude-sonnet-5", "high", 0, configuration);
        var opusWithDiff = ReviewAspectTimeoutPolicy.Derive(
            "claude", "claude-opus-5", "high", 200_000, configuration);

        Assert.True(opus.Seconds > sonnet.Seconds);
        Assert.True(opusWithDiff.Seconds > opus.Seconds);
        Assert.Equal("claude-opus-5", opus.Model);
    }

    [Fact]
    public void Derive_HonoursThePerToolchainBaseOverride()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewDecisionOrchestrator:AspectTimeoutSeconds:claude"] = "100",
            })
            .Build();

        var budget = ReviewAspectTimeoutPolicy.Derive(
            "claude", "claude-sonnet-5", "low", 0, configuration);

        Assert.Contains("base=100s", budget.Derivation);
        Assert.Equal(125, budget.Seconds);
    }

    [Fact]
    public void For_ReturnsATimeSpan()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(
            TimeSpan.FromSeconds(ReviewAspectTimeoutDefaults.ClaudeSeconds),
            ReviewAspectTimeoutPolicy.For("claude", configuration));
    }
}
