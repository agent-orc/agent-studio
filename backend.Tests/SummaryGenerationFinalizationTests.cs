using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SummaryGenerationFinalizationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_only_result_uses_agent_status_or_final_message_without_a_call(bool hasStatusFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "report-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "results"));
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "cli-output.log"),
            "[assistant/final] Compared both ledgers and found that receipt coverage differs. [[TASK_DONE]]\n");
        if (hasStatusFile)
            await File.WriteAllTextAsync(Path.Combine(root, "results", "status.md"),
                "# Research status\n\nCompared two ledgers for the same evidence window.\n"
                + "- The selector and usage receipts cover different calls.\n"
                + "- Partial receipt coverage prevents a complete reconciliation.\n");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime"),
                ["SummaryGeneration:FinalizationMaxAttempts"] = "1",
            }).Build();
            var oneShot = new WaitingOneShot();
            var service = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance,
                config, new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]));
            var task = new TaskInfo
            {
                Id = "report-summary", TaskKey = "REPORT-1", Mode = TaskModes.Research,
                Title = "Report summary fallback", TaskType = "research", State = TaskStates.AutoReview,
                FolderPath = root, WatchPath = root, ProjectName = "local",
            };

            var result = await service.FinalizeAsync(task);

            Assert.True(result.Generated);
            Assert.Equal(1, result.Attempt);
            var status = await File.ReadAllTextAsync(Path.Combine(root, "status.md"));
            Assert.Contains(hasStatusFile ? "The selector and usage receipts cover different calls."
                : "Compared both ledgers and found that receipt coverage differs.", status);
            if (hasStatusFile) Assert.Contains("Partial receipt coverage prevents a complete reconciliation.", status);
            Assert.Null(SummaryProtocolValidation.Validate(status));
            Assert.Equal(0, oneShot.Calls);
            Assert.Equal(SummaryAssessmentSources.Policy, SummaryAssessmentStore.ReadFor(root, status)?.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Missing_model_host_stops_inline_attempts_and_preserves_waiting_reason()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "cli-output.log"),
            "[assistant/final] [[TASK_DONE]]\n");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(Directory.GetCurrentDirectory(),
                    "prompts", "runtime"),
                ["SummaryGeneration:FinalizationMaxAttempts"] = "3",
            }).Build();
            var oneShot = new WaitingOneShot();
            var service = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance,
                config, new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]));
            var task = new TaskInfo
            {
                Id = "waiting-summary", TaskKey = "WAIT-1", Title = "Wait for a model host",
                TaskType = "feature", Mode = TaskModes.Coding, State = TaskStates.Progress,
                FolderPath = root, WatchPath = root, ProjectName = "local",
            };
            var outcome = await service.FinalizeAsync(task);
            Assert.False(outcome.Generated);
            Assert.True(outcome.Deferred);
            Assert.Equal(0, outcome.Attempt);
            Assert.Equal("waiting for a host with model-acceptance:claude:claude-sonnet-5", outcome.Error);
            Assert.Equal(1, oneShot.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Explicit_blocker_keeps_the_reason_and_completed_lane_contradiction()
    {
        var root = Path.Combine(Path.GetTempPath(), "blocked-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TaskPaths.LogsDir(root));
        try
        {
            await File.WriteAllTextAsync(TaskPaths.CliOutputLog(root),
                "[12:00:00.000] [stdout] [[TASK_BLOCKED: Waiting for the upstream review receipt.]]\n");
            var configuration = new ConfigurationBuilder().Build();
            var oneShot = new WaitingOneShot();
            var service = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance,
                configuration, new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]));
            var task = new TaskInfo
            {
                Id = "blocked", TaskKey = "BLOCKED-1", Title = "Verify delivery", Mode = TaskModes.Coding,
                State = TaskStates.Completed, FolderPath = root, WatchPath = root, ProjectName = "test",
            };

            Assert.True((await service.FinalizeAsync(task)).Generated);
            Assert.Equal(0, oneShot.Calls);
            var status = await File.ReadAllTextAsync(Path.Combine(root, "status.md"));
            Assert.Null(SummaryProtocolValidation.Validate(status));
            Assert.Contains("Waiting for the upstream review receipt.", status);
            var assessment = Assert.IsType<SummaryAssessment>(SummaryAssessmentStore.ReadFor(root, status));
            Assert.Contains(assessment.Inconsistencies, item =>
                item.Kind == SummaryInconsistencyKinds.FailureVersusCompleted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class WaitingOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Claude;
        public int Calls { get; private set; }
        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(CliOneShotResult.SpawnFailure(
                "waiting for a host with model-acceptance:claude:claude-sonnet-5",
                DateTime.UnixEpoch, DateTime.UnixEpoch));
        }
    }

    [Fact]
    public async Task Local_successful_summary_remains_ready_on_first_attempt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "summary-finalization-tests",
            Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "cli-output.log"),
            "[assistant/analysis] Local core run completed.\n[assistant/final] [[TASK_DONE]]\n");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PromptTemplates:RuntimePath"] = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "prompts",
                        "runtime"),
                    ["SummaryGeneration:FinalizationMaxAttempts"] = "3",
                })
                .Build();
            var oneShot = new SuccessfulSummaryOneShot();
            var service = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                configuration,
                new RuntimePromptService(
                    configuration,
                    NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]));
            var task = new TaskInfo
            {
                Id = "local-summary",
                TaskKey = "LOCAL-1",
                Title = "Local summary parity",
                TaskType = "feature",
                Mode = TaskModes.Coding,
                State = TaskStates.Progress,
                FolderPath = root,
                WatchPath = root,
                ProjectName = "local",
            };

            var result = await service.FinalizeAsync(
                task,
                new TerminalRunOutcome(
                    TerminalRunOutcomeKinds.Success,
                    "Success",
                    ShouldMoveToReview: true,
                    ShouldShowFailureToast: false,
                    Reason: "agent emitted TASK_DONE"));

            Assert.True(result.Generated);
            Assert.Equal(TaskSummaryStatus.Ready, result.Status);
            Assert.Equal(1, result.Attempt);
            Assert.Equal(3, result.MaxAttempts);
            Assert.Equal(1, oneShot.Calls);
            var markdown = await File.ReadAllTextAsync(Path.Combine(root, "status.md"));
            Assert.Contains("- Result: Success", markdown, StringComparison.Ordinal);
            Assert.Contains("Local completion remains unchanged.", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(
                TaskTransitionService.ResultScaffoldMarker,
                markdown,
                StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of an isolated test folder.
            }
        }
    }

    private sealed class SuccessfulSummaryOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Claude;

        public int Calls { get; private set; }

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            const string markdown = """
                # Status

                - Result: Success
                - Case: feature
                - Duration: unknown

                ## Overview

                - Problem: Local completion must preserve its Result.
                - Solution: Local completion remains unchanged.

                ## What Was Done

                - The application generated this Result from the completed core run.

                ## Open Items

                - None.
                """;
            var now = DateTime.UnixEpoch;
            return Task.FromResult(new CliOneShotResult(
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
                Error: null));
        }
    }
}
