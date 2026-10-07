using AgentStudio.Cli;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelEquivalenceCatalogTests
{
    private readonly ModelEquivalenceCatalog _catalogue = new();

    [Theory]
    [InlineData(ModelIds.ClaudeOpus5, "high", ModelIds.Gpt56Sol, "high")]
    [InlineData(ModelIds.ClaudeSonnet5, null, ModelIds.Gpt56Sol, "medium")]
    [InlineData(ModelIds.ClaudeHaiku45, null, ModelIds.Gpt56Luna, "medium")]
    public void ClaudeClass_MapsToComparableSelectableCodexRoute(
        string source, string? thinking, string target, string targetThinking)
    {
        var route = _catalogue.TryGetRoute(CliTypes.Claude, source, thinking, CliTypes.Codex);

        Assert.NotNull(route);
        Assert.Equal(target, route.ToModel);
        Assert.Equal(targetThinking, route.ToThinkingLevel);
        Assert.Contains("TokenEconomy", route.CatalogueVersion);
        Assert.NotNull(route.FromInputPerMTok);
        Assert.NotNull(route.ToInputPerMTok);
    }

    [Fact]
    public void RetiredMini_NeverAppearsInPublishedRoutes()
        => Assert.DoesNotContain(_catalogue.Routes, route =>
            string.Equals(route.FromModel, ModelIds.Gpt54Mini, StringComparison.OrdinalIgnoreCase)
            || string.Equals(route.ToModel, ModelIds.Gpt54Mini, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AutomaticCodexFallbacks_StayWithinStudioPolicyTiers()
    {
        // AGT-2903 re-based the tiers on GPT-6; the gpt-5.6 siblings stay in the
        // policy as declared provider-rejection fallbacks of the GPT-6 tiers.
        var policy = new ModelRoutingPolicyRegistry().Policy;
        var policyModels = policy.Tiers.Select(tier => tier.Model)
            .Concat(policy.ProviderRejectionFallbacks
                .Where(fallback => fallback.CliType == CliTypes.Codex)
                .Select(fallback => fallback.ToModel))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(_catalogue.Routes);
        Assert.All(_catalogue.Routes, route =>
        {
            if (route.FromCliType == CliTypes.Codex)
                Assert.Contains(route.FromModel, policyModels);
            if (route.ToCliType == CliTypes.Codex)
                Assert.Contains(route.ToModel, policyModels);
        });
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void ClaudeRoute_PreservesEachSupportedSourceThinkingLevel(string thinkingLevel)
    {
        var route = _catalogue.TryGetRoute(
            CliTypes.Claude, ModelIds.ClaudeOpus5, thinkingLevel, CliTypes.Codex);

        Assert.NotNull(route);
        Assert.Equal(thinkingLevel, route.FromThinkingLevel);
        Assert.Equal(thinkingLevel, route.ToThinkingLevel);
    }

    [Fact]
    public void ProviderFallback_SelectsOnlyTheMatchingCatalogueThinkingLevel()
    {
        var medium = _catalogue.TryGetRoute(
            CliTypes.Codex, ModelIds.Gpt56Sol, "medium", CliTypes.Claude);
        var xhigh = _catalogue.TryGetRoute(
            CliTypes.Codex, ModelIds.Gpt56Sol, "xhigh", CliTypes.Claude);

        Assert.NotNull(medium);
        Assert.Equal("medium", medium.FromThinkingLevel);
        Assert.Equal(ModelIds.ClaudeSonnet5, medium.ToModel);
        Assert.Equal("high", medium.ToThinkingLevel);
        Assert.Null(xhigh);
    }

    [Fact]
    public void UnsupportedThinkingFloor_ReturnsNoDowngradedRoute()
    {
        // TokenEconomy 0.3.6 (TE-59) added max to the GPT-5.6 Sol ladder, so
        // Opus 5/max now has an exact-level Codex route ...
        var max = _catalogue.TryGetRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "max", CliTypes.Codex);
        Assert.NotNull(max);
        Assert.Equal("max", max.ToThinkingLevel);
        // ... while a level the source ladder lacks still gets no downgraded route.
        Assert.Null(_catalogue.TryGetRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "ultra", CliTypes.Codex));
    }
}
