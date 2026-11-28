

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Verifies the workload default and deployment overrides for automatic grades.
/// </summary>
public class CodeReviewGradeModelSelectorTests
{
    [Fact]
    public void Resolve_EmptyConfig_DefaultsToSolAtMedium()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve(null, null);

        Assert.Equal(ModelIds.Gpt62Sol, model);
        Assert.Equal(CliTypes.Codex, cli);
        Assert.Equal("medium", CodeReviewGradeModelSelector.DefaultThinkingLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankModel_FallsBackToSol(string blank)
    {
        var (model, _) = CodeReviewGradeModelSelector.Resolve(blank, null);

        Assert.Equal(ModelIds.Gpt62Sol, model);
    }

    [Fact]
    public void Resolve_RespectsExplicitModelOverride()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve("claude-sonnet-4-6", "codex");

        Assert.Equal("claude-sonnet-4-6", model);
        Assert.Equal("codex", cli);
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundOverride()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve("  claude-opus-4-8  ", "  claude  ");

        Assert.Equal("claude-opus-4-8", model);
        Assert.Equal("claude", cli);
    }

}
