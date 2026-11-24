using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class ProjectTestSelectionGuardTests
{
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
