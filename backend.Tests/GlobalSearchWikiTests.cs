using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GlobalSearchWikiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"global-search-wiki-{Guid.NewGuid():N}");

    [Fact]
    public void Search_ReturnsWikiTitlesAndHeadingsWithoutBodyOnlyMatches()
    {
        Write("architecture.md", "# Architecture map\n\n## Recovery authority\n\nOverview.\n");
        Write("notes.md", "# Notes\n\nRecovery authority occurs only in body text.\n");
        var service = Build();

        var response = service.Search("Recovery authority",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wiki" }, 20);

        var item = Assert.Single(response.Wiki);
        Assert.Equal("Architecture map", item.Title);
        Assert.Equal("Recovery authority", item.Subtitle);
        Assert.Equal("architecture.md", item.Path);
        Assert.True(item.IsWiki);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    private GlobalSearchService Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Wiki Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var indexes = new GlobalSearchIndexes(config, git, NullLogger<GlobalSearchIndexes>.Instance);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance, git, workbenches);
        var wiki = new WikiSearchService(
            scanner, registry, new CliOneShotRegistry([]), config,
            new AgentStudio.Prompts.RuntimePromptService(
                config, NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance),
            NullLogger<WikiSearchService>.Instance);
        return new GlobalSearchService(scanner, indexes, registry, docs,
            NullLogger<GlobalSearchService>.Instance, wiki, workbenches);
    }

    private void Write(string relPath, string content)
    {
        var path = Path.Combine(_root, "docs", relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
