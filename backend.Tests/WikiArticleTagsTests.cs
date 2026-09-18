using AgentStudio.Areas;
using AgentStudio.Docs;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Article front-matter tags and the tree pruning they feed (AGT-2803). Both
/// halves are pure, so neither needs a repository.
/// </summary>
public class WikiArticleTagsTests
{
    [Fact]
    public void FrontmatterTags_ReadsTheInlineList() =>
        Assert.Equal(["delivery-chain", "incident"], ProjectDocsService.FrontmatterTags("""
            ---
            title: A page
            tags: [delivery-chain, incident]
            ---

            # A page
            """));

    [Fact]
    public void FrontmatterTags_ReadsTheBlockList() =>
        Assert.Equal(["observation", "evidence"], ProjectDocsService.FrontmatterTags("""
            ---
            tags:
              - observation
              - evidence
            status: current
            ---

            # A page
            """));

    [Fact]
    public void FrontmatterTags_DropsIdsOutsideTheStableGrammarAndDeduplicates() =>
        Assert.Equal(["observation"], ProjectDocsService.FrontmatterTags("""
            ---
            tags: [observation, "Not An Id", observation, ""]
            ---
            """));

    [Fact]
    public void FrontmatterTags_WithoutFrontMatterOrWithoutTagsIsEmpty()
    {
        Assert.Empty(ProjectDocsService.FrontmatterTags("# A page\n\ntags: [observation]\n"));
        Assert.Empty(ProjectDocsService.FrontmatterTags("---\ntitle: A page\n---\n"));
    }

    [Fact]
    public void WikiTreeFilter_KeepsMatchingPagesAndTheFoldersThatLeadToThem()
    {
        var tree = Tree();

        var filtered = WikiTreeFilter.Apply(tree, TagFilter.Parse("delivery-chain", null));

        var operations = Assert.Single(filtered.Root);
        Assert.Equal("operations", operations.Name);
        Assert.Equal(["merge.md"], operations.Children.Select(node => node.Name));
    }

    [Fact]
    public void WikiTreeFilter_DropsAFolderWithoutAMatchingDescendant()
    {
        var filtered = WikiTreeFilter.Apply(Tree(), TagFilter.Parse("retention", null));

        Assert.Empty(filtered.Root);
    }

    [Fact]
    public void WikiTreeFilter_WithoutAFilterReturnsTheTreeUnchanged()
    {
        var tree = Tree();

        Assert.Same(tree, WikiTreeFilter.Apply(tree, TagFilter.None));
    }

    private static WikiTree Tree() => new("Project", "docs", true,
    [
        new WikiTreeNode("operations", "operations", "operations", "folder",
        [
            new WikiTreeNode("merge.md", "Merge", "operations/merge.md", "md", [], null, null,
                ["delivery-chain", "decision"]),
            new WikiTreeNode("watcher.md", "Watcher", "operations/watcher.md", "md", [], null, null,
                ["observation"]),
        ], null),
        new WikiTreeNode("start", "start", "start", "folder",
        [
            new WikiTreeNode("README.md", "Start", "start/README.md", "md", [], null, null, null),
        ], null),
    ]);
}
