using System.Collections.Concurrent;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3424 (AGT-3400 PK7, option X-B): Remote Review runs its tool commands
/// serially and then its agent aspects with at most four in flight. The fake
/// aspects run on a <see cref="FakeTimeProvider"/>, so "four aspects of 10 s"
/// cost 10 s of attempt time when they overlap and 40 s when they do not.
/// </summary>
public sealed class RemoteReviewAspectParallelismTests : IDisposable
{
    private const string SubjectRef = "refs/heads/task/aspects";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-02T08:00:00Z"));
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "review-aspect-parallel-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;

    public RemoteReviewAspectParallelismTests()
    {
        _origin = Path.Combine(_root, "origin.git");
    }

    [Fact]
    public async Task Four_ten_second_aspects_finish_in_under_twenty_seconds_with_verdicts_in_plan_order()
    {
        var runner = new FakeAspectRunner(_time);
        var registeredTimers = new ConcurrentQueue<string>();
        runner.Work = (command, ct) =>
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(10), _time, ct);
            registeredTimers.Enqueue(command.StepId);
            return delay;
        };
        var workspace = await PreparedAsync("attempt-four-aspects", Aspects(4), runner);
        var startedAt = _time.GetUtcNow();

        var attempt = workspace.ExecutePlanAsync(default);
        await runner.InFlightReached(4).WaitAsync(Patience);
        Assert.Equal(4, registeredTimers.Count);
        _time.Advance(TimeSpan.FromSeconds(10));
        var evidence = await attempt.WaitAsync(Patience);

        Assert.True(_time.GetUtcNow() - startedAt < TimeSpan.FromSeconds(20));
        Assert.Equal(4, runner.MaxInFlight);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], evidence.Verdicts.Select(item => item.Aspect));
        Assert.All(evidence.Verdicts, verdict => Assert.Equal("pass", verdict.Status));
    }

    [Fact]
    public async Task Aspects_that_finish_in_reverse_are_recorded_and_checkpointed_one_at_a_time()
    {
        var runner = new FakeAspectRunner(_time);
        var gates = GatesFor(4);
        runner.Work = (command, ct) => gates[command.StepId].Task.WaitAsync(ct);
        var workspace = await PreparedAsync("attempt-reverse-order", Aspects(4), runner);
        var checkpoints = new List<IReadOnlyList<string>>();
        var writing = 0;
        var overlappingWrites = 0;

        var attempt = workspace.ExecutePlanAsync(default, resume: null, checkpoint: async (progress, _) =>
        {
            if (Interlocked.Increment(ref writing) > 1) Interlocked.Increment(ref overlappingWrites);
            await Task.Yield();
            checkpoints.Add(progress.Verdicts!.Select(item => item.Aspect).ToArray());
            Interlocked.Decrement(ref writing);
        });
        await runner.InFlightReached(4).WaitAsync(Patience);
        foreach (var step in new[] { "aspect-4", "aspect-3", "aspect-2", "aspect-1" })
            gates[step].SetResult();
        var evidence = await attempt.WaitAsync(Patience);

        Assert.Equal(0, overlappingWrites);
        Assert.Equal([1, 2, 3, 4], checkpoints.Select(item => item.Count));
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], evidence.Verdicts.Select(item => item.Aspect));
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], evidence.Commands.Select(item => item.StepId));
    }

    [Fact]
    public async Task Restart_after_a_crash_mid_run_reruns_only_the_aspects_without_a_checkpoint()
    {
        var runner = new FakeAspectRunner(_time);
        var gates = GatesFor(4);
        runner.Work = (command, ct) => gates[command.StepId].Task.WaitAsync(ct);
        var workspace = await PreparedAsync("attempt-crash-resume", Aspects(4), runner);
        ReviewExecutionCheckpoint? saved = null;
        var firstCheckpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var crashed = workspace.ExecutePlanAsync(default, resume: null, checkpoint: (progress, _) =>
        {
            saved = progress;
            firstCheckpoint.TrySetResult();
            // The worker dies right after it persisted the second aspect.
            if (progress.Verdicts!.Count == 2) throw new WorkerCrashedException();
            return Task.CompletedTask;
        });
        await runner.InFlightReached(4).WaitAsync(Patience);
        gates["aspect-3"].SetResult();
        await firstCheckpoint.Task.WaitAsync(Patience);
        gates["aspect-1"].SetResult();
        await Assert.ThrowsAsync<WorkerCrashedException>(() => crashed.WaitAsync(Patience));

        Assert.Equal(["aspect-3", "aspect-1"], saved!.Verdicts!.Select(item => item.Aspect));
        Assert.Equal(["aspect-2", "aspect-4"], runner.Cancelled.Order());

        runner.Work = (_, _) => Task.CompletedTask;
        var evidence = await workspace.ExecutePlanAsync(default, saved, checkpoint: null).WaitAsync(Patience);

        Assert.Equal(["aspect-2", "aspect-4"], runner.Started.Skip(4).Order());
        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], evidence.Verdicts.Select(item => item.Aspect));
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], evidence.Commands.Select(item => item.StepId));
    }

    [SkippableFact]
    public async Task Tool_commands_finish_before_the_first_aspect_starts()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The tool command is a POSIX shell script.");
        var marker = Path.Combine(_root, "tool-finished");
        var runner = new FakeAspectRunner(_time);
        var sawTool = new ConcurrentQueue<bool>();
        runner.Work = (_, _) =>
        {
            sawTool.Enqueue(File.Exists(marker));
            return Task.CompletedTask;
        };
        // The plan lists an aspect first on purpose: tools still run first.
        ReviewCommandDto[] plan =
        [
            Aspect(1),
            new("verify-tool", "build-tests", "sh", ["-c", $"sleep 1; printf done > '{marker}'"]),
            Aspect(2),
        ];
        var workspace = await PreparedAsync("attempt-tools-first", plan, runner);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal([true, true], sawTool);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(["verify-tool", "aspect-1", "aspect-2"], evidence.Commands.Select(item => item.StepId));
    }

    [SkippableFact]
    public async Task A_tool_infrastructure_failure_stops_the_attempt_before_any_aspect_starts()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The tool command is a POSIX shell script.");
        var runner = new FakeAspectRunner(_time);
        ReviewCommandDto[] plan =
        [
            new("verify-tool", "build-tests", "sh", ["-c", "exit 127"]),
            .. Aspects(2),
        ];
        var workspace = await PreparedAsync("attempt-tool-stops", plan, runner);

        var failure = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal("ToolUnavailable", failure.Classification);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Codex_input_too_large_refusal_is_review_infrastructure()
    {
        var runner = new FakeAspectRunner(_time)
        {
            Reply = _ => new ProcessResult(1, string.Empty,
                "Error: turn/start: Input exceeds the maximum length of 1048576 characters. " +
                "(code -32602), data: {\"input_error_code\":\"input_too_large\",\"actual_chars\":1071145}"),
        };
        var workspace = await PreparedAsync("attempt-input-refused", [Aspect(1)], runner);

        var failure = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal(ReviewCliRefusalPolicy.InputTooLarge, failure.Classification);
        Assert.Equal("ReviewInfra", failure.Evidence!.Outcome);
        Assert.Contains("input_too_large", failure.Message);
        Assert.Single(failure.Evidence.Commands);
    }

    [Fact]
    public async Task Delivery_sized_visual_prompt_is_bounded_before_the_cli_and_reports_omissions()
    {
        var runner = new FakeAspectRunner(_time);
        var aspect = Aspect(1) with
        {
            StepId = "aspect-visual-review",
            Aspect = "visual-review",
            Prompt = new string('x', 1_071_145),
        };
        var workspace = await PreparedAsync("attempt-large-visual", [aspect], runner);

        var evidence = await workspace.ExecutePlanAsync(default);

        var prompt = Assert.Single(runner.Prompts);
        Assert.True(prompt.Length <= ReviewAspectInputBudget.MaximumCharacters);
        Assert.Contains("Authored context:", prompt);
        var report = Assert.Single(evidence.Artifacts, artifact =>
            artifact.Name == "review-input-budget-aspect-visual-review.txt");
        Assert.Contains("Authored context:", System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(report.ContentBase64!)));
    }

    [Fact]
    public async Task An_aspect_infrastructure_failure_cancels_the_running_aspects_and_reports_all_evidence()
    {
        var runner = new FakeAspectRunner(_time);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Work = (command, ct) => command.StepId switch
        {
            "aspect-1" => finishFirst.Task,
            "aspect-2" => failSecond.Task,
            _ => Task.Delay(Timeout.InfiniteTimeSpan, _time, ct),
        };
        runner.Reply = command => command.StepId == "aspect-2"
            ? new ProcessResult(124, string.Empty, "killed on its budget")
            : null;
        var workspace = await PreparedAsync("attempt-aspect-timeout", Aspects(4), runner);
        var firstRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempt = workspace.ExecutePlanAsync(default, resume: null, checkpoint: (_, _) =>
        {
            firstRecorded.TrySetResult();
            return Task.CompletedTask;
        });
        await runner.InFlightReached(4).WaitAsync(Patience);
        finishFirst.SetResult();
        await firstRecorded.Task.WaitAsync(Patience);
        failSecond.SetResult();
        var failure = await Assert.ThrowsAsync<ReviewInfrastructureException>(() => attempt.WaitAsync(Patience));

        Assert.Equal("AspectTimeout", failure.Classification);
        Assert.Equal(["aspect-3", "aspect-4"], runner.Cancelled.Order());
        Assert.Equal(["aspect-1", "aspect-2"], failure.Evidence!.Commands.Select(item => item.StepId));
    }

    [Fact]
    public async Task A_blocking_verdict_does_not_stop_the_other_aspects()
    {
        var runner = new FakeAspectRunner(_time);
        runner.Reply = command => command.StepId == "aspect-1"
            ? new ProcessResult(
                0,
                "[[ASPECT_VERDICT: status=block; summary=Aspect one refuses the change.; "
                + "evidence_checked=feature.txt; missing=a test]]",
                string.Empty)
            : null;
        var workspace = await PreparedAsync("attempt-block-continues", Aspects(4), runner);

        var evidence = await workspace.ExecutePlanAsync(default).WaitAsync(Patience);

        Assert.Equal("ProductFailure", evidence.Outcome);
        Assert.Empty(runner.Cancelled);
        Assert.Equal(["block", "pass", "pass", "pass"], evidence.Verdicts.Select(item => item.Status));
    }

    [Fact]
    public async Task Six_aspects_never_run_more_than_four_at_a_time()
    {
        var runner = new FakeAspectRunner(_time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Work = (_, ct) => release.Task.WaitAsync(ct);
        var workspace = await PreparedAsync("attempt-cap-four", Aspects(6), runner);

        var attempt = workspace.ExecutePlanAsync(default);
        await runner.InFlightReached(4).WaitAsync(Patience);
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], runner.Started.Order());
        release.SetResult();
        var evidence = await attempt.WaitAsync(Patience);

        Assert.Equal(4, runner.MaxInFlight);
        Assert.Equal(6, evidence.Verdicts.Count);
    }

    [Fact]
    public async Task Parallelism_one_runs_the_aspects_serially_in_plan_order()
    {
        var runner = new FakeAspectRunner(_time);
        var workspace = await PreparedAsync("attempt-serial", Aspects(4), runner, aspectParallelism: 1);

        var evidence = await workspace.ExecutePlanAsync(default).WaitAsync(Patience);

        Assert.Equal(1, runner.MaxInFlight);
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], runner.Started);
        Assert.Equal(4, evidence.Verdicts.Count);
    }

    [Fact]
    public async Task Claude_shared_prefix_is_written_by_first_aspect_before_siblings_start()
    {
        var runner = new FakeAspectRunner(_time);
        runner.Usage = command => command.StepId == "aspect-1"
            ? new RemoteAgentUsage("claude-opus-5", 0, 0, 0, 1000, false) : null;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Work = (command, ct) => command.StepId == "aspect-1"
            ? releaseFirst.Task.WaitAsync(ct) : releaseRest.Task.WaitAsync(ct);
        var plan = Aspects(4).Select(command => command with
        {
            FileName = "claude",
            CliType = "claude",
            Model = "claude-opus-5",
            Prompt = "Shared brief\n<!-- remote-review-shared-material-v1 -->\nAspect " + command.Aspect,
        }).ToArray();
        var workspace = await PreparedAsync("attempt-cache-prefix", plan, runner);

        var attempt = workspace.ExecutePlanAsync(default);
        await runner.InFlightReached(1).WaitAsync(Patience);
        Assert.Equal(["aspect-1"], runner.Started);
        releaseFirst.SetResult();
        await runner.InFlightReached(3).WaitAsync(Patience);
        Assert.Equal(3, runner.MaxInFlight);
        releaseRest.SetResult();
        var evidence = await attempt.WaitAsync(Patience);

        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(4, evidence.Verdicts.Count);
        var prefixes = runner.Prompts.Select(prompt => prompt[..prompt.IndexOf("Aspect aspect-", StringComparison.Ordinal)]).ToArray();
        Assert.All(prefixes, prefix => Assert.Equal(prefixes[0], prefix));
        Assert.Contains("Shared brief", prefixes[0]);
        Assert.Contains("feature.txt", prefixes[0]);
        Assert.DoesNotContain("remote-review-shared-material-v1", prefixes[0]);
    }

    [Fact]
    public async Task Claude_aspects_remain_serial_when_first_call_reports_no_cache_usage()
    {
        var runner = new FakeAspectRunner(_time);
        var plan = Aspects(4).Select(command => command with
        {
            CliType = "claude",
            Model = "claude-opus-5",
            Prompt = "Shared brief\n<!-- remote-review-shared-material-v1 -->\nAspect " + command.Aspect,
        }).ToArray();
        var workspace = await PreparedAsync("attempt-cache-unconfirmed", plan, runner);

        var evidence = await workspace.ExecutePlanAsync(default).WaitAsync(Patience);

        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(1, runner.MaxInFlight);
        Assert.Equal(["aspect-1", "aspect-2", "aspect-3", "aspect-4"], runner.Started);
    }

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(16, 4)]
    public void Configured_parallelism_is_clamped_between_serial_and_four(int configured, int expected)
        => Assert.Equal(expected, ReviewAspectFanOut.Normalize(configured));

    private static Dictionary<string, TaskCompletionSource> GatesFor(int count)
        => Enumerable.Range(1, count).ToDictionary(
            index => $"aspect-{index}",
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private static ReviewCommandDto[] Aspects(int count)
        => Enumerable.Range(1, count).Select(Aspect).ToArray();

    private static ReviewCommandDto Aspect(int index)
        => new(
            $"aspect-{index}",
            $"aspect-{index}",
            "codex",
            [],
            TimeoutSeconds: 30,
            ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: $"Grade aspect {index}.",
            CliType: CliSelection.CodexCli,
            Model: "gpt-5.4-mini",
            ThinkingLevel: "high");

    private async Task<RemoteReviewWorkspace> PreparedAsync(
        string attemptId,
        IReadOnlyList<ReviewCommandDto> plan,
        IReviewAgentCommandRunner runner,
        int aspectParallelism = ReviewAspectFanOut.MaximumParallelism)
    {
        var sha = await SeedOriginAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            "run-" + attemptId,
            TaskServerClient.RepositoryIdentity(_origin)!,
            _origin,
            sha,
            SubjectRef,
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(
                plan,
                plan.Select(command => command.Aspect).ToArray(),
                IntegrationRef: "refs/heads/main"),
            now);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            1,
            now,
            now.AddMinutes(2),
            "active",
            $"review-{attemptId}-f1",
            25400);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "review"),
            BaseBranch = "main",
            ReviewAspectParallelism = aspectParallelism,
        };
        var workspace = new RemoteReviewWorkspace(options, subject, lease, _ => { }, runner);
        await workspace.PrepareAsync(null!, default);
        return workspace;
    }

    /// <summary>A main branch and a subject branch that adds <c>feature.txt</c>.</summary>
    private async Task<string> SeedOriginAsync()
    {
        if (Directory.Exists(_origin))
            return (await GitAsync(_origin, "rev-parse", SubjectRef)).StdOut.Trim();
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "product.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        await GitAsync(seed, "checkout", "-b", "task/aspects");
        await File.WriteAllTextAsync(Path.Combine(seed, "feature.txt"), "delivered feature");
        await GitAsync(seed, "add", "feature.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "subject");
        await GitAsync(seed, "push", "origin", "task/aspects");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose()
    {
        ResilientDirectory.TryDelete(_root);
    }

    private sealed class WorkerCrashedException : Exception;

    /// <summary>
    /// Records which aspects started or were cancelled, and the
    /// highest number in flight. By default an aspect passes at once.
    /// </summary>
    private sealed class FakeAspectRunner(FakeTimeProvider time) : IReviewAgentCommandRunner
    {
        private readonly ConcurrentQueue<(int Count, TaskCompletionSource Signal)> _inFlightWaiters = new();
        private int _inFlight;
        private int _maxInFlight;

        public Func<ReviewCommandDto, CancellationToken, Task> Work { get; set; } = (_, _) => Task.CompletedTask;
        public Func<ReviewCommandDto, ProcessResult?> Reply { get; set; } = _ => null;
        public Func<ReviewCommandDto, RemoteAgentUsage?> Usage { get; set; } = _ => null;
        public ConcurrentQueue<string> Started { get; } = new();
        public ConcurrentQueue<string> Prompts { get; } = new();
        public ConcurrentQueue<string> Cancelled { get; } = new();
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public Task InFlightReached(int count) => Await(_inFlightWaiters, count, () => Volatile.Read(ref _inFlight));

        public string PlannedExecutable(ReviewCommandDto command) => command.FileName;

        public async Task<CommandExecution> RunAsync(ReviewCommandDto command, CancellationToken ct)
        {
            var startedAt = time.GetUtcNow().UtcDateTime;
            Started.Enqueue(command.StepId);
            Prompts.Enqueue(command.Prompt ?? string.Empty);
            var counted = false;
            try
            {
                // Count the aspect only after Work has registered any fake timer.
                var work = Work(command, ct);
                var inFlight = Interlocked.Increment(ref _inFlight);
                counted = true;
                var max = Volatile.Read(ref _maxInFlight);
                while (inFlight > max && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
                    max = Volatile.Read(ref _maxInFlight);
                Signal(_inFlightWaiters, inFlight);
                await work;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.Enqueue(command.StepId);
                throw;
            }
            finally
            {
                if (counted) Interlocked.Decrement(ref _inFlight);
            }
            var process = Reply(command) ?? new ProcessResult(
                0,
                $"[[ASPECT_VERDICT: status=pass; summary={command.Aspect} passed.; "
                + "evidence_checked=feature.txt; missing=none]]",
                string.Empty);
            return new CommandExecution(process, startedAt, time.GetUtcNow().UtcDateTime, null, Usage(command));
        }

        private static Task Await(
            ConcurrentQueue<(int Count, TaskCompletionSource Signal)> waiters,
            int count,
            Func<int> current)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Enqueue((count, signal));
            if (current() >= count) signal.TrySetResult();
            return signal.Task;
        }

        private static void Signal(ConcurrentQueue<(int Count, TaskCompletionSource Signal)> waiters, int reached)
        {
            foreach (var (count, signal) in waiters)
                if (reached >= count) signal.TrySetResult();
        }
    }
}
