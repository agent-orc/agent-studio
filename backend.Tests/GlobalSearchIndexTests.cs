using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Search;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the indexed contract behind the global palette (AGT-2723): a query
/// pays for the revision, never for the keystroke.
///
/// <list type="bullet">
/// <item>Git domains are cached per repository HEAD, so a second query with a
/// different term spawns no <c>git ls-files</c> or <c>git log</c>.</item>
/// <item>The task blob is cached per snapshot generation, so a second query
/// re-reads no card.</item>
/// <item>The stream delivers tasks before any repository frame and closes with
/// <c>done</c>.</item>
/// <item>A cancelled stream stops the search instead of finishing it.</item>
/// </list>
/// </summary>
public sealed class GlobalSearchIndexTests : IDisposable
{
    private const string Project = "demo";

    private readonly ITestOutputHelper _output;
    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _repository;

    public GlobalSearchIndexTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Path.Combine(Path.GetTempPath(), "atp-search-index-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        _repository = Path.Combine(_workspace, "repo");
        Directory.CreateDirectory(_watchPath);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        InitRepository();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public void SecondQueryOnTheSameHeadSpawnsNoGitProcess()
    {
        var (_, index, _) = Build();

        var first = index.GetFiles(_repository, CancellationToken.None);
        var commitsFirst = index.GetCommits(_repository, CancellationToken.None);
        var spawnsAfterFirstQuery = index.GitSpawns;

        // A different query string must not move the cache key.
        var second = index.GetFiles(_repository, CancellationToken.None);
        var commitsSecond = index.GetCommits(_repository, CancellationToken.None);

        Assert.False(first.Stats.CacheHit);
        Assert.False(commitsFirst.Stats.CacheHit);
        Assert.Equal(2, spawnsAfterFirstQuery);
        Assert.True(second.Stats.CacheHit);
        Assert.True(commitsSecond.Stats.CacheHit);
        Assert.Equal(spawnsAfterFirstQuery, index.GitSpawns);
        Assert.Contains("README-search-proof.md", second.Paths);
        Assert.Contains(commitsSecond.Commits, commit => commit.Subject.Contains("indexed subject"));
    }

    [Fact]
    public void SearchRunsTwoDifferentQueriesWithOneGitSpawnPerDomain()
    {
        var (service, index, _) = Build();
        var domains = Domains("commits", "files");

        service.Search("search-proof", domains, 20);
        var spawnsAfterFirstQuery = index.GitSpawns;
        service.Search("indexed", domains, 20);

        Assert.Equal(2, spawnsAfterFirstQuery);
        Assert.Equal(spawnsAfterFirstQuery, index.GitSpawns);
    }

    [Fact]
    public void TaskBlobIsCachedPerSnapshotGenerationAndMatchesPromptText()
    {
        WriteJob("2-ready", "card-one", "First card", "Contains the phrase peculiar-token in the prompt.");
        var (service, index, _) = Build();

        var first = service.Search("peculiar-token", Domains("tasks"), 20);
        var readsAfterFirstQuery = index.TaskReads;
        var second = service.Search("peculiar", Domains("tasks"), 20);

        Assert.Single(first.Tasks);
        Assert.Equal("First card", first.Tasks[0].Title);
        Assert.Single(second.Tasks);
        Assert.True(readsAfterFirstQuery > 0, "the first query must build the blob");
        Assert.Equal(readsAfterFirstQuery, index.TaskReads);
    }

    [Fact]
    public void TaskBlobRebuildRereadsOnlyTheChangedCard()
    {
        WriteJob("2-ready", "card-one", "First card", "alpha body");
        WriteJob("2-ready", "card-two", "Second card", "beta body");
        var (service, index, scanner) = Build();
        service.Search("body", Domains("tasks"), 20);
        var readsAfterWarm = index.TaskReads;

        File.WriteAllText(
            Path.Combine(_watchPath, "2-ready", "card-two", "prompt.md"),
            "beta body rewritten with gamma-token");
        scanner.InvalidateCache();

        var rebuilt = service.Search("gamma-token", Domains("tasks"), 20);

        Assert.Equal(4, readsAfterWarm);
        Assert.Single(rebuilt.Tasks);
        Assert.Equal("Second card", rebuilt.Tasks[0].Title);
        // The blob is cached per card, so only card-two's two files are re-read;
        // card-one's stamp is unchanged and costs two stat calls, no read.
        Assert.Equal(readsAfterWarm + 2, index.TaskReads);
    }

    /// <summary>
    /// The index is stamped with the published snapshot generation, and that
    /// stamp only advances when something takes a snapshot. Search must be the
    /// thing that takes it, or a card mutated while nobody else was reading the
    /// board would stay invisible until the safety TTL expired.
    /// </summary>
    [Fact]
    public void ACardMutatedAfterTheIndexWarmedIsFoundOnTheNextQuery()
    {
        WriteJob("2-ready", "card-one", "First card", "alpha body");
        var (service, _, scanner) = Build();
        service.Search("alpha", Domains("tasks"), 20);
        service.Search("alpha", Domains("tasks"), 20);

        WriteJob("2-ready", "card-two", "Second card", "delta-token body");
        scanner.InvalidateCache();

        var found = service.Search("delta-token", Domains("tasks"), 20);

        Assert.Single(found.Tasks);
        Assert.Equal("Second card", found.Tasks[0].Title);
    }

    [Fact]
    public async Task StreamEmitsTasksBeforeAnyRepositoryFrameAndClosesWithDone()
    {
        WriteJob("2-ready", "card-one", "Streaming card", "mentions search-proof");
        var (service, _, _) = Build();

        var frames = new List<GlobalSearchStreamEvent>();
        await foreach (var frame in service.StreamAsync("search-proof", Domains("tasks", "commits", "files"), 20))
            frames.Add(frame);

        var names = frames.Select(frame => frame.Name).ToList();
        Assert.Equal("meta", names[0]);
        Assert.Equal("tasks", names[1]);
        Assert.Equal("done", names[^1]);
        Assert.True(
            names.IndexOf("tasks") < names.IndexOf("repository"),
            "task results must not wait for any repository");

        var meta = Assert.IsType<GlobalSearchMetaFrame>(frames[0].Payload);
        Assert.Equal(1, meta.Repositories);
        var tasks = Assert.IsType<GlobalSearchTasksFrame>(frames[1].Payload);
        Assert.Single(tasks.Items);

        var repository = Assert.IsType<GlobalSearchRepositoryFrame>(
            frames.First(frame => frame.Name == "repository").Payload);
        Assert.Equal(1, repository.Index);
        Assert.Equal(1, repository.Total);
        Assert.Contains(repository.Files, item => item.Path == "README-search-proof.md");
        Assert.Null(repository.CommitsError);
        Assert.Null(repository.FilesError);
    }

    [Fact]
    public async Task CancellingTheStreamStopsBeforeTheRepositoryFrames()
    {
        var (service, _, _) = Build();
        using var cancellation = new CancellationTokenSource();

        var frames = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var frame in service.StreamAsync(
                "search-proof", Domains("tasks", "commits", "files"), 20, cancellation.Token))
            {
                frames.Add(frame.Name);
                if (frame.Name == "meta") await cancellation.CancelAsync();
            }
        });

        Assert.Equal(["meta"], frames);
    }

    /// <summary>
    /// The acceptance measurement, taken against this repository (a real
    /// checkout with thousands of paths and a deep history) rather than the
    /// two-file fixture: once the index is warm, a query with a new term is an
    /// in-memory scan. The bound is deliberately loose so this is a regression
    /// guard for "we went back to spawning git per keystroke", not a
    /// machine-speed assertion.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public void WarmIndexAnswersANewTermWithoutSpawningGit()
    {
        var repository = LocateThisRepository();
        Skip.If(repository is null, "Not running from a git checkout.");
        var (service, index, _) = Build(searchRepository: repository);
        var domains = Domains("commits", "files");

        var cold = System.Diagnostics.Stopwatch.StartNew();
        var first = service.Search("readme", domains, 20);
        cold.Stop();
        var spawnsAfterFirstQuery = index.GitSpawns;

        var warm = System.Diagnostics.Stopwatch.StartNew();
        var second = service.Search("angular", domains, 20);
        warm.Stop();

        _output.WriteLine($"cold={cold.ElapsedMilliseconds}ms warm={warm.ElapsedMilliseconds}ms spawns={index.GitSpawns}");
        Assert.Equal(2, spawnsAfterFirstQuery);
        Assert.Equal(spawnsAfterFirstQuery, index.GitSpawns);
        Assert.NotEmpty(first.Files);
        Assert.NotEmpty(second.Files);
        Assert.True(warm.ElapsedMilliseconds < 1_000, $"warm query took {warm.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void RepositoryFailureDegradesThatDomainWithoutHidingTheOthers()
    {
        WriteJob("2-ready", "card-one", "Survivor card", "mentions search-proof");
        // A directory that is not a git repository: git exits non-zero.
        var broken = Path.Combine(_workspace, "not-a-repo");
        Directory.CreateDirectory(broken);
        var (service, _, _) = Build(extraWatchPath: broken);

        var response = service.Search("search-proof", Domains("tasks", "commits", "files"), 20);

        Assert.Single(response.Tasks);
        Assert.Contains(response.Files, item => item.Path == "README-search-proof.md");
        Assert.True(response.Errors.ContainsKey("files") || response.Errors.ContainsKey("commits"));
    }

    private static HashSet<string> Domains(params string[] domains) =>
        new(domains, StringComparer.OrdinalIgnoreCase);

    /// <summary>Walks up from the test binary to the enclosing checkout, or null
    /// when the tests do not run from one.</summary>
    private static string? LocateThisRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private (GlobalSearchService Service, GlobalSearchIndex Index, TaskScannerService Scanner) Build(
        string? extraWatchPath = null, string? searchRepository = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = searchRepository ?? _repository,
            ["WatchPaths:0:RepositoryPath"] = searchRepository ?? _repository,
        };
        if (extraWatchPath != null)
        {
            // Its own (card-free) task path, so the broken repository adds a
            // failing git domain without duplicating the demo project's cards.
            var brokenTasks = Path.Combine(_workspace, "projects", "broken");
            Directory.CreateDirectory(brokenTasks);
            settings["WatchPaths:1:Name"] = "broken";
            settings["WatchPaths:1:Path"] = brokenTasks;
            settings["WatchPaths:1:RootPath"] = extraWatchPath;
            settings["WatchPaths:1:RepositoryPath"] = extraWatchPath;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        scanner.SetIndexCache(new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var index = new GlobalSearchIndex(scanner, git, NullLogger<GlobalSearchIndex>.Instance);
        var service = new GlobalSearchService(
            scanner, index,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<GlobalSearchService>.Instance);
        return (service, index, scanner);
    }

    private void WriteJob(string state, string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), "status");
    }

    private void InitRepository()
    {
        Directory.CreateDirectory(_repository);
        RunGit("init");
        RunGit("config", "user.email", "search@example.test");
        RunGit("config", "user.name", "Search Fixture");
        File.WriteAllText(Path.Combine(_repository, "README-search-proof.md"), "proof");
        RunGit("add", "README-search-proof.md");
        RunGit("commit", "-m", "add an indexed subject line");
    }

    private void RunGit(params string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new("git")
        {
            WorkingDirectory = _repository, UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }
}
