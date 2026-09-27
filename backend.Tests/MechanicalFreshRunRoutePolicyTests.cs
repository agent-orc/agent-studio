using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MechanicalFreshRunRoutePolicyTests
{
    [Theory]
    [InlineData("semantic-conflict", "gpt-5.6-sol", "medium")]
    [InlineData("failed-deterministic-gate", "gpt-5.6-terra", "medium")]
    [InlineData("pending-mechanical-continuation", "gpt-5.6-terra", "medium")]
    public void Fresh_fallback_clears_the_policy_floor(string reason, string model, string thinking)
    {
        var folder = Path.Combine(Path.GetTempPath(), "fresh-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "prompt.md"), "A local feature change.");
            var task = new TaskInfo { Id = "AGT-1", Title = "Feature", TaskType = TaskTypes.Feature, FolderPath = folder, ModelExplicit = false };
            var spec = new RunSpecDto("codex", "gpt-5.6-luna", "medium");
            Assert.False(task.ModelExplicit);
            var routed = MechanicalFreshRunRoutePolicy.Qualify(spec, task, reason, new ModelRoutingPolicyRegistry());
            Assert.Equal(model, routed.Model);
            Assert.Equal(thinking, routed.ThinkingLevel);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Critical_prompt_raises_the_fresh_round_to_the_hard_floor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fresh-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "prompt.md"), "Repair a fencing lease authority bug.");
            var task = new TaskInfo { Id = "AGT-2", Title = "Fix lease", TaskType = TaskTypes.Bug, FolderPath = folder, ModelExplicit = false };
            Assert.False(task.ModelExplicit);
            var routed = MechanicalFreshRunRoutePolicy.Qualify(
                new RunSpecDto("claude", "claude-haiku-4-5", "medium"),
                task, "semantic-conflict", new ModelRoutingPolicyRegistry());
            Assert.Equal("claude-opus-5", routed.Model);
            Assert.Equal("xhigh", routed.ThinkingLevel);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Explicit_model_pin_is_preserved_for_a_rejected_resume()
    {
        var task = new TaskInfo
        {
            Id = "AGT-3", Title = "Feature", TaskType = TaskTypes.Feature,
            ModelExplicit = true,
        };
        var pinned = new RunSpecDto("codex", "gpt-5.6-luna", "medium");
        var routed = MechanicalFreshRunRoutePolicy.Qualify(
            pinned, task, "pending-mechanical-continuation", new ModelRoutingPolicyRegistry());
        Assert.Equal(pinned, routed);
    }
}
