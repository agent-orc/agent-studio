using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelEquivalenceCatalogNewModelsTests
{
    private readonly ModelEquivalenceCatalog _catalogue = new();

    [Theory]
    [InlineData(CliTypes.Claude, ModelIds.ClaudeOpus55, "high", CliTypes.Codex, ModelIds.Gpt56Sol, "high")]
    [InlineData(CliTypes.Codex, ModelIds.Gpt6Sol, "medium", CliTypes.Claude, ModelIds.ClaudeSonnet5, "high")]
    public void Proposal_successor_resolves_the_predecessors_catalogued_route(
        string fromCli, string fromModel, string thinking,
        string toCli, string expectedModel, string expectedThinking)
    {
        var route = _catalogue.TryGetRoute(fromCli, fromModel, thinking, toCli);

        Assert.NotNull(route);
        Assert.Equal(fromModel, route.FromModel);
        Assert.Equal(expectedModel, route.ToModel);
        Assert.Equal(expectedThinking, route.ToThinkingLevel);
    }

    [Fact]
    public void Proposal_successor_without_a_predecessor_route_waits()
        => Assert.Null(_catalogue.TryGetRoute(
            CliTypes.Codex, ModelIds.Gpt6Sol, "high", CliTypes.Claude));

    [Fact]
    public void Proposal_successors_are_never_automatic_route_targets()
        => Assert.DoesNotContain(_catalogue.Routes, route =>
            route.ToModel is ModelIds.ClaudeOpus55 or ModelIds.Gpt6Sol or ModelIds.Gpt6Luna);
}
