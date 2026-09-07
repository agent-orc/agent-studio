using AgentStudio.Search;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The regression these cover: the palette used to key its git memo by
/// <c>(repository, query)</c>, so every new search term re-spawned
/// <c>git log</c> and <c>git ls-files</c> in every checkout - about 15 of each
/// per keystroke-settled query. The corpus is now keyed by HEAD alone.
/// </summary>
public sealed class GlobalSearchIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"global-search-index-{Guid.NewGuid():N}");

    public GlobalSearchIndexTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "README-search-proof.md"), "proof");
        RunGit("add", "README-search-proof.md");
        RunGit("commit", "-m", "feat: streamed global search");
    }

    [Fact]
    public void FileIndex_SecondQueryOnTheSameHeadSpawnsNoGit()
    {
        var indexes = Build();

        var (first, firstFromCache) = indexes.FileIndex(_root);
        var (second, secondFromCache) = indexes.FileIndex(_root);

        Assert.Contains("README-search-proof.md", first);
        Assert.Equal(first, second);
        Assert.False(firstFromCache);
        Assert.True(secondFromCache);
        // One build total: the cache key carries HEAD, not the query, so any
        // number of different search terms reuse this corpus.
        Assert.Equal(1, indexes.CorpusBuilds);
    }

    [Fact]
    public void CommitIndex_SecondQueryOnTheSameHeadSpawnsNoGit()
    {
        var indexes = Build();

        var (first, firstFromCache) = indexes.CommitIndex(_root);
        var (_, secondFromCache) = indexes.CommitIndex(_root);

        Assert.Contains(first, commit => commit.Subject == "feat: streamed global search");
        Assert.False(firstFromCache);
        Assert.True(secondFromCache);
        Assert.Equal(1, indexes.CorpusBuilds);
    }

    [Fact]
    public void CommitIndex_RebuildsWhenHeadMoves()
    {
        var git = BuildGit();
        var indexes = new GlobalSearchIndexes(Configuration(), git, NullLogger<GlobalSearchIndexes>.Instance);
        indexes.CommitIndex(_root);

        File.WriteAllText(Path.Combine(_root, "second.md"), "second");
        RunGit("add", "second.md");
        RunGit("commit", "-m", "chore: second commit");
        // Production lets the 2s HEAD TTL roll over; a test that commits and
        // re-queries immediately has to drop it explicitly.
        git.InvalidateHeadKeyedCaches();

        var (commits, fromCache) = indexes.CommitIndex(_root);

        Assert.False(fromCache);
        Assert.Equal(2, indexes.CorpusBuilds);
        Assert.Contains(commits, commit => commit.Subject == "chore: second commit");
    }

    [Fact]
    public void CommitWindow_IsBoundedAndConfigurable()
    {
        Assert.Equal(2_000, Build().CommitWindow);
        Assert.Equal(500, Build(new Dictionary<string, string?> { ["Search:CommitWindow"] = "500" }).CommitWindow);
        // Out-of-range values clamp rather than disabling the bound.
        Assert.Equal(100, Build(new Dictionary<string, string?> { ["Search:CommitWindow"] = "1" }).CommitWindow);
    }

    [Fact]
    public void TaskText_ReadsDiskOnlyWhenTheCardChanged()
    {
        var folder = Path.Combine(_root, "job");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Indexed prompt body");
        var indexes = Build();
        var task = new TaskInfo { TaskKey = "demo/job", FolderPath = folder, LastActivity = new DateTime(2026, 9, 1) };

        Assert.Contains("Indexed prompt body", indexes.TaskText(task));
        Assert.Contains("Indexed prompt body", indexes.TaskText(task));
        Assert.Equal(1, indexes.TaskTextReads);

        File.WriteAllText(Path.Combine(folder, "status.md"), "Status body");
        var moved = task with { LastActivity = new DateTime(2026, 9, 2) };

        Assert.Contains("Status body", indexes.TaskText(moved));
        Assert.Equal(2, indexes.TaskTextReads);
    }

    [Fact]
    public void PruneTaskText_DropsBlobsOfCardsThatNoLongerExist()
    {
        var folder = Path.Combine(_root, "job");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Indexed prompt body");
        var indexes = Build();
        var task = new TaskInfo { TaskKey = "demo/job", FolderPath = folder, LastActivity = new DateTime(2026, 9, 1) };
        indexes.TaskText(task);

        indexes.PruneTaskText([]);
        indexes.TaskText(task);

        Assert.Equal(2, indexes.TaskTextReads);
    }

    private GlobalSearchIndexes Build(Dictionary<string, string?>? settings = null) =>
        new(Configuration(settings), BuildGit(), NullLogger<GlobalSearchIndexes>.Instance);

    private GitService BuildGit()
    {
        var config = Configuration();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private IConfiguration Configuration(Dictionary<string, string?>? settings = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = _root,
            ["WatchPaths:0:RootPath"] = _root,
        }).Build();

    private void RunGit(params string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new("git") {
            WorkingDirectory = _root, UseShellExecute = false, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
