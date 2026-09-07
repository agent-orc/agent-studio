using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteReviewPlanBuilderLateBindingTests
{
    [Fact]
    public void ResolveAgentCommandsForClaim_IgnoresFrozenRouteAndUsesCurrentStepSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewDecisionOrchestrator:Cli"] = "codex",
                ["ReviewDecisionOrchestrator:AspectModel"] = "gpt-5.4-mini",
            })
            .Build();
        var builder = new RemoteReviewPlanBuilder(null!, configuration);
        var task = new TaskInfo
        {
            Id = "AGT-LATE-BIND",
            TaskKey = "AGT-LATE-BIND",
            ProjectName = "Fixture",
            TaskType = TaskTypes.Bug,
            Mode = TaskModes.Coding,
        };
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["aspect-code-quality"] = new()
                {
                    CliType = "claude",
                    Model = "claude-sonnet-5",
                    ThinkingLevel = "medium",
                },
            },
        };
        var tool = new Contract.ReviewCommandDto(
            "verify-1",
            "build-tests",
            "dotnet",
            ["test"]);
        var frozenAgent = new Contract.ReviewCommandDto(
            "aspect-code-quality",
            "code-quality",
            "codex",
            [],
            ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
            Prompt: "Review this result.",
            CliType: "codex",
            Model: "gpt-5.4-mini",
            ThinkingLevel: "high");
        var stored = new Contract.ReviewPlanDto(
            [tool, frozenAgent],
            ["build-tests", "code-quality"],
            IntegrationRef: "refs/heads/main");

        var resolved = builder.ResolveAgentCommandsForClaim(
            task,
            settings,
            stored,
            "refs/heads/develop");

        Assert.Same(tool, resolved.Commands[0]);
        var effectiveAgent = resolved.Commands[1];
        Assert.Equal("aspect-code-quality", effectiveAgent.StepId);
        Assert.Equal("code-quality", effectiveAgent.Aspect);
        Assert.Equal("claude", effectiveAgent.FileName);
        Assert.Equal("claude", effectiveAgent.CliType);
        Assert.Equal("claude-sonnet-5", effectiveAgent.Model);
        Assert.Equal("medium", effectiveAgent.ThinkingLevel);
        Assert.Equal("refs/heads/develop", resolved.IntegrationRef);

        Assert.Equal("codex", frozenAgent.CliType);
        Assert.Equal("gpt-5.4-mini", frozenAgent.Model);
    }
}
