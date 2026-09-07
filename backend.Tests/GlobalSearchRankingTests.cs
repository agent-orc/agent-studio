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
    public void FileIndex_ListsTrackedFilesOnceAndMatchesInMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"global-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init");
            File.WriteAllText(Path.Combine(root, "README-search-proof.md"), "proof");
            RunGit(root, "add", "README-search-proof.md");

            // The index is query-independent; matching is a pure in-memory pass
            // over it, so the same listing answers any number of terms.
            var paths = GlobalSearchService.ReadFilePaths(root);
            var target = new GlobalSearchService.RepositoryTarget("Fixture", root, "#fff");

            var results = GlobalSearchService.MatchFiles(paths, target, "search-proof");

            Assert.Single(results);
            Assert.Equal("README-search-proof.md", results[0].Path);
            Assert.Empty(GlobalSearchService.MatchFiles(paths, target, "no-such-file"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static GlobalSearchItem Item(string title) =>
        new("files", "Agent Studio", "#fff", title, title);

    private static void RunGit(string root, params string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new("git") {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
