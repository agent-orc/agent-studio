using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

// clock-independent: the fixed now is passed to every measured cycle-time build.

/// <summary>A measurement fixture, with no timing pass/fail threshold.</summary>
public sealed class CycleTimePerformanceTests
{
    private readonly ITestOutputHelper _output;
    public CycleTimePerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "MachineBound")]
    public void SyntheticStore_RecordsColdAndWarmBuildTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "cycle-time-perf-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "tasks");
        var now = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            Directory.CreateDirectory(Path.Combine(watch, TaskStates.Completed));
            for (var index = 0; index < 2100; index++)
            {
                var key = $"PERF-{index:0000}";
                var completed = index < 250 ? now.AddHours(-index % 120 - 1)
                    : now.AddDays(-14).AddMinutes(-index);
                var created = completed.AddHours(-3);
                var folder = Path.Combine(watch, TaskStates.Completed, key);
                Directory.CreateDirectory(Path.Combine(folder, "logs"));
                File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
                {
                    id = key, key, title = key, state = TaskStates.Completed, order = 1,
                    agent = "codex", createdAt = created, enteredLaneAt = completed,
                }, json));
                File.WriteAllText(Path.Combine(folder, "prompt.md"), $"# {key}\n");
                var events = new List<TimelineEvent>
                {
                    new() { Ts = created, Kind = TimelineEventKinds.PromptCreated,
                        Details = new() { ["targetState"] = TaskStates.Ready } },
                    new() { Ts = created.AddMinutes(30), Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.Ready, ["to"] = TaskStates.Progress, ["cause"] = "claimed" } },
                    new() { Ts = created.AddHours(1), Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.Progress, ["to"] = TaskStates.AutoReview, ["cause"] = "delivered" } },
                    new() { Ts = created.AddHours(1).AddMinutes(10), Kind = TimelineEventKinds.ReviewAttemptClaimed,
                        RunId = "review-1", Details = new() { ["attemptId"] = "review-1" } },
                    new() { Ts = created.AddHours(1).AddMinutes(20), Kind = TimelineEventKinds.PostStepFinished,
                        RunId = "review-1", Details = new() { ["attemptId"] = "review-1", ["pipelineStepId"] = "aspect-code-quality", ["durationMs"] = "600000" } },
                    new() { Ts = created.AddHours(1).AddMinutes(21), Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.AutoReview, ["to"] = TaskStates.HumanReview, ["cause"] = "review-verdict" } },
                    new() { Ts = completed, Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Completed, ["cause"] = "accepted" } },
                };
                for (var extra = 0; extra < 33; extra++)
                    events.Add(new TimelineEvent { Ts = created.AddMinutes(extra + 1), Kind = TimelineEventKinds.AgentRunStarted });
                File.WriteAllLines(Path.Combine(folder, "logs", "timeline.jsonl"),
                    events.OrderBy(e => e.Ts).Select(e => JsonSerializer.Serialize(e, json)));
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
                ["WatchPaths:0:Name"] = "Synthetic",
                ["WatchPaths:0:Path"] = watch,
                ["WatchPaths:0:RootPath"] = watch,
            }).Build();
            var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
            var service = new ProjectCycleTimeService(scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<ProjectCycleTimeService>.Instance);
            var timer = Stopwatch.StartNew();
            var cold = service.Build("Synthetic", "7d", now)!;
            var coldMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var indexWarmService = new ProjectCycleTimeService(scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<ProjectCycleTimeService>.Instance);
            var indexWarmCold = indexWarmService.Build("Synthetic", "7d", now)!;
            var indexWarmColdMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var warm = service.Build("Synthetic", "7d", now)!;
            var warmMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(2100, cold.Coverage.TasksInProject);
            Assert.Equal(250, cold.Coverage.TasksInWindow);
            Assert.Equal(cold.Coverage.TasksInWindow, indexWarmCold.Coverage.TasksInWindow);
            Assert.Equal(cold.Coverage.TasksInWindow, warm.Coverage.TasksInWindow);
            var line = $"Synthetic store: 2,100 tasks, 250 completed in 7d, 40 ledger rows/task. "
                + $"Cold including index {coldMs:F1} ms; cold with warm index {indexWarmColdMs:F1} ms; warm {warmMs:F1} ms.";
            _output.WriteLine(line);
            var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
            if (!string.IsNullOrWhiteSpace(results))
            {
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(results, "synthetic-timing.txt"), line + Environment.NewLine);
                File.WriteAllText(Path.Combine(results, "sample-response.json"),
                    JsonSerializer.Serialize(cold with { Tasks = cold.Tasks.Take(2).ToList() }, json));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
