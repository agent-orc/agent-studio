using AgentStudio.Cli;
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
    public void UnsupportedThinkingFloor_ReturnsNoDowngradedRoute()
        => Assert.Null(_catalogue.TryGetRoute(
            CliTypes.Claude, ModelIds.ClaudeOpus5, "max", CliTypes.Codex));
}
