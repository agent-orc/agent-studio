using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SummaryGenerationValidationTests : IDisposable
{
    private const string ValidSummary = """
        # Status

        - Result: Success
        - Case: bugfix
        - Duration: 1 min

        ## Overview
        - Problem: Malformed model output replaced the task Result.
        - Solution: Validate the protocol before publishing, verified by regression tests.

        ## What Was Done
        - Added structural validation and a bounded retry.

        ## Open Items
        None.
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "summary-validation-" + Guid.NewGuid().ToString("N"));
    private readonly PipelineExecutionLog _pipeline = new(NullLogger<PipelineExecutionLog>.Instance);
    private static readonly TerminalRunOutcome CompletedOutcome = new(
        TerminalRunOutcomeKinds.Success, "Success", ShouldMoveToReview: true,
        ShouldShowFailureToast: false, Reason: "agent emitted TASK_DONE");

    [Fact]
    public async Task Greeting_is_rejected_without_overwriting_previous_result()
    {
        var (service, task, oneShot) = Arrange("Hello! How can I help you today?");

        await service.GenerateAsync(task, CompletedOutcome);

        Assert.Equal("Previous Result", File.ReadAllText(Path.Combine(_root, "status.md")));
        Assert.Equal(TaskSummaryStatus.Failed, service.GetState(task.TaskKey)!.Status);
        Assert.Contains("# Status", service.GetState(task.TaskKey)!.ErrorMessage);
        Assert.Equal(2, oneShot.Requests.Count);
        var step = SummaryStep();
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Contains("# Status", step.Reason);
        Assert.Contains("Attempt 1:", step.VerdictSummary);
        Assert.Contains("Attempt 2:", step.VerdictSummary);
        Assert.Equal(new[] { false, false }, UsageOutcomes());
    }

    [Fact]
    public async Task Well_formed_summary_is_accepted_without_retry()
    {
        var (service, task, oneShot) = Arrange(ValidSummary);

        await service.GenerateAsync(task, CompletedOutcome);

        Assert.Equal(ValidSummary, ReadSummary());
        Assert.Equal(TaskSummaryStatus.Ready, service.GetState(task.TaskKey)!.Status);
        Assert.Single(oneShot.Requests);
        Assert.Equal(PipelineStepStatus.Passed, SummaryStep().Status);
        Assert.Equal(new[] { true }, UsageOutcomes());
    }

    [Fact]
    public async Task Invalid_output_retries_once_with_identical_inputs_and_accepts_valid_retry()
    {
        var (service, task, oneShot) = Arrange("Hello!", ValidSummary);
        oneShot.BeforeReply = call =>
        {
            if (call != 2) return;
            Assert.Equal("Previous Result", File.ReadAllText(Path.Combine(_root, "status.md")));
            Assert.Equal(PipelineStepStatus.Failed, SummaryStep().Status);
            Assert.Contains("# Status", SummaryStep().Reason);
        };

        await service.GenerateAsync(task, CompletedOutcome);

        Assert.Equal(ValidSummary, ReadSummary());
        Assert.Equal(TaskSummaryStatus.Ready, service.GetState(task.TaskKey)!.Status);
        Assert.Equal(2, oneShot.Requests.Count);
        Assert.Equal(oneShot.Requests[0], oneShot.Requests[1]);
        Assert.Equal(PipelineStepStatus.Passed, SummaryStep().Status);
        Assert.Contains("Attempt 1: Invalid summary protocol:", SummaryStep().VerdictSummary);
        Assert.Contains("Attempt 2: passed", SummaryStep().VerdictSummary);
        Assert.Equal(new[] { false, true }, UsageOutcomes());
    }

    [Fact]
    public async Task Finalization_does_not_multiply_exhausted_format_retries()
    {
        var (service, task, oneShot) = Arrange("Hello!");

        var result = await service.FinalizeAsync(task);

        Assert.Equal(TaskSummaryStatus.Degraded, result.Status);
        Assert.Equal(2, oneShot.Requests.Count);
        Assert.Equal("Previous Result", File.ReadAllText(Path.Combine(_root, "status.md")));
        Assert.Equal(PipelineStepStatus.Failed, SummaryStep().Status);
    }

    [Fact]
    public async Task Interim_summary_rejects_malformed_output_and_preserves_result()
    {
        var (service, task, oneShot) = Arrange("Hello!");

        var result = await service.GenerateInterimAsync(task);

        Assert.False(result.Ok);
        Assert.Contains("# Status", result.Error);
        Assert.Equal(2, oneShot.Requests.Count);
        Assert.Equal("Previous Result", File.ReadAllText(Path.Combine(_root, "status.md")));
        Assert.Empty(_pipeline.Read(_root)!.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_echo_or_fenced_output_is_rejected_before_publishing(bool echoPrompt)
    {
        var (service, task, oneShot) = Arrange("```markdown\n" + ValidSummary + "\n```");
        oneShot.EchoPrompt = echoPrompt;

        await service.GenerateAsync(task);

        Assert.Equal(TaskSummaryStatus.Failed, service.GetState(task.TaskKey)!.Status);
        Assert.Equal("Previous Result", File.ReadAllText(Path.Combine(_root, "status.md")));
        Assert.Equal(2, oneShot.Requests.Count);
        Assert.Equal(PipelineStepStatus.Failed, SummaryStep().Status);
    }

    public static TheoryData<string> MalformedProtocols => new()
    {
        "",
        "Hello! How can I help?",
        ValidSummary.Replace("## Open Items\nNone.", "## Open Items\n- "),
        ValidSummary.Replace("## Open Items\nNone.", "## Open Items\n<!-- empty -->"),
        ValidSummary.Replace("## What Was Done\n- Added structural validation and a bounded retry.", "## What Was Done"),
        ValidSummary.Replace("- Problem: Malformed model output replaced the task Result.", "- Problem:"),
        ValidSummary.Replace("- Solution: Validate the protocol before publishing, verified by regression tests.", "- Solution: <one sentence naming what was done>"),
        ValidSummary.Replace("## Open Items", "## Missing Section"),
        ValidSummary.Replace("- Case: bugfix", "- Case: made-up"),
        ValidSummary.Replace("- Result: Success", "- Result: <Success|Failed|NoOp|Blocked|NeedsInput|Partial>"),
        ValidSummary.Replace("- Duration: 1 min", "- Duration:"),
        ValidSummary + "\n# Status\n",
        ValidSummary + "\n## Open Items\nNone.",
        ValidSummary + "\n{{log}}",
    };

    [Theory]
    [MemberData(nameof(MalformedProtocols))]
    public void Structural_validation_rejects_missing_empty_or_echoed_protocol(string markdown)
        => Assert.StartsWith(SummaryProtocolValidation.ErrorPrefix, SummaryProtocolValidation.Validate(markdown));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Structural_validation_accepts_complete_protocol_in_both_line_endings(string newline)
        => Assert.Null(SummaryProtocolValidation.Validate(ValidSummary.Replace("\n", newline)));

    private string ReadSummary() => File.ReadAllText(Path.Combine(_root, "status.md")).Replace("\r\n", "\n").Trim();

    private PipelineStepExecution SummaryStep() => Assert.Single(_pipeline.Read(_root)!.Steps, step => step.StepId == "summary");

    private bool[] UsageOutcomes() => File.ReadAllLines(Path.Combine(_root, AdHocUsageRecorder.LogFileName))
        .Select(line => System.Text.Json.JsonSerializer.Deserialize<AdHocUsageRecord>(line,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Ok).ToArray();

    private (SummaryGenerationService Service, TaskInfo Task, ScriptedOneShot OneShot) Arrange(params string[] replies)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "cli-output.log"), "Fixed summary validation.\n[assistant/final] [[TASK_DONE]]\n");
        File.WriteAllText(Path.Combine(_root, "status.md"), "Previous Result");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        _pipeline.Begin(_root, new TaskPipeline { Id = "summary-test" }, "test", "summary-validation");
        var oneShot = new ScriptedOneShot(replies);
        var service = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration,
            new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
            usage: new AdHocUsageRecorder(NullLogger<AdHocUsageRecorder>.Instance, configuration),
            oneShotRegistry: new CliOneShotRegistry([oneShot]),
            pipelineLog: _pipeline);
        var task = new TaskInfo
        {
            Id = "summary-validation", TaskKey = "TEST-1", Title = "Validate Result",
            State = TaskStates.Progress, FolderPath = _root, WatchPath = _root, ProjectName = "test",
        };
        return (service, task, oneShot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedOneShot(string[] replies) : ICliOneShot
    {
        public string CliType => CliTypes.Claude;
        public List<CliOneShotRequest> Requests { get; } = [];
        public Action<int>? BeforeReply { get; set; }
        public bool EchoPrompt { get; set; }

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var reply = EchoPrompt ? request.Prompt : replies[Math.Min(Requests.Count, replies.Length - 1)];
            Requests.Add(request);
            BeforeReply?.Invoke(Requests.Count);
            return Task.FromResult(new CliOneShotResult(
                true, 0, reply, "", TimeSpan.FromMilliseconds(1), reply, null, null,
                new AgentMessageLatency(RequestedAt: DateTime.UtcNow), null));
        }
    }
}
