using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationCompilePlanTests
{
    [Theory]
    [InlineData("frontend/src/app/app.ts", 1, "frontend")]
    [InlineData("backend/Features/Pipeline/RemoteGateWorker.cs", 2, "backend")]
    [InlineData("backend/OrchestratorApi.csproj", 2, "backend")]
    [InlineData("global.json", 2, "backend")]
    public void Compile_check_uses_only_the_changed_stack(string path, int commands, string stack)
    {
        var root = RepositoryRoot.Find();
        var selected = IntegrationCompilePlan.TryPlan(root, Request(), VerifyCommandPlanner.Plan(root, null), [path]);
        Assert.NotNull(selected);
        Assert.Equal(commands, selected.Commands.Count);
        Assert.All(selected.PreparationCommands!, command => Assert.EndsWith(stack, command));
        Assert.All(selected.Commands, command => Assert.Equal(stack == "frontend",
            command.Command.Contains("npm-frontend", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("scripts/check-release-build.sh")]
    [InlineData("unknown.txt")]
    public void Shared_or_unmapped_input_keeps_the_complete_declared_build(string path)
    {
        var root = RepositoryRoot.Find();
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request(), VerifyCommandPlanner.Plan(root, null), [path]));
    }

    [Fact]
    public void Review_and_release_plans_do_not_use_the_integration_map()
    {
        var root = RepositoryRoot.Find();
        var plan = VerifyCommandPlanner.Plan(root, null);
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request() with { IntegrationPriority = false }, plan, ["frontend/a.ts"]));
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request() with { RequiredTestLevel = TestExecutionLevels.Full }, plan, ["frontend/a.ts"]));
        var both = IntegrationCompilePlan.TryPlan(root, Request(), plan, ["frontend/a.ts", "runner/b.cs"]);
        Assert.Equal(3, both!.Commands.Count);
    }

    private static BuildTestGateRequest Request() => new("repo", new string('a', 40), "merge")
    { IntegrationPriority = true, RequiredTestLevel = TestExecutionLevels.CompileOnly };
}
