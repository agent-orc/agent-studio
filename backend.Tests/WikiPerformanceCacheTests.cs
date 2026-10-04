using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Building the wiki tree used to open one file per doc-node for title
/// extraction and stat the complete docs tree on every request. Recent-edits
/// and history also spawned a fresh <c>git log</c> on every navigation. These
/// tests pin the central eager-cache properties, measured deterministically via
/// per-request <see cref="GitProcessTelemetry"/> rollup (files read + git spawns)
/// rather than wall-clock timing:
/// <list type="number">
/// <item>a warm tree request opens zero files and reuses its ETag;</item>
/// <item>a watcher or mutation rebuilds before the next reader;</item>
/// <item>recent / history / revision are served with zero git spawns while HEAD
/// is unchanged, and refresh when a new commit moves HEAD.</item>
/// </list>
/// A separate <see cref="TreeAndRecent_Benchmark_PrintsColdVsWarm"/> prints the
/// cold-vs-warm wall-clock numbers that back the results note.
/// </summary>
// MachineBound 20.07.: git-spawn-/Cache-Timing, Perf-Charakter
[Trait("Category", "MachineBound")]
public class WikiPerformanceCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public WikiPerformanceCacheTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-perf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---- Tree cache: warm request opens no files and keeps its ETag ----

    [Fact]
    public void GetWikiTreeResult_WarmRequest_OpensNoFiles_AndReusesETag()
    {
        var projectRoot = Path.Combine(_tempDir, "tree-warm");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsDir, "concepts"));
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(docsDir, "concepts", $"doc{i}.md"), $"# Title {i}\nbody");

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log, ("Tree", projectRoot));

        Assert.True(docs.PreloadWikiContent("Tree"));
        var cold = docs.GetWikiTreeResult("Tree");

        var warm = docs.GetWikiTreeResult("Tree");
        var warmRollup = LastRollup(log, "wiki/tree");

        Assert.NotNull(cold);
        Assert.NotNull(warm);
        // Startup preload paid the only fill. Both HTTP-shaped reads open no
        // files and return the identical (byte-for-byte) ETag.
        Assert.Equal(0, warmRollup.Files);
        Assert.Equal(cold!.ETag, warm!.ETag);
    }

    [Fact]
    public void GetWikiTreeResult_WatcherInvalidation_EagerlyRebuildsBeforeNextRead()
    {
        var projectRoot = Path.Combine(_tempDir, "tree-edit");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(docsDir);
        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(docsDir, $"doc{i}.md"), $"# Original {i}\nbody");

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log, ("Edit", projectRoot));

        var cold = docs.GetWikiTreeResult("Edit");
        Assert.NotNull(cold);

        // Change one doc's H1 (and length, so the signature differs regardless of
        // filesystem mtime resolution).
        File.WriteAllText(Path.Combine(docsDir, "doc3.md"), "# Renamed heading now\nbody body");
        docs.InvalidateWikiContent("Edit", WikiContentCache.InvalidationSource.Watcher);

        var after = docs.GetWikiTreeResult("Edit");
        var rebuildRollup = LastRollup(log, "wiki/tree");

        Assert.NotNull(after);
        // The signature changed, so the ETag changed and the new title is visible.
        Assert.NotEqual(cold!.ETag, after!.ETag);
        var doc3 = FindNode(after.Tree.Root, "doc3.md");
        Assert.NotNull(doc3);
        Assert.Equal("Renamed heading now", doc3!.Title);
        // Rebuild happened eagerly during invalidation, outside the reader.
        Assert.Equal(0, rebuildRollup.Files);
    }

    [Fact]
    public void GetWikiTreeResult_HeadMovesOutsideDocs_RefreshesSourceCommitAndETag()
    {
        var repoRoot = Path.Combine(_tempDir, "tree-source-head");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "guide.md"), "# Guide\nbody");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed docs");

        var entries = new[] { (Name: "Source", RootPath: repoRoot) };
        var config = BuildConfig(entries);
        var scanner = BuildScanner(entries);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance, git);

        var first = docs.GetWikiTreeResult("Source");
        Assert.NotNull(first);
        var firstCommit = first!.Tree.Source?.Commit;

        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "non-wiki change\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "change outside docs");
        git.InvalidateHeadKeyedCaches();
        docs.InvalidateWikiContent("Source");

        var refreshed = docs.GetWikiTreeResult("Source");
        Assert.NotNull(refreshed);
        Assert.NotEqual(firstCommit, refreshed!.Tree.Source?.Commit);
        Assert.NotEqual(first.ETag, refreshed.ETag);
        Assert.Equal(git.GetHeadShaCached(repoRoot), refreshed.Tree.Source?.Commit);
    }

    [Fact]
    public void WikiBranchSnapshot_ReadsConfiguredCommit_WithoutSwitchingCheckout_AndCachesArchive()
    {
        var repoRoot = Path.Combine(_tempDir, "branch-source");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "source.md"), "# Main\nmain");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "main docs");
        RunGit(repoRoot, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(docsDir, "source.md"), "# Develop\ndevelop");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "develop docs");
        RunGit(repoRoot, "checkout -q main");

        var git = BuildGitService(("Branch", repoRoot));
        var first = git.GetWikiBranchSnapshotCached(repoRoot, "develop");
        var second = git.GetWikiBranchSnapshotCached(repoRoot, "develop");

        Assert.True(first.Success, first.Error);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Contains("develop", File.ReadAllText(Path.Combine(first.RootPath, "docs", "source.md")));
        Assert.Contains("main", File.ReadAllText(Path.Combine(repoRoot, "docs", "source.md")));
        Assert.Equal("main", git.GetStatusForRepoRoot(repoRoot).Branch);
    }

    [Fact]
    public void ConfiguredWikiBranch_DrivesTreeContentAndHistory_WithoutSwitchingCheckout()
    {
        var repoRoot = Path.Combine(_tempDir, "configured-branch-source");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "source.md"), "# Main\nmain\n");
        WriteWorkbench(docsDir, "checkout-only", "Checkout only");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "main docs");
        RunGit(repoRoot, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(docsDir, "source.md"), "# Develop\ndevelop\n");
        Directory.Delete(Path.Combine(docsDir, "operations", "checkout-only"), recursive: true);
        WriteWorkbench(docsDir, "branch-only", "Branch only");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "develop docs");
        RunGit(repoRoot, "checkout -q main");

        var entries = new[] { (Name: "Configured", RootPath: repoRoot) };
        var config = BuildConfig(entries);
        var scanner = BuildScanner(entries);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(
            Path.Combine(repoRoot, ".orchestrator", "jobs"), "Configured", DefaultWorkspace.Id);
        registry.SetWikiSourceBranch(project.Id, "develop");
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var docs = new ProjectDocsService(
            scanner,
            registry,
            NullLogger<ProjectDocsService>.Instance,
            git,
            workbenches);

        var tree = docs.GetWikiTreeResult("Configured");
        var content = docs.ReadWikiFile("Configured", "source.md");
        var missing = docs.ReadWikiFileResult("Configured", "missing.md");
        var catalogue = docs.GetWikiWorkbenchCatalogue("Configured", includeHistory: true);
        var history = docs.GetWikiHistory("Configured", "source.md", git);

        Assert.NotNull(tree);
        Assert.Equal("branch", tree!.Tree.Source?.Mode);
        Assert.Equal("develop", tree.Tree.Source?.Branch);
        Assert.False(tree.Tree.Source?.Writable);
        Assert.Equal("Develop", Assert.Single(
            tree.Tree.Root,
            node => node.RelPath == "source.md").Title);
        Assert.Contains("develop", content?.Content);
        Assert.Null(missing.File);
        Assert.Contains("Wiki source 'develop'", missing.Error);
        var workbench = Assert.Single(catalogue!.Items);
        Assert.Equal("branch-only", workbench.Id);
        Assert.Contains("Branch only", docs.ReadWikiWorkbench("Configured", workbench.Id)!.Html);
        Assert.DoesNotContain(catalogue.Items, item => item.Id == "checkout-only");
        Assert.Equal("develop docs", history?.History.Commits[0].Subject);
        Assert.Contains("main", File.ReadAllText(Path.Combine(docsDir, "source.md")));
        Assert.Equal("main", git.GetStatusForRepoRoot(repoRoot).Branch);
    }

    private static void WriteWorkbench(string docsDir, string id, string title)
    {
        var directory = Path.Combine(docsDir, "operations", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(directory, "workbench.json"), $$"""
          {
            "schemaVersion": 1,
            "id": "{{id}}",
            "title": "{{title}}",
            "summary": "Source convergence fixture.",
            "entrypoint": "index.html",
            "status": "active",
            "phase": "testing",
            "updatedAt": "2026-08-10T10:00:00Z"
          }
          """);
    }

    // ---- Recent edits: HEAD-memoized, zero spawns while HEAD is unchanged ----

    [Fact]
    public void GetWikiRecentEditsResult_WarmRequest_NoGitSpawn_NoFileRead_ThenRefreshesOnNewCommit()
    {
        var repoRoot = Path.Combine(_tempDir, "recent");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "alpha.md"), "# Alpha\nbody");
        File.WriteAllText(Path.Combine(docsDir, "beta.md"), "# Beta\nbody");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed docs");

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log, ("Recent", repoRoot));
        var git = BuildGitService(("Recent", repoRoot));

        var cold = docs.GetWikiRecentEditsResult("Recent", git, 10);
        var coldRollup = LastRollup(log, "wiki/recent");

        var warm = docs.GetWikiRecentEditsResult("Recent", git, 10);
        var warmRollup = LastRollup(log, "wiki/recent");

        Assert.NotNull(cold);
        Assert.Equal(2, cold!.Edits.Edits.Count);
        Assert.True(coldRollup.Spawns >= 1, $"cold spawns={coldRollup.Spawns}");
        // Warm: HEAD unchanged -> the whole payload is memoized. No git log walk,
        // no per-row title read, and the ETag is stable.
        Assert.Equal(0, warmRollup.Spawns);
        Assert.Equal(0, warmRollup.Files);
        Assert.Equal(cold.ETag, warm!.ETag);

        // A new commit moves HEAD; after invalidating the (2s TTL) HEAD probe the
        // refreshed list includes the new page and the ETag changes.
        File.WriteAllText(Path.Combine(docsDir, "gamma.md"), "# Gamma\nnew");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "add gamma");
        git.InvalidateHeadKeyedCaches();
        docs.InvalidateWikiContent(
            "Recent",
            WikiContentCache.InvalidationSource.Watcher);

        var refreshed = docs.GetWikiRecentEditsResult("Recent", git, 10);
        Assert.NotNull(refreshed);
        Assert.Equal(3, refreshed!.Edits.Edits.Count);
        Assert.Equal("gamma.md", refreshed.Edits.Edits[0].RelPath); // newest first
        Assert.NotEqual(cold.ETag, refreshed.ETag);
    }

    // ---- History: HEAD-memoized git side, only the live frontmatter is re-read ----

    [Fact]
    public void GetWikiHistory_WarmRequest_NoGitSpawn_ThenRefreshesOnNewCommit()
    {
        var repoRoot = Path.Combine(_tempDir, "history");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "note.md"), "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log, ("History", repoRoot));
        var git = BuildGitService(("History", repoRoot));

        var cold = docs.GetWikiHistory("History", "note.md", git);
        var coldRollup = LastRollup(log, "wiki/history");

        var warm = docs.GetWikiHistory("History", "note.md", git);
        var warmRollup = LastRollup(log, "wiki/history");

        Assert.NotNull(cold);
        Assert.Single(cold!.History.Commits);
        Assert.True(coldRollup.Spawns >= 1, $"cold spawns={coldRollup.Spawns}");
        // Warm: the git history + trailer are served from the HEAD memo (0 spawns).
        // The frontmatter is still read live (one small file) so uncommitted meta
        // edits are never stale - that is the single expected warm file read.
        Assert.Equal(0, warmRollup.Spawns);
        Assert.Equal(1, warmRollup.Files);
        Assert.Equal(cold.ETag, warm!.ETag);

        // An unrelated commit moves repository HEAD but must not wake a client
        // watching note.md. The validator is scoped to the selected document.
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "# Repo");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update unrelated readme");
        git.InvalidateHeadKeyedCaches();

        var afterUnrelatedCommit = docs.GetWikiHistory("History", "note.md", git);
        Assert.NotNull(afterUnrelatedCommit);
        Assert.Single(afterUnrelatedCommit!.History.Commits);
        Assert.Equal(cold.ETag, afterUnrelatedCommit.ETag);

        // Second commit -> HEAD moves -> history refreshes to two commits.
        File.WriteAllText(Path.Combine(docsDir, "note.md"), "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note");
        git.InvalidateHeadKeyedCaches();

        var refreshed = docs.GetWikiHistory("History", "note.md", git);
        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed!.History.Commits.Count);
        Assert.NotEqual(cold.ETag, refreshed.ETag);
    }

    // ---- Revision: content-addressed, cached permanently ----

    [Fact]
    public void GetWikiRevision_IsContentAddressed_SecondCallHasNoGitSpawn()
    {
        var repoRoot = Path.Combine(_tempDir, "revision");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        InitRepo(repoRoot);
        File.WriteAllText(Path.Combine(docsDir, "note.md"), "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        var log = new CapturingLogger<ProjectDocsService>();
        var docs = BuildDocsService(log, ("Rev", repoRoot));
        var git = BuildGitService(("Rev", repoRoot));

        var sha = git.GetFileHistory(repoRoot, "docs/note.md")[0].Sha;

        // Change and re-commit so the working tree differs from the revision.
        File.WriteAllText(Path.Combine(docsDir, "note.md"), "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note");

        var cold = docs.GetWikiRevision("Rev", sha, "note.md", git);
        var coldRollup = LastRollup(log, "wiki/revision");

        var warm = docs.GetWikiRevision("Rev", sha, "note.md", git);
        var warmRollup = LastRollup(log, "wiki/revision");

        Assert.NotNull(cold);
        Assert.Contains("first", cold!.Revision.Content);
        Assert.DoesNotContain("second", cold.Revision.Content);
        Assert.True(coldRollup.Spawns >= 1, $"cold spawns={coldRollup.Spawns}");
        // The bytes at a concrete sha never change, so the second read is free.
        Assert.Equal(0, warmRollup.Spawns);
        Assert.Equal(cold.ETag, warm!.ETag);
        Assert.Equal(ProjectDocsService.FormatETag("wiki-rev-" + sha), warm.ETag);
    }

    // ---- Endpoint conditional GET: matching If-None-Match -> 304 ----

    [Fact]
    public void ConditionalOk_MatchingIfNoneMatch_Returns304_AndAlwaysSetsETag()
    {
        const string etag = "\"wiki-tree-abc123\"";

        // No validator held by the client -> a 200 result, with the ETag +
        // no-cache headers set (which is what makes the next reload conditional).
        var fresh = new DefaultHttpContext();
        var freshResult = ProjectDocsEndpoints.ConditionalOk(fresh, etag, new { hello = "world" });
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(freshResult).StatusCode);
        Assert.Equal(etag, fresh.Response.Headers.ETag.ToString());
        Assert.Equal("no-cache", fresh.Response.Headers.CacheControl.ToString());

        // Client already holds the current version -> 304 (no body), ETag echoed.
        var reload = new DefaultHttpContext();
        reload.Request.Headers.IfNoneMatch = etag;
        var reloadResult = ProjectDocsEndpoints.ConditionalOk(reload, etag, new { hello = "world" });
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsAssignableFrom<IStatusCodeHttpResult>(reloadResult).StatusCode);
        Assert.Equal(etag, reload.Response.Headers.ETag.ToString());

        // A stale validator (client holds an old version) -> a fresh 200, not 304.
        var stale = new DefaultHttpContext();
        stale.Request.Headers.IfNoneMatch = "\"wiki-tree-OLD\"";
        var staleResult = ProjectDocsEndpoints.ConditionalOk(stale, etag, new { hello = "world" });
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(staleResult).StatusCode);
    }

    // ---- Benchmark: cold vs warm wall-clock (prints to test output) ----

    [Fact]
    public void TreeAndRecent_Benchmark_PrintsColdVsWarm()
    {
        // A synthetic docs/ tree at roughly the scale of the reference project
        // (~355 doc files across ~80 folders) so the printed numbers are
        // representative of a real wiki entry.
        var projectRoot = Path.Combine(_tempDir, "bench");
        var docsDir = Path.Combine(projectRoot, "docs");
        var folders = 40;
        var perFolder = 9;
        for (var d = 0; d < folders; d++)
        {
            var dir = Path.Combine(docsDir, $"{d:D2}-area");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < perFolder; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"{f:D2}-page.md"),
                    $"# Area {d} page {f}\n\nLorem ipsum dolor sit amet, consectetur adipiscing elit.\n");
            }
        }
        var total = folders * perFolder;

        var docs = BuildDocsService(NullLogger<ProjectDocsService>.Instance, ("Bench", projectRoot));

        var sw = Stopwatch.StartNew();
        var cold = docs.GetWikiTreeResult("Bench");
        sw.Stop();
        var coldMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (var i = 0; i < 20; i++) docs.GetWikiTreeResult("Bench");
        sw.Stop();
        var warmMs = sw.Elapsed.TotalMilliseconds / 20.0;

        Assert.NotNull(cold);
        _output.WriteLine($"[wiki/tree] docs={total} cold={coldMs:F1}ms warm(avg of 20)={warmMs:F2}ms " +
                          $"speedup={(warmMs > 0 ? coldMs / warmMs : double.PositiveInfinity):F0}x");
        // Warm must be dramatically cheaper than cold; a very loose bound so the
        // assertion is about "the cache works", not about absolute machine speed.
        Assert.True(warmMs < coldMs, $"warm {warmMs:F2}ms should beat cold {coldMs:F1}ms");
    }

    [Fact]
    public void CurrentRepository_WarmWikiEndpoints_PrintTimingsAndTelemetry()
    {
        var repoRoot = FindRepoRoot();
        var entries = new[] { (Name: "CurrentRepo", RootPath: repoRoot) };
        var config = BuildConfig(entries);
        var scanner = BuildScanner(entries);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var log = new CapturingLogger<ProjectDocsService>();
        var docs = new ProjectDocsService(scanner, registry, log, git, workbenches);
        var cache = new WikiContentCache(docs, NullLogger<WikiContentCache>.Instance);
        docs.SetWikiContentCache(cache);
        var physicalDocs = Path.Combine(repoRoot, ProjectDocsService.WikiRel);
        var physicalFiles = Directory.EnumerateFiles(
            physicalDocs, "*", SearchOption.AllDirectories).Count();
        var physicalFolders = Directory.EnumerateDirectories(
            physicalDocs, "*", SearchOption.AllDirectories).Count() + 1;

        var preload = Stopwatch.StartNew();
        Assert.True(cache.Preload("CurrentRepo"));
        preload.Stop();

        // Prime only the independent HEAD-keyed Git projections. The docs
        // projection was already paid by preload.
        _ = docs.GetWikiRecentEditsResult("CurrentRepo", git, 12);
        _ = docs.GetWikiPulse("CurrentRepo", git, 12);
        _ = docs.GetWikiFolder("CurrentRepo", "", git);

        static double AverageMs(int repetitions, Action action)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < repetitions; i++) action();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / repetitions;
        }

        var treeMs = AverageMs(20, () => Assert.NotNull(docs.GetWikiTreeResult("CurrentRepo")));
        var fullMs = AverageMs(20, () => Assert.NotNull(docs.GetWikiOverview("CurrentRepo")));
        var recentMs = AverageMs(5, () => Assert.NotNull(docs.GetWikiRecentEditsResult("CurrentRepo", git, 12)));
        var pulseMs = AverageMs(3, () => Assert.NotNull(docs.GetWikiPulse("CurrentRepo", git, 12)));
        var folderMs = AverageMs(10, () => Assert.NotNull(docs.GetWikiFolder("CurrentRepo", "", git)));
        var homeMs = AverageMs(20, () => Assert.NotNull(docs.GetWikiHome("CurrentRepo")));

        var overview = docs.GetWikiOverview("CurrentRepo")!;
        var treeTelemetry = LastRollup(log, "wiki/tree");
        var fullTelemetry = LastRollup(log, "wiki");
        var recentTelemetry = LastRollup(log, "wiki/recent");
        var pulseTelemetry = LastRollup(log, "wiki/pulse");
        var folderTelemetry = LastRollup(log, "wiki/folder");
        var homeTelemetry = LastRollup(log, "wiki/home");

        _output.WriteLine(
            $"[wiki/cache] files={physicalFiles} folders={physicalFolders} pages={overview.Files.Count} "
            + $"preload={preload.Elapsed.TotalMilliseconds:F1}ms fills={cache.Fills}");
        _output.WriteLine(
            $"[wiki/tree] warm={treeMs:F2}ms spawns={treeTelemetry.Spawns} files={treeTelemetry.Files}");
        _output.WriteLine(
            $"[wiki] warm={fullMs:F2}ms spawns={fullTelemetry.Spawns} files={fullTelemetry.Files}");
        _output.WriteLine(
            $"[wiki/recent] warm={recentMs:F2}ms spawns={recentTelemetry.Spawns} files={recentTelemetry.Files}");
        _output.WriteLine(
            $"[wiki/pulse] warm={pulseMs:F2}ms spawns={pulseTelemetry.Spawns} files={pulseTelemetry.Files}");
        _output.WriteLine(
            $"[wiki/folder] warm={folderMs:F2}ms spawns={folderTelemetry.Spawns} files={folderTelemetry.Files}");
        _output.WriteLine(
            $"[wiki/home] warm={homeMs:F2}ms spawns={homeTelemetry.Spawns} files={homeTelemetry.Files}");

        Assert.Equal(1, cache.Fills);
        Assert.Equal((0, 0), treeTelemetry);
        Assert.Equal((0, 0), fullTelemetry);
        Assert.Equal((0, 0), recentTelemetry);
        Assert.Equal((0, 0), pulseTelemetry);
        Assert.Equal((0, 0), folderTelemetry);
        Assert.Equal((0, 0), homeTelemetry);
    }

    // ---- helpers ----

    private static WikiTreeNode? FindNode(IEnumerable<WikiTreeNode> nodes, string name)
    {
        foreach (var n in nodes)
        {
            if (n.Name == name) return n;
            var hit = FindNode(n.Children, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static (int Spawns, int Files) LastRollup(CapturingLogger<ProjectDocsService> log, string label)
    {
        for (var i = log.Entries.Count - 1; i >= 0; i--)
        {
            var e = log.Entries[i];
            var lbl = Field(e, "Label")?.ToString();
            if (lbl != label) continue;
            var spawns = Convert.ToInt32(Field(e, "Spawns"));
            var files = Convert.ToInt32(Field(e, "FileReads"));
            return (spawns, files);
        }
        throw new Xunit.Sdk.XunitException($"no git-info rollup captured for label '{label}'");
    }

    private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> state, string key)
    {
        foreach (var kv in state)
            if (kv.Key == key) return kv.Value;
        return null;
    }

    private ProjectDocsService BuildDocsService(ILogger<ProjectDocsService> logger, params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries),
               new ProjectRegistry(BuildConfig(entries), NullLogger<ProjectRegistry>.Instance),
               logger);

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
        => new(NullLogger<GitService>.Instance, BuildScanner(entries), BuildConfig(entries));

    private static IConfiguration BuildConfig((string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TaskScannerService BuildScanner((string Name, string RootPath)[] entries)
    {
        var config = BuildConfig(entries);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static void InitRepo(string repoRoot)
    {
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
    }

    private static string FindRepoRoot()
        => RepositoryRoot.Find();

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private static void RunGitArgs(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that captures each log call's structured
/// state so a test can read back the <see cref="GitProcessTelemetry"/> rollup
/// fields (spawns / file reads / label) without parsing a formatted string.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    public List<IReadOnlyList<KeyValuePair<string, object?>>> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
        {
            lock (_gate) Entries.Add(kvs);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
