using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3753 - the card detail served an integration tip a day old because no
/// index run published: writes of other task worktrees superseded every run in
/// flight, and a superseded result was always discarded.
/// </summary>
public sealed class GitStateIndexSupersededRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "git-index-superseded-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("worktrees/AGT-1/index", false)]
    [InlineData("worktrees/AGT-1/HEAD", false)]
    [InlineData("worktrees/AGT-1/logs/HEAD", false)]
    [InlineData("worktrees/AGT-1/ORIG_HEAD", false)]
    [InlineData("logs/refs/remotes/origin/develop", false)]
    [InlineData("refs/remotes/origin/develop.lock", false)]
    [InlineData("index", false)]
    [InlineData("refs/remotes/origin/develop", true)]
    [InlineData("packed-refs", true)]
    [InlineData("HEAD", true)]
    [InlineData("config", true)]
    public void Only_writes_that_can_move_an_index_input_trigger_a_run(string relative, bool relevant)
    {
        var common = Path.Combine(_root, ".git");
        var path = Path.Combine(common, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(relevant, GitStateIndexService.IsIndexRelevantGitPath(path, common, common));
    }

    [Fact]
    public void A_linked_checkout_still_watches_its_own_worktree_head()
    {
        var common = Path.Combine(_root, ".git");
        var own = Path.Combine(common, "worktrees", "studio");

        Assert.True(GitStateIndexService.IsIndexRelevantGitPath(Path.Combine(own, "HEAD"), own, common));
        Assert.False(GitStateIndexService.IsIndexRelevantGitPath(Path.Combine(own, "index"), own, common));
        Assert.False(GitStateIndexService.IsIndexRelevantGitPath(
            Path.Combine(common, "worktrees", "AGT-2", "HEAD"), own, common));
    }

    [Fact]
    public async Task A_run_superseded_again_and_again_publishes_after_the_bound()
    {
        var jobs = Path.Combine(_root, "jobs");
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var mainRef = Path.Combine(repo, ".git", "refs", "heads", "main");
        File.WriteAllText(mainRef, new string('0', 40) + "\n");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "proj",
            ["WatchPaths:0:Path"] = jobs,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var watcher = new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance,
            new ConfigurationBuilder().Build());
        var cache = new TaskListGitProjectionCache();
        var calls = 0;
        var outcomes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // Every run moves the branch while it computes, as a busy batch gate does.
        Task<TaskListGitProjection> Build(IReadOnlyCollection<TaskInfo> _)
        {
            var call = Interlocked.Increment(ref calls);
            if (call <= 10)
                File.WriteAllText(mainRef, call.ToString("x").PadLeft(40, '0') + "\n");
            return Task.FromResult(TaskListGitProjection.Empty);
        }
        using var service = new GitStateIndexService(scanner, watcher, cache, Build, _ => { },
            NullLogger.Instance, new GitStateIndexOptions(MaxConcurrentRepos: 1,
                Debounce: TimeSpan.FromMilliseconds(20), SweepInterval: TimeSpan.FromMinutes(5),
                SlowRunWarnMs: TimeSpan.FromSeconds(5)),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 13, 0, 0, TimeSpan.Zero)));
        service.RunCompleted += report => outcomes.Enqueue(report.Outcome);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!outcomes.Contains("published"))
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), $"No run published: {string.Join(",", outcomes)}");
                await Task.Delay(10);
            }
            var untilPublished = outcomes.TakeWhile(outcome => outcome != "published").ToArray();
            Assert.Equal(Enumerable.Repeat("superseded", GitStateIndexService.MaxConsecutiveSupersededRuns - 1),
                untilPublished);
            Assert.NotNull(cache.ReadFreshness([new TaskInfo { Id = "X", WatchPath = jobs }]).GitStateAt);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
