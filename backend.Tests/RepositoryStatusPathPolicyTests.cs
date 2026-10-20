using Xunit;

namespace AgentStudio.Tests;

public sealed class RepositoryStatusPathPolicyTests
{
    [Theory]
    [InlineData("docs/a/workbench.json", "docs/a/workbench.json", true)]
    [InlineData("docs/a/", "docs/a/workbench.json", true)]
    [InlineData("docs/", "docs/a/workbench.json", true)]
    [InlineData("docs/a/", "docs/ab/workbench.json", false)]
    [InlineData("docs/a", "docs/a/workbench.json", false)]
    [InlineData("docs/a/other.json", "docs/a/workbench.json", false)]
    [InlineData("docs/old.json -> docs/a/workbench.json", "docs/a/workbench.json", true)]
    [InlineData("docs/a/workbench.json -> docs/new.json", "docs/a/workbench.json", true)]
    [InlineData("\"docs/a b/\"", "docs/a b/workbench.json", true)]
    [InlineData("docs\\a\\", "docs/a/workbench.json", true)]
    public void CoveredPaths_MatchesExactEntriesAndUntrackedDirectoryPrefixes(
        string statusPath, string target, bool covered)
    {
        var result = RepositoryStatusPathPolicy.CoveredPaths([statusPath], [target], StringComparer.Ordinal);

        Assert.Equal(covered, result.Count == 1);
    }

    [Fact]
    public void CoveredPaths_ReturnsEachCoveredTargetOnce()
    {
        var result = RepositoryStatusPathPolicy.CoveredPaths(
            ["docs/a/", "docs/a/workbench.json", "README.md"],
            ["docs/a/workbench.json", "docs/b/workbench.json", "/README.md"],
            StringComparer.Ordinal);

        Assert.Equal(["docs/a/workbench.json", "README.md"], result);
    }
}
