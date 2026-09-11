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

    [Fact]
    public void For_ReturnsATimeSpan()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(
            TimeSpan.FromSeconds(ReviewAspectTimeoutDefaults.ClaudeSeconds),
            ReviewAspectTimeoutPolicy.For("claude", configuration));
    }
}
