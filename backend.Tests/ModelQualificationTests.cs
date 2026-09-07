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
            Model("gpt-5.6-sol", "medium", "high", "xhigh"),
            Model("gpt-5.6-terra", "medium"),
            Model("gpt-5.6-luna", "medium"),
        ],
    };

    [Fact]
    public void NewFeatureWithoutExplicitSelectionGetsPolicyTier()
    {
        var service = Service(economyMode: false);
        var task = Task("feature-default", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Add a bounded settings panel.", Catalogue, []);

        Assert.Equal("2026-09-07", result.PolicyVersion);
        Assert.Equal("terra-medium", result.PolicyTier);
        Assert.Equal("gpt-5.6-terra", result.RecommendedModel);
        Assert.Equal("medium", result.RecommendedThinkingLevel);
        Assert.Equal(result.RecommendedModel, result.SelectedModel);
        Assert.Equal("policy", result.SelectionSource);
        Assert.Contains("feature defaults to terra-medium", result.Reason);
    }

    [Fact]
    public void EconomyModeLowersEligibleFeatureOneTier()
    {
        var service = Service(economyMode: true);
        var task = Task("feature-economy", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Add a bounded settings panel.", Catalogue, []);

        Assert.True(result.EconomyMode);
        Assert.True(result.EconomyDowngraded);
        Assert.Equal("luna-medium", result.PolicyTier);
        Assert.Equal("gpt-5.6-luna", result.SelectedModel);
        Assert.Equal("policy-economy", result.SelectionSource);
    }

    [Fact]
    public void EconomyModeNeverCrossesBugCorrectnessFloor()
    {
        var service = Service(economyMode: true);
        var task = Task("unclear-bug", TaskTypes.Bug, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(task, "Investigate an intermittent rendering failure.", Catalogue, []);

        Assert.False(result.EconomyDowngraded);
        Assert.Equal("terra-medium", result.PolicyTier);
        Assert.Equal("terra-medium", result.CorrectnessFloorTier);
        Assert.Equal("gpt-5.6-terra", result.SelectedModel);
    }

    [Fact]
    public void CriticalWorkStaysSolXhighWithEconomyMode()
    {
        var service = Service(economyMode: true);
        var task = Task("critical", TaskTypes.Feature, modelExplicit: false, thinkingExplicit: false);

        var result = service.Qualify(
            task,
            "Prevent stale-write data-loss in a distributed authority state machine.",
            Catalogue,
            []);

        Assert.False(result.EconomyDowngraded);
        Assert.Equal("sol-xhigh", result.PolicyTier);
        Assert.Equal("sol-xhigh", result.CorrectnessFloorTier);
        Assert.Equal("gpt-5.6-sol", result.SelectedModel);
        Assert.Equal("xhigh", result.SelectedThinkingLevel);
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

        Assert.Equal("gpt-5.6-luna", result.RecommendedModel);
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
        Vendor = "test",
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
