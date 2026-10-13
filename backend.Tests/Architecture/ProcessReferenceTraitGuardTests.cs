using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// AGT-3108: the deferred Remote Review plan selects the test classes that
/// start a changed process-level program by their <c>ProcessReference</c>
/// trait. A missing trait would let a change to that program skip its only
/// process test; a stale one would select a class for nothing. The rules live
/// in <see cref="ProcessReferenceTraitGuard"/>; its failure fixtures are in
/// <see cref="DotNetReferenceSelectionTests"/>.
/// </summary>
public sealed class ProcessReferenceTraitGuardTests
{
    [Fact]
    public void Every_process_test_names_the_program_it_starts()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var testProjects = Directory.EnumerateDirectories(root)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Where(TestSelectionPlanner.IsTestProject)
            .Select(project => Path.GetRelativePath(root, project).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Contains("backend.Tests/OrchestratorApi.Tests.csproj", testProjects);
        Assert.Contains("task-server.Tests/TaskServer.Tests.csproj", testProjects);

        var violations = testProjects
            .SelectMany(project => ProcessReferenceTraitGuard.Check(root, project))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "A test class that starts the built output of a ReferenceOutputAssembly=\"false\" project "
            + "carries [Trait(\"ProcessReference\", \"<ProjectFileName>\")], and only for such a project:\n  "
            + string.Join("\n  ", violations));
    }
}
