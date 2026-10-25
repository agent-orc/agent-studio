using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiContentCacheTests : IDisposable
{
    private const string ProjectName = "WikiCache";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wiki-content-cache-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ProjectDocsService _docs;
    private readonly WikiContentCache _cache;

    public WikiContentCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator", "jobs"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide", "start.md"), "# Start\n\nInitial summary.\n");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _root,
                ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
                ["TaskWatcher:DebounceMs"] = "50",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _docs = new ProjectDocsService(
            _scanner,
            registry,
            NullLogger<ProjectDocsService>.Instance);
        _cache = new WikiContentCache(_docs, NullLogger<WikiContentCache>.Instance);
        _docs.SetWikiContentCache(_cache);
    }

    [Fact]
    public void Wiki_file_read_refuses_delivered_symlink_outside_docs()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "wiki-outside-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(outside, "private content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "leak.md"), outside);
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "leak.png"), outside);
            var security = Path.Combine(_root, "docs", "operations", "security");
            Directory.CreateDirectory(security);
            File.CreateSymbolicLink(Path.Combine(security, "leak.md"), outside);
            Assert.Null(_docs.ReadWikiFile(ProjectName, "leak.md"));
            Assert.Null(_docs.ReadWikiAsset(ProjectName, "leak.png"));
            Assert.Null(_docs.ReadSecurityFile(ProjectName, "leak.md"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Wiki_mutations_refuse_delivered_directory_symlink()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "wiki-write-outside-" + Guid.NewGuid().ToString("N"))).FullName;
        var outsidePage = Path.Combine(outside, "page.md");
        File.WriteAllText(outsidePage, "# Outside\n");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "linked"), outside);

            Assert.False(_docs.WriteWikiFile(ProjectName, "linked/page.md", "changed").Success);
            Assert.False(_docs.SetWikiClassificationStatus(
                ProjectName, "linked/page.md", "archived", new WikiCompanionStore()).Success);
            Assert.False(_docs.CreateWikiPage(ProjectName, "linked/new.md", "new").Success);
            Assert.Null(_docs.ResolveWikiNodeFullPath(ProjectName, "linked/page.md"));
            Assert.Equal("# Outside\n", File.ReadAllText(outsidePage));
            Assert.False(File.Exists(outsidePage + ".meta.json"));
            Assert.False(File.Exists(Path.Combine(outside, "new.md")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Wiki_classification_refuses_delivered_companion_symlink()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(),
            "wiki-companion-outside-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outside, "private content");
        try
        {
            var companion = Path.Combine(_root, "docs", "guide", "start.md.meta.json");
            File.CreateSymbolicLink(companion, outside);

            Assert.False(_docs.SetWikiClassificationStatus(
                ProjectName, "guide/start.md", "archived", new WikiCompanionStore()).Success);
            Assert.Equal("private content", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Wiki_order_refuses_delivered_config_directory_symlink()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "wiki-order-outside-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(_root, "docs", "app")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(app, "config"), outside);

            Assert.False(_docs.SetWikiFolderOrder(ProjectName, "", ["guide"]).Success);
            Assert.False(_docs.SetWikiFileOrder(ProjectName, "guide", ["start.md"]).Success);
            Assert.False(File.Exists(Path.Combine(outside, "wiki-order.json")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Preload_FillsOnce_AndAllWikiReadersShareTheSnapshot()
    {
        Assert.True(_cache.Preload(ProjectName));
        Assert.Equal(1, _cache.Fills);

        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config);
        Assert.NotNull(_docs.GetWikiTreeResult(ProjectName));
        Assert.NotNull(_docs.GetWikiOverview(ProjectName));
        Assert.NotNull(_docs.GetWikiFolder(ProjectName, "guide", git));
        Assert.NotNull(_docs.GetWikiHome(ProjectName));
        Assert.NotNull(_docs.GetWikiRecentEditsResult(ProjectName, git));
        Assert.NotNull(_docs.GetWikiPulse(ProjectName, git));

        Assert.Equal(1, _cache.Fills);
        Assert.True(_cache.Hits >= 6);
    }

    [Fact]
    public void Mutation_EagerlyRebuilds_AndGuaranteesReadAfterWrite()
    {
        Assert.True(_cache.Preload(ProjectName));
        var before = _docs.GetWikiTreeResult(ProjectName);

        var write = _docs.WriteWikiFile(
            ProjectName,
            "guide/start.md",
            "# Changed immediately\n\nUpdated summary.\n");

        Assert.True(write.Success);
        Assert.True(write.Changed);
        Assert.Equal(2, _cache.Fills);
        Assert.Equal(1, _cache.MutationInvalidations);

        var after = _docs.GetWikiTreeResult(ProjectName);
        Assert.NotNull(after);
        Assert.NotEqual(before!.ETag, after!.ETag);
        Assert.Equal(
            "Changed immediately",
            Assert.Single(Assert.Single(after.Tree.Root).Children).Title);
        Assert.Equal("Updated summary.", Assert.Single(_docs.GetWikiFolder(ProjectName, "guide")!.Children).Summary);
        Assert.Equal(2, _cache.Fills);
    }

    [Fact]
    public async Task WatcherEvent_EagerlyRebuilds_BeforePublishingTheEvent()
    {
        Assert.True(_cache.Preload(ProjectName));
        using var watcher = new TaskWatcherService(
            _scanner,
            NullLogger<TaskWatcherService>.Instance,
            _config);
        var rebuilt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.OnWikiChanged += (project, _) =>
        {
            _cache.Invalidate(project, WikiContentCache.InvalidationSource.Watcher);
            rebuilt.TrySetResult(true);
        };

        var added = Path.Combine(_root, "docs", "guide", "external.md");
        File.WriteAllText(added, "# External\n");
        watcher.HandleWikiChange(ProjectName, added);

        Assert.True(await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, _cache.Fills);
        Assert.Equal(1, _cache.WatcherInvalidations);
        Assert.Contains(
            Assert.Single(_docs.GetWikiTreeResult(ProjectName)!.Tree.Root).Children,
            node => node.Name == "external.md");
        Assert.Equal(2, _cache.Fills);
    }

    // ---- Warmup: fills off the startup path, and warmup is not a read miss ----

    [Fact]
    public void Warmup_FillsEveryWatchedProject_AndIsNotCountedAsAReadMiss()
    {
        var warmup = new WikiCacheWarmupService(
            _cache,
            _scanner,
            NullLogger<WikiCacheWarmupService>.Instance);

        warmup.WarmAllProjects(CancellationToken.None);

        var afterWarmup = _cache.GetStats();
        Assert.Equal(1, afterWarmup.Preloads);
        Assert.Equal(1, afterWarmup.Fills);
        // The whole point of preloading: no reader ever paid for the cold fill.
        Assert.Equal(0, afterWarmup.Misses);

        Assert.NotNull(_docs.GetWikiTreeResult(ProjectName));

        var afterRead = _cache.GetStats();
        Assert.Equal(1, afterRead.Hits);
        Assert.Equal(0, afterRead.Misses);
        Assert.Equal(1, afterRead.Fills);
    }

    [Fact]
    public async Task Warmup_StartAsync_ReturnsWhileTheFillIsStillRunning()
    {
        // A blocking build function stands in for a large docs/ tree. If the
        // warmup ran on the startup path, StartAsync could not complete until
        // the gate is released.
        using var gate = new ManualResetEventSlim(false);
        using var fillStarted = new ManualResetEventSlim(false);
        var slowCache = new WikiContentCache(
            _ =>
            {
                fillStarted.Set();
                gate.Wait();
                return null;
            },
            NullLogger<WikiContentCache>.Instance);
        var warmup = new WikiCacheWarmupService(
            slowCache,
            _scanner,
            NullLogger<WikiCacheWarmupService>.Instance);

        await warmup.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fillStarted.Wait(TimeSpan.FromSeconds(5)), "warmup never started filling");
        // Startup already returned while the fill is demonstrably still in flight.
        Assert.False(gate.IsSet);

        gate.Set();
        await warmup.StopAsync(CancellationToken.None);
    }

    // ---- Single point of usage: search shares the one snapshot ----

    [Fact]
    public async Task Search_GatesOnTheCentralSnapshot_AndSeesTheEagerRefill()
    {
        var search = new WikiSearchService(
            _scanner,
            new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new CliOneShotRegistry([]),
            _config,
            new AgentStudio.Prompts.RuntimePromptService(
                _config,
                NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance),
            NullLogger<WikiSearchService>.Instance,
            _cache);

        Assert.True(_cache.Preload(ProjectName));
        var hitsBeforeSearch = _cache.GetStats().Hits;

        var first = await search.SearchAsync(ProjectName, "Initial", semantic: false, limit: 10);
        Assert.NotNull(first);
        Assert.Contains(first!.Results, r => r.RelPath == "guide/start.md");
        // The staleness probe read the central snapshot instead of walking docs/.
        Assert.True(
            _cache.GetStats().Hits > hitsBeforeSearch,
            "search did not read through the central wiki cache");
        Assert.Equal(1, _cache.GetStats().Fills);

        var write = _docs.WriteWikiFile(
            ProjectName,
            "guide/start.md",
            "# Start\n\nZeppelin summary.\n");
        Assert.True(write.Success);
        // The mutation already rebuilt the snapshot eagerly.
        Assert.Equal(2, _cache.GetStats().Fills);

        var second = await search.SearchAsync(ProjectName, "Zeppelin", semantic: false, limit: 10);
        Assert.NotNull(second);
        Assert.Contains(second!.Results, r => r.RelPath == "guide/start.md");
        // Serving the new content cost no additional fill.
        Assert.Equal(2, _cache.GetStats().Fills);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
