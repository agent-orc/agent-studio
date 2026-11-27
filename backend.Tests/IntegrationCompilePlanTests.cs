using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using AgentStudio.TaskServer.Contracts;
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
        using var fixture = new DefinitionFixture();
        var root = fixture.Root;
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
        using var fixture = new DefinitionFixture();
        var root = fixture.Root;
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request(), VerifyCommandPlanner.Plan(root, null), [path]));
    }

    [Fact]
    public void Review_and_release_plans_do_not_use_the_integration_map()
    {
        using var fixture = new DefinitionFixture();
        var root = fixture.Root;
        var plan = VerifyCommandPlanner.Plan(root, null);
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request() with { IntegrationPriority = false }, plan, ["frontend/a.ts"]));
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request() with { RequiredTestLevel = TestExecutionLevels.Full }, plan, ["frontend/a.ts"]));
        var both = IntegrationCompilePlan.TryPlan(root, Request(), plan, ["frontend/a.ts", "runner/b.cs"]);
        Assert.Equal(3, both!.Commands.Count);
    }

    [Theory]
    [InlineData("frontend/a.ts")]
    [InlineData(".nvmrc")]
    [InlineData("scripts/npm-frontend.sh")]
    [InlineData("backend/a.cs")]
    [InlineData("Directory.Build.props")]
    [InlineData("tools/SilentCatchAnalyzer/Analyzer.cs")]
    public void Absent_map_uses_the_same_scope_for_the_known_repository_layout(string path)
    {
        using var fixture = new DefinitionFixture();
        using var absent = new DefinitionFixture(includeMap: false, knownLayout: true);
        var root = absent.Root;
        Assert.Empty(ProjectDefinitionReader.ReadWorkspace(root).Definition!.IntegrationBuild);
        var expected = IntegrationCompilePlan.TryPlan(fixture.Root, Request(),
            VerifyCommandPlanner.Plan(fixture.Root, null), [path]);
        var actual = IntegrationCompilePlan.TryPlan(root, Request(), VerifyCommandPlanner.Plan(root, null), [path]);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Commands, actual.Commands);
        Assert.Equal(expected.PreparationCommands, actual.PreparationCommands);
    }

    [Fact]
    public void Absent_map_with_a_different_layout_or_build_keeps_the_full_build()
    {
        using var fixture = new DefinitionFixture(includeMap: false);
        var definition = Path.Combine(fixture.Root, ".agent-studio/project.yml");
        Assert.Null(IntegrationCompilePlan.TryPlan(fixture.Root, Request(),
            VerifyCommandPlanner.Plan(fixture.Root, null), ["frontend/a.ts"]));
        foreach (var file in new[] { "agent-taskboard.sln", "backend/OrchestratorApi.csproj", "frontend/package.json",
            ".nvmrc", "scripts/check-release-build.sh", "scripts/npm-frontend.sh", ".agent-studio/prepare-integration.sh" })
        {
            var target = Path.Combine(fixture.Root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "");
        }
        File.WriteAllText(definition, File.ReadAllText(definition).Replace(
            "dotnet build agent-taskboard.sln --no-restore", "dotnet build other.sln"));
        Assert.Null(IntegrationCompilePlan.TryPlan(fixture.Root, Request(),
            VerifyCommandPlanner.Plan(fixture.Root, null), ["frontend/a.ts"]));
    }

    [Theory]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("scripts/check-release-build.sh")]
    [InlineData("unknown.txt")]
    public void Absent_map_keeps_the_full_build_for_shared_inputs(string path)
    {
        using var fixture = new DefinitionFixture(includeMap: false, knownLayout: true);
        var root = fixture.Root;
        Assert.Null(IntegrationCompilePlan.TryPlan(root, Request(), VerifyCommandPlanner.Plan(root, null), [path]));
    }

    private sealed class DefinitionFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "integration-plan-" + Guid.NewGuid().ToString("N"));

        internal DefinitionFixture(bool includeMap = true, bool knownLayout = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, ".agent-studio"));
            var yaml = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "testdata/project-integration-build.project.yml"));
            if (!includeMap) yaml = yaml[..yaml.IndexOf("# Only merge", StringComparison.Ordinal)];
            File.WriteAllText(Path.Combine(Root, ".agent-studio/project.yml"), yaml);
            if (knownLayout)
                foreach (var file in new[] { "agent-taskboard.sln", "backend/OrchestratorApi.csproj", "frontend/package.json",
                    "scripts/check-release-build.sh", "scripts/npm-frontend.sh", ".agent-studio/prepare-integration.sh" })
                {
                    var target = Path.Combine(Root, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "");
                }
            foreach (var file in new[] { ".agent-studio/prepare", ".nvmrc", "global.json" })
                File.Copy(Path.Combine(RepositoryRoot.Find(), file), Path.Combine(Root, file));
            var read = ProjectDefinitionReader.ReadWorkspace(Root);
            Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => issue.Message)));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static BuildTestGateRequest Request() => new("repo", new string('a', 40), "merge")
    { IntegrationPriority = true, RequiredTestLevel = TestExecutionLevels.CompileOnly };
}
