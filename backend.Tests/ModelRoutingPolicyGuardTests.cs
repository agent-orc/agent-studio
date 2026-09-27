using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2808: economy mode must never route bug/feature cards onto a Haiku-class
/// model via the positional catalogue fallback, and Recommend() must never
/// leave ThinkingLevel null.
/// </summary>
public sealed class ModelRoutingPolicyGuardTests
{
    private static readonly CliModelCatalog ClaudeCatalogue = new()
    {
        Source = "test-claude",
        FetchedAt = DateTime.UtcNow,
        Models =
        [
            ClaudeModel("claude-haiku-4-5", "medium"),
            ClaudeModel("claude-sonnet-5", "low", "medium", "high"),
            ClaudeModel("claude-opus-5", "low", "medium", "high", "xhigh"),
        ],
    };

    private static readonly CliModelCatalog GptCatalogue = new()
    {
        Source = "test-gpt",
        FetchedAt = DateTime.UtcNow,
        Models =
        [
            GptModel("gpt-5.6-luna", "medium"),
            GptModel("gpt-5.6-sol", "low", "medium", "high", "xhigh"),
            GptModel("gpt-5.6-terra", "medium"),
        ],
    };

    [Theory]
    [InlineData(TaskTypes.Feature)]
    [InlineData(TaskTypes.Bug)]
    public void EconomyModeNeverRoutesClaudeCardsToHaiku(string taskType)
    {
        var registry = new ModelRoutingPolicyRegistry();
        var recommendation = registry.Recommend(taskType, ClaudeCatalogue, economyMode: true);

        Assert.NotEqual("claude-haiku-4-5", recommendation.Model, StringComparer.OrdinalIgnoreCase);

        var sonnetLowRank = TierRank(registry, "sonnet-low");
        var selectedRank = TierRank(registry, recommendation.Tier);
        Assert.True(selectedRank >= sonnetLowRank,
            $"selected tier '{recommendation.Tier}' fell below the sonnet-low floor");
    }

    public static IEnumerable<object[]> TaskTypeEconomyCatalogueMatrix()
    {
        foreach (var taskType in TaskTypes.All)
        foreach (var economyMode in new[] { true, false })
        foreach (var catalogue in new[] { ClaudeCatalogue, GptCatalogue })
            yield return [taskType, economyMode, catalogue];
    }

    [Theory]
    [MemberData(nameof(TaskTypeEconomyCatalogueMatrix))]
    public void RecommendNeverLeavesThinkingLevelNull(string taskType, bool economyMode, CliModelCatalog catalogue)
    {
        var registry = new ModelRoutingPolicyRegistry();
        var recommendation = registry.Recommend(taskType, catalogue, economyMode);

        Assert.False(string.IsNullOrWhiteSpace(recommendation.ThinkingLevel));
    }

    [Fact]
    public void PolicyDocumentLoadsAndValidates()
    {
        var registry = new ModelRoutingPolicyRegistry();
        Assert.NotEmpty(registry.Policy.Tiers);
        Assert.True(registry.Policy.Tiers.Any(t => t.Id == "sonnet-low"));
        var astra = registry.ProviderRejectionFallback(CliTypes.Codex, ModelIds.Gpt6Astra);
        Assert.Equal(ModelIds.Gpt56Sol, astra!.ToModel);
    }

    [Fact]
    public void ProviderRejectionFallback_ResolvesRegisteredModelAlias()
    {
        var registry = new ModelRoutingPolicyRegistry();

        var fallback = registry.ProviderRejectionFallback(CliTypes.Claude, "claude-opus-5-5");

        Assert.NotNull(fallback);
        Assert.Equal(ModelIds.ClaudeOpus5, fallback.FromModel);
    }

    [Fact]
    public void Provider_rejection_sibling_must_clear_the_cards_correctness_floor()
    {
        var registry = new ModelRoutingPolicyRegistry();
        var critical = registry.CorrectnessFloor(
            TaskTypes.Bug,
            "Protect distributed lease ownership",
            "Prevent stale-write data-loss in the runner.");

        Assert.Equal("sol-xhigh", critical!.Id);
        Assert.True(registry.RouteMeetsFloor(ModelIds.Gpt56Sol, "xhigh", critical));
        Assert.False(registry.RouteMeetsFloor(ModelIds.Gpt56Sol, "medium", critical));
    }

    [Fact]
    public void MalformedPolicyStillThrows()
    {
        var malformed = new ModelRoutingPolicyDocument
        {
            Version = "",
            WikiPath = "docs/x.md",
            Tiers = [],
        };
        Assert.Throws<InvalidOperationException>(() => new ModelRoutingPolicyRegistry(malformed));
    }

    private static int TierRank(ModelRoutingPolicyRegistry registry, string tierId)
        => registry.Policy.Tiers.First(t => string.Equals(t.Id, tierId, StringComparison.OrdinalIgnoreCase)).Rank;

    private static CliModelInfo ClaudeModel(string id, params string[] levels) => new()
    {
        Id = id,
        Label = id,
        Vendor = "anthropic",
        Available = true,
        ThinkingLevels = levels.ToList(),
        DefaultThinkingLevel = levels.FirstOrDefault(),
    };

    private static CliModelInfo GptModel(string id, params string[] levels) => new()
    {
        Id = id,
        Label = id,
        Vendor = "openai",
        Available = true,
        ThinkingLevels = levels.ToList(),
        DefaultThinkingLevel = levels.FirstOrDefault(),
    };
}
