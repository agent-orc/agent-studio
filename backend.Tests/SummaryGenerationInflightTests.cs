

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the summary-state projection and per-task coordination rules. Normal
/// duplicates share the active call, while terminal completion queues one
/// refresh so final integration evidence cannot be silently dropped.
/// </summary>
public class SummaryGenerationInflightTests
{
    private static readonly DateTime Now = new(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoPriorState_NotInflight()
    {
        Assert.False(SummaryGenerationService.IsInflight(null, Now, 90));
    }

    [Fact]
    public void Generating_StartedJustNow_IsInflight()
    {
        var prev = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Generating,
            StartedAt = Now.AddSeconds(-5)
        };
        Assert.True(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Generating_StartedLongerAgoThanTimeout_NotInflight()
    {
        var prev = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Generating,
            StartedAt = Now.AddSeconds(-200)
        };
        // Treated as stuck so the user can recover by hitting Regenerate.
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Ready_NeverInflight()
    {
        var prev = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Ready,
            StartedAt = Now.AddSeconds(-1)
        };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Failed_NeverInflight()
    {
        var prev = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Failed,
            StartedAt = Now.AddSeconds(-1)
        };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Generating_WithoutStartedAt_NotInflight()
    {
        // Defensive: an upstream that forgot to set StartedAt must not
        // wedge the slot forever.
        var prev = new TaskSummaryState { Status = TaskSummaryStatus.Generating };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public async Task Terminal_regeneration_queues_one_refresh_after_an_inflight_summary()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "summary-terminal-regeneration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllTextAsync(Path.Combine(root, "prompt.md"),
            "Deliver a task-level Result that includes terminal integration evidence.");
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "cli-output.log"),
            "[assistant/final] Core work finished. [[TASK_DONE]]\n");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PromptTemplates:RuntimePath"] = Path.Combine(
                        Directory.GetCurrentDirectory(), "prompts", "runtime"),
                })
                .Build();
            var oneShot = new BlockingSummaryOneShot();
            var service = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                configuration,
                new RuntimePromptService(
                    configuration,
                    NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]));
            var task = new TaskInfo
            {
                Id = "terminal-refresh",
                TaskKey = "AGT-TERMINAL",
                Key = "AGT-TERMINAL",
                Title = "Terminal summary includes final evidence",
                TaskType = "bugfix",
                Mode = TaskModes.Coding,
                State = TaskStates.AutoReview,
                FolderPath = root,
                WatchPath = root,
                ProjectName = "summary-tests",
            };

            var initial = service.GenerateAsync(task);
            await oneShot.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.AppendAllTextAsync(Path.Combine(root, "logs", "cli-output.log"),
                "[taskboard] Terminal integration gate passed.\n");

            var terminal = service.RegenerateAfterCurrentAsync(task with
            {
                State = TaskStates.Completed,
            });
            var duplicateTerminal = service.RegenerateAfterCurrentAsync(task with
            {
                State = TaskStates.Completed,
            });
            oneShot.ReleaseFirstCall.TrySetResult(true);

            await Task.WhenAll(initial, terminal, duplicateTerminal)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, oneShot.Calls);
            Assert.Contains(
                "Terminal integration gate passed.",
                oneShot.Prompts[1],
                StringComparison.Ordinal);
            Assert.Contains(
                "Generation 2 includes terminal evidence.",
                await File.ReadAllTextAsync(Path.Combine(root, "status.md")),
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* Best-effort cleanup of an isolated test folder. */ }
        }
    }

    private sealed class BlockingSummaryOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Codex;

        public int Calls { get; private set; }
        public List<string> Prompts { get; } = [];
        public TaskCompletionSource<bool> FirstCallStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstCall { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            var call = ++Calls;
            Prompts.Add(request.Prompt);
            if (call == 1)
            {
                FirstCallStarted.TrySetResult(true);
                await ReleaseFirstCall.Task.WaitAsync(ct);
            }

            var markdown = $$"""
                # Status

                - Result: Success
                - Case: bugfix

                ## Overview

                - Generation {{call}} includes terminal evidence.

                ## What Was Done

                - Regenerated the Result from the latest evidence.

                ## Open Items

                - None.
                """;
            var now = DateTime.UtcNow;
            return new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: markdown,
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(1),
                ParsedText: markdown,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: now,
                    CompletedAt: now.AddMilliseconds(1),
                    TotalMs: 1),
                Error: null);
        }
    }
}
