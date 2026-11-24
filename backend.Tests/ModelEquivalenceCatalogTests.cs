using AgentStudio.Cli;
using TokenEconomy;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelEquivalenceCatalogTests
{
    private readonly ModelEquivalenceCatalog _catalogue = new(TokenTestServices.Pricing());

    [Fact]
    public void Every_published_pair_comes_from_Token_Economy_in_either_direction()
    {
        var knowledge = ModelRoutingKnowledgeBase.Default;
        Assert.NotEmpty(_catalogue.Routes);
        Assert.All(_catalogue.Routes, pair => Assert.Contains(knowledge.Routes, route =>
            knowledge.FallbacksFor(route.Id).Any(fallback =>
                route.ModelId == pair.FromModel && route.ThinkingLevel == pair.FromThinkingLevel
                    && fallback.ModelId == pair.ToModel && fallback.ThinkingLevel == pair.ToThinkingLevel
                || route.ModelId == pair.ToModel && route.ThinkingLevel == pair.ToThinkingLevel
                    && fallback.ModelId == pair.FromModel && fallback.ThinkingLevel == pair.FromThinkingLevel)));
    }

    [Theory]
    [InlineData("codex", "gpt-6-sol", "medium", "claude", "claude-sonnet-5", "high")]
    [InlineData("claude", "claude-sonnet-5", "high", "codex", "gpt-6-sol", "medium")]
    public void Exact_pair_and_reasoning_mapping_work_in_both_directions(string provider, string model, string level,
        string targetProvider, string targetModel, string targetLevel)
    {
        var pair = _catalogue.TryGetRoute(provider, model, level, targetProvider);
        Assert.NotNull(pair);
        Assert.Equal(targetModel, pair.ToModel);
        Assert.Equal(targetLevel, pair.ToThinkingLevel);
        Assert.Contains("TokenEconomy", pair.CatalogueVersion);
        Assert.NotNull(pair.FromInputPerMTok);
        Assert.NotNull(pair.ToInputPerMTok);
    }

    [Theory]
    [InlineData("codex", "gpt-6-sol", "xhigh", "claude")]
    [InlineData("claude", "claude-opus-5-5", "low", "codex")]
    [InlineData("claude", "claude-haiku-5-5", "low", "codex")]
    [InlineData("claude", "claude-opus-5", "xhigh", "codex")]
    public void Missing_pair_never_inherits_a_guessed_model_or_reasoning_mapping(string provider, string model,
        string level, string targetProvider)
        => Assert.Null(_catalogue.TryGetRoute(provider, model, level, targetProvider));
}
