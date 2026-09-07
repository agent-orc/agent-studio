using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the indexed-search contract behind AGT-2723. The old implementation
/// re-read every card's prompt/status and re-spawned `git log` plus
/// `git ls-files` per repository on every keystroke, because the memo key
/// carried the query string. These tests assert the three properties that
/// replaced it: task text is answered from memory, a new query against an
/// unchanged HEAD costs zero git processes, and the stream delivers tasks
/// before repository results and stops when the caller cancels.
/// </summary>
public sealed class GlobalSearchIndexTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _repository;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache _cache;
    private readonly TaskSearchIndex _taskIndex;
    private readonly GitService _git;
    private readonly GlobalSearchService _search;

    public GlobalSearchIndexTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-search-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "project");
        _repository = Path.Combine(_workspace, "repo");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repository);

        InitRepository();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repository,
                ["WatchPaths:0:RepositoryPath"] = _repository,
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(_cache);
        _taskIndex = new TaskSearchIndex(_scanner, _cache, NullLogger<TaskSearchIndex>.Instance);
        _git = new GitService(NullLogger<GitService>.Instance, _scanner, _config);
        _search = new GlobalSearchService(
            _taskIndex, _scanner,
            _git,
            new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<GlobalSearchService>.Instance);
    }

    [Fact]
    public void SecondQueryOnTheSameHead_SpawnsNoGitProcess()
    {
        WriteCard("2-ready", "card-one", "First card", "nothing to see");

        var warm = _search.Search("orangutan", Domains("commits", "files"), 20);
        Assert.Single(warm.Files);

        var spawnsAfterWarmup = Volatile.Read(ref GlobalSearchService.GitProcessSpawns);
        var second = _search.Search("badger", Domains("commits", "files"), 20);

        // Different term, same HEAD: served entirely from the in-memory index.
        Assert.Equal(spawnsAfterWarmup, Volatile.Read(ref GlobalSearchService.GitProcessSpawns));
        Assert.Contains(second.Files, item => item.Path == "docs/badger.md");
        Assert.Contains(second.Commits, item => item.Title.Contains("badger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaskDomain_AnswersFromMemoryWithoutReadingTheCardOnTheRequestPath()
    {
        WriteCard("2-ready", "card-one", "First card", "the marker word is pangolin");

        var warm = _search.Search("pangolin", Domains("tasks"), 20);
        Assert.Single(warm.Tasks);

        // Remove the source document without invalidating the task cache. A
        // request that still finds the text proves the match came from the
        // index, not from disk.
        File.Delete(Path.Combine(_watchPath, "2-ready", "card-one", "prompt.md"));

        var afterDelete = _search.Search("pangolin", Domains("tasks"), 20);
        Assert.Equal("First card", Assert.Single(afterDelete.Tasks).Title);
    }

    [Fact]
    public void TaskIndex_RereadsACardOnlyWhenItsDocumentsChange()
    {
        WriteCard("2-ready", "card-one", "First card", "first body");
        Assert.Single(_search.Search("first body", Domains("tasks"), 20).Tasks);

        File.WriteAllText(Path.Combine(_watchPath, "2-ready", "card-one", "prompt.md"), "second body");
        _cache.Invalidate();
        // Force the rebuild the request path would otherwise do in background.
        _ = _scanner.ScanAllJobsWithArchive();
        WaitUntil(() => _search.Search("second body", Domains("tasks"), 20).Tasks.Count == 1);

        Assert.Empty(_search.Search("first body", Domains("tasks"), 20).Tasks);
    }

    [Fact]
    public async Task StreamAsync_DeliversTasksBeforeRepositoryResultsAndSummarisesLast()
    {
        WriteCard("2-ready", "card-one", "Badger card", "badger in the prompt");

        var frames = new List<(string Name, object Payload)>();
        await _search.StreamAsync("badger", Domains("tasks", "commits", "files"), 20,
            (name, payload, _) => { frames.Add((name, payload)); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("start", frames[0].Name);
        Assert.Equal(1, Assert.IsType<GlobalSearchStart>(frames[0].Payload).Repositories);

        var chunks = frames.Where(f => f.Name == "chunk").Select(f => (GlobalSearchChunk)f.Payload).ToList();
        Assert.Equal("tasks", chunks[0].Domain);
        Assert.Contains(chunks, c => c.Domain == "files");
        Assert.Contains(chunks, c => c.Domain == "commits");
        // Every repository domain reports progress so the palette can render "i of n".
        Assert.All(frames.Where(f => f.Name == "progress").Select(f => (GlobalSearchProgress)f.Payload),
            progress => Assert.Equal(1, progress.Total));

        Assert.Equal("done", frames[^1].Name);
        Assert.True(Assert.IsType<GlobalSearchSummary>(frames[^1].Payload).DomainDurationMs.ContainsKey("tasks"));
    }

    [Fact]
    public async Task StreamAsync_StopsWorkWhenTheCallerCancels()
    {
        WriteCard("2-ready", "card-one", "Badger card", "badger in the prompt");
        using var cancellation = new CancellationTokenSource();

        var emitted = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _search.StreamAsync("badger", Domains("tasks", "commits", "files"), 20,
                (name, _, _) =>
                {
                    emitted.Add(name);
                    // Abort the way the palette does: on the first frame the
                    // operator's next keystroke invalidates the whole search.
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(["start"], emitted);
    }

    // ------------------------------------------------------------------ fixtures

    private static HashSet<string> Domains(params string[] domains) =>
        new(domains, StringComparer.OrdinalIgnoreCase);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
        Assert.Fail("Condition was not met within 5 seconds.");
    }

    private void InitRepository()
    {
        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.email", "fixture@example.com");
        RunGit("config", "user.name", "Fixture");
        Directory.CreateDirectory(Path.Combine(_repository, "docs"));
        File.WriteAllText(Path.Combine(_repository, "docs", "badger.md"), "badger");
        File.WriteAllText(Path.Combine(_repository, "docs", "orangutan.md"), "orangutan");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed badger and orangutan fixtures");
    }

    private void WriteCard(string state, string slug, string title, string prompt)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = slug,
                key = slug.ToUpperInvariant(),
                title,
                state,
                order = 1,
                agent = "claude",
                ownerClientId = DefaultClientIdentity.Id,
            }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), prompt);
    }

    private void RunGit(params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = _repository, UseShellExecute = false, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
