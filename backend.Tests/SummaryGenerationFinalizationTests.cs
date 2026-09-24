using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SummaryGenerationFinalizationTests
{
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
                    ["TaskRepository"] = root,
                })
                .Build();
            var oneShot = new SuccessfulSummaryOneShot();
            var usageRecorder = new AdHocUsageRecorder(
                NullLogger<AdHocUsageRecorder>.Instance, configuration);
            var pipelineLog = new PipelineExecutionLog(
                NullLogger<PipelineExecutionLog>.Instance);
            var service = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                configuration,
                new RuntimePromptService(
                    configuration,
                    NullLogger<RuntimePromptService>.Instance),
                usage: usageRecorder,
                oneShotRegistry: new CliOneShotRegistry([oneShot]),
                pipelineLog: pipelineLog);
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
            await File.WriteAllTextAsync(Path.Combine(root, "prompt.md"),
                "Deliver local summary parity with one verified acceptance criterion.");
            pipelineLog.Begin(root, PipelineCatalogue.Standard, task.ProjectName, task.Id);

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
            await service.GenerateAsync(task);
            Assert.Equal(2, oneShot.Calls);
            var usageRows = usageRecorder.ReadAll();
            Assert.Equal(2, usageRows.Count);
            var usage = usageRows[0];
            Assert.Equal("LOCAL-1", usage.TaskKey);
            Assert.Equal(1, usage.RunNumber);
            Assert.Equal(CliTypes.Codex, usage.CliType);
            Assert.Equal("medium", usage.ThinkingLevel);
            Assert.True(usage.EstimatedCostUsd > 0);
            var summaryStep = Assert.Single(pipelineLog.Read(root)!.Steps,
                step => step.StepId == PipelineCatalogue.SummaryStepId);
            Assert.Equal(PipelineStepStatus.Passed, summaryStep.Status);
            Assert.Equal(2, summaryStep.InvocationCount);
            Assert.Equal(2_500, summaryStep.InputTokens);
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
        public string CliType => CliTypes.Codex;

        public int Calls { get; private set; }

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(CliTypes.Codex, request.CliType);
            Assert.Equal(ModelIds.Gpt56Luna, request.Model);
            Assert.Equal("medium", request.ThinkingLevel);
            const string markdown = """
                # Status

                - Result: Success
                - Case: feature

                ## Overview

                - Local completion remains unchanged.

                ## What Was Done

                - The application generated this Result from the completed core run.

                ## Open Items

                - None.
                """;
            var now = DateTime.UtcNow;
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: markdown,
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(1),
                ParsedText: markdown,
                Usage: new OrchestratorTokenUsage
                {
                    Model = ModelIds.Gpt56Luna,
                    InputTokens = 1_250,
                    OutputTokens = 220,
                    CacheReadTokens = 4_000,
                    CacheCreationTokens = 500,
                },
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: now,
                    CompletedAt: now.AddMilliseconds(1),
                    TotalMs: 1),
                Error: null));
        }
    }
}
