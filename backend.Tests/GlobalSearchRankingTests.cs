using AgentStudio.Search;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GlobalSearchRankingTests
{
    [Fact]
    public void RankItems_PutsExactThenPrefixBeforeContains()
    {
        var items = new[]
        {
            Item("notes/AGT-2034-details.md"),
            Item("AGT-2034 follow-up"),
            Item("AGT-2034"),
        };

        var ranked = GlobalSearchService.RankItems(items, "AGT-2034").Select(x => x.Title).ToList();

        Assert.Equal(new[] { "AGT-2034", "AGT-2034 follow-up", "notes/AGT-2034-details.md" }, ranked);
    }

    [Fact]
    public void MatchFiles_MatchesPathsFromTheIndexWithoutTouchingDisk()
    {
        var paths = new[] { "README-search-proof.md", "docs/guide.md", "docs/app/contract.md", "src/main.cs" };

        var results = GlobalSearchService.MatchFiles(paths, Repository(), "search-proof");

        Assert.Single(results);
        Assert.Equal("README-search-proof.md", results[0].Path);
        Assert.Equal("Fixture", results[0].ProjectName);
    }

    [Fact]
    public void MatchFiles_RoutesDocsToTheWikiButNeverTheDocsAppCodeContract()
    {
        var results = GlobalSearchService.MatchFiles(
            ["docs/guide.md", "docs/app/contract.md"], Repository(), ".md");

        Assert.Equal(new[] { true, false }, results.Select(r => r.IsWiki));
    }

    [Fact]
    public void MatchCommits_MatchesSubjectShaAndShortSha()
    {
        var commits = new[]
        {
            new CommitIndexEntry("aaaaaaaabbbbbbbb", "aaaaaaa", "feat: global search streaming"),
            new CommitIndexEntry("ccccccccdddddddd", "ccccccc", "chore: unrelated"),
        };

        Assert.Single(GlobalSearchService.MatchCommits(commits, Repository(), "streaming"));
        Assert.Single(GlobalSearchService.MatchCommits(commits, Repository(), "aaaaaaaabbbb"));
        Assert.Single(GlobalSearchService.MatchCommits(commits, Repository(), "ccccccc"));
        Assert.Equal(2, GlobalSearchService.MatchCommits(commits, Repository(), "c").Count);
    }

    private static SearchRepository Repository() => new("Fixture", "/tmp/fixture", "#fff");

    private static GlobalSearchItem Item(string title) =>
        new("files", "Agent Studio", "#fff", title, title);
}
