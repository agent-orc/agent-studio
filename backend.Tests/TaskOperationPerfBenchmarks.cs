using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Opt-in CI benchmark for ASS-870. It seeds the post-ASS-617 flat storage
/// shape (tasks/&lt;bucket&gt;/&lt;key&gt; plus id/by-state.json) and gates hot task
/// operations against the explicit 30 ms p95 budget.
///
/// Enable with RUN_TASK_OPERATION_PERF=1. It is skipped by default because the
/// 20k-file fixture is intentionally heavy for normal unit-test runs.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class TaskOperationPerfBenchmarks : IDisposable
{
    private const int JobCount = 20_000;
    private const int Samples = 40;
    private const double BudgetMs = TaskOperationTimingFilter.BudgetMs;

    private readonly string _root;

    public TaskOperationPerfBenchmarks()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-task-op-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [SkippableFact]
    public void FlatStorage_TaskOperations_P95Under30ms_At20kTasks()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("RUN_TASK_OPERATION_PERF") == "1",
            "Set RUN_TASK_OPERATION_PERF=1 to run the 20k task-operation benchmark.");

        SeedFlatTasks(JobCount);
        var scanner = BuildScanner(withCache: true);
        Assert.Equal(JobCount, scanner.ScanAllJobs().Count);

        var byStateRead = Measure(Samples, () => _ = TaskLayoutIndex.ReadByState(_root)[TaskStates.Ready].Count);

        var moves = Measure(Samples, i =>
        {
            var key = $"PERF-{i + 1}";
            var result = TaskLayoutTransition.ChangeState(_root, key, TaskStates.Progress, NullLogger.Instance);
            Assert.True(result.Changed);
        });

        var batchMove = Measure(5, batch =>
        {
            var start = 100 + batch * 100;
            for (var offset = 0; offset < 100; offset++)
            {
                var result = TaskLayoutTransition.ChangeState(
                    _root,
                    $"PERF-{start + offset}",
                    TaskStates.AutoReview,
                    NullLogger.Instance);
                Assert.True(result.Changed);
            }
        });

        var creates = Measure(Samples, i =>
        {
            var n = JobCount + i + 1;
            var key = $"PERF-{n}";
            var dir = WriteFlatTask(key, TaskStates.Backlog, order: n);
            TaskLayoutIndex.Upsert(_root, key, TaskStorageLayout.Location(n, key), TaskStates.Backlog, NullLogger.Instance);
            Assert.True(File.Exists(Path.Combine(dir, "task.json")));
        });

        var cachedListRead = Measure(Samples, () =>
        {
            var jobs = scanner.ScanAllJobs();
            Assert.True(jobs.Count >= JobCount);
        });

        var detailRead = Measure(Samples, i =>
        {
            var detail = scanner.GetJobDetail($"task-{i + 1:D5}", _root);
            Assert.NotNull(detail);
        });

        var report = new[]
        {
            byStateRead.WithName("by-state-index-read"),
            moves.WithName("move-state-change"),
            batchMove.WithName("batch-move-100-total"),
            batchMove.Divide(100).WithName("batch-move-100-per-item"),
            creates.WithName("create-storage-mutation"),
            cachedListRead.WithName("cached-list-read"),
            detailRead.WithName("task-detail-read"),
        };

        foreach (var item in report)
        {
            Assert.True(
                item.P95 < BudgetMs,
                $"{item.Name} p95={item.P95:0.###} ms, p50={item.P50:0.###} ms, p99={item.P99:0.###} ms exceeds {BudgetMs:0.###} ms at {JobCount} tasks.");
        }
    }

    private void SeedFlatTasks(int count)
    {
        var byState = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [TaskStates.Ready] = new List<string>(count)
        };
        var byKey = new Dictionary<string, string>(count, StringComparer.Ordinal);

        for (var i = 1; i <= count; i++)
        {
            var key = $"PERF-{i}";
            WriteFlatTask(key, TaskStates.Ready, i);
            var location = TaskStorageLayout.Location(i, key);
            byState[TaskStates.Ready].Add(location);
            byKey[key] = location;
        }

        TaskLayoutIndex.Write(_root, byState, byKey, NullLogger.Instance);
    }

    private string WriteFlatTask(string key, string state, int order)
    {
        TaskStorageLayout.TryParseKeyNumber(key, out var n);
        var dir = TaskStorageLayout.JobDir(_root, n, key);
        Directory.CreateDirectory(dir);
        var json = new Dictionary<string, object?>
        {
            ["id"] = $"task-{n:D5}",
            ["key"] = key,
            ["title"] = $"Task {n:D5}",
            ["state"] = state,
            ["order"] = order,
            ["agent"] = CliTypes.Codex,
            ["cliType"] = CliTypes.Codex
        };
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(json));
        return dir;
    }

    private TaskScannerService BuildScanner(bool withCache)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "perf",
                ["WatchPaths:0:Path"] = _root,
                ["TaskRepository"] = Path.GetDirectoryName(_root)
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        if (withCache)
        {
            var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
            scanner.SetIndexCache(cache);
        }
        return scanner;
    }

    private static BenchStats Measure(int samples, Action body) =>
        Measure(samples, _ => body());

    private static BenchStats Measure(int samples, Action<int> body)
    {
        var elapsed = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            body(i);
            sw.Stop();
            elapsed[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(elapsed);
        return new BenchStats("", elapsed[PercentileIndex(samples, 0.50)], elapsed[PercentileIndex(samples, 0.95)], elapsed[PercentileIndex(samples, 0.99)]);
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private readonly record struct BenchStats(string Name, double P50, double P95, double P99)
    {
        public BenchStats WithName(string name) => this with { Name = name };
        public BenchStats Divide(double divisor) => new(Name, P50 / divisor, P95 / divisor, P99 / divisor);
    }
}
