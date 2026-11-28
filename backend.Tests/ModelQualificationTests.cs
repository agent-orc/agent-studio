using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelQualificationTests
{
    private static readonly CliModelCatalog Catalogue = new()
    {
        Source = "test-live-catalogue",
        FetchedAt = DateTime.UtcNow,
        Models =
        [
            Model("gpt-6-sol", "low", "medium", "high", "xhigh", "max", "ultra"),
            Model("gpt-6-luna", "low", "medium", "high", "xhigh", "max"),
            Model("gpt-5.6-sol", "medium", "high", "xhigh"),
            Model("gpt-5.6-terra", "medium"),
            Model("gpt-5.6-luna", "medium"),
            Model("gpt-6.2-sol", "low", "medium", "high", "xhigh", "max", "ultra"),
            Model("gpt-6.2-astra", "low", "medium", "high", "xhigh", "max", "ultra"),
        ],
    };

    [Theory]
    [InlineData(ModelIds.Gpt62Sol, "low")]
    [InlineData(ModelIds.Gpt62Sol, "medium")]
    [InlineData(ModelIds.Gpt62Sol, "high")]
    [InlineData(ModelIds.Gpt62Sol, "xhigh")]
    [InlineData(ModelIds.Gpt62Sol, "max")]
    [InlineData(ModelIds.Gpt62Sol, "ultra")]
    [InlineData(ModelIds.Gpt62Astra, "low")]
    [InlineData(ModelIds.Gpt62Astra, "ultra")]
    public void Gpt62ExplicitPin_WinsQualificationAtItsLevel(string model, string level)
    {
        var task = Task("gpt62-design", TaskTypes.Feature, modelExplicit: true, thinkingExplicit: true) with
        { Model = model, ThinkingLevel = level };

        var result = Service(economyMode: true).Qualify(task, "Build an interactive deployment view.", Catalogue, []);

        Assert.Equal(model, result.SelectedModel);
        Assert.Equal(level, result.SelectedThinkingLevel);
        Assert.Equal("task-override", result.SelectionSource);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Fable52ExplicitPin_WinsQualification(string level)
    {
        var task = Task("fable-ui", TaskTypes.Feature, modelExplicit: true, thinkingExplicit: true) with
        { CliType = CliTypes.Claude, Model = ModelIds.ClaudeFable52, ThinkingLevel = level };
        var result = Service(economyMode: true).Qualify(task, "Build an interactive deployment view.", ClaudeModelDiscovery.FallbackCatalog(), []);
        Assert.Equal(ModelIds.ClaudeFable52, result.SelectedModel);
        Assert.Equal(level, result.SelectedThinkingLevel);
        Assert.Equal("task-override", result.SelectionSource);
    }

    [Fact]
    public void SolIsRecommendedAndAstraRequiresAnExplicitPin()
    {
        foreach (var type in new[] { TaskTypes.Feature, TaskTypes.Bug })
        {
            var task = Task("gpt62-policy-" + type, type, modelExplicit: false, thinkingExplicit: false);
            var result = Service(economyMode: false).Qualify(task, "Add a bounded settings panel.", Catalogue, []);
            Assert.Equal(ModelIds.Gpt62Sol, result.RecommendedModel);
            Assert.Equal(ModelIds.Gpt62Sol, result.SelectedModel);
        }
    }

    [Fact]
    public void NewFeatureWithoutExplicitSelectionGetsPolicyTier()
    {
        var service = Service(economyMode: false);
        var task = Task("feature-default", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Add a bounded settings panel.", Catalogue, []);

        Assert.Equal("2026-11-28", result.PolicyVersion);
        Assert.Equal("sol-medium", result.PolicyTier);
        Assert.Equal(ModelIds.Gpt62Sol, result.RecommendedModel);
        Assert.Equal("medium", result.RecommendedThinkingLevel);
        Assert.Equal(result.RecommendedModel, result.SelectedModel);
        Assert.Equal("policy", result.SelectionSource);
        Assert.Contains("feature defaults to sol-medium", result.Reason);
    }

    [Fact]
    public void EconomyModeLowersEligibleFeatureOneTier()
    {
        var service = Service(economyMode: true);
        var task = Task("feature-economy", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Add a bounded settings panel.", Catalogue, []);

        Assert.True(result.EconomyMode);
        Assert.True(result.EconomyDowngraded);
        // AGT-2903: Sol/medium lowers one step to Terra/medium, above the sonnet-low economy floor.
        Assert.Equal("terra-medium", result.PolicyTier);
        Assert.Equal(ModelIds.Gpt62Sol, result.SelectedModel);
        Assert.Equal("policy-economy", result.SelectionSource);
    }

    [Fact]
    public void EconomyModeNeverCrossesBugCorrectnessFloor()
    {
        var service = Service(economyMode: true);
        var task = Task("unclear-bug", TaskTypes.Bug, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Investigate an intermittent rendering failure.", Catalogue, []);

        // AGT-2903: the bug default is Sol/medium; economy mode stops at the Terra floor.
        Assert.True(result.EconomyDowngraded);
        Assert.Equal("terra-medium", result.PolicyTier);
        Assert.Equal("terra-medium", result.CorrectnessFloorTier);
        Assert.Equal(ModelIds.Gpt62Sol, result.SelectedModel);
    }

    [Fact]
    public void CriticalWorkStaysSolHighWithEconomyMode()
    {
        var service = Service(economyMode: true);
        var task = Task("critical", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(
            task,
            "Prevent stale-write data-loss in a distributed authority state machine.",
            Catalogue,
            []);

        Assert.False(result.EconomyDowngraded);
        Assert.Equal("sol-high", result.PolicyTier);
        Assert.Equal("sol-high", result.CorrectnessFloorTier);
        Assert.Equal(ModelIds.Gpt62Sol, result.SelectedModel);
        Assert.Equal("high", result.SelectedThinkingLevel);
    }

    [Fact]
    public void ExplicitCardModelAndReasoningAlwaysWinButRecommendationRemains()
    {
        var service = Service(economyMode: false);
        var task = Task("override", TaskTypes.Chore, modelExplicit: true, thinkingExplicit: true) with
        {
            Model = "gpt-5.6-sol",
            ThinkingLevel = "xhigh",
        };

        var result = service.Qualify(task, "Polish CSS spacing.", Catalogue, []);

        Assert.Equal(ModelIds.Gpt62Sol, result.RecommendedModel);
        Assert.Equal("gpt-5.6-sol", result.SelectedModel);
        Assert.Equal("xhigh", result.SelectedThinkingLevel);
        Assert.Equal("task-override", result.SelectionSource);
        Assert.Contains("card override wins", result.Reason);
    }

    private static ModelQualificationService Service(bool economyMode) => new(
        new ModelRoutingPolicyRegistry(),
        new FixedRoutingMode(economyMode),
        new JsonlAppender(),
        NullLogger<ModelQualificationService>.Instance);

    private static CliModelInfo Model(string id, params string[] thinkingLevels) => new()
    {
        Id = id,
        Label = id,
        Vendor = "openai",
        Available = true,
        ThinkingLevels = thinkingLevels.ToList(),
        DefaultThinkingLevel = thinkingLevels.FirstOrDefault(),
    };

    private static TaskInfo Task(string id, string taskType, bool modelExplicit, bool thinkingExplicit) => new()
    {
        Id = id,
        Title = id,
        ProjectName = "test-project",
        FolderPath = Path.GetTempPath(),
        CliType = CliTypes.Codex,
        Model = "gpt-5.6-sol",
        ThinkingLevel = "xhigh",
        ModelExplicit = modelExplicit,
        ThinkingLevelExplicit = thinkingExplicit,
        TaskType = taskType,
    };

    private sealed class FixedRoutingMode(bool economyMode) : IModelRoutingModeProvider
    {
        public bool EconomyMode { get; } = economyMode;
    }
}
