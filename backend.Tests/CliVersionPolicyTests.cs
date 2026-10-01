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

    [Theory]
    [InlineData(ModelIds.ClaudeOpus55, "claude-code", "2.1.270", "2.1.281")]
    [InlineData(ModelIds.Gpt6Sol, "codex-cli", "0.154.0", "0.155.0")]
    [InlineData(ModelIds.Gpt6Luna, "codex-cli", "0.154.0", "0.155.0")]
    public void New_models_explain_the_required_cli_version(
        string modelId, string cliLabel, string installed, string minimum)
    {
        Assert.Equal(minimum, ModelMetadataRegistry.Find(modelId)?.MinimumCliVersion);
        Assert.Contains(minimum[..minimum.LastIndexOf('.')],
            ModelMetadataRegistry.UnavailableOnInstalledCliNote(cliLabel, installed, modelId));
    }

    [Theory]
    [InlineData(ModelIds.Gpt6Sol)]
    [InlineData(ModelIds.Gpt6Luna)]
    public void New_gpt6_models_have_the_published_context_window(string modelId)
        => Assert.Equal(1_050_000, ModelMetadataRegistry.ContextWindowFor(modelId));

    [Theory]
    [InlineData("2.1.270 (Claude Code)", "2.1.270")]
    [InlineData("codex-cli 0.154.0", "0.154.0")]
    public void Cli_version_output_is_normalized_before_floor_comparison(string output, string expected)
    {
        Assert.Equal(expected, SemanticCliVersion.FromCliOutput(output));
        Assert.Contains($"host has {expected}",
            ModelMetadataRegistry.UnavailableOnInstalledCliNote(
                output.StartsWith("codex", StringComparison.Ordinal) ? "codex-cli" : "claude-code",
                output,
                output.StartsWith("codex", StringComparison.Ordinal) ? ModelIds.Gpt6Sol : ModelIds.ClaudeOpus55));
    }
}
