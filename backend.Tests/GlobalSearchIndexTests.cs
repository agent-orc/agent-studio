using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Search;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the indexes that replaced per-query git spawns and per-query card file
/// reads: a second query on an unchanged checkout must be a pure memory scan,
/// and the streamed delivery must put the warm task domain in front of the git
/// domains.
/// </summary>
public sealed class GlobalSearchIndexTests : IDisposable
{
    private const string ProjectName = "Fixture";

    private readonly string _root;

    public GlobalSearchIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"global-search-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Git("init");
        Git("config", "user.email", "fixture@example.com");
        Git("config", "user.name", "Fixture");
        Write("README-search-proof.md", "proof");
        Write("docs/guide.md", "guide");
        Git("add", ".");
        Git("commit", "-m", "feat: seed the search fixture");
    }

    [Fact]
    public void PathIndex_IsKeyedByHeadOnlySoANewQueryStillHits()
    {
        var index = BuildIndex(out _);

        var first = index.Paths(_root);
        var second = index.Paths(_root);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        // Both queries below are answered from the one index the miss produced.
        Assert.Single(GlobalSearchService.MatchFiles(second.Value, Repository(), "search-proof"));
        Assert.Single(GlobalSearchService.MatchFiles(second.Value, Repository(), "guide"));
    }

    [Fact]
    public void PathIndex_PicksUpANewFileAfterTheNextCommit()
    {
        var index = BuildIndex(out var git);
        Assert.Empty(GlobalSearchService.MatchFiles(index.Paths(_root).Value, Repository(), "late-arrival"));

        Write("late-arrival.md", "added later");
        Git("add", ".");
        Git("commit", "-m", "chore: add a file");
        git.InvalidateHeadKeyedCaches();

        var refreshed = index.Paths(_root);
        Assert.False(refreshed.CacheHit);
        Assert.Single(GlobalSearchService.MatchFiles(refreshed.Value, Repository(), "late-arrival"));
    }

    [Fact]
    public void CommitIndex_IsKeyedByHeadOnlyAndCarriesTheParsedFields()
    {
        var index = BuildIndex(out _);

        var first = index.Commits(_root);
        var second = index.Commits(_root);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        var seeded = Assert.Single(second.Value);
        Assert.Equal("feat: seed the search fixture", seeded.Subject);
        Assert.Equal(40, seeded.Sha.Length);
        Assert.StartsWith(seeded.ShortSha, seeded.Sha, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskIndex_ReusesCardTextUntilTheCardItselfChanges()
    {
        var folder = Path.Combine(_root, "cards", "AGT-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Indexed prompt about streaming");
        var card = new TaskInfo
        {
            Id = "agt-1", TaskKey = "agt-1", Key = "AGT-1", Title = "Card",
            State = "3-progress", ProjectName = ProjectName, FolderPath = folder,
        };

        var generation = 0L;
        var index = new TaskSearchIndex(
            () => [card], () => generation, NullLogger<TaskSearchIndex>.Instance);

        Assert.Contains("streaming", index.Entries()[0].Blob, StringComparison.Ordinal);
        Assert.Equal(1, index.CardReads);

        // A generation bump with an untouched card re-stats but does not re-read.
        generation = 1;
        Assert.Single(index.Entries());
        Assert.Equal(1, index.CardReads);

        // A rewritten card is reindexed on the next generation.
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Rewritten prompt about cancellation");
        generation = 2;
        Assert.Contains("cancellation", index.Entries()[0].Blob, StringComparison.Ordinal);
        Assert.Equal(2, index.CardReads);
    }

    [Fact]
    public void TaskIndex_BlobIsLowercaseAndCoversKeyTitleStateAndCardText()
    {
        var folder = Path.Combine(_root, "cards", "AGT-2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "status.md"), "Waiting On Review");
        var card = new TaskInfo
        {
            Id = "agt-2", TaskKey = "agt-2", Key = "AGT-2", Title = "Streamed Palette",
            State = "5-human-review", ProjectName = ProjectName, FolderPath = folder,
        };

        var blob = new TaskSearchIndex(() => [card], () => 0, NullLogger<TaskSearchIndex>.Instance)
            .Entries()[0].Blob;

        Assert.Equal(blob.ToLowerInvariant(), blob);
        Assert.Contains("agt-2", blob, StringComparison.Ordinal);
        Assert.Contains("streamed palette", blob, StringComparison.Ordinal);
        Assert.Contains("5-human-review", blob, StringComparison.Ordinal);
        Assert.Contains("waiting on review", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_DeliversTasksBeforeAnyRepositoryChunk()
    {
        var service = BuildService();

        var chunks = new List<GlobalSearchChunk>();
        await foreach (var chunk in service.StreamAsync("search-proof", Domains("tasks", "commits", "files"), 20))
            chunks.Add(chunk);

        Assert.Equal("tasks", chunks[0].Domain);
        Assert.All(chunks.Skip(1), chunk => Assert.NotEqual("tasks", chunk.Domain));
        // Each git chunk names its checkout and how far the sweep has got.
        Assert.All(chunks.Skip(1), chunk =>
        {
            Assert.Equal(ProjectName, chunk.Repository);
            Assert.Equal(chunk.Total, chunk.Completed);
        });
        var files = Assert.Single(chunks.Where(chunk => chunk.Domain == "files")).Items;
        Assert.Contains(files, item => item.Path == "README-search-proof.md");
    }

    [Fact]
    public async Task StreamAsync_SkipsDomainsTheCallerDidNotAskFor()
    {
        var service = BuildService();

        var domains = new List<string>();
        await foreach (var chunk in service.StreamAsync("search-proof", Domains("files"), 20))
            domains.Add(chunk.Domain);

        Assert.Equal(["files"], domains);
    }

    [Fact]
    public async Task StreamAsync_CancelledBeforeItStartsDoesNoWork()
    {
        var service = BuildService();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.StreamAsync("search-proof", Domains("tasks", "files"), 20, cancelled.Token))
            {
            }
        });
    }

    [Fact]
    public async Task StreamAsync_AbandonedAfterTheTaskChunkStopsTheSweep()
    {
        var service = BuildService();

        // Breaking out of the loop disposes the iterator, which is what an
        // aborted palette request does to an in-flight sweep.
        await foreach (var chunk in service.StreamAsync("search-proof", Domains("tasks", "commits", "files"), 20))
        {
            Assert.Equal("tasks", chunk.Domain);
            break;
        }
    }

    [Fact]
    public async Task SearchAsync_KeepsTheSingleResponseContract()
    {
        var service = BuildService();

        var response = await service.SearchAsync("search-proof", Domains("tasks", "commits", "files"), 20);

        Assert.Equal("search-proof", response.Query);
        Assert.Empty(response.Errors);
        Assert.Contains(response.Files, item => item.Path == "README-search-proof.md");
    }

    private static HashSet<string> Domains(params string[] domains) =>
        new(domains, StringComparer.OrdinalIgnoreCase);

    private static SearchRepository Repository() => new(ProjectName, "/tmp/fixture", "#fff");

    private RepositorySearchIndex BuildIndex(out GitService git)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new RepositorySearchIndex(git);
    }

    private GlobalSearchService BuildService()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new GlobalSearchService(
            new TaskSearchIndex(() => [], () => 0, NullLogger<TaskSearchIndex>.Instance),
            new RepositorySearchIndex(git),
            scanner,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<GlobalSearchService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        })
        .Build();

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void Git(params string[] args)
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
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "GlobalSearchIndexTests: fixture cleanup");
        }
    }
}
