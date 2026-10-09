using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: policy version dates are catalogue identities, never compared with current time.

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
        FetchedAt = DateTime.UnixEpoch,
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
        FetchedAt = DateTime.UnixEpoch,
        Models =
        [
            GptModel("gpt-6-sol", "low", "medium", "high", "xhigh", "max", "ultra"),
            GptModel("gpt-6-luna", "low", "medium", "high", "xhigh", "max"),
            GptModel("gpt-5.6-luna", "medium"),
            GptModel("gpt-5.6-sol", "low", "medium", "high", "xhigh"),
            GptModel("gpt-5.6-terra", "medium"),
        ],
    };

    /// <summary>A codex-cli older than 0.155.0: no GPT-6 model in discovery.</summary>
    private static readonly CliModelCatalog Gpt56OnlyCatalogue = new()
    {
        Source = "test-gpt56",
        FetchedAt = DateTime.UnixEpoch,
        Models =
        [
            GptModel("gpt-5.5", "low", "medium", "high", "xhigh"),
            GptModel("gpt-5.6-luna", "low", "medium"),
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
        Assert.Equal(ModelIds.ClaudeOpus5,
            registry.ProviderRejectionFallback(CliTypes.Claude, ModelIds.ClaudeOpus55)!.ToModel);
        Assert.Equal(ModelIds.Gpt56Sol,
            registry.ProviderRejectionFallback(CliTypes.Codex, ModelIds.Gpt6Sol)!.ToModel);
        Assert.Equal(ModelIds.Gpt56Luna,
            registry.ProviderRejectionFallback(CliTypes.Codex, ModelIds.Gpt6Luna)!.ToModel);
        Assert.Equal(ModelIds.Gpt6Sol,
            registry.ProviderRejectionFallback(CliTypes.Codex, ModelIds.Gpt61Sol)!.ToModel);
        Assert.Equal(ModelIds.ClaudeSonnet5,
            registry.ProviderRejectionFallback(CliTypes.Claude, ModelIds.ClaudeSonnet55)!.ToModel);
    }

    [Fact]
    public void Policy_rebases_luna_and_sol_tiers_on_gpt6_and_keeps_terra()
    {
        var registry = new ModelRoutingPolicyRegistry();
        string ModelOf(string tier) => registry.Policy.Tiers.Single(t => t.Id == tier).Model;

        Assert.Equal("2026-10-04", registry.Policy.Version);
        Assert.Equal(5, registry.Policy.Tiers.Count);
        Assert.Equal(ModelIds.Gpt6Luna, ModelOf("luna-medium"));
        Assert.Equal(ModelIds.Gpt6Sol, ModelOf("sonnet-low"));
        Assert.Equal(ModelIds.Gpt56Terra, ModelOf("terra-medium"));
        Assert.Equal(ModelIds.Gpt6Sol, ModelOf("sol-medium"));
        Assert.Equal(ModelIds.Gpt6Sol, ModelOf("sol-xhigh"));
        Assert.Equal("sonnet-low", registry.Policy.TaskTypeDefaults[TaskTypes.Feature].EconomyFloorTier);
        Assert.Equal("terra-medium", registry.Policy.TaskTypeDefaults[TaskTypes.Bug].HardFloorTier);
    }

    [Theory]
    [InlineData(TaskTypes.Feature)]
    [InlineData(TaskTypes.Bug)]
    public void New_feature_and_bug_cards_route_to_gpt6_sol_medium(string taskType)
    {
        var registry = new ModelRoutingPolicyRegistry();
        var codex = registry.Recommend(taskType, GptCatalogue, economyMode: false);
        var claude = registry.Recommend(taskType, ClaudeCatalogue, economyMode: false);

        Assert.Equal("sol-medium", codex.Tier);
        Assert.Equal(ModelIds.Gpt6Sol, codex.Model);
        Assert.Equal("medium", codex.ThinkingLevel);
        Assert.Equal("2026-10-04", codex.PolicyVersion);
        // The Anthropic override for Sol/medium is the same Sonnet 5/medium route Terra used.
        Assert.Equal("sol-medium", claude.Tier);
        Assert.Equal(ModelIds.ClaudeSonnet5, claude.Model);
        Assert.Equal("medium", claude.ThinkingLevel);
    }

    [Fact]
    public void Chore_cards_route_to_gpt6_luna_medium()
    {
        var recommendation = new ModelRoutingPolicyRegistry()
            .Recommend(TaskTypes.Chore, GptCatalogue, economyMode: false);

        Assert.Equal("luna-medium", recommendation.Tier);
        Assert.Equal(ModelIds.Gpt6Luna, recommendation.Model);
    }

    [Fact]
    public void Economy_mode_lowers_a_feature_card_one_step_to_terra_not_below_its_floor()
    {
        var recommendation = new ModelRoutingPolicyRegistry()
            .Recommend(TaskTypes.Feature, GptCatalogue, economyMode: true);

        Assert.True(recommendation.EconomyDowngraded);
        Assert.Equal("terra-medium", recommendation.Tier);
        Assert.Equal(ModelIds.Gpt56Terra, recommendation.Model);
    }

    [Theory]
    [InlineData(TaskTypes.Chore, "gpt-5.6-luna")]
    [InlineData(TaskTypes.Feature, "gpt-5.6-sol")]
    [InlineData(TaskTypes.Bug, "gpt-5.6-sol")]
    public void A_cli_without_gpt6_keeps_the_declared_gpt56_sibling(string taskType, string expectedModel)
    {
        var recommendation = new ModelRoutingPolicyRegistry()
            .Recommend(taskType, Gpt56OnlyCatalogue, economyMode: false);

        Assert.Equal(expectedModel, recommendation.Model);
        Assert.Equal("medium", recommendation.ThinkingLevel);
    }

    [Fact]
    public void ProviderRejectionFallback_ResolvesRegisteredModelAlias()
    {
        var registry = new ModelRoutingPolicyRegistry();

        var fallback = registry.ProviderRejectionFallback(CliTypes.Claude, "claude-opus-5.5");

        Assert.NotNull(fallback);
        Assert.Equal(ModelIds.ClaudeOpus55, fallback.FromModel);
        Assert.Equal(ModelIds.ClaudeOpus5, fallback.ToModel);
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
        Assert.True(registry.RouteMeetsFloor(ModelIds.Gpt6Sol, "xhigh", critical));
        Assert.False(registry.RouteMeetsFloor(ModelIds.Gpt6Sol, "medium", critical));
        // The declared gpt-5.6 sibling inherits the GPT-6 tier it replaces.
        Assert.True(registry.RouteMeetsFloor(ModelIds.Gpt56Sol, "xhigh", critical));
        Assert.False(registry.RouteMeetsFloor(ModelIds.Gpt56Sol, "medium", critical));
        Assert.False(registry.RouteMeetsFloor(ModelIds.Gpt56Luna, "max", critical));
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
