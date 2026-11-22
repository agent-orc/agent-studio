using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationRecoveryRoutingTests
{
    [Theory]
    [InlineData("feature", "Update a local label", "low")]
    [InlineData("bug", "Fix a local parser", "medium")]
    [InlineData("feature", "Update the public protocol", "medium")]
    [InlineData("bug", "Fix lease ownership", "high")]
    public void Merge_round_uses_the_lowest_supported_effort_within_its_floor(
        string taskType, string title, string expected)
    {
        var job = new TaskInfo
        {
            Model = "gpt-6-sol", ThinkingLevel = "high",
            TaskType = taskType, Title = title, ModelExplicit = false, ThinkingLevelExplicit = false,
        };

        var route = TaskIntegrationRecoveryService.SelectRecoveryRoute(job, new ModelRoutingPolicyRegistry());

        Assert.Equal($"gpt-6-sol/{expected}", route.Selected);
        Assert.Equal("gpt-6-sol/high", route.Previous);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Explicit_operator_pins_are_retained(bool modelPin, bool thinkingPin)
    {
        var job = new TaskInfo
        {
            Model = "gpt-6-sol", ThinkingLevel = "high", TaskType = "feature",
            ModelExplicit = modelPin, ThinkingLevelExplicit = thinkingPin,
        };

        var route = TaskIntegrationRecoveryService.SelectRecoveryRoute(job, new ModelRoutingPolicyRegistry());

        Assert.Equal(route.Previous, route.Selected);
        Assert.True(route.Pinned);
    }

    [Theory]
    [InlineData("gpt-6-sol", "low")]
    [InlineData("gpt-6-luna", "medium")]
    [InlineData("unknown-model", "high")]
    public void Recovery_does_not_raise_effort_or_invent_a_model_route(string model, string effort)
    {
        var job = new TaskInfo { Model = model, ThinkingLevel = effort, TaskType = "feature", ModelExplicit = false, ThinkingLevelExplicit = false };

        var route = TaskIntegrationRecoveryService.SelectRecoveryRoute(job, new ModelRoutingPolicyRegistry());

        Assert.Equal(route.Previous, route.Selected);
    }
}
