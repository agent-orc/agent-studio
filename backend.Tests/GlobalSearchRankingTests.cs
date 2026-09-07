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
    public void TakeRanked_KeepsTheBestItemsInTheSameOrderAsAFullSort()
    {
        var items = new[]
        {
            Item("notes/AGT-2034-details.md"),
            Item("AGT-2034 follow-up"),
            Item("AGT-2034"),
            Item("archive/AGT-2034.md"),
        };

        var top = GlobalSearchService.TakeRanked(items, "AGT-2034", 2).Select(x => x.Title).ToList();

        Assert.Equal(new[] { "AGT-2034", "AGT-2034 follow-up" }, top);
    }

    [Fact]
    public void TakeRanked_FindsAnExactMatchThatArrivesAfterTheLimitIsFull()
    {
        // A two-character query matches most paths in a real repository. The
        // bounded selection still has to surface the exact match that shows up
        // only after the window is already full.
        var items = Enumerable.Range(0, 200).Select(i => Item($"src/md-{i}.ts"))
            .Append(Item("md"))
            .ToList();

        var top = GlobalSearchService.TakeRanked(items, "md", 5).Select(x => x.Title).ToList();

        Assert.Equal("md", top[0]);
    }

    [Fact]
    public void MatchFiles_RoutesDocsToTheWikiButNeverDocsApp()
    {
        var repository = new GlobalSearchRepository("Fixture", "/tmp/fixture", "#fff");
        var paths = new[] { "docs/system/domains/frontend.md", "docs/app/contract.md", "src/frontend.ts" };

        var matched = GlobalSearchService.MatchFiles(paths, "frontend", repository, 10);

        Assert.Equal(2, matched.Count);
        Assert.True(matched.Single(item => item.Path == "docs/system/domains/frontend.md").IsWiki);
        Assert.False(matched.Single(item => item.Path == "src/frontend.ts").IsWiki);
    }

    [Fact]
    public void MatchCommits_MatchesSubjectAndShaPrefixFromTheIndexedBlob()
    {
        var repository = new GlobalSearchRepository("Fixture", "/tmp/fixture", "#fff");
        var commits = new[]
        {
            Commit("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "a1b2c3d", "Fix the palette spinner"),
            Commit("b1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "b1b2c3d", "Unrelated work"),
        };

        var bySubject = GlobalSearchService.MatchCommits(commits, "palette", "palette", repository, 10);
        var bySha = GlobalSearchService.MatchCommits(commits, "a1b2c3d", "a1b2c3d", repository, 10);

        Assert.Equal("Fix the palette spinner", Assert.Single(bySubject).Title);
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", Assert.Single(bySha).Sha);
    }

    private static GlobalSearchItem Item(string title) =>
        new("files", "Agent Studio", "#fff", title, title);

    private static IndexedCommit Commit(string sha, string shortSha, string subject) =>
        new(sha, shortSha, subject, $"{sha}\x1f{shortSha}\x1f{subject}".ToLowerInvariant());
}
