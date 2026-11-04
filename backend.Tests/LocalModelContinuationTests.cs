using AgentStudio.Cli;
using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalModelContinuationTests
{
    [Theory]
    [InlineData("codex", "gpt-5.6-sol", "model-capacity-sibling")]
    [InlineData("claude", "claude-sonnet-5", "model-capacity-equivalent")]
    public void Local_runner_uses_pending_model_fallback_for_the_fresh_attempt(
        string targetCli, string targetModel, string reason)
    {
        var pending = new ModelFallbackInfo("gpt-6-sol", targetModel,
            reason, targetCli, "medium");
        var requested = ProjectRunner.RequestedRoute("codex", "gpt-6-sol", "medium", pending);
        Assert.Equal((targetCli, targetModel, "medium"), requested);

        var admission = new QuotaAdmissionPlan(QuotaAdmissionOutcome.LaunchPrimary,
            targetCli, targetModel, "medium", false, "healthy", null, null);
        var selected = ProjectRunner.ApplyPendingModelRoute(admission, pending);
        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, selected.Outcome);
        Assert.True(selected.IsFallback);
        Assert.Equal(pending, selected.ModelFallback);
    }
}
