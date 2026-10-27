using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3322 (Git facts S2): <see cref="GitStateIndexService"/> as the only
/// writer of the Git fact store. A restart serves the last generation as
/// <c>restored</c> before any Git process and confirms it from files; a task
/// event without a changed card input starts no Git; <c>git config</c> runs
/// only for a configuration change; a killed run, a corrupt store, a
/// repository-wide failure and a stale producer all keep the last good
/// generation. Every clock is injected.
/// </summary>
public sealed class GitStateIndexRestartTests : IDisposable
{
    private static readonly TimeSpan ProgressBudget = TimeSpan.FromSeconds(60);
    private readonly string _root = AgentStudio.TestSupport.TestTempRoot.Create("git-index-restart");
    private readonly FakeTimeProvider _clock = new();

    public GitStateIndexRestartTests()
    {
        Jobs = Path.Combine(_root, "jobs");
        Repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Repo);
        RunGit(Repo, "init", "-q", "-b", "main");
        RunGit(Repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "first");
        WriteTask(TaskStates.Progress, "card-a");
        WriteTask(TaskStates.HumanReview, "card-b");
    }

    private string Jobs { get; }
    private string Repo { get; }
    private string StorePath => Path.Combine(_root, "state", GitFactStoreSchema.FileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task Restart_WithUnchangedInputs_ServesRestoredBeforeGitAndBecomesReadyWithZeroSpawns()
    {
        var first = await RunUntilPublishedAsync();
        var publishedAt = first.Cache.ReadTask(Card("card-a")).ComputedAt;
        Assert.NotNull(publishedAt);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await using var second = Start(debounce: TimeSpan.FromSeconds(1));
        await second.Service.Restoration;

        // Served from the store before the startup run has been dispatched.
        var restored = second.Cache.ReadTask(Card("card-a"));
        Assert.Equal("restored", restored.State);
        Assert.Equal(publishedAt, restored.ComputedAt);
        Assert.Equal("task/card-a", restored.Data!.Merge!.Branch);
        var freshness = second.Cache.ReadFreshness([Card("card-a")]);
        Assert.True(freshness.Restored);
        Assert.True(freshness.Stale);
        var status = second.Service.GetRepositoryStatuses().Single();
        Assert.Equal(GitRepositoryHealthPolicy.Restored, status.State);
        Assert.Equal(300, status.AgeSeconds);
        Assert.Equal(0, second.Builds);

        var confirmed = await second.WaitForAsync(report => report.Outcome == "confirmed");
        Assert.Equal(0, confirmed.Spawns);
        Assert.False(confirmed.ConfigCaptured);
        Assert.Equal(0, second.Builds);
        Assert.All(second.Reports, report => Assert.Equal(0, report.Spawns));
        var ready = second.Cache.ReadTask(Card("card-a"));
        Assert.Equal("ready", ready.State);
        Assert.Equal("task/card-a", ready.Data!.Merge!.Branch);
        Assert.Equal(GitRepositoryHealthPolicy.Ready, second.Service.GetRepositoryStatuses().Single().State);
    }

    [Fact]
    public async Task Restart_WithAMovedRef_KeepsServingRestoredUntilTheDeltaRunPublishes()
    {
        await RunUntilPublishedAsync();
        RunGit(Repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "second");

        await using var second = Start(gate: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await second.Service.Restoration;
        await WaitUntilAsync(() => second.Builds == 1);
        Assert.Equal("restored", second.Cache.ReadTask(Card("card-a")).State);

        second.Gate!.SetResult();
        var published = await second.WaitForAsync(report => report.Outcome == "published");
        Assert.False(published.ConfigCaptured);
        Assert.Equal("ready", second.Cache.ReadTask(Card("card-a")).State);
        Assert.Equal(2, new GitFactStore(StorePath, NullLogger.Instance, _clock).LoadCurrent("proj")!.Generation.Generation);
    }

    [Fact]
    public async Task TaskEvent_WithoutAChangedCardInput_StartsNoGitProcess()
    {
        await using var running = Start();
        var first = await running.WaitForAsync(report => report.Outcome == "published");
        Assert.True(first.ConfigCaptured);

        var before = running.Reports.Count;
        for (var i = 0; i < 5; i++)
        {
            running.Service.RequestRefresh("proj", "task-event");
            await WaitUntilAsync(() => running.Reports.Count > before + i);
        }
        var events = running.Reports.Skip(before).Where(report => report.Trigger == "task-event").ToArray();
        Assert.Equal(5, events.Length);
        Assert.All(events, report =>
        {
            Assert.Equal("unchanged", report.Outcome);
            Assert.Equal(0, report.Spawns);
            Assert.False(report.ConfigCaptured);
        });

        // A changed card input recomputes, still without resolving the configuration.
        WriteTask(TaskStates.Progress, "card-c");
        running.Service.RequestRefresh("proj", "task-event");
        var changed = await running.WaitForAsync(report => report.Outcome == "published", skip: before);
        Assert.False(changed.ConfigCaptured);
        Assert.Equal(1, changed.Spawns); // the builder's own recorded process only
    }

    [Fact]
    public async Task ConfigEvent_ResolvesConfigurationAgainAndAdvancesTheGenerationOnlyOnAChange()
    {
        await using var running = Start();
        await running.WaitForAsync(report => report.Outcome == "published");
        Assert.Equal(1, running.Service.GetRepositoryStatuses().Single().ConfigGeneration);
        var config = Path.Combine(Repo, ".git", "config");

        // Rewriting identical bytes is a configuration event without a change.
        var mark = running.Reports.Count;
        File.WriteAllBytes(config, File.ReadAllBytes(config));
        running.Service.RequestRefresh("proj", "fs-watch");
        await WaitUntilAsync(() => running.Reports.Skip(mark).Any(report => report.ConfigCaptured));
        Assert.Equal(1, running.Service.GetRepositoryStatuses().Single().ConfigGeneration);

        mark = running.Reports.Count;
        File.AppendAllText(config, "[remote \"origin\"]\n\turl = https://example.invalid/moved.git\n");
        running.Service.RequestRefresh("proj", "fs-watch");
        var moved = await running.WaitForAsync(report => report.ConfigCaptured && report.Outcome == "published",
            skip: mark);
        Assert.True(moved.Spawns >= 1);
        Assert.Equal(2, running.Service.GetRepositoryStatuses().Single().ConfigGeneration);

        await WaitUntilAsync(() => !running.Service.IsRunning("proj"));
        mark = running.Reports.Count;
        running.Service.RequestRefresh("proj", "task-event");
        var quiet = await running.WaitForAsync(report => report.Trigger == "task-event", skip: mark);
        Assert.Equal("unchanged", quiet.Outcome);
        Assert.False(quiet.ConfigCaptured);
        Assert.Equal(0, quiet.Spawns);

        var stored = new GitFactStore(StorePath, NullLogger.Instance, _clock).LoadCurrent("proj")!;
        Assert.Equal(2, stored.Generation.ConfigGeneration);
        Assert.Equal("https://example.invalid/moved.git", stored.Repository.Config!.OriginUrl);
    }

    [Fact]
    public async Task ProcessStoppedDuringARun_LeavesThePreviousGenerationCurrentAndRestorable()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Start(gate: gate, gateFromBuild: 2);
        await using (running)
        {
            await running.WaitForAsync(report => report.Outcome == "published");
            RunGit(Repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-q", "--allow-empty", "-m", "second");
            running.Service.RequestRefresh("proj", "ref-moved");
            await WaitUntilAsync(() => running.Builds == 2);
        }

        var stored = new GitFactStore(StorePath, NullLogger.Instance, _clock).LoadCurrent("proj")!;
        Assert.Equal(1, stored.Generation.Generation);
        await using var restarted = Start(debounce: TimeSpan.FromSeconds(1));
        await restarted.Service.Restoration;
        var restored = restarted.Cache.ReadTask(Card("card-a"));
        Assert.Equal("restored", restored.State);
        Assert.Equal("task/card-a", restored.Data!.Merge!.Branch);
    }

    [Fact]
    public async Task CorruptStoreOnStart_IsRebuiltWhileCardsAnswerWarmingWithoutRequestGit()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllBytes(StorePath, Enumerable.Repeat((byte)0x33, 4096).ToArray());

        await using var running = Start(debounce: TimeSpan.FromSeconds(1));
        await running.Service.Restoration;
        Assert.Equal(GitFactStoreHealthEvent.CorruptStoreRecovered, running.Store.LastHealthEvent!.Kind);
        using (var telemetry = GitProcessTelemetry.BeginRequest("tasks/detail/git", NullLogger.Instance,
                   includeNested: true, _clock))
        {
            Assert.Equal("warming", running.Cache.ReadTask(Card("card-a")).State);
            Assert.Equal("warming", running.Cache.ReadTask(Card("card-b")).State);
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }

        await running.WaitForAsync(report => report.Outcome == "published");
        Assert.Equal("ready", running.Cache.ReadTask(Card("card-a")).State);
        Assert.Equal(1, running.Store.LoadCurrent("proj")!.Generation.Generation);
    }

    [Fact]
    public async Task RepositoryWideLookupFailure_KeepsTheLastGoodGenerationAndRecordsDegradedHealth()
    {
        var failing = 0;
        await using var running = Start(transform: projection => Volatile.Read(ref failing) == 1
            ? projection with { RepositoryFailure = new GitRepositoryLookupFailure("integration",
                "repository-lookup-failed", "IOException: repository unavailable") }
            : projection);
        await running.WaitForAsync(report => report.Outcome == "published");
        Volatile.Write(ref failing, 1);
        WriteTask(TaskStates.Progress, "card-c");
        running.Service.RequestRefresh("proj", "task-event");
        await running.WaitForAsync(report => report.Outcome == "degraded");

        var kept = running.Cache.ReadTask(Card("card-a"));
        Assert.Equal("stale", kept.State);
        Assert.Equal("repository-degraded", kept.ReasonCode);
        Assert.Equal("task/card-a", kept.Data!.Merge!.Branch);
        var status = running.Service.GetRepositoryStatuses().Single();
        Assert.Equal(GitRepositoryHealthPolicy.Degraded, status.State);
        Assert.Contains("integration", status.LastError);
        Assert.Equal(1, running.Store.LoadCurrent("proj")!.Generation.Generation);
        var health = running.Store.ReadHealth("proj")!;
        Assert.Equal(GitRepositoryHealthPolicy.Degraded, health.State);
        Assert.Equal(1, health.LastGoodGeneration);
        Assert.True(health.ConsecutiveFailures >= 1);
    }

    [Fact]
    public async Task RelocatedRepository_IsNotRestored()
    {
        await RunUntilPublishedAsync();
        var moved = Path.Combine(_root, "moved-repo");
        Directory.Move(Repo, moved);

        await using var second = Start(debounce: TimeSpan.FromSeconds(1), repositoryPath: moved);
        await second.Service.Restoration;
        Assert.Equal("warming", second.Cache.ReadTask(Card("card-a")).State);
        await second.WaitForAsync(report => report.Outcome == "published");
        Assert.Equal("ready", second.Cache.ReadTask(Card("card-a")).State);
    }

    [Fact]
    public async Task OlderProducer_IsFencedOutOfTheStoreButKeepsServingItsOwnReaders()
    {
        await using var running = Start();
        await running.WaitForAsync(report => report.Outcome == "published");
        var newer = new GitFactStore(StorePath, NullLogger.Instance, _clock);
        Assert.True(newer.AcquireProducerEpoch("proj", "producer-newer")
            > running.Service.GetRepositoryStatuses().Single().ProducerEpoch);

        var mark = running.Reports.Count;
        WriteTask(TaskStates.Progress, "card-c");
        running.Service.RequestRefresh("proj", "task-event");
        await running.WaitForAsync(report => report.Outcome == "published", skip: mark);

        Assert.Equal(GitRepositoryHealthPolicy.Fenced, running.Service.GetRepositoryStatuses().Single().State);
        Assert.Equal("ready", running.Cache.ReadTask(Card("card-c")).State);
        var stored = newer.LoadCurrent("proj")!;
        Assert.Equal(1, stored.Generation.Generation);
        Assert.DoesNotContain(stored.Cards, card => card.TaskKey.EndsWith("card-c", StringComparison.Ordinal));
    }

    private async Task<Harness> RunUntilPublishedAsync()
    {
        var harness = Start();
        await harness.WaitForAsync(report => report.Outcome == "published");
        await harness.DisposeAsync();
        return harness;
    }

    private TaskInfo Card(string id)
        => Scanner().ScanAllJobsRaw().Single(task => task.Id == id);

    private TaskScannerService Scanner(string? repositoryPath = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "proj",
            ["WatchPaths:0:Path"] = Jobs,
            ["WatchPaths:0:RootPath"] = repositoryPath ?? Repo,
            ["WatchPaths:0:RepositoryPath"] = repositoryPath ?? Repo,
        }).Build();
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
    }

    private Harness Start(TimeSpan? debounce = null, TaskCompletionSource? gate = null, int gateFromBuild = 1,
        Func<TaskListGitProjection, TaskListGitProjection>? transform = null, string? repositoryPath = null)
    {
        var scanner = Scanner(repositoryPath);
        var cache = new TaskListGitProjectionCache();
        var store = new GitFactStore(StorePath, NullLogger.Instance, _clock);
        var harness = new Harness(cache, store, gate);
        async Task<TaskListGitProjection> Build(IReadOnlyCollection<TaskInfo> tasks)
        {
            if (Interlocked.Increment(ref harness.BuildCount) >= gateFromBuild && gate is not null)
                await gate.Task;
            GitProcessTelemetry.Record("rev-list", 1, 0);
            var projection = TaskListGitProjection.Empty with
            {
                Merge = tasks.ToDictionary(task => task.TaskKey,
                    task => new TaskMergeSignal { Branch = "task/" + task.Id }, StringComparer.Ordinal),
                Signatures = tasks.ToDictionary(task => task.TaskKey, TaskGitSignature.For, StringComparer.Ordinal),
                SubjectVersions = tasks.ToDictionary(task => task.TaskKey,
                    task => cache.SubjectVersion(task.FolderPath), StringComparer.Ordinal),
            };
            return transform is null ? projection : transform(projection);
        }
        var service = new GitStateIndexService(scanner,
            new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, new ConfigurationBuilder().Build()),
            cache, Build, _ => { }, NullLogger.Instance,
            new GitStateIndexOptions(1, debounce ?? TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1),
                TimeSpan.FromSeconds(30)) { MaxRetries = 0 },
            _clock, factStore: store, producerId: "producer-test");
        harness.Service = service;
        service.RunCompleted += harness.Reports.Enqueue;
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return harness;
    }

    private void WriteTask(string state, string id)
    {
        var folder = Path.Combine(Jobs, state, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"{id}\",\"state\":\"{state}\",\"title\":\"Card {id}\"}}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > ProgressBudget) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
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

    private sealed class Harness(TaskListGitProjectionCache cache, GitFactStore store, TaskCompletionSource? gate)
        : IAsyncDisposable
    {
        public int BuildCount;
        public TaskListGitProjectionCache Cache { get; } = cache;
        public GitFactStore Store { get; } = store;
        public TaskCompletionSource? Gate { get; } = gate;
        public GitStateIndexService Service { get; set; } = null!;
        public ConcurrentQueue<GitIndexRunReport> Reports { get; } = new();
        public int Builds => Volatile.Read(ref BuildCount);

        public async Task<GitIndexRunReport> WaitForAsync(Func<GitIndexRunReport, bool> match, int skip = 0)
        {
            GitIndexRunReport? found = null;
            await WaitUntilAsync(() => (found = Reports.Skip(skip).FirstOrDefault(match)) is not null);
            return found!;
        }

        /// <summary>Stops like a killed process: a gated run is abandoned, never released into a publish.</summary>
        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Gate?.TrySetResult();
            Service.Dispose();
        }
    }
}
