using System.Diagnostics;
using AgentStudio.Pipeline;
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
            var authorityNow = now;
            var authority = new AttemptAuthorityService(NullLogger<AttemptAuthorityService>.Instance, () => authorityNow);
            var tokenSummaries = new Dictionary<string, TaskTokenSummary>();
            var batchStore = new BatchGateStore(Path.Combine(root, "batch-gates"));
            Directory.CreateDirectory(Path.Combine(watch, TaskStates.Completed));
            for (var index = 0; index < 2100; index++)
            {
                var key = $"PERF-{index:0000}";
                var completed = index < 250 ? now.AddHours(-index % 120 - 1)
                    : now.AddDays(-14).AddMinutes(-index);
                var created = completed.AddHours(-3);
                authorityNow = created.AddHours(1);
                var run = authority.AcquireRun(key, "Synthetic", null, "runner", "host", 120, "claim-" + key).RunAttempt!;
                Assert.Equal(AttemptWriteStatus.Accepted, authority.SettleRun(new SettleRunAttemptRequest
                {
                    Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + key),
                    Outcome = "done", ResultSha = new string('a', 40),
                }).Status);
                var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(key, "Synthetic",
                    new string('a', 40), run.AttemptId, "requirements", "policy", [], "review-" + key)).ReviewAttempt!;
                tokenSummaries[key] = new TaskTokenSummary
                {
                    Calls = 2, LastUpdate = completed,
                    Entries = [new TaskTokenCall { Ts = created.AddMinutes(50),
                        ParticipantId = "agent:remote-runner:" + run.AttemptId, InputTokens = 1000,
                        CacheReadTokens = 9000, EstimatedApiCostUsd = 1m, ModelPriced = true },
                        new TaskTokenCall { Ts = created.AddMinutes(79), ParticipantId = "support:remote-review:" + review.AttemptId,
                            OutputTokens = 500, EstimatedApiCostUsd = 0.2m, ModelPriced = true }],
                };
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
                        Details = new() { ["from"] = TaskStates.Ready, ["to"] = TaskStates.Progress, ["cause"] = "claimed", ["attemptId"] = run.AttemptId } },
                    new() { Ts = created.AddHours(1), Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.Progress, ["to"] = TaskStates.AutoReview, ["cause"] = "delivered" } },
                    new() { Ts = created.AddHours(1).AddMinutes(10), Kind = TimelineEventKinds.ReviewAttemptClaimed,
                        RunId = review.AttemptId, Details = new() { ["attemptId"] = review.AttemptId } },
                    new() { Ts = created.AddHours(1).AddMinutes(20), Kind = TimelineEventKinds.PostStepFinished,
                        RunId = review.AttemptId, Details = new() { ["attemptId"] = review.AttemptId, ["pipelineStepId"] = "aspect-code-quality", ["durationMs"] = "600000" } },
                    new() { Ts = created.AddHours(1).AddMinutes(21), Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.AutoReview, ["to"] = TaskStates.HumanReview, ["cause"] = "review-verdict" } },
                    new() { Ts = completed, Kind = TimelineEventKinds.LaneChanged,
                        Details = new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Completed, ["cause"] = "accepted" } },
                };
                Directory.CreateDirectory(Path.Combine(folder, "post-steps"));
                File.WriteAllText(Path.Combine(folder, "post-steps", "build-test-gate-1.log"),
                    $"startedAtUtc={created.AddMinutes(70):O} queueWaitMs=60000 durationMs=60000\n");
                if (index < 16)
                {
                    var batchId = "batch-" + index;
                    var batchFolder = batchStore.BatchDirectory(batchId);
                    var scope = new BatchGateScope("Synthetic", "repo", "develop", "full", "digest", "v1");
                    var done = new DateTimeOffset(created.AddMinutes(80));
                    var subject = new BatchGateSubject(key, "Synthetic", "repo", "develop", "full", "digest", "v1",
                        "refs/heads/result", new string('a', 40), run.AttemptId, 1, 1, new string('a', 40),
                        true, true, true, true, false, null, true, done, 1);
                    Write(Path.Combine(batchFolder, "manifest.json"), new BatchGateManifest(batchId, scope,
                        new string('a', 40), "membership", done, [subject], [], [key]));
                    Write(Path.Combine(batchFolder, "runs", "run.json"), new BatchGateRunRecord("run", batchId,
                        "membership", new string('a', 40), new string('b', 40), "full", "digest", "host", 1,
                        ["build"], done.AddSeconds(10), "gate.log"));
                    Write(Path.Combine(batchFolder, "state", "00000001.json"), new BatchGateState(batchId,
                        "membership", BatchPhase.Green, new string('b', 40), 1, done.AddSeconds(50)));
                    Write(Path.Combine(root, "batch-gates", "executions", batchId + ".json"),
                        new BatchGateExecutionFact("Synthetic", batchId, "run", new string('b', 40), "Ok", "host",
                            done.AddSeconds(10), done.AddSeconds(50), 10000, 0, "gate.log"));
                }
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
            var tokens = new FakeTokenAggregator(new Dictionary<string, Dictionary<string, TaskTokenSummary>>
                { ["Synthetic"] = tokenSummaries });
            var service = new ProjectCycleTimeService(scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<ProjectCycleTimeService>.Instance, tokens: tokens, batchGate: batchStore, attemptAuthority: authority);
            var timer = Stopwatch.StartNew();
            var cold = service.Build("Synthetic", "7d", now)!;
            var coldMs = timer.Elapsed.TotalMilliseconds;
            Assert.Single(tokens.Calls);
            timer.Restart();
            var indexWarmService = new ProjectCycleTimeService(scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<ProjectCycleTimeService>.Instance, tokens: tokens, batchGate: batchStore, attemptAuthority: authority);
            var indexWarmCold = indexWarmService.Build("Synthetic", "7d", now)!;
            var indexWarmColdMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(2, tokens.Calls.Count);
            timer.Restart();
            var warm = service.Build("Synthetic", "7d", now)!;
            var warmMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var memoWarm = service.Build("Synthetic", "6d", now)!;
            var memoWarmMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(cold.Coverage.TasksInWindow, memoWarm.Coverage.TasksInWindow);
            Assert.All(cold.Tasks, row => Assert.Equal(10_500, row.EpisodeTokens));
            Assert.Contains(cold.Tasks, row => row.ReviewAttempts > 0);
            Assert.Equal(2100, cold.Coverage.TasksInProject);
            Assert.Equal(250, cold.Coverage.TasksInWindow);
            Assert.Equal(cold.Coverage.TasksInWindow, indexWarmCold.Coverage.TasksInWindow);
            Assert.Equal(cold.Coverage.TasksInWindow, warm.Coverage.TasksInWindow);
            var line = $"Synthetic store: 2,100 tasks, 250 completed in 7d, 40 ledger rows/task; 4,200 token calls from the cached token snapshot; 2,100 live review projections; 2,100 gate logs; 64 batch-store files. "
                + $"Cold including index {coldMs:F1} ms; cold with warm index {indexWarmColdMs:F1} ms; warm {warmMs:F1} ms; uncached project build with warm task/evidence memos {memoWarmMs:F1} ms.";
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
        void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, json));
        }
    }
}
