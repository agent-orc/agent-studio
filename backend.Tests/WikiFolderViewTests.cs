using System.Diagnostics;

using AgentStudio.Git;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki folder-overview surface
/// (<see cref="ProjectDocsService.GetWikiFolder"/>): one directory level with
/// folders-first alphabetical ordering, sniffed page titles (H1 / frontmatter /
/// &lt;title&gt; / file-name fallback), markup-stripped 240-char summaries,
/// non-recursive folder child counts, and the standard traversal guard.
/// </summary>
public class WikiFolderViewTests : IDisposable
{
    private const string ProjectName = "FolderProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiFolderViewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-folder-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Listing + sorting ----

    [Fact]
    public void GetWikiFolder_Root_ListsFoldersFirstThenPagesAlphabetically()
    {
        WritePage("zeta/inner.md", "# Inner\n");
        WritePage("alpha/other.md", "# Other\n");
        WritePage("b-page.md", "# B\n");
        WritePage("a-page.md", "# A\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, null);

        Assert.NotNull(view);
        Assert.Equal("", view!.Path);
        Assert.Equal("docs", view.Name);
        Assert.Equal(
            new[] { "alpha", "zeta", "a-page.md", "b-page.md" },
            view.Children.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "folder", "folder", "page", "page" }, view.Children.Select(c => c.Kind).ToArray());
        Assert.Equal(new string?[] { null, null, "md", "md" }, view.Children.Select(c => c.FileType).ToArray());
    }

    [Fact]
    public void GetWikiFolder_Subfolder_ListsItsChildrenWithRelPaths()
    {
        WritePage("guide/sub/deep.md", "# Deep\n");
        WritePage("guide/intro.md", "# Intro\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "guide");

        Assert.NotNull(view);
        Assert.Equal("guide", view!.Path);
        Assert.Equal("guide", view.Name);
        Assert.Collection(view.Children,
            c => { Assert.Equal("sub", c.Name); Assert.Equal("guide/sub", c.RelPath); Assert.Equal("folder", c.Kind); },
            c => { Assert.Equal("intro.md", c.Name); Assert.Equal("guide/intro.md", c.RelPath); Assert.Equal("page", c.Kind); });
    }

    // ---- Titles ----

    [Fact]
    public void GetWikiFolder_ExtractsTitlesFromH1FrontmatterHtmlTitleAndFileName()
    {
        WritePage("h1.md", "---\ntitle: Ignored When H1 Present\n---\n\n# Heading Title\n\nBody.\n");
        WritePage("frontmatter.md", "---\ntitle: Frontmatter Title\nmodel: x\n---\n\nNo heading here.\n");
        WritePage("10-fallback-name.md", "Just prose, no heading.\n");
        WritePage("titled.html", "<html><head><title>Html Title</title></head><body><p>Hi</p></body></html>");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        string TitleOf(string name) => view!.Children.Single(c => c.Name == name).Title;
        Assert.Equal("Heading Title", TitleOf("h1.md"));
        Assert.Equal("Frontmatter Title", TitleOf("frontmatter.md"));
        Assert.Equal("fallback-name", TitleOf("10-fallback-name.md")); // order prefix stripped
        Assert.Equal("Html Title", TitleOf("titled.html"));
    }

    // ---- Summaries ----

    [Fact]
    public void GetWikiFolder_SummaryIsFirstParagraphStrippedAndCapped()
    {
        WritePage("prose.md",
            "---\ntitle: T\n---\n\n# Prose\n\nThis has **bold**, a [link](https://x.example), and `code` inline.\nSecond line of the same paragraph.\n\nNext paragraph is not included.\n");
        WritePage("long.md", "# Long\n\n" + string.Join(" ", Enumerable.Repeat("wordy", 100)) + "\n");
        WritePage("page.html",
            "<html><head><title>H</title><style>p{color:red}</style></head><body><h1>H</h1><p>First <b>html</b> paragraph.</p><p>Second.</p></body></html>");
        WritePage("folder/inner.md", "# Inner\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        var prose = view!.Children.Single(c => c.Name == "prose.md");
        Assert.Equal("This has bold, a link, and code inline. Second line of the same paragraph.", prose.Summary);

        var longPage = view.Children.Single(c => c.Name == "long.md");
        Assert.NotNull(longPage.Summary);
        Assert.True(longPage.Summary!.Length <= 240, $"summary length {longPage.Summary.Length} > 240");

        var html = view.Children.Single(c => c.Name == "page.html");
        Assert.Equal("First html paragraph.", html.Summary);

        var folder = view.Children.Single(c => c.Name == "folder");
        Assert.Null(folder.Summary);
    }

    // ---- Folder metadata ----

    [Fact]
    public void GetWikiFolder_ChildCountIsDirectPagesPlusNonEmptySubfolders()
    {
        WritePage("guide/one.md", "# One\n");
        WritePage("guide/two.html", "<title>Two</title>");
        WritePage("guide/sub/deep.md", "# Deep\n");
        WritePage("guide/sub/deeper/leaf.md", "# Leaf\n"); // not counted (recursive)
        Directory.CreateDirectory(Path.Combine(_docsDir, "guide", "empty")); // no pages -> not counted

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        var guide = view!.Children.Single(c => c.Name == "guide");
        Assert.Equal("folder", guide.Kind);
        Assert.Equal(3, guide.ChildCount); // one.md + two.html + sub
        Assert.Null(guide.Size);

        var page = BuildDocsService().GetWikiFolder(ProjectName, "guide")!
            .Children.Single(c => c.Name == "one.md");
        Assert.Null(page.ChildCount);
        Assert.True(page.Size > 0);
        Assert.True(page.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void GetWikiFolder_FreshCheckoutUsesCachedGitAuthorDates_AndMarksUntrackedMtime()
    {
        var committedAt = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        const int pageCount = 381;
        for (var i = 0; i < pageCount; i++)
            WritePage($"target-architecture/page-{i:D3}.md", $"# Page {i}\n");

        InitRepo(_tempDir);
        RunGit(_tempDir, "add", "-A");
        RunGitAt(_tempDir, committedAt, "commit", "-q", "-m", "seed old architecture pages");

        // A fresh clone/checkout gives every working-tree file a current mtime.
        // That serving-copy timestamp must not make historical pages look new.
        var checkoutTime = DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(_docsDir, "target-architecture"), "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, checkoutTime);

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log);
        var git = BuildGitService();

        var root = docs.GetWikiFolder(ProjectName, "", git);
        var coldRollup = LastRollup(log);
        var folder = root!.Children.Single(c => c.RelPath == "target-architecture");
        Assert.Equal(committedAt, folder.UpdatedAt);
        Assert.Equal("git", folder.UpdatedAtSource);
        // One batched log. The repo-root lookup and the HEAD probe read the
        // repository files (AGT-3567) and spawn Git only where those files do
        // not settle them, for example on Windows or under a symlinked path.
        var expectedColdSpawns = 1
            + (GitRefFiles.WorkTreeRoot(_tempDir) is null ? 1 : 0)
            + (GitRefFiles.TryResolveHead(_tempDir, out _) == GitRefFileAnswer.Unknown ? 1 : 0);
        Assert.Equal(expectedColdSpawns, coldRollup.Spawns);

        var pages = docs.GetWikiFolder(ProjectName, "target-architecture", git);
        var warmRollup = LastRollup(log);
        Assert.Equal(pageCount, pages!.Children.Count);
        Assert.All(pages.Children, page =>
        {
            Assert.Equal(committedAt, page.UpdatedAt);
            Assert.Equal("git", page.UpdatedAtSource);
        });
        Assert.Equal(0, warmRollup.Spawns); // the complete 381-page index is HEAD-cached

        WritePage("target-architecture/local-draft.md", "# Local draft\n");
        docs.InvalidateWikiContent(
            ProjectName,
            WikiContentCache.InvalidationSource.Watcher);
        var local = docs.GetWikiFolder(ProjectName, "target-architecture", git)!
            .Children.Single(c => c.Name == "local-draft.md");
        Assert.Equal("mtime", local.UpdatedAtSource);
        Assert.True(local.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void GetWikiFolder_SkipsCompanionsJsonHiddenAndEmptyFolders()
    {
        WritePage("real.md", "# Real\n");
        WritePage("real.md.meta.json", "{}");
        WritePage("real.md.report.html", "<html></html>");
        WritePage("data.json", "{ \"title\": \"Data\" }");
        WritePage("home.json", "{ \"sections\": [] }"); // wiki home config, never a page
        WritePage(".hidden/secret.md", "# Secret\n");
        Directory.CreateDirectory(Path.Combine(_docsDir, "empty"));

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        Assert.Equal(new[] { "real.md" }, view!.Children.Select(c => c.Name).ToArray());
    }

    // ---- Classification column (sidecar first, folder-default type fallback) ----

    [Fact]
    public void GetWikiFolder_PagesCarryClassification_FromSidecarOrFolderDefault()
    {
        WritePage("concepts/old.md", "# Old\n");
        WritePage("concepts/old.md.meta.json",
            """
            {
              "source": { "path": "docs/concepts/old.md" },
              "classification": {
                "status": "ueberholt",
                "supersededBy": "concepts/new.md",
                "type": "konzept",
                "analyzedAt": "2026-07-18"
              }
            }
            """);
        WritePage("proposals/finding.md", "# Finding\n");
        WritePage("plain/note.md", "# Note\n");

        var docs = BuildDocsService();

        var old = docs.GetWikiFolder(ProjectName, "concepts")!.Children.Single(c => c.Name == "old.md");
        Assert.NotNull(old.Classification);
        Assert.Equal("ueberholt", old.Classification!.Status);
        Assert.Equal("concepts/new.md", old.Classification.SupersededBy);
        Assert.Equal("konzept", old.Classification.Type);
        Assert.Equal("2026-07-18", old.Classification.AnalyzedAt);

        var finding = docs.GetWikiFolder(ProjectName, "proposals")!.Children.Single(c => c.Name == "finding.md");
        Assert.NotNull(finding.Classification);
        Assert.Equal("proposal", finding.Classification!.Type);
        Assert.Null(finding.Classification.Status);

        var note = docs.GetWikiFolder(ProjectName, "plain")!.Children.Single(c => c.Name == "note.md");
        Assert.NotNull(note.Classification);
        Assert.Equal("doc", note.Classification!.PageType);
        Assert.Null(note.Classification.Status);

        // Folder rows never carry a classification.
        var root = docs.GetWikiFolder(ProjectName, "")!;
        Assert.All(root.Children.Where(c => c.Kind == "folder"), c => Assert.Null(c.Classification));
    }

    [Fact]
    public void GetWikiFolder_PagesCarryAgentReadTotalsAndBoundedHistory()
    {
        WritePage("concepts/read.md", "# Read\n");
        WritePage("concepts/read.md.meta.json",
            """
            {
              "source": { "path": "docs/concepts/read.md" },
              "agentReads": {
                "total": 23,
                "lastReadAt": "2026-07-22T10:15:00Z",
                "recent": [
                  { "at": "2026-07-22T10:15:00Z", "taskKey": "AGT-2242" },
                  { "at": "2026-07-21T09:00:00Z", "taskKey": "AGT-2200" }
                ]
              }
            }
            """);

        var row = BuildDocsService().GetWikiFolder(ProjectName, "concepts")!
            .Children.Single(c => c.Name == "read.md");

        Assert.NotNull(row.AgentReads);
        Assert.Equal(23, row.AgentReads!.Total);
        Assert.Equal(DateTime.Parse("2026-07-22T10:15:00Z").ToUniversalTime(), row.AgentReads.LastReadAt);
        Assert.Equal(new[] { "AGT-2242", "AGT-2200" }, row.AgentReads.Recent.Select(r => r.TaskKey));
    }

    [Fact]
    public void GetWikiFolder_RuntimeAgentReadsOverrideLegacyWithoutRewritingCompanion()
    {
        WritePage("concepts/read.md", "# Read\n");
        WritePage("concepts/read.md.meta.json",
            """{"source":{"path":"docs/concepts/read.md"},"agentReads":{"total":2,"lastReadAt":null,"recent":[]}}""");
        var companionPath = Path.Combine(_docsDir, "concepts", "read.md.meta.json");
        var companionBefore = File.ReadAllText(companionPath);
        new WikiAgentReadStore().Increment(
            _docsDir,
            "concepts/read.md",
            new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc),
            "AGT-2507");

        var row = BuildDocsService().GetWikiFolder(ProjectName, "concepts")!
            .Children.Single(c => c.Name == "read.md");

        Assert.Equal(companionBefore, File.ReadAllText(companionPath));
        Assert.Equal(3, row.AgentReads?.Total);
        Assert.Equal("AGT-2507", Assert.Single(row.AgentReads!.Recent).TaskKey);
    }

    // ---- Guards ----

    [Fact]
    public void GetWikiFolder_RejectsTraversalRootedAndMissingPaths()
    {
        WritePage("guide/intro.md", "# Intro\n");
        File.WriteAllText(Path.Combine(_tempDir, "outside.md"), "# Outside\n");
        var docs = BuildDocsService();

        Assert.Null(docs.GetWikiFolder(ProjectName, "../"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "guide/../../"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "C:/temp"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "does-not-exist"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "guide/intro.md")); // a file, not a folder
        Assert.Null(docs.GetWikiFolder("NopeProject", ""));
    }

    // ---- helpers ----

    private void WritePage(string relPath, string content)
    {
        var full = Path.Combine(_docsDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ProjectDocsService BuildDocsService(ILogger<ProjectDocsService>? logger = null)
    {
        var config = BuildConfig();
        return new ProjectDocsService(
            BuildScanner(config),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            logger ?? NullLogger<ProjectDocsService>.Instance);
    }

    private GitService BuildGitService()
    {
        var config = BuildConfig();
        return new GitService(NullLogger<GitService>.Instance, BuildScanner(config), config);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:RootPath"] = _tempDir,
            ["WatchPaths:0:Path"] = Path.Combine(_tempDir, ".orchestrator", "jobs"),
        })
        .Build();

    private static TaskScannerService BuildScanner(IConfiguration config)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static (int Spawns, int Files) LastRollup(CapturingLogger<ProjectDocsService> log)
    {
        var entry = log.Entries.Last(e =>
            e.Any(kv => kv.Key == "Label" && Equals(kv.Value, "wiki/folder")));
        int Field(string key) => Convert.ToInt32(entry.Single(kv => kv.Key == key).Value);
        return (Field("Spawns"), Field("FileReads"));
    }

    private static void InitRepo(string repoRoot)
    {
        RunGit(repoRoot, "init", "-q", "-b", "main");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
    }

    private static void RunGit(string cwd, params string[] args)
        => RunGitAt(cwd, null, args);

    private static void RunGitAt(string cwd, DateTime? at, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (at is { } timestamp)
        {
            var iso = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["GIT_AUTHOR_DATE"] = iso;
            psi.Environment["GIT_COMMITTER_DATE"] = iso;
        }
        using var process = Process.Start(psi)!;
        // Drain both pipes while waiting and wait for the exit itself: a 15 s
        // cap made `git add -A` fail with an empty error on a loaded host
        // (full-suite run, every core busy), and reading stderr only after
        // the cap hides the reason.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult()}{stdout.GetAwaiter().GetResult()}");
    }
}
