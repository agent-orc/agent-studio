using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3322 (Git facts S2) budget: on a 3,000-card board, an unchanged-input
/// index run has p95 at most 2 s and starts 0 Git processes, with complete
/// per-card results; a restart restores and confirms the stored generation
/// with 0 Git processes. The fixture has 2,050 archived and 320 review cards,
/// escalated cards, duplicate ids, packed refs and a linked worktree.
/// Elapsed time is measured by the product's run stopwatch, so the test is
/// MachineBound; the reference host is recorded in the card result.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class GitFactsUnchangedInputBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int Cards = 3000;
    private const int MeasuredRuns = 30;
    private static readonly TimeSpan P95Budget = TimeSpan.FromSeconds(2);
    private readonly string _root = AgentStudio.TestSupport.TestTempRoot.Create("git-facts-benchmark");
    private readonly FakeTimeProvider _clock = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task UnchangedInputIndexRun_On3000Cards_HasP95UnderTwoSecondsAndZeroSpawns()
    {
        var (jobs, repo, distinct) = BuildFixture();
        var store = Path.Combine(_root, "state", GitFactStoreSchema.FileName);

        var coldScan = Stopwatch.StartNew();
        var fixtureCards = NewScanner(jobs, repo).ScanAllJobsRaw().Count;
        output.WriteLine($"cold scanner pass over {fixtureCards} cards: {coldScan.ElapsedMilliseconds} ms");

        var first = await StartAsync(jobs, repo, store);
        try
        {
            var published = await first.WaitAsync(report => report.Outcome == "published");
            output.WriteLine($"first build: {published.ElapsedMs} ms, spawns {published.Spawns}");
            var tasks = first.Scanner.ScanAllJobsRaw();
            var selected = TaskProjectionCards.Select(tasks);
            Assert.Equal(distinct, selected.Length);
            Assert.All(selected, task => Assert.Equal("ready", first.Cache.ReadTask(task).State));

            var durations = new List<long>();
            for (var i = 0; i < MeasuredRuns + 3; i++)
            {
                var mark = first.Reports.Count;
                first.Service.RequestRefresh("bench", "task-event");
                var run = await first.WaitAsync(report => report.Trigger == "task-event", mark);
                Assert.Equal("unchanged", run.Outcome);
                Assert.Equal(0, run.Spawns);
                if (i >= 3) durations.Add(run.ElapsedMs); // three warm-up runs
            }
            durations.Sort();
            var p50 = durations[durations.Count / 2];
            var p95 = durations[(int)Math.Ceiling(durations.Count * 0.95) - 1];
            output.WriteLine($"unchanged-input runs={durations.Count} p50={p50} ms p95={p95} ms max={durations[^1]} ms "
                + $"cards={tasks.Count} distinct={distinct} host={Environment.MachineName} cpus={Environment.ProcessorCount} "
                + $"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            Assert.True(p95 <= P95Budget.TotalMilliseconds, $"unchanged-input p95 {p95} ms exceeds {P95Budget.TotalMilliseconds} ms");
        }
        finally { await first.StopAsync(); }

        var restarted = await StartAsync(jobs, repo, store);
        try
        {
            var confirmed = await restarted.WaitAsync(report => report.Outcome == "confirmed");
            output.WriteLine($"restart confirm: {confirmed.ElapsedMs} ms, spawns {confirmed.Spawns}");
            Assert.Equal(0, confirmed.Spawns);
            Assert.Equal(0, restarted.Builds);
            Assert.All(TaskProjectionCards.Select(restarted.Scanner.ScanAllJobsRaw()),
                task => Assert.Equal("ready", restarted.Cache.ReadTask(task).State));
        }
        finally { await restarted.StopAsync(); }
    }

    private (string Jobs, string Repo, int Distinct) BuildFixture()
    {
        var repo = Path.Combine(_root, "repo");
        var jobs = Path.Combine(_root, "jobs");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-q", "-b", "develop");
        RunGit(repo, "-c", "user.name=Bench", "-c", "user.email=bench@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "base");
        for (var i = 0; i < 200; i++) RunGit(repo, "branch", $"task/card-{i:D4}");
        RunGit(repo, "pack-refs", "--all");
        RunGit(repo, "branch", "task/loose-after-pack");
        RunGit(repo, "worktree", "add", "-q", "--detach", Path.Combine(_root, "worktree"));

        var duplicates = 0;
        for (var i = 0; i < Cards; i++)
        {
            var state = i < 2050 ? TaskStates.Archive
                : i < 2270 ? TaskStates.HumanReview
                : i < 2370 ? TaskStates.AutoReview
                : i < 2400 ? TaskStates.Escalated
                : i < 2700 ? TaskStates.Completed
                : TaskStates.Progress;
            // Ten live cards reuse an archived id, as the 11 October incident did.
            var id = i >= 2990 ? $"card-{i - 2990:D4}" : $"card-{i:D4}";
            if (i >= 2990) duplicates++;
            var folder = Path.Combine(jobs, state, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "task.json"),
                $"{{\"id\":\"{id}\",\"state\":\"{state}\",\"title\":\"Card {i}\",\"integrationBranch\":\"develop\"}}");
        }
        return (jobs, repo, Cards - duplicates);
    }

    private static TaskScannerService NewScanner(string jobs, string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "bench",
            ["WatchPaths:0:Path"] = jobs,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
    }

    private async Task<Harness> StartAsync(string jobs, string repo, string storePath)
    {
        var scanner = NewScanner(jobs, repo);
        var cache = new TaskListGitProjectionCache();
        var harness = new Harness(scanner, cache);
        Task<TaskListGitProjection> Build(IReadOnlyCollection<TaskInfo> tasks)
        {
            Interlocked.Increment(ref harness.BuildCount);
            return Task.FromResult(TaskListGitProjection.Empty with
            {
                Merge = tasks.ToDictionary(task => task.TaskKey,
                    task => new TaskMergeSignal { Branch = "task/" + task.Id, IntegrationBranch = "develop" },
                    StringComparer.Ordinal),
                Signatures = tasks.ToDictionary(task => task.TaskKey, TaskGitSignature.For, StringComparer.Ordinal),
                SubjectVersions = tasks.ToDictionary(task => task.TaskKey,
                    task => cache.SubjectVersion(task.FolderPath), StringComparer.Ordinal),
            });
        }
        harness.Service = new GitStateIndexService(scanner,
            new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, new ConfigurationBuilder().Build()),
            cache, Build, _ => { }, NullLogger.Instance,
            new GitStateIndexOptions(1, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1), TimeSpan.FromSeconds(30)),
            _clock, factStore: new GitFactStore(storePath, NullLogger.Instance, _clock),
            fingerprintBranches: _ => ["develop"], producerId: "benchmark");
        harness.Service.RunCompleted += harness.Reports.Enqueue;
        await harness.Service.StartAsync(CancellationToken.None);
        return harness;
    }

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class Harness(TaskScannerService scanner, TaskListGitProjectionCache cache)
    {
        public int BuildCount;
        public TaskScannerService Scanner { get; } = scanner;
        public TaskListGitProjectionCache Cache { get; } = cache;
        public GitStateIndexService Service { get; set; } = null!;
        public ConcurrentQueue<GitIndexRunReport> Reports { get; } = new();
        public int Builds => Volatile.Read(ref BuildCount);

        public async Task<GitIndexRunReport> WaitAsync(Func<GitIndexRunReport, bool> match, int skip = 0)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var found = Reports.Skip(skip).FirstOrDefault(match);
                if (found is not null) return found;
                if (watch.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("No matching index run.");
                await Task.Delay(10);
            }
        }

        public async Task StopAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }
}
