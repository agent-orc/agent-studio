using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the curated wiki home surface
/// (<see cref="ProjectDocsService.GetWikiHome"/>): parsing
/// <c>docs/app/config/home.json</c> with per-link <c>exists</c> flags (dead links are
/// kept and flagged, not dropped), the empty-sections degradation for a missing
/// or malformed file, and the traversal guard on link targets. Also proves the
/// shipped seed in this repository parses and only references existing pages.
/// </summary>
public class WikiHomeTests : IDisposable
{
    private const string ProjectName = "HomeProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiHomeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-home-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void GetWikiHome_ParsesSectionsAndAnnotatesExistsFlags()
    {
        WriteDoc("guides/setup.md", "# Setup\n");
        WriteDoc("app/config/home.json", """
            {
              "sections": [
                {
                  "title": "Einstieg",
                  "links": [
                    { "relPath": "guides/setup.md", "label": "Setup-Guide", "note": "Erste Schritte." },
                    { "relPath": "missing/page.md", "label": "Fehlt", "note": "Noch nicht geschrieben." }
                  ]
                },
                {
                  "title": "Leer",
                  "links": []
                }
              ]
            }
            """);

        var home = BuildDocsService().GetWikiHome(ProjectName);

        Assert.NotNull(home);
        Assert.Equal(2, home!.Sections.Count);

        var first = home.Sections[0];
        Assert.Equal("Einstieg", first.Title);
        Assert.Collection(first.Links,
            l =>
            {
                Assert.Equal("guides/setup.md", l.RelPath);
                Assert.Equal("Setup-Guide", l.Label);
                Assert.Equal("Erste Schritte.", l.Note);
                Assert.True(l.Exists);
            },
            l =>
            {
                Assert.Equal("missing/page.md", l.RelPath);
                Assert.False(l.Exists); // kept and flagged, not dropped
            });

        Assert.Equal("Leer", home.Sections[1].Title);
        Assert.Empty(home.Sections[1].Links);
    }

    [Fact]
    public void GetWikiHome_MissingFile_ReturnsEmptySections()
    {
        var home = BuildDocsService().GetWikiHome(ProjectName);

        Assert.NotNull(home);
        Assert.Empty(home!.Sections);
    }

    [Fact]
    public void GetWikiHome_MalformedJson_ReturnsEmptySections()
    {
        WriteDoc("app/config/home.json", "{ this is not json ");

        var home = BuildDocsService().GetWikiHome(ProjectName);

        Assert.NotNull(home);
        Assert.Empty(home!.Sections);
    }

    [Fact]
    public void GetWikiHome_TraversalLinkTarget_IsFlaggedNotExisting()
    {
        // The file exists OUTSIDE docs/ - the traversal guard must refuse to see it.
        File.WriteAllText(Path.Combine(_tempDir, "outside.md"), "# Outside\n");
        WriteDoc("app/config/home.json", """
            {
              "sections": [
                { "title": "Boese", "links": [ { "relPath": "../outside.md", "label": "Escape" } ] }
              ]
            }
            """);

        var home = BuildDocsService().GetWikiHome(ProjectName);

        var link = Assert.Single(Assert.Single(home!.Sections).Links);
        Assert.False(link.Exists);
    }

    [Fact]
    public void GetWikiHome_UnknownProject_ReturnsNull()
    {
        Assert.Null(BuildDocsService().GetWikiHome("NopeProject"));
    }

    /// <summary>
    /// The shipped seed (<c>docs/app/config/home.json</c> in this repository) must
    /// parse into curated sections whose links all point at pages that exist.
    /// </summary>
    [Fact]
    public void GetWikiHome_RepoSeed_ParsesWithAllLinksExisting()
    {
        var repoRoot = FindRepoRoot();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "SeedRepo",
                ["WatchPaths:0:RootPath"] = repoRoot,
                ["WatchPaths:0:Path"] = Path.Combine(repoRoot, ".orchestrator", "jobs"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var docs = new ProjectDocsService(
            scanner,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<ProjectDocsService>.Instance);

        var home = docs.GetWikiHome("SeedRepo");

        Assert.NotNull(home);
        // The seed grows with curation; the honest invariant is "curated,
        // non-empty, and every link resolves" - not a fixed section count.
        Assert.True(home!.Sections.Count >= 3,
            $"expected at least 3 curated sections, found {home.Sections.Count}");
        Assert.All(home.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.All(home.Sections, s => Assert.NotEmpty(s.Links));
        Assert.All(home.Sections.SelectMany(s => s.Links),
            l => Assert.True(l.Exists, $"seed link '{l.RelPath}' does not exist on disk"));
    }

    // ---- helpers ----

    private static string FindRepoRoot()
        => RepositoryRoot.Find();

    private void WriteDoc(string relPath, string content)
    {
        var full = Path.Combine(_docsDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ProjectDocsService BuildDocsService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _tempDir,
                ["WatchPaths:0:Path"] = Path.Combine(_tempDir, ".orchestrator", "jobs"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new ProjectDocsService(
            scanner,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<ProjectDocsService>.Instance);
    }
}
