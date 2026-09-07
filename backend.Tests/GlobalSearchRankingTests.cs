using AgentStudio.Search;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GlobalSearchRankingTests
{
    private static readonly GlobalSearchTarget Fixture = new("Agent Studio", "/tmp/fixture", "#fff");

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
    public void MatchFiles_MatchesPathSubstringsAndRoutesDocsToTheWiki()
    {
        string[] paths = ["README-search-proof.md", "docs/system/domains/frontend.md", "docs/app/contract.json", "src/main.ts"];

        var matches = GlobalSearchService.MatchFiles(paths, Fixture, "md").ToList();

        Assert.Equal(
            new[] { "README-search-proof.md", "docs/system/domains/frontend.md" },
            matches.Select(x => x.Path));
        Assert.False(matches[0].IsWiki);
        Assert.True(matches[1].IsWiki);
    }

    [Fact]
    public void MatchFiles_KeepsDocsAppOutOfTheWikiViewer()
    {
        var matches = GlobalSearchService.MatchFiles(["docs/app/contract.json"], Fixture, "contract").ToList();

        Assert.False(Assert.Single(matches).IsWiki);
    }

    [Fact]
    public void MatchCommits_MatchesShaPrefixAndSubject()
    {
        IndexedCommit[] commits =
        [
            new("abc123def456", "abc123d", "feat: streamed search"),
            new("999888777666", "9998887", "chore: unrelated"),
        ];

        Assert.Equal("abc123def456", Assert.Single(GlobalSearchService.MatchCommits(commits, Fixture, "abc123")).Sha);
        Assert.Equal("abc123def456", Assert.Single(GlobalSearchService.MatchCommits(commits, Fixture, "streamed")).Sha);
        Assert.Empty(GlobalSearchService.MatchCommits(commits, Fixture, "nothing-here"));
    }

    private static GlobalSearchItem Item(string title) =>
        new("files", "Agent Studio", "#fff", title, title);
}
