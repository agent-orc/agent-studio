using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2850 regression. 16.09.2026: a finished remote run uploaded its result,
/// the summary one-shot sat in the host load throttle until the upload
/// request was aborted, and the resulting <c>TaskCanceledException</c> turned a
/// delivered run into a requeued one. The delivered result must survive a
/// cancelled summary: the runner is acknowledged, the card carries a pending
/// summary, and a later retry makes the summary real - even after the card has
/// moved on to its review lane.
/// </summary>
public sealed class RemoteResultFinalizationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _progressFolder;

    public RemoteResultFinalizationServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "remote-result-finalization-tests",
            Guid.NewGuid().ToString("N"));
        _progressFolder = Path.Combine(_root, TaskStates.Progress, "AGT-2850");
        Directory.CreateDirectory(Path.Combine(_progressFolder, "logs"));
        File.WriteAllText(
            Path.Combine(_progressFolder, "logs", "cli-output.log"),
            """
            [2026-09-16T13:40:00Z] [stdout] Implemented and verified.
            [2026-09-16T13:48:00Z] [stdout] [[TASK_DONE]]
            [2026-09-16T13:48:01Z] [stdout] [taskboard] claude CLI exited: status=completed, exitCode=0, duration=1180.0s

            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* best-effort cleanup of an isolated test folder */ }
    }

    [Fact]
    public async Task Cancelled_summary_still_acknowledges_the_result_and_a_later_retry_succeeds()
    {
        var oneShot = new ScriptedSummaryOneShot(
            SummaryAttempt.Throws(new TaskCanceledException("A task was canceled.")),
            SummaryAttempt.Throws(new TaskCanceledException("A task was canceled.")),
            SummaryAttempt.Throws(new TaskCanceledException("A task was canceled.")),
            SummaryAttempt.Succeeds());
        var scanner = new MutableScanner(Task(_progressFolder, TaskStates.Progress));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var service = Service(oneShot, scanner, timeline, new StubLoadGate(throttled: false));

        var plan = await service.FinalizeForAcknowledgementAsync(scanner.Single);

        // The delivered result is acknowledged; only the summary is owed.
        Assert.Equal(ResultSummaryDelivery.Degraded, plan.Delivery);
        Assert.False(plan.Generated);
        Assert.False(plan.CommitStatusDocument);
        Assert.True(plan.ScheduleRetry);
        Assert.StartsWith("degraded:", plan.ResultDocumentStatus);
        Assert.Contains("canceled", plan.ResultDocumentStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_progressFolder, "status.md")));

        var pending = Assert.Single(service.Pending);
        Assert.Equal("AGT-2850", pending.TaskKey);
        var pendingRow = Assert.Single(
            timeline.ReadAll(_progressFolder),
            row => row.Kind == TimelineEventKinds.ResultSummaryPending);
        Assert.StartsWith("Summary pending (degraded:", pendingRow.Summary);

        // The run completed, so the card left Progress for 4-auto-review before
        // the retry came due. The retry has to find the card's CURRENT folder.
        var reviewFolder = MoveTo(TaskStates.AutoReview);
        scanner.Replace(Task(reviewFolder, TaskStates.AutoReview));

        var recovered = await service.RunDueRetriesAsync(DateTime.UtcNow.AddMinutes(10));

        Assert.Equal(1, recovered);
        Assert.Empty(service.Pending);
        Assert.Equal(4, oneShot.SummaryCalls);
        var status = await File.ReadAllTextAsync(Path.Combine(reviewFolder, "status.md"));
        Assert.Contains("- Result: Success", status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            TaskTransitionService.ResultScaffoldMarker,
            status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throttled_host_defers_the_summary_without_spending_a_one_shot()
    {
        var oneShot = new ScriptedSummaryOneShot(SummaryAttempt.Succeeds());
        var scanner = new MutableScanner(Task(_progressFolder, TaskStates.Progress));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var gate = new StubLoadGate(throttled: true);
        var service = Service(oneShot, scanner, timeline, gate);

        var plan = await service.FinalizeForAcknowledgementAsync(scanner.Single);

        Assert.Equal(ResultSummaryDelivery.Pending, plan.Delivery);
        Assert.Equal("pending:load-throttle", plan.ResultDocumentStatus);
        Assert.True(plan.ScheduleRetry);
        Assert.Equal(0, oneShot.SummaryCalls);
        Assert.Single(service.Pending);
        Assert.Equal(
            "Summary pending (load throttle)",
            Assert.Single(
                timeline.ReadAll(_progressFolder),
                row => row.Kind == TimelineEventKinds.ResultSummaryPending).Summary);

        // Still saturated: every due pass re-defers without burning the budget,
        // so a long saturation window cannot abandon the owed summary.
        for (var pass = 1; pass <= RemoteResultFinalizationService.DefaultMaxRetries + 2; pass++)
        {
            Assert.Equal(0, await service.RunDueRetriesAsync(DateTime.UtcNow.AddMinutes(10 * pass)));
            Assert.Equal(0, oneShot.SummaryCalls);
            Assert.Equal(0, Assert.Single(service.Pending).Attempts);
        }

        gate.Throttled = false;
        Assert.Equal(1, await service.RunDueRetriesAsync(DateTime.UtcNow.AddHours(4)));
        Assert.Empty(service.Pending);
        Assert.Contains(
            "- Result: Success",
            await File.ReadAllTextAsync(Path.Combine(_progressFolder, "status.md")),
            StringComparison.Ordinal);
    }

    private string MoveTo(string state)
    {
        var target = Path.Combine(_root, state, "AGT-2850");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(_progressFolder, target);
        return target;
    }

    private static TaskInfo Task(string folderPath, string state) => new()
    {
        Id = "AGT-2850",
        TaskKey = "AGT-2850",
        Key = "AGT-2850",
        Title = "A degraded result summary must not lose a delivered run",
        TaskType = "bugfix",
        Mode = TaskModes.Coding,
        State = state,
        FolderPath = folderPath,
        WatchPath = Path.GetDirectoryName(Path.GetDirectoryName(folderPath))!,
        ProjectName = "agent-runner-01",
    };

    private static RemoteResultFinalizationService Service(
        ICliOneShot oneShot,
        ITaskScanner scanner,
        TimelineLog timeline,
        ILoadThrottleGate gate)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "prompts",
                    "runtime"),
                ["SummaryGeneration:FinalizationMaxAttempts"] = "3",
                ["SummaryGeneration:AcknowledgementBudgetSeconds"] = "30",
            })
            .Build();
        var summaries = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration,
            new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
            oneShotRegistry: new CliOneShotRegistry([oneShot]));
        return new RemoteResultFinalizationService(
            summaries,
            scanner,
            timeline,
            configuration,
            NullLogger<RemoteResultFinalizationService>.Instance,
            gate);
    }

    private sealed class MutableScanner(TaskInfo task) : ITaskScanner
    {
        private TaskInfo _task = task;

        public TaskInfo Single => _task;

        public void Replace(TaskInfo task) => _task = task;

        public List<TaskInfo> ScanAllJobs() => [_task];
    }

    private sealed class StubLoadGate(bool throttled) : ILoadThrottleGate
    {
        public bool Throttled { get; set; } = throttled;

        public LoadThrottleDecision Current => new(Throttled, Throttled ? 97 : 12, TimeSpan.Zero);

        public bool WasRecentlyActive => Throttled;

        public Task WaitUntilReadyAsync(string reason, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed record SummaryAttempt(Exception? Throw)
    {
        public static SummaryAttempt Throws(Exception exception) => new(exception);
        public static SummaryAttempt Succeeds() => new((Exception?)null);
    }

    /// <summary>
    /// Stands in for the project-routed summary one-shot. The load throttle surfaces as a
    /// <see cref="TaskCanceledException"/> out of the queued call, which is
    /// exactly the shape that lost the two runs on 16.09.2026.
    /// </summary>
    private sealed class ScriptedSummaryOneShot(params SummaryAttempt[] attempts) : ICliOneShot
    {
        private readonly Queue<SummaryAttempt> _attempts = new(attempts);

        // The summary pipeline now defaults to the bounded-output Codex route.
        // Register the fake on that route so this fixture continues to exercise
        // finalization behavior rather than the missing-route failure path.
        public string CliType => CliTypes.Codex;

        public int SummaryCalls { get; private set; }

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            if (!string.Equals(
                    request.Source,
                    AdHocUsageSources.SummaryGeneration,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected one-shot source '{request.Source}'.");
            }

            SummaryCalls++;
            var attempt = _attempts.Count > 0 ? _attempts.Dequeue() : SummaryAttempt.Succeeds();
            if (attempt.Throw is not null) throw attempt.Throw;

            const string markdown = """
                # Status

                - Result: Success
                - Case: bugfix

                ## Overview

                - The delivered remote result kept its acknowledgement.

                ## What Was Done

                - The retried summary produced this document.

                ## Open Items

                - None.
                """;
            var requestedAt = DateTime.UtcNow;
            var completedAt = requestedAt.AddMilliseconds(1);
            return System.Threading.Tasks.Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: markdown,
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(1),
                ParsedText: markdown,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: requestedAt,
                    CompletedAt: completedAt,
                    TotalMs: 1),
                Error: null));
        }
    }
}
