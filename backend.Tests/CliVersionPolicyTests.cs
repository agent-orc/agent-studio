using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionPolicyTests
{
    [Theory]
    [InlineData("0.144.1", "0.153.0", false)]
    [InlineData("0.153.0-alpha.2", "0.153.0", false)]
    [InlineData("0.153.0-rc.1", "0.153.0-rc.1", true)]
    [InlineData("0.153.0", "0.153.0-rc.9", true)]
    [InlineData("v0.154.0+build.7", "0.153.0", true)]
    public void Semantic_minimum_comparison_handles_prereleases(
        string installed,
        string minimum,
        bool expected)
        => Assert.Equal(expected, SemanticCliVersion.IsAtLeast(installed, minimum));

    [Fact]
    public void Astra_registry_carries_its_actionable_cli_floor()
    {
        var astra = ModelMetadataRegistry.Find(ModelIds.Gpt6Astra);

        Assert.NotNull(astra);
        Assert.Equal("0.153.0", astra.MinimumCliVersion);
        Assert.Equal(
            "Needs codex-cli ≥ 0.153 (host has 0.144.1).",
            ModelMetadataRegistry.UnavailableOnInstalledCliNote(
                "codex-cli", "0.144.1", ModelIds.Gpt6Astra));
    }
}
