using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3046: the closing Result assessment. Evidence-bundle assembly, the
/// deterministic policy matrix, structured-output parsing with its malformed
/// fallbacks, step selection, and the persisted step row.
/// </summary>
public sealed class SummaryAssessmentTests
{
    private const string MergeSha = "5d480157fb91f5bd7908a4579aed63e1ae213a2e";
    private const string SalvageSha = "7aa00e27f5d80c26a220148683f7bd6ac556ac3d";
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private const string CleanStatus = """
        # Status

        - Result: Success
        - Case: feature
        - Duration: 6 min
        - Files: 3

        ## Overview
        - Problem: The board needed a filter.
        - Solution: Added the filter.

        ## What Was Done
        - Added `filter.ts`.

        ## Open Items
        - None.
        """;

    // --- AGT-2798 fixture -----------------------------------------------------

    [Fact]
    public void Agt2798_fixture_is_delivered_via_operator_merge_with_named_contradictions()
    {
        var (info, folder) = LoadAgt2798();
        var bundle = SummaryEvidenceAssembler.ReadFromFolder(
            info, new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            new TimelineLog(NullLogger<TimelineLog>.Instance), "NeedsInput");
        var status = File.ReadAllText(Path.Combine(folder, "status.md"));

        var assessment = SummaryGenerationService.BuildAssessment(bundle, status, assessmentJson: null, "claude-opus-5-5", Now);

        Assert.Equal(SummaryOutcomes.DeliveredViaOperatorMerge, assessment.Outcome);
        Assert.Equal(SummaryAssessmentSources.Policy, assessment.Source);
        Assert.NotNull(assessment.FallbackReason);

        var files = Assert.Single(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.FilesVersusNoCode);
        Assert.Contains("Files: 6", files.SignalA);
        Assert.Contains("no code change", files.SignalB);
        Assert.Contains("5d480157f", files.Explanation);
        Assert.Contains("7aa00e27f", files.Explanation);
        Assert.False(files.WorkMayBeLost);

        var failure = Assert.Single(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.FailureVersusCompleted);
        Assert.Contains("post-integrate-merge", failure.SignalA);
        Assert.Contains("6-completed", failure.SignalB);
        Assert.Contains("5d480157f", failure.Explanation);
        Assert.False(failure.WorkMayBeLost);

        Assert.Contains(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.EmptyCommitsVersusIntegrated);
        Assert.DoesNotContain(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.SalvageNotIntegrated);
    }

    [Fact]
    public void Evidence_bundle_projects_every_signal_from_the_fixture_folder()
    {
        var (info, _) = LoadAgt2798();
        var bundle = SummaryEvidenceAssembler.ReadFromFolder(
            info, new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            new TimelineLog(NullLogger<TimelineLog>.Instance), "NeedsInput");

        Assert.Equal("AGT-2798", bundle.TaskKey);
        Assert.Equal(TaskStates.Completed, bundle.Lane);
        Assert.Equal("NeedsInput", bundle.RunOutcome);
        Assert.Equal(3, bundle.LaneHistory.Count);
        Assert.EndsWith("6-completed", bundle.LaneHistory[^1].Change);
        Assert.NotNull(bundle.Pipeline);
        // Pending rows and the summary step itself stay out of the bundle.
        Assert.DoesNotContain(bundle.Pipeline!.Steps, s => s.StepId == PipelineCatalogue.SummaryAssessmentStepId);
        Assert.DoesNotContain(bundle.Pipeline.Steps, s => s.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        var failed = Assert.Single(bundle.Pipeline.Steps, s => s.Status == "failed");
        Assert.Equal("no-delivery-commit", failed.FailureCode);
        Assert.Empty(bundle.Commits);
        Assert.False(bundle.CodeActivityDetected);
        var record = Assert.Single(bundle.IntegrationRecords);
        Assert.Equal(MergeSha, record.IntegrationSha);
        var salvage = Assert.Single(bundle.SalvageRefs);
        Assert.Equal(SalvageSha, salvage.CommitSha);
        Assert.Equal("NeedsInput", salvage.RunStatus);
        Assert.Equal(CompletionClaimBases.OperatorOverride, bundle.CompletionClaim!.Basis);
        Assert.Equal("awaiting-sight-review", bundle.SightReviewVerdict);
        Assert.DoesNotContain(bundle.ReviewVerdicts, v => v.StepId == PipelineCatalogue.ConceptSightReviewGateStepId);
        Assert.Null(bundle.StatusHeader);

        var json = SummaryEvidenceAssembler.ToPromptJson(bundle);
        Assert.Contains(MergeSha, json);
        Assert.Contains("\"lane\": \"6-completed\"", json);
        Assert.DoesNotContain("statusHeader", json);
    }

    [Fact]
    public void Status_header_parser_reads_only_the_head_lines()
    {
        var header = SummaryEvidenceAssembler.ParseStatusHeader(
            "# Status\n\n- Result: Success\n- Files: 6 files\n- Tests: 12 passed\n\n## What Was Done\n- Files: 99\n");
        Assert.Equal(new SummaryStatusHeader("Success", 6, "12 passed"), header);
        Assert.Null(SummaryEvidenceAssembler.ParseStatusHeader("# Status\n\n## Overview\n- Problem: x"));
        Assert.Null(SummaryEvidenceAssembler.ParseStatusHeader(null));
    }

    // --- policy matrix --------------------------------------------------------

    [Fact]
    public void Clean_delivered_card_has_no_inconsistencies()
    {
        var bundle = Bundle() with
        {
            Lane = TaskStates.Completed,
            Commits = [new SummaryCommitEvidence("a1b2c3d4e5f6a7b8c9d0", "feat: filter", 3, false)],
            CodeActivityDetected = true,
            IntegrationRecords =
            [
                new SummaryIntegrationEvidence(IntegrationRecordClasses.IntegratedVerified, "main",
                    "f00dfeedf00dfeedf00d", null, ["a1b2c3d4e5f6a7b8c9d0"], "gate passed"),
            ],
            CompletionClaim = new SummaryCompletionClaimEvidence(
                CompletionClaimBases.IntegratedDelivery, "contained in main", null, "a1b2c3d4e5f6a7b8c9d0", null),
            Pipeline = new SummaryPipelineEvidence(1, true,
                [new SummaryStepEvidence(PipelineCatalogue.CoreAgentRunStepId, "passed", null, null, null)]),
        };

        var assessment = SummaryGenerationService.BuildAssessment(bundle, CleanStatus, null, null, Now);

        Assert.Equal(SummaryOutcomes.Delivered, assessment.Outcome);
        Assert.Empty(assessment.Inconsistencies);
    }

    [Theory]
    [InlineData("NeedsInput", TaskStates.HumanReview, SummaryOutcomes.ParkedOnDecision)]
    [InlineData("Blocked", TaskStates.AutoReview, SummaryOutcomes.ParkedOnDecision)]
    [InlineData("Success", TaskStates.Escalated, SummaryOutcomes.ParkedOnDecision)]
    [InlineData("NoOp", TaskStates.HumanReview, SummaryOutcomes.NoOp)]
    [InlineData("Failed", TaskStates.AutoReview, SummaryOutcomes.Failed)]
    [InlineData("Success", TaskStates.AutoReview, SummaryOutcomes.Delivered)]
    [InlineData("Failed", TaskStates.Completed, SummaryOutcomes.Delivered)]
    public void Outcome_classification_matrix(string result, string lane, string expected)
    {
        var bundle = Bundle() with { Lane = lane, StatusHeader = new SummaryStatusHeader(result, null, null) };
        Assert.Equal(expected, SummaryAssessmentPolicy.Assess(bundle, Now).Outcome);
    }

    [Fact]
    public void Salvage_without_integration_is_flagged_as_possible_loss()
    {
        var bundle = Bundle() with
        {
            Lane = TaskStates.HumanReview,
            SalvageRefs = [new SummarySalvageEvidence("agent-studio/salvage/r/AGT-1/x", SalvageSha, "pushed", "Failed")],
            StatusHeader = new SummaryStatusHeader("Failed", 2, null),
        };

        var assessment = SummaryAssessmentPolicy.Assess(bundle, Now);

        Assert.Equal(SummaryOutcomes.Failed, assessment.Outcome);
        var salvage = Assert.Single(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.SalvageNotIntegrated);
        Assert.True(salvage.WorkMayBeLost);
        Assert.Contains("7aa00e27f", salvage.SignalA);
        var files = Assert.Single(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.FilesVersusNoCode);
        Assert.True(files.WorkMayBeLost);
        Assert.Contains("salvage commit `7aa00e27f`", files.Explanation);
    }

    [Fact]
    public void Completed_card_without_any_backing_record_flags_possible_loss()
    {
        var bundle = Bundle() with
        {
            Lane = TaskStates.Completed,
            Pipeline = new SummaryPipelineEvidence(1, true,
                [new SummaryStepEvidence(PipelineCatalogue.IntegrateMergeStepId, "failed", "merge conflict", null, null)]),
        };

        var failure = Assert.Single(SummaryAssessmentPolicy.Assess(bundle, Now).Inconsistencies);
        Assert.Equal(SummaryInconsistencyKinds.FailureVersusCompleted, failure.Kind);
        Assert.Contains("merge conflict", failure.SignalA);
        Assert.True(failure.WorkMayBeLost);
    }

    // --- structured output ----------------------------------------------------

    [Fact]
    public void Split_removes_the_block_so_the_prose_validates()
    {
        var output = CleanStatus + "\n<assessment>\n{\"outcome\":\"delivered\",\"inconsistencies\":[]}\n</assessment>\n";

        var split = SummaryAssessmentParser.Split(output);

        Assert.DoesNotContain("assessment>", split.Markdown);
        Assert.Null(SummaryProtocolValidation.Validate(split.Markdown));
        Assert.Contains("\"outcome\":\"delivered\"", split.AssessmentJson);
    }

    [Fact]
    public void Split_drops_an_unterminated_block_and_reports_no_json()
    {
        var split = SummaryAssessmentParser.Split(CleanStatus + "\n<assessment>\n{\"outcome\":");
        Assert.Null(split.AssessmentJson);
        Assert.DoesNotContain("<assessment>", split.Markdown);
        Assert.Null(SummaryProtocolValidation.Validate(split.Markdown));
    }

    [Fact]
    public void Model_block_wins_outcome_and_explains_policy_items_while_keeping_them()
    {
        var (info, folder) = LoadAgt2798();
        var bundle = SummaryEvidenceAssembler.ReadFromFolder(info,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            new TimelineLog(NullLogger<TimelineLog>.Instance), "NeedsInput");
        var json = JsonSerializer.Serialize(new
        {
            outcome = "Delivered-Via-Operator-Merge",
            outcomeReason = "Operator merge 5d480157f carried the salvaged work.",
            inconsistencies = new object[]
            {
                new { kind = "files-vs-no-code", signalA = "Files: 6", signalB = "no code change",
                      explanation = "Salvage 7aa00e27f was merged as 5d480157f.", workMayBeLost = false },
                new { kind = "Result vs Lane", signalA = "Result: NeedsInput", signalB = "lane 6-completed",
                      explanation = "N1 and N2 were answered after the run.", workMayBeLost = false },
                new { kind = "x", signalA = "", signalB = "dropped: empty signal" },
            },
        });

        var assessment = SummaryGenerationService.BuildAssessment(
            bundle, File.ReadAllText(Path.Combine(folder, "status.md")), json, "claude-opus-5-5", Now);

        Assert.Equal(SummaryAssessmentSources.Model, assessment.Source);
        Assert.Equal(SummaryOutcomes.DeliveredViaOperatorMerge, assessment.Outcome);
        Assert.Equal(SummaryOutcomes.DeliveredViaOperatorMerge, assessment.PolicyOutcome);
        Assert.Null(assessment.FallbackReason);
        Assert.Equal("claude-opus-5-5", assessment.Model);
        var files = Assert.Single(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.FilesVersusNoCode);
        Assert.Equal("Salvage 7aa00e27f was merged as 5d480157f.", files.Explanation);
        Assert.Contains("Files: 6", files.SignalA);
        Assert.Contains(assessment.Inconsistencies, i => i.Kind == SummaryInconsistencyKinds.FailureVersusCompleted);
        Assert.Contains(assessment.Inconsistencies, i => i.Kind == "result-vs-lane");
        Assert.DoesNotContain(assessment.Inconsistencies, i => i.SignalB.StartsWith("dropped", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "no assessment block")]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("[1,2", "not valid JSON")]
    [InlineData("{\"outcome\":\"shipped\",\"inconsistencies\":[]}", "unknown outcome")]
    public void Malformed_model_block_falls_back_to_the_policy(string? json, string reasonFragment)
    {
        var bundle = Bundle() with { Lane = TaskStates.AutoReview };

        var assessment = SummaryGenerationService.BuildAssessment(bundle, CleanStatus, json, "m", Now);

        Assert.Equal(SummaryAssessmentSources.Policy, assessment.Source);
        Assert.Equal(assessment.PolicyOutcome, assessment.Outcome);
        Assert.Contains(reasonFragment, assessment.FallbackReason);
    }

    [Fact]
    public void Fenced_json_inside_the_block_is_tolerated()
    {
        var parsed = SummaryAssessmentParser.TryParse("```json\n{\"outcome\":\"no-op\",\"inconsistencies\":[]}\n```", out var error);
        Assert.Null(error);
        Assert.Equal(SummaryOutcomes.NoOp, parsed!.Outcome);
    }

    [Fact]
    public void Merge_caps_the_list()
    {
        var policy = SummaryAssessmentPolicy.Assess(Bundle(), Now);
        var many = Enumerable.Range(0, 20)
            .Select(i => new SummaryInconsistency { Kind = $"k{i}", SignalA = "a", SignalB = "b" })
            .ToList();
        var merged = SummaryAssessmentParser.Merge(policy,
            new SummaryAssessmentParser.ModelAssessment(null, null, many), "m", null);
        Assert.Equal(SummaryAssessmentParser.MaxInconsistencies, merged.Inconsistencies.Count);
        Assert.Equal(SummaryAssessmentSources.Policy, merged.Source);
    }

    // --- step configuration ---------------------------------------------------

    [Fact]
    public void Step_defaults_to_claude_opus_5_5_low()
    {
        var selection = SummaryGenerationService.ResolveStepSelection(settings: null);
        Assert.Equal(CliTypes.Claude, selection.Cli);
        Assert.Equal(ModelIds.ClaudeOpus55, selection.Model);
        Assert.Equal("low", selection.ThinkingLevel);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceCatalogue, selection.ModelSource);
    }

    [Fact]
    public void Step_override_selects_cli_model_and_thinking()
    {
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.SummaryAssessmentStepId] = new()
                {
                    CliType = CliTypes.Claude, Model = ModelIds.ClaudeSonnet5, ThinkingLevel = "high",
                },
            },
        };
        var selection = SummaryGenerationService.ResolveStepSelection(settings);
        Assert.Equal(ModelIds.ClaudeSonnet5, selection.Model);
        Assert.Equal("high", selection.ThinkingLevel);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceStep, selection.ModelSource);
    }

    [Fact]
    public void Project_orchestrator_model_for_another_cli_falls_back_to_the_catalogue_default()
    {
        var settings = new ProjectSettings { OrchestratorModel = ModelIds.Gpt55 };
        var selection = SummaryGenerationService.ResolveStepSelection(settings);
        Assert.Equal(CliTypes.Claude, selection.Cli);
        Assert.Equal(ModelIds.ClaudeOpus55, selection.Model);
    }

    [Fact]
    public void Catalogue_lists_the_step_in_every_pipeline_as_a_mandatory_model_step()
    {
        foreach (var pipeline in PipelineCatalogue.All)
        {
            var step = Assert.Single(pipeline.Post, s => s.Id == PipelineCatalogue.SummaryAssessmentStepId);
            Assert.False(PipelineStepConfigResolver.CanDisable(step));
            Assert.True(PipelineStepModelDefaults.UsesModel(step));
            Assert.Equal(ModelIds.ClaudeOpus55, PipelineStepModelDefaults.RuntimeDefaultFor(step));
            Assert.Equal(CliTypes.Claude, PipelineStepModelDefaults.RuntimeDefaultCliFor(step));
            Assert.Equal("low", PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(step));
        }
    }

    // --- service wiring -------------------------------------------------------

    [Fact]
    public async Task Generation_writes_status_assessment_and_a_costed_step_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-assessment-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "cli-output.log"), "[assistant/final] [[TASK_DONE]]\n");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PromptTemplates:RuntimePath"] = Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime"),
                })
                .Build();
            var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
            pipelineLog.Begin(root, PipelineCatalogue.Standard, "local", "job-1");
            var oneShot = new ScriptedOneShot(CleanStatus + "\n<assessment>{\"outcome\":\"delivered\",\"inconsistencies\":[]}</assessment>");
            var service = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                configuration,
                new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
                oneShotRegistry: new CliOneShotRegistry([oneShot]),
                pipelineLog: pipelineLog,
                timeline: new TimelineLog(NullLogger<TimelineLog>.Instance));
            var task = new TaskInfo
            {
                Id = "job-1", TaskKey = "LOCAL-2", Title = "t", TaskType = "feature", Mode = TaskModes.Coding,
                State = TaskStates.Progress, FolderPath = root, WatchPath = root, ProjectName = "local",
            };

            await service.GenerateAsync(task);

            Assert.Equal(TaskSummaryStatus.Ready, service.GetState("LOCAL-2")!.Status);
            var request = Assert.Single(oneShot.Requests);
            Assert.Equal(ModelIds.ClaudeOpus55, request.Model);
            Assert.Equal("low", request.ThinkingLevel);
            Assert.Contains("EVIDENCE", request.Prompt);
            Assert.Contains("\"taskKey\": \"LOCAL-2\"", request.Prompt);
            Assert.DoesNotContain("{{evidence}}", request.Prompt);

            var status = await File.ReadAllTextAsync(Path.Combine(root, "status.md"));
            Assert.DoesNotContain("assessment>", status);
            var assessment = SummaryAssessmentStore.Read(root);
            Assert.NotNull(assessment);
            Assert.Equal(SummaryAssessmentSources.Model, assessment!.Source);
            Assert.Equal(SummaryOutcomes.Delivered, assessment.Outcome);

            var row = Assert.Single(pipelineLog.Read(root)!.Steps, s => s.StepId == PipelineCatalogue.SummaryAssessmentStepId);
            Assert.Equal(PipelineStepStatus.Passed, row.Status);
            Assert.Equal(StepKind.Orchestrator, row.Kind);
            Assert.Equal("low", row.ThinkingLevel);
            Assert.Equal(1200, row.InputTokens);
            Assert.Equal(300, row.OutputTokens);
            Assert.Equal(StepCostBasis.Model, row.CostBasis);
            Assert.NotNull(row.EstimatedCostUsd);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // --- helpers --------------------------------------------------------------

    private static SummaryEvidenceBundle Bundle() => new() { TaskKey = "AGT-1", Lane = TaskStates.AutoReview };

    private static (TaskInfo Info, string Folder) LoadAgt2798()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "summary-assessment", "AGT-2798");
        var card = JsonSerializer.Deserialize<TaskInfo>(
            File.ReadAllText(Path.Combine(folder, "card.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return (card with { FolderPath = folder, WatchPath = folder }, folder);
    }

    private sealed class ScriptedOneShot(string reply) : ICliOneShot
    {
        public string CliType => CliTypes.Claude;
        public List<CliOneShotRequest> Requests { get; } = [];

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: reply,
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(5),
                ParsedText: reply,
                Usage: new OrchestratorTokenUsage
                {
                    Model = request.Model, InputTokens = 1200, OutputTokens = 300, ThinkingLevel = request.ThinkingLevel,
                },
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: Now,
                    CompletedAt: Now,
                    TotalMs: 5),
                Error: null));
        }
    }
}
