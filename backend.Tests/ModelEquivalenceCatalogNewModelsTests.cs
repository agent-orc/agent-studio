using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelEquivalenceCatalogNewModelsTests
{
    [Theory]
    [InlineData(CliTypes.Claude, ModelIds.ClaudeOpus55, CliTypes.Codex, ModelIds.Gpt56Sol)]
    [InlineData(CliTypes.Codex, ModelIds.Gpt6Sol, CliTypes.Claude, ModelIds.ClaudeOpus5)]
    public void Proposal_successor_resolves_the_catalogued_comparable_tier(
        string fromCli, string fromModel, string toCli, string expected)
    {
        var result = new ModelEquivalenceCatalog().TryGetEquivalent(
            fromCli, fromModel, "high", toCli);
        Assert.Equal(expected, result?.Model);
        Assert.Equal("high", result?.ThinkingLevel);
    }
}
