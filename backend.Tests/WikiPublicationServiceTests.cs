using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end behaviour of hosted wiki publication against a real repository
/// triple: a bare <c>origin</c>, an authoring clone that pushes accepted
/// documentation, and the deployment checkout the hosted wiki serves. Nothing
/// in these tests copies a file into the deployment checkout by hand, which is
/// the point of the feature.
/// </summary>
public sealed class WikiPublicationServiceTests : IDisposable
{
    private const string ProjectName = "Hosted";

    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "wiki-publication-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _author;
    private readonly string _server;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ProjectRecord _project;
    private readonly GitService _git;
    private readonly WikiPublicationService _publication;
    private readonly ProjectDocsService _docs;
    private readonly WikiContentCache _cache;

    public WikiPublicationServiceTests()
    {
        _origin = Path.Combine(_temp, "origin.git");
        _author = Path.Combine(_temp, "author");
        _server = Path.Combine(_temp, "server");
        Directory.CreateDirectory(_temp);

        Git(_temp, "init", "--bare", "-q", "-b", "main", _origin);
        Git(_temp, "clone", "-q", _origin, _author);
        Identify(_author);
        WriteDoc(_author, "start.md", "# Start\n\nFirst accepted page.\n");
        Git(_author, "add", "-A");
        Git(_author, "commit", "-q", "-m", "docs: first accepted page");
        Git(_author, "push", "-q", "-u", "origin", "main");
        Git(_author, "checkout", "-q", "-b", "develop");
        Git(_author, "push", "-q", "-u", "origin", "develop");

        Git(_temp, "clone", "-q", _origin, _server);
        Identify(_server);
        Directory.CreateDirectory(Path.Combine(_server, ".orchestrator", "jobs"));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _server,
                ["WatchPaths:0:Path"] = Path.Combine(_server, ".orchestrator", "jobs"),
                ["WikiPublication:Enabled"] = "true",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _project = _registry.EnsureProjectForStorage(
            Path.Combine(_server, ".orchestrator", "jobs"), ProjectName, DefaultWorkspace.Id);
        _registry.SetWikiSourceBranch(_project.Id, "origin/develop");

        _git = new GitService(NullLogger<GitService>.Instance, _scanner, _config);
        _publication = new WikiPublicationService(
            _scanner, _registry, NullLogger<WikiPublicationService>.Instance, _git, _config);
        _docs = new ProjectDocsService(
            _scanner, _registry, NullLogger<ProjectDocsService>.Instance, _git,
            publication: _publication);
        _cache = new WikiContentCache(_docs, NullLogger<WikiContentCache>.Instance);
        _docs.SetWikiContentCache(_cache);
        _publication.SetWikiContentCache(_cache);
    }

    // ---- Acceptance 1 + 2: the accepted revision becomes visible, on one commit ----

    [Fact]
    public void Synchronize_PublishesTheAcceptedRevision_AndEveryReadResolvesAgainstThatCommit()
    {
        var outcome = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Promoted, outcome.Status);
        Assert.Null(outcome.FromSha);
        Assert.Equal(DevelopSha(), outcome.ToSha);

        var report = _publication.GetReport(ProjectName)!;
        Assert.True(report.Enabled);
        Assert.Equal("origin/develop", report.SourceRef);
        Assert.Equal(DevelopSha(), report.PublishedSha);
        Assert.NotNull(report.PublishedAtUtc);

        var tree = _docs.GetWikiTreeResult(ProjectName);
        Assert.Equal("branch", tree!.Tree.Source!.Mode);
        Assert.Equal(DevelopSha(), tree.Tree.Source.Commit);
        Assert.Contains(tree.Tree.Root, node => node.RelPath == "start.md");
        Assert.Contains("First accepted page", _docs.ReadWikiFile(ProjectName, "start.md")!.Content);

        // Overview, folder view, Pulse, and the revision preview all read the
        // published snapshot, so the whole surface is single-valued for this
        // commit rather than each endpoint resolving the ref on its own.
        Assert.Contains(_docs.GetWikiOverview(ProjectName)!.Files, f => f.RelPath == "start.md");
        Assert.NotNull(_docs.GetWikiPulse(ProjectName, _git));
        Assert.Contains(_docs.GetWikiFolder(ProjectName, null, _git)!.Children, c => c.RelPath == "start.md");
        Assert.Contains(
            "First accepted page",
            _docs.GetWikiRevision(ProjectName, DevelopSha(), "start.md", _git)!.Revision.Content);
    }

    /// <summary>
    /// The published revision is a promotion boundary, not a live follow of the
    /// ref. A commit that lands on the accepted branch is invisible until the
    /// next supervised promotion, which is what keeps every surface on one
    /// commit while the branch moves.
    /// </summary>
    [Fact]
    public void PublishedRevision_StaysPinnedWhileTheAcceptedBranchMovesOn()
    {
        var published = _publication.Synchronize(ProjectName).ToSha;

        PublishDoc("second.md", "# Second\n", "docs: second accepted page");
        Git(_server, "fetch", "-q", "origin");

        var tree = _docs.GetWikiTreeResult(ProjectName)!;
        Assert.Equal(published, tree.Tree.Source!.Commit);
        Assert.DoesNotContain(tree.Tree.Root, node => node.RelPath == "second.md");
        Assert.NotEqual(published, DevelopSha());

        Assert.Equal(WikiPublicationStatus.Promoted, _publication.Synchronize(ProjectName).Status);
        Assert.Contains(
            _docs.GetWikiTreeResult(ProjectName)!.Tree.Root,
            node => node.RelPath == "second.md");
    }

    /// <summary>
    /// The hosted read session stays read-only. Publication does not turn the
    /// deployment checkout into a write target.
    /// </summary>
    [Fact]
    public void Synchronize_DoesNotMakeTheHostedWikiWritable()
    {
        _publication.Synchronize(ProjectName);

        var write = _docs.WriteWikiFile(ProjectName, "start.md", "# Edited\n");

        Assert.False(write.Success);
        Assert.False(_docs.GetWikiTreeResult(ProjectName)!.Tree.Source!.Writable);
        Assert.NotNull(_docs.WikiWriteBlockReason(ProjectName));
    }

    // ---- Acceptance 6: no-op sync ----

    [Fact]
    public void Synchronize_WithoutANewRevision_IsANoOpAndKeepsTheSameCommit()
    {
        var first = _publication.Synchronize(ProjectName);
        var fills = _cache.Fills;

        var second = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.NoOp, second.Status);
        Assert.Equal(first.ToSha, second.ToSha);
        Assert.Equal(first.ToSha, second.FromSha);
        Assert.Equal(fills, _cache.Fills);
    }

    // ---- Acceptance 6: new revision + cache refresh ----

    [Fact]
    public void Synchronize_AfterANewAcceptedRevision_PromotesRecordsBothShasAndRefreshesTheCache()
    {
        var first = _publication.Synchronize(ProjectName);
        Assert.Contains(_docs.GetWikiTreeResult(ProjectName)!.Tree.Root, n => n.RelPath == "start.md");

        PublishDoc("second.md", "# Second\n\nA newly accepted page.\n", "docs: second accepted page");
        var second = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Promoted, second.Status);
        Assert.Equal(first.ToSha, second.FromSha);
        Assert.Equal(DevelopSha(), second.ToSha);
        Assert.NotEqual(second.FromSha, second.ToSha);

        // No explicit invalidation: promotion refreshed the wiki cache itself.
        var tree = _docs.GetWikiTreeResult(ProjectName)!;
        Assert.Equal(second.ToSha, tree.Tree.Source!.Commit);
        Assert.Contains(tree.Tree.Root, node => node.RelPath == "second.md");
        Assert.Contains("newly accepted", _docs.ReadWikiFile(ProjectName, "second.md")!.Content);
    }

    /// <summary>
    /// A revision pushed to the accepted branch is picked up by one scheduled
    /// tick, with no file copied into the deployment checkout and no checkout
    /// switch: the working tree stays on its own branch throughout.
    /// </summary>
    [Fact]
    public void ScheduledTick_PublishesANewAcceptedRevisionWithoutTouchingTheCheckout()
    {
        var worker = new WikiPublicationSyncService(
            _publication, NullLogger<WikiPublicationSyncService>.Instance);
        worker.SyncAllProjects("scheduled", CancellationToken.None);

        PublishDoc("release-note.md", "# Release\n\nShipped.\n", "docs: release note");
        worker.SyncAllProjects("scheduled", CancellationToken.None);

        Assert.Equal(DevelopSha(), _publication.GetReport(ProjectName)!.PublishedSha);
        Assert.Contains(
            _docs.GetWikiTreeResult(ProjectName)!.Tree.Root,
            node => node.RelPath == "release-note.md");
        Assert.False(File.Exists(Path.Combine(_server, "docs", "release-note.md")));
        Assert.Equal("main", _git.GetStatusForRepoRoot(_server).Branch);
    }

    // ---- Acceptance 3 + 6: invalid revision and failed fetch ----

    [Fact]
    public void Synchronize_WithAnUnresolvableRevision_FailsTypedAndKeepsThePreviousRevisionOnline()
    {
        var published = _publication.Synchronize(ProjectName).ToSha;
        _registry.SetWikiSourceBranch(_project.Id, "release/never-cut");

        var outcome = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Failed, outcome.Status);
        Assert.Equal(WikiPublicationFailure.RevisionNotFound, outcome.Failure);
        Assert.Equal(published, _publication.GetReport(ProjectName)!.PublishedSha);
    }

    [Fact]
    public void Synchronize_WithATaskBranchRef_RefusesToPublishUnacceptedWork()
    {
        var published = _publication.Synchronize(ProjectName).ToSha;
        Git(_author, "checkout", "-q", "-b", "task/spike");
        WriteDoc(_author, "spike.md", "# Spike\n");
        Git(_author, "add", "-A");
        Git(_author, "commit", "-q", "-m", "docs: spike");
        Git(_author, "push", "-q", "-u", "origin", "task/spike");
        _registry.SetWikiSourceBranch(_project.Id, "origin/task/spike");

        var outcome = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationFailure.UnacceptedRef, outcome.Failure);
        Assert.Equal(published, _publication.GetReport(ProjectName)!.PublishedSha);
    }

    [Fact]
    public void Synchronize_WithAFailedFetch_KeepsThePublishedTreeReadable()
    {
        var published = _publication.Synchronize(ProjectName).ToSha;
        Directory.Delete(_origin, recursive: true);

        var outcome = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Failed, outcome.Status);
        Assert.Equal(WikiPublicationFailure.FetchFailed, outcome.Failure);

        var report = _publication.GetReport(ProjectName)!;
        Assert.Equal(published, report.PublishedSha);
        Assert.Equal(WikiPublicationFailure.FetchFailed, report.LastFailure!.Failure);
        Assert.Contains(
            _docs.GetWikiTreeResult(ProjectName)!.Tree.Root,
            node => node.RelPath == "start.md");
    }

    // ---- Acceptance 6: interrupted promotion ----

    /// <summary>
    /// Simulates a process that died between extracting and publishing a
    /// snapshot: the SHA directory exists but carries no docs tree. The next
    /// attempt must rebuild it completely rather than adopting the fragment.
    /// </summary>
    [Fact]
    public void Synchronize_AfterAnInterruptedPromotion_RebuildsTheCompleteTree()
    {
        PublishDoc("second.md", "# Second\n\nA newly accepted page.\n", "docs: second accepted page");
        var promoted = _publication.Synchronize(ProjectName);
        var snapshotRoot = SnapshotRootFor(promoted.ToSha!);

        Directory.Delete(Path.Combine(snapshotRoot, "docs"), recursive: true);
        File.WriteAllText(Path.Combine(snapshotRoot, "docs.tar"), "half written");

        var recovered = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Promoted, recovered.Status);
        Assert.Equal(promoted.ToSha, recovered.ToSha);
        var tree = _docs.GetWikiTreeResult(ProjectName)!;
        Assert.Contains(tree.Tree.Root, node => node.RelPath == "start.md");
        Assert.Contains(tree.Tree.Root, node => node.RelPath == "second.md");
        Assert.False(File.Exists(Path.Combine(snapshotRoot, "docs.tar")));
    }

    // ---- Acceptance 4 + 6: rollback ----

    [Fact]
    public void Rollback_RestoresThePreviousRevisionAndReportsBothShas()
    {
        var first = _publication.Synchronize(ProjectName);
        PublishDoc("second.md", "# Second\n\nA newly accepted page.\n", "docs: second accepted page");
        var second = _publication.Synchronize(ProjectName);

        var rolledBack = _publication.Rollback(ProjectName);

        Assert.Equal(WikiPublicationStatus.RolledBack, rolledBack.Status);
        Assert.Equal(second.ToSha, rolledBack.FromSha);
        Assert.Equal(first.ToSha, rolledBack.ToSha);

        var tree = _docs.GetWikiTreeResult(ProjectName)!;
        Assert.Equal(first.ToSha, tree.Tree.Source!.Commit);
        Assert.DoesNotContain(tree.Tree.Root, node => node.RelPath == "second.md");
        Assert.Contains(tree.Tree.Root, node => node.RelPath == "start.md");
    }

    /// <summary>
    /// A rollback that the next scheduled tick undoes is not a rollback. The
    /// hold is what makes the recovery hold, and only an operator-forced sync
    /// releases it.
    /// </summary>
    [Fact]
    public void Rollback_HoldsTheProjectUntilAnOperatorForcesASync()
    {
        var first = _publication.Synchronize(ProjectName).ToSha;
        PublishDoc("second.md", "# Second\n", "docs: second accepted page");
        var second = _publication.Synchronize(ProjectName).ToSha;
        _publication.Rollback(ProjectName);

        var scheduled = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Held, scheduled.Status);
        Assert.Equal(first, _publication.GetReport(ProjectName)!.PublishedSha);
        Assert.True(_publication.GetReport(ProjectName)!.Held);
        Assert.DoesNotContain(
            _docs.GetWikiTreeResult(ProjectName)!.Tree.Root,
            node => node.RelPath == "second.md");

        var resumed = _publication.Synchronize(ProjectName, "operator", force: true);

        Assert.Equal(WikiPublicationStatus.Promoted, resumed.Status);
        Assert.Equal(second, resumed.ToSha);
        Assert.False(_publication.GetReport(ProjectName)!.Held);
    }

    [Fact]
    public void Rollback_TwiceDoesNotSilentlyRollForward()
    {
        var first = _publication.Synchronize(ProjectName).ToSha;
        PublishDoc("second.md", "# Second\n", "docs: second accepted page");
        _publication.Synchronize(ProjectName);
        _publication.Rollback(ProjectName);

        var second = _publication.Rollback(ProjectName);

        Assert.Equal(WikiPublicationFailure.RollbackUnavailable, second.Failure);
        Assert.Equal(first, _publication.GetReport(ProjectName)!.PublishedSha);
    }

    [Fact]
    public void Rollback_WithoutAPreviousRevision_FailsTypedAndChangesNothing()
    {
        var published = _publication.Synchronize(ProjectName).ToSha;

        var outcome = _publication.Rollback(ProjectName);

        Assert.Equal(WikiPublicationStatus.Failed, outcome.Status);
        Assert.Equal(WikiPublicationFailure.RollbackUnavailable, outcome.Failure);
        Assert.Equal(published, _publication.GetReport(ProjectName)!.PublishedSha);
        Assert.False(_publication.GetReport(ProjectName)!.RollbackAvailable);
    }

    // ---- Acceptance 5: concurrent readers ----

    /// <summary>
    /// A reader loop runs while three revisions are promoted underneath it.
    /// Every observed tree must be one whole published generation: the page set
    /// is always a complete prefix of the publication history, never a partial
    /// or empty tree.
    /// </summary>
    // MachineBound: a real reader thread races real promotions; the invariant is
    // deterministic but the interleaving depends on host scheduling.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task Promotion_IsAtomicForConcurrentReaders()
    {
        _publication.Synchronize(ProjectName);

        var stop = new CancellationTokenSource();
        var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var tree = _docs.GetWikiTreeResult(ProjectName);
                Assert.NotNull(tree);
                Assert.Contains(tree!.Tree.Root, node => node.RelPath == "start.md");
                observed.Add(tree.Tree.Root.Count);
            }
        });

        for (var generation = 1; generation <= 3; generation++)
        {
            PublishDoc($"page-{generation}.md", $"# Page {generation}\n", $"docs: page {generation}");
            Assert.Equal(WikiPublicationStatus.Promoted, _publication.Synchronize(ProjectName).Status);
        }

        await stop.CancelAsync();
        await reader;

        Assert.NotEmpty(observed);
        Assert.All(observed, count => Assert.InRange(count, 1, 4));
        Assert.Equal(4, _docs.GetWikiTreeResult(ProjectName)!.Tree.Root.Count);
    }

    // ---- Disabled projects ----

    [Fact]
    public void Synchronize_WithoutAConfiguredSourceRef_LeavesTheProjectCheckoutBacked()
    {
        _registry.SetWikiSourceBranch(_project.Id, null);

        var outcome = _publication.Synchronize(ProjectName);

        Assert.Equal(WikiPublicationStatus.Disabled, outcome.Status);
        Assert.Empty(_publication.PublicationProjectNames());
        Assert.Equal("checkout", _docs.GetWikiTreeResult(ProjectName)!.Tree.Source!.Mode);
    }

    // ---- fixture helpers ----

    private string DevelopSha() => _git.GetRefShaFresh(_server, "origin/develop")!;

    private string SnapshotRootFor(string sha) =>
        Path.Combine(GitService.WikiSnapshotBaseDir(_server), sha);

    private void PublishDoc(string relPath, string content, string message)
    {
        WriteDoc(_author, relPath, content);
        Git(_author, "add", "-A");
        Git(_author, "commit", "-q", "-m", message);
        Git(_author, "push", "-q", "origin", "develop");
    }

    private static void WriteDoc(string repoRoot, string relPath, string content)
    {
        var full = Path.Combine(repoRoot, "docs", relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Identify(string repoRoot)
    {
        Git(repoRoot, "config", "user.email", "test@example.com");
        Git(repoRoot, "config", "user.name", "test");
    }

    private static void Git(string cwd, params string[] args)
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
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
