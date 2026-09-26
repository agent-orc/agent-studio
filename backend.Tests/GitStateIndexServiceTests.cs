using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2726: unit coverage for <see cref="GitStateIndexService"/> itself -
/// debounced single-flight coalescing per repository, stale-while-revalidate
/// snapshot serving, bounded cross-repository concurrency, and change-driven
/// (not request-driven) re-indexing. The full request-path/HEAD-churn flow is
/// covered end to end by
/// <see cref="JobsEndpointPerfTests.TaskListEndpoints_ColdAndHeadChurn_StartNoGitProcessAndReturnUnderOneSecond"/>;
/// these tests isolate the indexer's own scheduling logic with fake
/// build/warm delegates so they run in milliseconds.
/// </summary>
public sealed class GitStateIndexServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string NewRepoWatchPath(string projectName)
    {
        var root = Path.Combine(Path.GetTempPath(), "git-state-index-test-" + Guid.NewGuid().ToString("N"));
        var jobs = Path.Combine(root, "jobs");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(jobs);
        Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), new string('0', 40) + "\n");
        _tempDirs.Add(root);
        return jobs;

        // Callers read WatchPathEntry.Path (jobs) and RootPath/RepositoryPath
        // (repo) via the config built in BuildScanner below.
    }

    private static TaskScannerService BuildScanner(string projectName, string jobsPath, string repoPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = jobsPath,
                ["WatchPaths:0:RootPath"] = repoPath,
                ["WatchPaths:0:RepositoryPath"] = repoPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static TaskWatcherService BuildWatcher(TaskScannerService scanner)
    {
        var config = new ConfigurationBuilder().Build();
        return new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, config);
    }

    private static GitStateIndexOptions FastOptions(int maxConcurrentRepos = 2) => new(
        MaxConcurrentRepos: maxConcurrentRepos,
        Debounce: TimeSpan.FromMilliseconds(20),
        SweepInterval: TimeSpan.FromSeconds(5),
        SlowRunWarnMs: TimeSpan.FromSeconds(5));

    /// <summary>A fake per-repository build delegate that counts and can block on demand.</summary>
    private sealed class FakeBuilder
    {
        private readonly int _spawnsPerRun;
        public int Calls;
        public int ConcurrentCalls;
        public int PeakConcurrentCalls;
        public TaskCompletionSource<bool>? Gate;

        public FakeBuilder(int spawnsPerRun = 7) => _spawnsPerRun = spawnsPerRun;

        public async Task<TaskListGitProjection> BuildAsync(IReadOnlyCollection<TaskInfo> tasks)
        {
            Interlocked.Increment(ref Calls);
            var concurrent = Interlocked.Increment(ref ConcurrentCalls);
            InterlockedMax(ref PeakConcurrentCalls, concurrent);
            try
            {
                if (Gate is { } gate) await gate.Task;
                for (var i = 0; i < _spawnsPerRun; i++) GitProcessTelemetry.Record("rev-list", 1, 0);
                return TaskListGitProjection.Empty;
            }
            finally
            {
                Interlocked.Decrement(ref ConcurrentCalls);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int initial;
            do
            {
                initial = Volatile.Read(ref target);
                if (value <= initial) return;
            } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
        }
    }

    /// <summary>
    /// How long a wait for background indexer progress may take before the
    /// test calls it stuck. Every condition here is reached in milliseconds on
    /// an idle machine; the budget exists only to turn a hang into a failure,
    /// so it is sized for the worst scheduling delay a fully loaded suite run
    /// can impose rather than for the expected duration. A five-second budget
    /// measured how busy the machine was, not whether the indexer worked, and
    /// is what timed these tests out under the full suite. The wait still
    /// returns the instant the condition holds, so nothing is slowed down and
    /// no wrong answer is tolerated.
    /// </summary>
    private static readonly TimeSpan IndexerProgressBudget = TimeSpan.FromSeconds(60);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? IndexerProgressBudget);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task RequestRefresh_SingleTrigger_ProducesOneRunAndPublishesSnapshot()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder();
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The "startup" pass fires for every known repository without any
            // external trigger.
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 1);
            Assert.Contains("proj", service.KnownProjects);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestRefresh_BurstOfTriggersDuringDebounceWindow_CollapsesToOneRun()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder();
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 1);
            var callsAfterStartup = Volatile.Read(ref builder.Calls);

            // Simulate the historical pattern: many trigger events (board
            // polls / task-folder writes) landing faster than the debounce
            // window. This is the exact scenario that used to recompute the
            // whole board on every one of these - now it collapses to one run.
            for (var i = 0; i < 300; i++) service.RequestRefresh("proj", "replay");

            await Task.Delay(200); // > debounce (20ms), short window to observe coalescing
            await WaitUntilAsync(() => !service.IsRunning("proj"));

            var totalCalls = Volatile.Read(ref builder.Calls);
            Assert.True(
                totalCalls - callsAfterStartup <= 2,
                $"300 rapid triggers inside one debounce window produced {totalCalls - callsAfterStartup} runs; expected at most 2 (debounce + at most one coalesced rerun).");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestRefresh_TriggerArrivingMidRun_CoalescesIntoOneRerunAfterCurrentRunFinishes()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Block the startup run in flight, then fire a burst of triggers
            // while it is still running - they must coalesce into exactly one
            // rerun, not one per trigger.
            await WaitUntilAsync(() => service.IsRunning("proj"));
            for (var i = 0; i < 50; i++) service.RequestRefresh("proj", "mid-run-burst");

            builder.Gate.SetResult(true);
            // First run completes, exactly one coalesced rerun follows and
            // then finishes (the fake no longer blocks on the second call).
            //
            // Wait for the rerun itself, not for a gap in IsRunning: between
            // the first run completing and the coalesced rerun starting the
            // service is legitimately not running, so a waiter on
            // !IsRunning can return in that gap and leave only the fixed
            // 100 ms below to cover the rerun's whole scheduling latency. On
            // a loaded host that window is not enough and the assert reads
            // Calls=1 for a service that did exactly the right thing - the
            // 15:55 full-suite failure this reproduces. The rerun's own call
            // count is the observable, so wait on that first.
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 2);
            await WaitUntilAsync(() => !service.IsRunning("proj"));
            // Quiet window kept afterwards: it is what would surface a third
            // run, which is the failure this test exists to catch.
            await Task.Delay(100);

            Assert.Equal(2, Volatile.Read(ref builder.Calls));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InFlightRun_ServesThePriorSnapshotAsStaleUntilTheRunCompletes()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var task = new TaskInfo
        {
            Id = "task-1",
            TaskKey = "watch::task-1",
            Title = "task-1",
            State = TaskStates.Completed,
            ProjectName = "proj",
            WatchPath = jobsPath,
        };
        var priorSignal = new TaskMergeSignal { Branch = "task/task-1" };
        cache.SetSnapshot(jobsPath, new TaskListGitProjection(
            new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal) { [task.TaskKey] = priorSignal },
            new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
            new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
            new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal),
            new Dictionary<string, AgentStudio.Review.ReviewProjectionView>(StringComparer.Ordinal)),
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new FakeBuilder { Gate = gate };
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => service.IsRunning("proj"));

            // The run is in flight: reads must still return the prior
            // snapshot (never block, never spawn Git), but freshness reports
            // Stale so the UI can show "updating".
            var stillServed = cache.ReadCacheOnly([task]);
            Assert.Same(priorSignal, stillServed.Merge[task.TaskKey]);
            Assert.True(cache.ReadFreshness([task]).Stale);

            gate.SetResult(true);
            await WaitUntilAsync(() => !service.IsRunning("proj"));

            Assert.False(cache.ReadFreshness([task]).Stale);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestPath_NeverSpawnsGit_EvenAfterARunThatDidSpawn()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder(spawnsPerRun: 7);
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 1);
            await WaitUntilAsync(() => !service.IsRunning("proj"));

            using var telemetry = GitProcessTelemetry.BeginRequest("tasks/list", NullLogger.Instance, includeNested: true);
            var task = new TaskInfo
            {
                Id = "task-1", TaskKey = "watch::task-1", Title = "t", State = TaskStates.Completed,
                ProjectName = "proj", WatchPath = jobsPath,
            };
            _ = cache.ReadCacheOnly([task]);
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CrossRepositoryConcurrency_NeverExceedsTheConfiguredBound()
    {
        const int repoCount = 6;
        const int maxConcurrent = 2;
        var jobsPaths = Enumerable.Range(0, repoCount).Select(i => NewRepoWatchPath($"proj-{i}")).ToArray();

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            Enumerable.Range(0, repoCount).SelectMany(i => new[]
            {
                new KeyValuePair<string, string?>($"WatchPaths:{i}:Name", $"proj-{i}"),
                new KeyValuePair<string, string?>($"WatchPaths:{i}:Path", jobsPaths[i]),
                new KeyValuePair<string, string?>($"WatchPaths:{i}:RootPath", Path.Combine(Path.GetDirectoryName(jobsPaths[i])!, "repo")),
                new KeyValuePair<string, string?>($"WatchPaths:{i}:RepositoryPath", Path.Combine(Path.GetDirectoryName(jobsPaths[i])!, "repo")),
            })).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new FakeBuilder { Gate = gate };
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(maxConcurrent), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // All six repositories fire their "startup" trigger at once;
            // the process-wide semaphore must cap how many run concurrently.
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) == maxConcurrent);
            await Task.Delay(100);
            Assert.Equal(maxConcurrent, Volatile.Read(ref builder.ConcurrentCalls));

            gate.SetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) == repoCount);

            Assert.True(
                builder.PeakConcurrentCalls <= maxConcurrent,
                $"peak concurrent index runs was {builder.PeakConcurrentCalls}, over the configured bound of {maxConcurrent}.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FileSystemChange_UnderGitRefs_TriggersAReindexWithoutAnyExplicitRequestRefreshCall()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder();
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 1);
            await WaitUntilAsync(() => !service.IsRunning("proj"));
            var callsAfterStartup = Volatile.Read(ref builder.Calls);

            // Simulate a ref move (a commit on the current branch) purely as
            // a filesystem write - no git binary needed to prove the watcher
            // reacts to it.
            File.WriteAllText(Path.Combine(repoPath, ".git", "refs", "heads", "main"), new string('1', 40) + "\n");

            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) > callsAfterStartup);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Load-fixture regression test replaying the measured 2026-09-06
    /// 20:00-20:55 trigger pattern from
    /// docs/system/reports (AGT-2726 task doc): 315 <c>tasks/list-refresh</c>
    /// calls in 55 minutes (~5.7/min) each averaging ~7.1 git spawns
    /// (2,243 spawns / 315 calls), on top of hundreds more from
    /// <c>board/integration-status</c> (437 calls), <c>board/merge-status</c>
    /// (328 calls), and <c>git/inventory</c> (173 calls) - about 1,253 combined
    /// trigger-shaped calls over the window, ~23/min.
    ///
    /// <para>
    /// Under the old request-cadence-triggered design, every one of those
    /// calls could queue its own recompute, which is exactly how the
    /// measurement produced ~3,980 git spawns in 55 minutes (~72/min). This
    /// fixture replays the same call VOLUME (not the literal 55 minutes of
    /// wall-clock) against the indexer and asserts the actual number of index
    /// runs - the only place a git spawn can occur - stays bounded by the
    /// debounce window, not by the trigger count. That is what keeps the real
    /// system under the acceptance target of 20 spawns/minute: spawns only
    /// happen on actual runs, and actual runs no longer scale with request
    /// traffic.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReplayOfMeasuredTriggerPattern_RunCountStaysBoundedByDebounceNotByTriggerVolume()
    {
        const int spawnsPerRunHistoricalAverage = 7; // 2,243 spawns / 315 calls, rounded.
        const int combinedTriggerCallsInWindow = 1253; // 315 + 437 + 328 + 173, see docstring.
        const double acceptedSpawnsPerMinute = 20;

        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var watcher = BuildWatcher(scanner);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder(spawnsPerRunHistoricalAverage);
        using var service = new GitStateIndexService(
            scanner, watcher, cache, builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 1);
            await WaitUntilAsync(() => !service.IsRunning("proj"));
            var callsAfterStartup = Volatile.Read(ref builder.Calls);

            // Replay the full measured call volume for one repository, fired
            // as fast as the test can issue them (a strictly harder case than
            // the real 55-minute spread, since real spacing gives even more
            // opportunity for triggers to land inside the same debounce
            // window or the same in-flight run).
            for (var i = 0; i < combinedTriggerCallsInWindow; i++) service.RequestRefresh("proj", "replay");

            await Task.Delay(200);
            await WaitUntilAsync(() => !service.IsRunning("proj"));

            var runsFromReplay = Volatile.Read(ref builder.Calls) - callsAfterStartup;
            var spawnsFromReplay = runsFromReplay * spawnsPerRunHistoricalAverage;
            var naiveRequestDrivenSpawns = combinedTriggerCallsInWindow * spawnsPerRunHistoricalAverage;

            // The regression bound: however many triggers land, the indexer
            // must not re-run once per trigger. A handful of runs (debounce
            // coalescing plus at most one rerun per already-in-flight run) is
            // the whole point of the redesign.
            Assert.True(
                runsFromReplay <= 5,
                $"{combinedTriggerCallsInWindow} replayed triggers produced {runsFromReplay} index runs; " +
                "expected a small constant regardless of trigger volume (debounce + single-flight coalescing).");
            Assert.True(
                spawnsFromReplay < naiveRequestDrivenSpawns,
                $"Replay spawns ({spawnsFromReplay}) should be far below the naive per-request-triggered total " +
                $"({naiveRequestDrivenSpawns}) for the same {combinedTriggerCallsInWindow}-call volume.");
            // Sanity check against the acceptance target's shape: even
            // spread over the full 55-minute measurement window, this
            // fixture's replayed spawn count is nowhere near the 20/min
            // budget (55 * 20 = 1,100), let alone the historical ~3,980.
            Assert.True(spawnsFromReplay < 55 * acceptedSpawnsPerMinute);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnchangedTaskEvent_DoesNotRecomputeGitProjection()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var scanner = BuildScanner("proj", jobsPath, Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo"));
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder();
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner), cache,
            builder.BuildAsync, _ => { }, NullLogger.Instance, FastOptions(), TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => !service.IsRunning("proj") && Volatile.Read(ref builder.Calls) > 0);
            var before = Volatile.Read(ref builder.Calls);
            for (var i = 0; i < 40; i++) service.RequestRefresh("proj", "task-event");
            await Task.Delay(150);
            Assert.Equal(before, Volatile.Read(ref builder.Calls));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RefChangeDuringRefresh_DiscardsOldComputationAndPublishesRerun()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var builder = new FakeBuilder
        {
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner),
            new TaskListGitProjectionCache(), builder.BuildAsync, _ => { }, NullLogger.Instance,
            FastOptions(), TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) == 1);
            File.WriteAllText(Path.Combine(repoPath, ".git", "refs", "heads", "main"),
                new string('2', 40) + "\n");
            builder.Gate!.SetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 2);
            await WaitUntilAsync(() => !service.IsRunning("proj")
                && service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null);
            Assert.True(Volatile.Read(ref builder.Calls) >= 2);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task IncludedConfigChangeDuringRefresh_DiscardsOldComputation()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var extra = Path.Combine(Path.GetDirectoryName(jobsPath)!, "included.conf");
        RunGit(repoPath, "init", "-q");
        File.WriteAllText(extra, "[remote \"origin\"]\nurl = https://example.invalid/one.git\n");
        File.WriteAllText(Path.Combine(repoPath, ".git", "config"),
            $"[include]\npath = {extra}\n");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var builder = new FakeBuilder
        {
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner),
            new TaskListGitProjectionCache(), builder.BuildAsync, _ => { }, NullLogger.Instance,
            FastOptions(), TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) == 1);
            var capturedConfig = GitConfigSignature.Capture(repoPath);
            Assert.Equal("https://example.invalid/one.git", capturedConfig.OriginUrl);
            File.WriteAllText(extra, "[remote \"origin\"]\nurl = https://example.invalid/two.git\n");
            Assert.False(GitConfigSignature.FilesUnchanged(capturedConfig));
            builder.Gate!.SetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 2);
            await WaitUntilAsync(() => service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MissingRepository_KeepsPriorSnapshotAndReportsFailure()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var repoPath = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var scanner = BuildScanner("proj", jobsPath, repoPath);
        var cache = new TaskListGitProjectionCache();
        var builder = new FakeBuilder();
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner), cache,
            builder.BuildAsync, _ => { }, NullLogger.Instance,
            FastOptions() with { MaxRetries = 0 }, TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => !service.IsRunning("proj")
                && service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null);
            var previous = service.GetRepositoryStatuses().Single().GitStateAt;
            Directory.Delete(repoPath, recursive: true);
            service.RequestRefresh("proj", "repo-deleted");
            var task = new TaskInfo { TaskKey = "job", WatchPath = jobsPath };
            await WaitUntilAsync(() => cache.ReadTask(task).ReasonCode == "repository-unavailable");
            Assert.Equal(previous, service.GetRepositoryStatuses().Single().GitStateAt);
            Assert.Equal("stale", cache.ReadTask(task).State);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DeletedLinkedWorktree_MarksTheLastSnapshotStale()
    {
        var temp = Path.Combine(Path.GetTempPath(), "git-index-worktree-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(temp);
        var main = Path.Combine(temp, "main");
        var worktree = Path.Combine(temp, "task-worktree");
        var jobs = Path.Combine(temp, "jobs");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(jobs);
        RunGit(main, "init", "-q");
        RunGit(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "first");
        RunGit(main, "worktree", "add", "--detach", "-q", worktree);

        var before = GitRefSignature.Capture(worktree);
        RunGit(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "second");
        Assert.NotEqual(before, GitRefSignature.Capture(worktree));

        var scanner = BuildScanner("proj", jobs, worktree);
        var cache = new TaskListGitProjectionCache();
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner), cache,
            _ => Task.FromResult(TaskListGitProjection.Empty), _ => { }, NullLogger.Instance,
            FastOptions() with { MaxRetries = 0 }, TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => !service.IsRunning("proj")
                && service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null);
            Directory.Delete(worktree, recursive: true);
            service.RequestRefresh("proj", "worktree-deleted");
            await WaitUntilAsync(() => cache.ReadTask(new TaskInfo
                { TaskKey = "job", WatchPath = jobs }).ReasonCode == "repository-unavailable");
            Assert.NotNull(service.GetRepositoryStatuses().Single().GitStateAt);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ProjectRepositoryPathChange_ReplacesTheOldSnapshotAndIndexesTheNewRepo()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var firstRepo = Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo");
        var secondJobs = NewRepoWatchPath("second");
        var secondRepo = Path.Combine(Path.GetDirectoryName(secondJobs)!, "repo");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "proj",
            ["WatchPaths:0:Path"] = jobsPath,
            ["WatchPaths:0:RootPath"] = firstRepo,
            ["WatchPaths:0:RepositoryPath"] = firstRepo,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var builder = new FakeBuilder();
        var cache = new TaskListGitProjectionCache();
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner), cache,
            builder.BuildAsync, _ => { }, NullLogger.Instance,
            FastOptions() with { SweepInterval = TimeSpan.FromMilliseconds(100) }, TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null);
            var firstGeneration = cache.Generation;
            config["WatchPaths:0:RootPath"] = secondRepo;
            config["WatchPaths:0:RepositoryPath"] = secondRepo;
            await WaitUntilAsync(() => Volatile.Read(ref builder.Calls) >= 2
                && cache.Generation > firstGeneration);
            await WaitUntilAsync(() => service.GetRepositoryStatuses().FirstOrDefault()?.GitStateAt is not null
                && !service.IsRunning("proj"));
            Assert.True(Volatile.Read(ref builder.Calls) >= 2);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task FailureRetriesAreBounded()
    {
        var jobsPath = NewRepoWatchPath("proj");
        var scanner = BuildScanner("proj", jobsPath, Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo"));
        var calls = 0;
        Task<TaskListGitProjection> Fail(IReadOnlyCollection<TaskInfo> _)
        {
            Interlocked.Increment(ref calls);
            throw new IOException("fake Git failure");
        }
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner),
            new TaskListGitProjectionCache(), Fail, _ => { }, NullLogger.Instance,
            FastOptions() with { MaxRetries = 2 }, TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref calls) == 3);
            await Task.Delay(250);
            Assert.Equal(3, Volatile.Read(ref calls));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [SkippableFact]
    public async Task HangingGitFake_DeadlineKillsChildAndReleasesRepositorySlot()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fake Git process uses /bin/sh.");
        var jobsPath = NewRepoWatchPath("proj");
        var scanner = BuildScanner("proj", jobsPath, Path.Combine(Path.GetDirectoryName(jobsPath)!, "repo"));
        var childDone = new TaskCompletionSource<GitProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TaskListGitProjection> Hang(IReadOnlyCollection<TaskInfo> _) => Task.Run(() =>
        {
            var start = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("sleep 30");
            var result = GitNetworkProcessRunner.Run(start,
                cancellationToken: GitProcessBudget.Token);
            childDone.TrySetResult(result);
            return TaskListGitProjection.Empty;
        });
        var cache = new TaskListGitProjectionCache();
        using var service = new GitStateIndexService(scanner, BuildWatcher(scanner), cache,
            Hang, _ => { }, NullLogger.Instance,
            FastOptions() with { RunDeadline = TimeSpan.FromMilliseconds(300), MaxRetries = 0 },
            TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var result = await childDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => !service.IsRunning("proj"));
            Assert.Equal(GitProcessFailureKind.Cancelled, result.FailureKind);
            Assert.Equal("timeout", cache.ReadTask(new TaskInfo { TaskKey = "job", WatchPath = jobsPath }).ReasonCode);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }
}
