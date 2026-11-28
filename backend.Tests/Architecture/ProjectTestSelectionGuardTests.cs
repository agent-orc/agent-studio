using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class ProjectTestSelectionGuardTests
{
    [Theory]
    [Trait("Category", "Guard")]
    [InlineData("backend.Tests/OrchestratorApi.Tests.csproj", "Category!=MachineBound", "&Category!=ReviewBase")]
    [InlineData("orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj", "Category!=MachineBound",
        "&FullyQualifiedName!~OrchestratorEngine.Tests.StandaloneReviewLoopCutoverTests")]
    [InlineData("backend.Tests/OrchestratorApi.Tests.csproj", "Category!=ReviewFlaky", null)]
    [InlineData("orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj", "Category!=ReviewFlaky", null)]
    public void Fixed_checks_only_replace_cases_covered_by_the_same_project_and_filter(
        string project, string filter, string? fixedExclusion)
    {
        const string selectedClass = "OrchestratorEngine.Tests.StandaloneReviewLoopCutoverTests";
        var root = RepositoryRoot.Find();
        var declared = VerifyCommandPlanner.Plan(root, null);
        var original = Assert.Single(declared.Commands, command => command.Command.StartsWith(
            $"dotnet test {project} --no-build ", StringComparison.Ordinal));
        var slice = $"({filter})&(FullyQualifiedName~{selectedClass})";
        var selected = original with { Command = $"dotnet test {project} --no-build --filter \"{slice}\"" };

        var plan = PerCardVerifyPlanner.WithReviewBase(root, declared,
            new StagedVerifyPlan([selected], new TestSelectionAudit()), ["contracts/Changed.cs"]);

        var retained = Assert.Single(plan.Commands, command => command.TestScope != ReviewBaseChecks.Scope);
        var expected = fixedExclusion is null ? selected.Command
            : $"dotnet test {project} --no-build --filter \"({slice})&(Category!=Guard&FullyQualifiedName!~.Architecture.{fixedExclusion})\"";
        Assert.Equal(expected, retained.Command);
        var replacement = Assert.Single(plan.Commands, command => command.TestScope == ReviewBaseChecks.Scope
            && command.Command.StartsWith($"dotnet test {project} ", StringComparison.Ordinal));
        Assert.Contains("(Category!=MachineBound)&(", replacement.Command);
        Assert.Contains(GuardFirstGatePlan.GuardFilter, replacement.Command);
        Assert.Contains(TestSelectionPlanner.Describe(retained), plan.Audit.SelectedCommands);
        Assert.Contains(TestSelectionPlanner.Describe(replacement), plan.Audit.SelectedCommands);
        Assert.Empty(plan.Audit.AttemptedTestCommands);
    }

    [Fact]
    [Trait("Category", "Guard")]
    [Trait("Category", "MachineBound")]
    public void EveryTrackedTopLevelSourceFolderAndDeclaredCommandHasAnImpactAssignment()
    {
        var root = RepositoryRoot.Find();
        var read = ProjectDefinitionReader.ReadWorkspace(root);
        Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => issue.Message)));
        // Until the next deploy the repository keeps its legacy definition.
        // Police the activation candidate without enabling a field older hosts refuse.
        var candidate = ProjectDefinitionReader.Parse(File.ReadAllText(Path.Combine(root,
            "testdata/project-test-selection.project.yml")));
        Assert.True(candidate.IsValid, string.Join("; ", candidate.Issues.Select(issue => issue.Message)));
        var definition = read.Definition!.TestSelection.Count > 0 ? read.Definition : candidate.Definition!;
        Assert.NotEmpty(definition.TestSelection);
        // Git's inventory ignores node_modules, build outputs and temporary evidence.
        using var process = Process.Start(new ProcessStartInfo("git", ["ls-files", "-z"])
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        var sourceFolders = files.Where(path => path.Contains('/')
            && Path.GetExtension(path) is ".cs" or ".csproj" or ".ts" or ".js" or ".mjs" or ".sh")
            .Select(path => path.Split('/')[0] + "/").Distinct().Order().ToArray();
        Assert.All(sourceFolders, folder => Assert.Contains(definition.TestSelection,
            row => row.Paths.Contains(folder)));
        Assert.False(ProjectTestSelectionPolicy.Select(definition, ["frontend/src/app/app.ts"]).Full,
            "An unassigned command must be mapped, rather than silently reverting every gate to full.");
        Assert.All(definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Routine),
            suite => Assert.Contains(definition.TestSelection, row => row.Suites.Contains(suite.Id)));
    }
}
