using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Decision matrix for <see cref="CodeReviewGradeReuse"/> (AGT-3081): the
/// input key encoding and every condition under which an earlier grade of the
/// open attempt may stand for the current pass.
/// </summary>
public class CodeReviewGradeReuseTests
{
    private const string JobFolder = "/jobs/demo";
    private const string ReportFile = "code-review-grade-a.md";
    private const string Finding =
        "The review report was never committed; commit the report and its screenshots.";

    [Fact]
    public void InputKey_IsStableForIdenticalRequests()
    {
        Assert.Equal(
            CodeReviewGradeReuse.ComputeInputKey(Request()),
            CodeReviewGradeReuse.ComputeInputKey(Request()));
        Assert.Matches("^sha256:[0-9a-f]{64}$", CodeReviewGradeReuse.ComputeInputKey(Request()));
    }

    [Fact]
    public void InputKey_KeepsNullDistinctFromEmpty()
    {
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = null }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = string.Empty }));
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { ThinkingLevel = null }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { ThinkingLevel = string.Empty }));
    }

    [Fact]
    public void InputKey_LengthPrefixPreventsBoundaryCollisions()
    {
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Diff = "ab", Commit = "c" }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Diff = "a", Commit = "bc" }));
    }

    public static TheoryData<string> KeyedFields => new()
    {
        "diff", "commit", "taskBody", "resultsInventory", "cardMode", "cliType", "model", "thinkingLevel",
    };

    [Theory]
    [MemberData(nameof(KeyedFields))]
    public void InputKey_ChangesWithEveryKeyedField(string field)
    {
        var baseline = Request();
        var changed = field switch
        {
            "diff" => baseline with { Diff = baseline.Diff + "x" },
            "commit" => baseline with { Commit = "11111111 (HEAD)" },
            "taskBody" => baseline with { TaskBody = baseline.TaskBody + "x" },
            "resultsInventory" => baseline with { ResultsInventory = baseline.ResultsInventory + "x" },
            "cardMode" => baseline with { CardMode = baseline.CardMode + "x" },
            "cliType" => baseline with { CliType = "codex" },
            "model" => baseline with { Model = "other-model" },
            "thinkingLevel" => baseline with { ThinkingLevel = "high" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(baseline),
            CodeReviewGradeReuse.ComputeInputKey(changed));
    }

    [Fact]
    public void TryReuse_MatchingKey_RebuildsTheEarlierReport()
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request());

        var report = CodeReviewGradeReuse.TryReuse(Row(), key, Report(key), JobFolder);

        Assert.NotNull(report);
        Assert.Equal(ReportFile, report!.FileName);
        Assert.Equal(Path.Combine(JobFolder, ReportFile), report.FilePath);
        Assert.Equal(CodeReviewGrade.D, report.Grade);
        Assert.Equal(AspectStatus.Block, report.Status);
        Assert.Equal("code-review:grade-d", report.ConcernTagId);
        Assert.Equal("Misses the goal.", report.Summary);
        Assert.Equal("claude-opus-5-5", report.Model);
        Assert.Equal("claude", report.CliType);
        Assert.Equal("low", report.ThinkingLevel);
        Assert.Equal("10c8e370 (HEAD)", report.Commit);
        Assert.Equal(12_000, report.DurationMs);
        Assert.Equal(DateTime.UnixEpoch, report.StartedAt);
        Assert.Null(report.ExecutionError);
        Assert.Null(report.Usage);
        Assert.Equal(Finding, Assert.Single(report.Findings!));
    }

    [Fact]
    public void TryReuse_HeadPlaceholderCommit_MapsBackToNull()
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = null });

        var report = CodeReviewGradeReuse.TryReuse(Row(), key, Report(key, commit: "(HEAD)"), JobFolder);

        Assert.NotNull(report);
        Assert.Null(report!.Commit);
    }

    public static TheoryData<string> RejectedCases => new()
    {
        "no-checkpoint", "no-verdict", "unknown-verdict", "no-evidence-ref", "missing-report",
        "no-frontmatter", "legacy-no-key", "other-key", "grade-mismatch", "wrong-type",
    };

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public void TryReuse_GradesAfresh_WhenAnyConditionFails(string scenario)
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request());
        var row = Row();
        var markdown = Report(key);
        switch (scenario)
        {
            case "no-checkpoint": row = null; break;
            case "no-verdict": row = row with { Verdict = null }; break;
            case "unknown-verdict": row = row with { Verdict = "?" }; break;
            case "no-evidence-ref": row = row with { EvidenceRef = null }; break;
            case "missing-report": markdown = null; break;
            case "no-frontmatter": markdown = "# Code Review - Quality Grade: D\n"; break;
            case "legacy-no-key": markdown = Report(inputKey: null); break;
            case "other-key": markdown = Report(CodeReviewGradeReuse.ComputeInputKey(Request() with { Model = "x" })); break;
            case "grade-mismatch": markdown = Report(key, grade: "B"); break;
            case "wrong-type": markdown = Report(key).Replace("type: code-review-grade", "type: code-review-step"); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.Null(CodeReviewGradeReuse.TryReuse(row, key, markdown, JobFolder));
    }

    public static TheoryData<string> RenderedInputs => new()
    {
        "project", "jobId", "title", "taskBody", "diff", "commit", "resultsInventory", "cardMode", "template",
    };

    [Theory]
    [MemberData(nameof(RenderedInputs))]
    public void RenderedKey_ChangedInputMisses_ThenIdenticalBytesHit(string field)
    {
        using var fixture = new GradeFixture();
        var request = Request();
        var originalPrompt = fixture.Service.RenderPromptForReuse(request);
        var originalKey = CodeReviewGradeReuse.ComputeInputKey(request, originalPrompt);
        var changed = field switch
        {
            "project" => request with { Project = "other" },
            "jobId" => request with { JobId = "other" },
            "title" => request with { JobTitle = "A different question" },
            "taskBody" => request with { TaskBody = "A different task" },
            "diff" => request with { Diff = "+different change" },
            "commit" => request with { Commit = "different commit" },
            "resultsInventory" => request with { ResultsInventory = "results/new.md" },
            "cardMode" => request with { CardMode = "Research card" },
            "template" => request,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        if (field == "template")
            fixture.Prompts.WriteOverride(CodeReviewStepService.GradePromptTemplate, GradeFixture.Template + "\nRevised rubric.");

        var changedPrompt = fixture.Service.RenderPromptForReuse(changed);
        var changedKey = CodeReviewGradeReuse.ComputeInputKey(changed, changedPrompt);
        Assert.NotEqual(originalPrompt, changedPrompt);
        Assert.Null(CodeReviewGradeReuse.TryReuse(Row(), changedKey, Report(originalKey), JobFolder));
        Assert.NotNull(CodeReviewGradeReuse.TryReuse(Row(), changedKey, Report(changedKey), JobFolder));
        Assert.Equal(changedKey, CodeReviewGradeReuse.ComputeInputKey(changed,
            fixture.Service.RenderPromptForReuse(changed)));
        Assert.False(File.Exists(fixture.Telemetry.LogPath));
    }

    [Fact]
    public void RenderedKey_UsesFinalBytesReviewerAndExecutionContract()
    {
        var request = Request();
        const string prompt = "identical rendered prompt";
        var key = CodeReviewGradeReuse.ComputeInputKey(request, prompt);
        // A template may omit or normalize a slot. Only the actual question matters.
        Assert.Equal(key, CodeReviewGradeReuse.ComputeInputKey(request with { JobTitle = "unused" }, prompt));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request, prompt + "\n"));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request with { CliType = "codex" }, prompt));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request with { Model = "other" }, prompt));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request with { ThinkingLevel = "high" }, prompt));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request, prompt, "next-contract"));
        Assert.NotEqual(key, CodeReviewGradeReuse.ComputeInputKey(request));
    }

    [Fact]
    public async Task ExhaustedQuota_WithCheckpoint_ReusesWithoutPendingDispatchOrReceipt()
    {
        using var fixture = new GradeFixture();
        var first = await fixture.RunAsync();
        Assert.NotNull(first);
        var before = fixture.Pipeline.Read(fixture.Job.FolderPath)!;
        var beforeTelemetry = File.ReadAllText(fixture.Telemetry.LogPath);
        fixture.Selection.Outcome = SelectionOutcome.Wait;

        var reused = await fixture.RunAsync();

        Assert.NotNull(reused);
        Assert.Equal(first.FileName, reused.FileName);
        Assert.Null(reused.Usage);
        Assert.Equal(1, fixture.Selection.Calls);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(before.Steps, fixture.Pipeline.Read(fixture.Job.FolderPath)!.Steps);
        Assert.Equal(beforeTelemetry, File.ReadAllText(fixture.Telemetry.LogPath));
        Assert.Single(Directory.GetFiles(fixture.Job.FolderPath, "code-review-grade-*.md"));
    }

    [Fact]
    public async Task CacheMiss_ExhaustedQuota_StaysPendingWithoutDispatchTelemetry()
    {
        using var fixture = new GradeFixture();
        Assert.NotNull(await fixture.RunAsync());
        var beforeTelemetry = File.ReadAllText(fixture.Telemetry.LogPath);
        fixture.Selection.Outcome = SelectionOutcome.Wait;

        Assert.Null(await fixture.RunAsync("changed task body"));

        Assert.Equal(2, fixture.Selection.Calls);
        Assert.Equal(1, fixture.ProviderCalls);
        var row = fixture.Pipeline.Read(fixture.Job.FolderPath)!.Steps
            .Single(step => step.StepId == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal(PipelineStepStatus.Pending, row.Status);
        Assert.Equal("quota exhausted", row.Reason);
        Assert.Equal(beforeTelemetry, File.ReadAllText(fixture.Telemetry.LogPath));
    }

    [Fact]
    public async Task CacheMiss_FallbackRekeysTheActualRenderedPrompt_AndKeepsQualityFloor()
    {
        using var fixture = new GradeFixture();
        fixture.Selection.Outcome = SelectionOutcome.Fallback;
        // Simulate an override changing after lookup, during admission.
        fixture.Selection.OnSelect = () => fixture.Prompts.WriteOverride(
            CodeReviewStepService.GradePromptTemplate, GradeFixture.Template + "\nNew effective rubric.");

        var report = await fixture.RunAsync();

        Assert.NotNull(report);
        Assert.True(fixture.Selection.SameClassOnly);
        Assert.Equal(CliTypes.Claude, report.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, report.Model);
        Assert.Contains("New effective rubric.", fixture.DispatchedPrompt);
        var fields = FrontmatterParser.Parse(File.ReadAllText(report.FilePath)).Fields;
        var actual = Request() with { CliType = report.CliType, Model = report.Model, ThinkingLevel = report.ThinkingLevel };
        Assert.Equal(CodeReviewGradeReuse.ComputeInputKey(actual, fixture.DispatchedPrompt!), fields[CodeReviewGradeReuse.InputKeyField]);
        Assert.Single(File.ReadAllLines(fixture.Telemetry.LogPath));
    }

    [Theory]
    [InlineData("completed-attempt")]
    [InlineData("new-attempt")]
    [InlineData("running")]
    [InlineData("missing-report")]
    [InlineData("changed-grade")]
    [InlineData("legacy-key")]
    public async Task InvalidCheckpoint_WithExhaustedQuota_CannotBypassAdmission(string scenario)
    {
        using var fixture = new GradeFixture();
        var first = await fixture.RunAsync();
        Assert.NotNull(first);
        switch (scenario)
        {
            case "completed-attempt": fixture.Pipeline.Complete(fixture.Job.FolderPath); break;
            case "new-attempt": fixture.Begin(); break;
            case "running":
                fixture.Pipeline.RecordStep(fixture.Job.FolderPath, new PipelineStepExecution
                {
                    StepId = PipelineCatalogue.CodeReviewGradeStepId, Kind = StepKind.Orchestrator,
                    Status = PipelineStepStatus.Running, StartedAt = first.StartedAt,
                });
                break;
            case "missing-report": File.Delete(first.FilePath); break;
            case "changed-grade":
                File.WriteAllText(first.FilePath, File.ReadAllText(first.FilePath).Replace("grade: B", "grade: A"));
                break;
            case "legacy-key":
                var text = File.ReadAllText(first.FilePath);
                var oldKey = FrontmatterParser.Parse(text).Fields[CodeReviewGradeReuse.InputKeyField];
                File.WriteAllText(first.FilePath, text.Replace(oldKey, CodeReviewGradeReuse.ComputeInputKey(Request())));
                break;
        }
        fixture.Selection.Outcome = SelectionOutcome.Wait;

        Assert.Null(await fixture.RunAsync());
        Assert.Equal(2, fixture.Selection.Calls);
        Assert.Equal(1, fixture.ProviderCalls);
    }

    private sealed class GradeFixture : IDisposable
    {
        public const string Template = "{{project}}/{{job_id}}: {{job_title}}\n{{task_body}}\n{{commit}}\n{{diff}}\n{{model}}\n{{results_inventory}}\n{{card_mode}}";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "grade-reuse-" + Guid.NewGuid().ToString("N"));
        private readonly ReviewDecisionOrchestrator _orchestrator;
        private readonly WatchPathEntry _entry;
        public RuntimePromptService Prompts { get; }
        public PromptCallTelemetryService Telemetry { get; }
        public CodeReviewStepService Service { get; }
        public PipelineExecutionLog Pipeline { get; }
        public TaskInfo Job { get; }
        public TestSelection Selection { get; } = new();
        public int ProviderCalls { get; private set; }
        public string? DispatchedPrompt { get; private set; }

        public GradeFixture()
        {
            _entry = new WatchPathEntry { Name = "demo", Path = Path.Combine(_root, "projects", "demo") };
            var folder = Path.Combine(_entry.Path, TaskStates.AutoReview, "grade");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
            {
                id = "grade", title = "Grade", state = TaskStates.AutoReview, taskType = TaskTypes.Chore,
            }));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = _entry.Name,
                ["WatchPaths:0:Path"] = _entry.Path,
                ["PromptTemplates:OverridePath"] = Path.Combine(_root, "overrides"),
                ["PromptTelemetry:Path"] = Path.Combine(_root, "prompt-calls.jsonl"),
            }).Build();
            Telemetry = new PromptCallTelemetryService(TokenTestServices.Pricing(), config, NullLogger<PromptCallTelemetryService>.Instance);
            Prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance, Telemetry);
            Prompts.WriteOverride(CodeReviewStepService.GradePromptTemplate, Template);
            Service = new CodeReviewStepService(Prompts, NullLogger<CodeReviewStepService>.Instance)
            {
                CliRunner = (_, _, prompt, _, _) =>
                {
                    ProviderCalls++;
                    DispatchedPrompt = prompt;
                    return Task.FromResult("[[CODE_REVIEW_GRADE: grade=B; summary=Small gaps.]]");
                },
            };
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
            Job = scanner.ScanJobFolder(folder, _entry, TaskStates.AutoReview)!;
            Assert.NotNull(Job);
            Pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
            Begin();
            _orchestrator = new ReviewDecisionOrchestrator(
                scanner, new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance), null!,
                new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance), Prompts,
                new AspectRunnerService(Prompts, NullLogger<AspectRunnerService>.Instance),
                new AutoReviewStatusSnapshot(), config, NullLogger<ReviewDecisionOrchestrator>.Instance,
                pipelineLog: Pipeline, codeReviewStep: Service, quotaAdmission: new QuotaAdmissionService(Selection));
        }

        public void Begin() => Pipeline.Begin(Job.FolderPath, PipelineCatalogue.Standard, _entry.Name, Job.Id);

        public Task<CodeReviewStepReport?> RunAsync(string taskBody = "original task")
            => (Task<CodeReviewStepReport?>)typeof(ReviewDecisionOrchestrator)
                .GetMethod("RunCodeReviewGradePostStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_orchestrator, new object?[] { _entry, Job, taskBody, null, CancellationToken.None })!;

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class TestSelection : IModelSelectionService
    {
        public SelectionOutcome Outcome { get; set; } = SelectionOutcome.Primary;
        public int Calls { get; private set; }
        public bool SameClassOnly { get; private set; }
        public Action? OnSelect { get; set; }

        public ModelSelectionDecision Select(ModelSelectionCall call)
        {
            Calls++;
            SameClassOnly = call.Request.Required.SameClassOnly;
            OnSelect?.Invoke();
            var pins = call.Request.Pins;
            var primary = new SelectionRoute(pins.CliType!, pins.Model, pins.ThinkingLevel);
            var result = new SelectionResult
            {
                Outcome = Outcome,
                Route = Outcome == SelectionOutcome.Fallback
                    ? new SelectionRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "high") : primary,
                Primary = primary,
                Rule = SelectionRules.QuotaCap,
                Reason = "quota exhausted",
            };
            return new ModelSelectionDecision(result, QuotaAdmissionPlan.From(result));
        }

        public bool IsOperatorSwitchActive(string? cliType) => false;
    }

    private static CodeReviewStepRequest Request() => new(
        Project: "demo",
        JobId: "WEB-23",
        JobTitle: "Website review",
        JobFolderPath: JobFolder,
        TaskBody: "# Task\n\nReview the website.",
        Diff: "diff --git a/a.txt b/a.txt\n+change",
        CliType: "claude",
        Model: "claude-opus-5-5")
    {
        Mode = CodeReviewMode.Grade,
        Commit = "10c8e370 (HEAD)",
        ThinkingLevel = "low",
        ResultsInventory = "results/report.md",
        CardMode = "Implementation card.",
    };

    private static PipelineStepExecution Row() => new()
    {
        StepId = PipelineCatalogue.CodeReviewGradeStepId,
        Kind = StepKind.Orchestrator,
        Status = PipelineStepStatus.Failed,
        StartedAt = DateTime.UnixEpoch,
        CompletedAt = DateTime.UnixEpoch.AddSeconds(12),
        DurationMs = 12_000,
        Verdict = "D",
        VerdictSummary = "Misses the goal.",
        EvidenceRef = ReportFile,
        Runs = 1,
    };

    private static string Report(string? inputKey, string grade = "D", string commit = "10c8e370 (HEAD)") =>
        "---\n" +
        "type: code-review-grade\n" +
        "model: claude-opus-5-5\n" +
        "cliType: claude\n" +
        "thinkingLevel: low\n" +
        $"commit: {commit}\n" +
        $"grade: {grade}\n" +
        "verdict: block\n" +
        "summary: Misses the goal.\n" +
        $"tag: code-review:grade-{grade.ToLowerInvariant()}\n" +
        (inputKey is null ? string.Empty : $"inputKey: {inputKey}\n") +
        "---\n\n" +
        $"# Code Review - Quality Grade: {grade}\n\n" +
        "## Reviewer reply\n\n" +
        "The report is missing.\n" +
        $"[[CODE_REVIEW_FINDING: text={Finding}]]\n" +
        $"[[CODE_REVIEW_GRADE: grade={grade}; summary=Misses the goal.]]\n";
}
