using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3015: every pipeline step execution records its model, tokens and cost;
/// the orchestrator steps reach the project ledger under their own kind; an
/// unpriced model stays unpriced; occurrences survive rounds and epochs; a
/// deterministic step records an explicit zero; and the decision payload on
/// the bus names the deciding model.
/// </summary>
public sealed class StepCostMeasurementTests : IDisposable
{
    private const string PricedModel = "claude-haiku-4-5";
    private const string UnpricedModel = "unpriced-test-model";
    // clock-independent: all dated rows and price windows use an injected date.
    private static readonly DateTime TestAt = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "agt-3015-" + Guid.NewGuid().ToString("N"));

    public StepCostMeasurementTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static PipelineExecutionLog NewLog() => new(NullLogger<PipelineExecutionLog>.Instance);

    // ---- model-backed policy matrix -------------------------------------

    [Theory]
    [InlineData(PipelineCatalogue.CoreAgentRunStepId, StepKind.Core, true)]
    [InlineData("aspect-code-quality", StepKind.Aspect, true)]
    [InlineData(PipelineCatalogue.DriftAdrCodeStepId, StepKind.Drift, true)]
    [InlineData(PipelineCatalogue.DriftCodePatternStepId, StepKind.Drift, false)]
    [InlineData(PipelineCatalogue.OrchestratorDecisionStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.PostAbortReviewStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.ConflictResolutionStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.CodeReviewGradeStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.TaskSpawnerStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.FailureInterventionStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.UiVisualVerdictStepId, StepKind.Orchestrator, true)]
    [InlineData(PipelineCatalogue.OrchestratorReviewStepId, StepKind.Orchestrator, false)]
    [InlineData(PipelineCatalogue.ConceptReviewStepId, StepKind.Orchestrator, false)]
    [InlineData(PipelineCatalogue.ConceptSightReviewGateStepId, StepKind.Orchestrator, false)]
    [InlineData(PipelineCatalogue.UiHumanReviewGateStepId, StepKind.Orchestrator, false)]
    [InlineData(PipelineCatalogue.QualityModelReviewStepId, StepKind.Analysis, true)]
    [InlineData(PipelineCatalogue.QualityAngularRulesStepId, StepKind.Analysis, false)]
    [InlineData(PipelineCatalogue.PromptEnrichmentStepId, StepKind.Module, false)]
    [InlineData(PipelineCatalogue.ModelQualificationStepId, StepKind.Module, false)]
    [InlineData(PipelineCatalogue.BuildTestGateStepId, StepKind.Tool, false)]
    public void IsModelBacked_matrix(string stepId, StepKind kind, bool expected)
        => Assert.Equal(expected, StepCostMeasurement.IsModelBacked(stepId, kind));

    [Fact]
    public void Every_orchestrator_step_in_the_catalogue_is_classified()
    {
        // A new orchestrator step must be a deliberate choice: either it calls
        // a model and must record one, or it is listed as a rule/human gate.
        var orchestratorSteps = PipelineCatalogue.All
            .SelectMany(pipeline => pipeline.AllSteps)
            .Append(PipelineCatalogue.AbortReviewStep)
            .Where(step => step.Kind == StepKind.Orchestrator)
            .Select(step => step.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains(PipelineCatalogue.OrchestratorDecisionStepId, orchestratorSteps);
        foreach (var id in orchestratorSteps)
        {
            var row = new PipelineStepExecution
            {
                StepId = id,
                Kind = StepKind.Orchestrator,
                Status = PipelineStepStatus.Passed,
            };
            var stamped = StepCostMeasurement.Stamp(row);
            // Either a measured zero (rule gate) or a visible gap: never a
            // silent blank that reads as free.
            if (StepCostMeasurement.IsModelBacked(id, StepKind.Orchestrator))
            {
                Assert.True(StepCostMeasurement.IsMissingModel(stamped), id);
                Assert.Null(stamped.EstimatedCostUsd);
            }
            else
            {
                Assert.Equal(StepCostBasis.Deterministic, stamped.CostBasis);
                Assert.Equal(0m, stamped.EstimatedCostUsd);
            }
        }
    }

    // ---- 1. a model-backed step without a model fails -------------------

    [Fact]
    public void Model_backed_step_without_a_model_is_a_measurement_gap()
    {
        var log = NewLog();
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", TestAt);
        log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CodeReviewGradeStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            StartedAt = TestAt,
            CompletedAt = TestAt,
        });

        var row = Row(log, PipelineCatalogue.CodeReviewGradeStepId);
        Assert.True(StepCostMeasurement.IsMissingModel(row));
        Assert.Null(row.CostBasis);
        Assert.Null(row.EstimatedCostUsd);
    }

    [Fact]
    public void Decision_recorded_inside_a_model_scope_names_model_tokens_and_cost()
    {
        var log = NewLog();
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", TestAt);
        var usage = new StepModelUsage(PricedModel, "low", "config", InputTokens: 1_000_000, OutputTokens: 200_000);
        var row = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            StartedAt = TestAt,
            CompletedAt = TestAt,
            Verdict = "accept",
            CostBasis = StepCostBasis.Deterministic,
        };
        using (DecisionModelContext.Use(usage))
        {
            log.RecordStep(_folder, DecisionModelContext.Current!.ApplyTo(row));
        }

        var recorded = Row(log, PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.False(StepCostMeasurement.IsMissingModel(recorded));
        Assert.Equal(PricedModel, recorded.Model);
        Assert.Equal("low", recorded.ThinkingLevel);
        Assert.Equal("config", recorded.ModelSource);
        Assert.Equal(StepCostBasis.Model, recorded.CostBasis);
        Assert.True(recorded.ModelPriced);
        Assert.Equal(2.00m, recorded.EstimatedCostUsd);
        Assert.Null(DecisionModelContext.Current);
    }

    // ---- 2. orchestrator tokens reach the ledger under their own kind ----

    [Fact]
    public void Orchestrator_step_tokens_reach_the_ledger_under_their_own_kind()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var record = Record("J1", now,
            Step(PipelineCatalogue.CoreAgentRunStepId, StepKind.Core, PricedModel, 100_000, 10_000),
            Step(PipelineCatalogue.CodeReviewGradeStepId, StepKind.Orchestrator, PricedModel, 1_000_000, 200_000),
            Step(PipelineCatalogue.OrchestratorDecisionStepId, StepKind.Orchestrator, PricedModel, 500_000, 100_000));

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", [record], days: 7, nowUtc: now);

        var orchestrator = Assert.Single(timeline.Kinds, kind => kind.Kind == "orchestrator");
        Assert.Equal(1_800_000, orchestrator.TotalTokens);
        Assert.Equal(3.00m, orchestrator.TotalCostUsd);
        Assert.Equal(2, orchestrator.Runs);
        var grade = Assert.Single(timeline.Steps, step => step.StepId == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal("orchestrator", grade.Kind);
        Assert.Equal(2.00m, grade.TotalCostUsd);
        Assert.Equal(3.00m, timeline.DecisionCost!.Deciding.PricedCostUsd);
        Assert.Equal(2, timeline.DecisionCost.Deciding.Runs);
        Assert.Equal(1, timeline.DecisionCost.AgentRuns.Runs);
    }

    [Fact]
    public void Receipt_backed_task_still_contributes_its_orchestrator_rows_without_double_counting_core()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var receipts = ProjectPipelineCostService.BuildReceiptRecords("P",
        [
            new OrchestratorLogEntry
            {
                Ts = now,
                JobId = "J1",
                ParticipantId = "agent:remote-runner:a1",
                TokenUsage = new OrchestratorTokenUsage { Model = PricedModel, InputTokens = 100_000, OutputTokens = 10_000 },
            },
        ]);
        var log = Record("J1", now,
            Step(PipelineCatalogue.CoreAgentRunStepId, StepKind.Core, PricedModel, 999_999, 999_999),
            Step(PipelineCatalogue.TaskSpawnerStepId, StepKind.Orchestrator, PricedModel, 1_000_000, 200_000));

        var merged = ProjectPipelineCostService.MergeSources(
            receipts, new HashSet<string>(StringComparer.Ordinal) { "J1" }, [("J1", log)]);
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", merged, days: 7, nowUtc: now);

        var core = Assert.Single(timeline.Kinds, kind => kind.Kind == "core");
        Assert.Equal(110_000, core.TotalTokens); // the receipt, not the log's core row
        Assert.Equal(1, core.Runs);               // counted once, from the log
        var orchestrator = Assert.Single(timeline.Kinds, kind => kind.Kind == "orchestrator");
        Assert.Equal(1_200_000, orchestrator.TotalTokens);
        Assert.Contains(timeline.Steps, step => step.StepId == PipelineCatalogue.TaskSpawnerStepId);
    }

    [Fact]
    public void Matching_orchestrator_receipt_and_step_execution_are_priced_once()
    {
        var receipts = ProjectPipelineCostService.BuildReceiptRecords("P",
        [
            new OrchestratorLogEntry
            {
                Ts = TestAt, JobId = "J1", ParticipantId = "orchestrator:P",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = PricedModel, InputTokens = 1_000_000, OutputTokens = 200_000,
                },
            },
            // A different call has no measured step yet and must remain in
            // the ledger rather than disappearing with the matched receipt.
            new OrchestratorLogEntry
            {
                Ts = TestAt.AddSeconds(1), JobId = "J1", ParticipantId = "orchestrator:P",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = PricedModel, InputTokens = 50_000, OutputTokens = 5_000,
                },
            },
        ]);
        var log = Record("J1", TestAt,
            Step(PipelineCatalogue.TaskSpawnerStepId, StepKind.Orchestrator, PricedModel, 1_000_000, 200_000));

        var merged = ProjectPipelineCostService.MergeSources(
            receipts, new HashSet<string>(StringComparer.Ordinal), [("J1", log)]);
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", merged, days: 7, nowUtc: TestAt);

        Assert.Equal(2, merged.Count); // measured step plus the unmatched receipt
        var spawner = Assert.Single(timeline.Steps, step => step.StepId == PipelineCatalogue.TaskSpawnerStepId);
        Assert.Equal(1, spawner.Runs);
        Assert.Equal(1_200_000, spawner.TotalTokens);
        Assert.Equal(1_255_000, timeline.TotalTokens);
        Assert.Equal(
            2.00m + TokenPricing.Estimate(PricedModel, 50_000, 5_000, 0, 0, TestAt).Total,
            timeline.DecisionCost!.Deciding.PricedCostUsd);
    }

    [Fact]
    public void Aspect_receipts_are_reconciled_with_measured_aspect_executions_not_discarded_wholesale()
    {
        var receipts = ProjectPipelineCostService.BuildReceiptRecords("P",
        [
            // Matches the measured aspect execution below: priced once.
            new OrchestratorLogEntry
            {
                Ts = TestAt, JobId = "J1", ParticipantId = "support:aspect-code-quality",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = PricedModel, InputTokens = 40_000, OutputTokens = 4_000,
                },
            },
            // The partial log has no execution for this call; its tokens and
            // cost must stay in the project totals.
            new OrchestratorLogEntry
            {
                Ts = TestAt.AddSeconds(1), JobId = "J1", ParticipantId = "support:aspect-requirement-fit",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = PricedModel, InputTokens = 30_000, OutputTokens = 3_000,
                },
            },
        ]);
        var log = Record("J1", TestAt,
            Step("aspect-code-quality", StepKind.Aspect, PricedModel, 40_000, 4_000));

        var merged = ProjectPipelineCostService.MergeSources(
            receipts, new HashSet<string>(StringComparer.Ordinal), [("J1", log)]);
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", merged, days: 7, nowUtc: TestAt);

        Assert.Equal(2, merged.Count); // measured execution plus the unmatched receipt
        var aspect = Assert.Single(timeline.Kinds, kind => kind.Kind == "aspect");
        Assert.Equal(77_000, aspect.TotalTokens);
        Assert.Equal(1, Assert.Single(timeline.Steps, step => step.StepId == "aspect-code-quality").Runs);
        Assert.Equal(
            TokenPricing.Estimate(PricedModel, 40_000, 4_000, 0, 0, TestAt).Total
            + TokenPricing.Estimate(PricedModel, 30_000, 3_000, 0, 0, TestAt).Total,
            timeline.TotalCostUsd);
    }

    [Fact]
    public void An_orchestrator_only_receipt_does_not_erase_core_step_usage()
    {
        var receipts = ProjectPipelineCostService.BuildReceiptRecords("P",
        [new OrchestratorLogEntry
        {
            Ts = TestAt, JobId = "J1", ParticipantId = "orchestrator:P",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = PricedModel, InputTokens = 50_000, OutputTokens = 5_000,
            },
        }]);
        var log = Record("J1", TestAt,
            Step(PipelineCatalogue.CoreAgentRunStepId, StepKind.Core, PricedModel, 100_000, 10_000));

        var merged = ProjectPipelineCostService.MergeSources(
            receipts, new HashSet<string>(StringComparer.Ordinal), [("J1", log)]);
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", merged, days: 7, nowUtc: TestAt);

        Assert.Equal(110_000, Assert.Single(timeline.Kinds, kind => kind.Kind == "core").TotalTokens);
        Assert.Equal(55_000, Assert.Single(timeline.Kinds, kind => kind.Kind == "orchestrator").TotalTokens);
    }

    // ---- 3. an unpriced model is unpriced, never zero --------------------

    [Fact]
    public void Unpriced_model_surfaces_as_unpriced_and_not_as_zero()
    {
        var stamped = StepCostMeasurement.Stamp(new PipelineStepExecution
        {
            StepId = PipelineCatalogue.TaskSpawnerStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            Model = UnpricedModel,
            InputTokens = 5_000,
            OutputTokens = 1_000,
        });
        Assert.Equal(StepCostBasis.Model, stamped.CostBasis);
        Assert.False(stamped.ModelPriced);
        Assert.Null(stamped.EstimatedCostUsd);

        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var record = Record("J1", now,
            Step(PipelineCatalogue.TaskSpawnerStepId, StepKind.Orchestrator, UnpricedModel, 5_000, 1_000),
            Step(PipelineCatalogue.CodeReviewGradeStepId, StepKind.Orchestrator, PricedModel, 1_000_000, 200_000));
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", [record], days: 7, nowUtc: now);
        var spawner = Assert.Single(timeline.Steps, step => step.StepId == PipelineCatalogue.TaskSpawnerStepId);
        Assert.True(spawner.AnyModelUnknown);
        Assert.Equal(1, spawner.UnpricedRuns);
        Assert.Contains(spawner.PricingGaps, gap => gap.ModelId == UnpricedModel);

        var deciding = timeline.DecisionCost!.Deciding;
        Assert.Equal(2.00m, deciding.PricedCostUsd);
        Assert.Equal(6_000, deciding.UnpricedTokens);
        Assert.Equal(1, deciding.UnpricedRuns);

        var card = PipelineCostCalculator.SummarizeDecisionCost(record);
        Assert.Equal(6_000, card.Deciding.UnpricedTokens);
        Assert.Equal(2.00m, card.Deciding.PricedCostUsd);
    }

    // ---- 4. occurrences survive a round and an epoch change --------------

    [Fact]
    public void Occurrence_count_survives_a_review_round_and_an_epoch_change()
    {
        var log = NewLog();
        var t0 = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", t0);

        // Round 1 and round 2 of the same attempt: two decisions.
        RecordDecision(log, t0.AddMinutes(1), "reissue", 1_000_000, 200_000);
        RecordDecision(log, t0.AddMinutes(5), "accept", 500_000, 100_000);

        var firstAttempt = Row(log, PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(2, firstAttempt.Runs);
        var earlier = Assert.Single(firstAttempt.EarlierRuns!);
        Assert.Equal("reissue", earlier.Verdict);
        Assert.Equal(2.00m, earlier.EstimatedCostUsd);
        Assert.Equal(1.00m, firstAttempt.EstimatedCostUsd);

        // Epoch change: the card is reissued and a new attempt begins.
        log.Complete(_folder, t0.AddMinutes(6));
        var second = log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", t0.AddMinutes(10));
        Assert.Equal(2, second.Attempt);
        RecordDecision(log, t0.AddMinutes(11), "accept", 500_000, 100_000);

        var record = log.Read(_folder)!;
        Assert.Equal(1, Row(log, PipelineCatalogue.OrchestratorDecisionStepId).Runs);
        var archived = Assert.Single(record.PreviousAttempts)
            .Steps.Single(step => step.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(2, archived.Runs);

        var card = PipelineCostCalculator.SummarizeDecisionCost(record);
        Assert.Equal(3, card.Deciding.Runs);
        Assert.Equal(4.00m, card.Deciding.PricedCostUsd);

        var project = ProjectPipelineCostService.BuildFromRecords(
            "P", [record, .. record.PreviousAttempts], days: 7, nowUtc: t0.AddHours(1));
        var decision = Assert.Single(project.Steps, step => step.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(3, decision.Runs);
        Assert.Equal(1, decision.Tasks);
        Assert.Equal(4.00m, decision.TotalCostUsd);

        // The live attempt's per-step cost covers both rounds of that attempt.
        var liveCost = PipelineCostCalculator.Summarize(record.PreviousAttempts[0]);
        var liveDecision = liveCost.Steps.Single(step => step.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(2, liveDecision.Runs);
        Assert.Equal(3.00m, liveDecision.CostUsd);
    }

    [Fact]
    public void A_running_then_terminal_write_is_one_execution()
    {
        var log = NewLog();
        var t0 = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", t0);
        var started = t0.AddMinutes(1);
        log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = "aspect-code-quality", Kind = StepKind.Aspect,
            Status = PipelineStepStatus.Running, StartedAt = started, Model = PricedModel,
        });
        log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = "aspect-code-quality", Kind = StepKind.Aspect,
            Status = PipelineStepStatus.Passed, StartedAt = started, CompletedAt = started.AddSeconds(30),
            Model = PricedModel, InputTokens = 1_000, OutputTokens = 100,
        });

        var row = Row(log, "aspect-code-quality");
        Assert.Equal(1, row.Runs);
        Assert.Null(row.EarlierRuns);
    }

    [Fact]
    public void Core_run_repeats_are_counted_but_not_priced_twice()
    {
        var log = NewLog();
        var t0 = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", t0);
        // The core writer carries its accumulated tokens on every write.
        RecordCore(log, t0.AddMinutes(1), PipelineStepStatus.Running, 0);
        RecordCore(log, t0.AddMinutes(1), PipelineStepStatus.Passed, 100_000);
        RecordCore(log, t0.AddMinutes(9), PipelineStepStatus.Running, 100_000);
        RecordCore(log, t0.AddMinutes(9), PipelineStepStatus.Passed, 200_000);

        var record = log.Read(_folder)!;
        var core = Row(log, PipelineCatalogue.CoreAgentRunStepId);
        Assert.Equal(2, core.Runs);
        var rollup = PipelineCostCalculator.SummarizeDecisionCost(record);
        Assert.Equal(2, rollup.AgentRuns.Runs);
        Assert.Equal(200_000, rollup.AgentRuns.Tokens);
        Assert.Equal(200_000, PipelineCostCalculator.Summarize(record).Steps
            .Single(step => step.StepId == PipelineCatalogue.CoreAgentRunStepId).TotalTokens);
    }

    // ---- 5. a deterministic step records an explicit zero ----------------

    [Theory]
    // These four orchestrator catalogue rows are actual rule checks or human
    // wait gates in the executors; no LLM call returns a usage receipt there.
    [InlineData(PipelineCatalogue.OrchestratorReviewStepId, StepKind.Orchestrator)]
    [InlineData(PipelineCatalogue.ConceptReviewStepId, StepKind.Orchestrator)]
    [InlineData(PipelineCatalogue.ConceptSightReviewGateStepId, StepKind.Orchestrator)]
    [InlineData(PipelineCatalogue.UiHumanReviewGateStepId, StepKind.Orchestrator)]
    [InlineData(PipelineCatalogue.BuildTestGateStepId, StepKind.Tool)]
    [InlineData(PipelineCatalogue.ModelQualificationStepId, StepKind.Module)]
    public void Deterministic_step_records_an_explicit_zero(string stepId, StepKind kind)
    {
        var log = NewLog();
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", TestAt);
        log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = stepId,
            Kind = kind,
            Status = PipelineStepStatus.Passed,
            StartedAt = TestAt,
            CompletedAt = TestAt,
            // Model qualification names the model it selected for the core
            // run; it did not run on it.
            Model = kind == StepKind.Module ? PricedModel : null,
        });

        var row = Row(log, stepId);
        Assert.Equal(StepCostBasis.Deterministic, row.CostBasis);
        Assert.Equal(0m, row.EstimatedCostUsd);
        Assert.Null(row.ModelPriced);
        Assert.False(StepCostMeasurement.IsMissingModel(row));
    }

    [Fact]
    public void Rule_decision_outside_a_model_scope_is_an_explicit_zero()
    {
        var stamped = StepCostMeasurement.Stamp(new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            CostBasis = StepCostBasis.Deterministic,
        });
        Assert.Equal(StepCostBasis.Deterministic, stamped.CostBasis);
        Assert.Equal(0m, stamped.EstimatedCostUsd);
        Assert.False(StepCostMeasurement.IsMissingModel(stamped));
    }

    [Fact]
    public void Unreached_step_terminalized_at_completion_records_not_run()
    {
        var log = NewLog();
        log.Begin(_folder, PipelineCatalogue.Standard, "P", "J1", TestAt);
        log.Complete(_folder, TestAt.AddMinutes(1));
        var row = Row(log, PipelineCatalogue.TaskSpawnerStepId);
        Assert.Equal(PipelineStepStatus.Skipped, row.Status);
        Assert.Equal(StepCostBasis.NotRun, row.CostBasis);
        Assert.Equal(0m, row.EstimatedCostUsd);
    }

    // ---- 6. decidedByModel on the bus decision payload --------------------

    [Fact]
    public void Decision_payload_names_the_deciding_model()
    {
        var payload = AgentMessageBusBridge.DecisionPayload(
            new StepModelUsage("gpt-5.4-mini", "medium", "config"));
        var json = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("model", json.GetProperty("decidedBy").GetString());
        Assert.Equal("gpt-5.4-mini", json.GetProperty("decidedByModel").GetString());
        Assert.Equal("medium", json.GetProperty("decidedByThinkingLevel").GetString());
        Assert.Equal("config", json.GetProperty("decidedByModelSource").GetString());
    }

    [Fact]
    public void Decision_payload_without_a_model_says_rule()
    {
        var payload = AgentMessageBusBridge.DecisionPayload(null);
        Assert.Equal("rule", payload.DecidedBy);
        Assert.Null(payload.DecidedByModel);
    }

    // ---- helpers ----------------------------------------------------------

    private PipelineStepExecution Row(PipelineExecutionLog log, string stepId)
        => log.Read(_folder)!.Steps.Single(step => step.StepId == stepId);

    private void RecordDecision(PipelineExecutionLog log, DateTime at, string verdict, long input, long output)
        => log.RecordStep(_folder, new StepModelUsage(PricedModel, null, "config", input, output).ApplyTo(
            new PipelineStepExecution
            {
                StepId = PipelineCatalogue.OrchestratorDecisionStepId,
                Kind = StepKind.Orchestrator,
                Status = PipelineStepStatus.Passed,
                StartedAt = at,
                CompletedAt = at,
                Verdict = verdict,
            }));

    private void RecordCore(PipelineExecutionLog log, DateTime startedAt, PipelineStepStatus status, long input)
        => log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CoreAgentRunStepId,
            Kind = StepKind.Core,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == PipelineStepStatus.Running ? null : startedAt.AddMinutes(2),
            Model = PricedModel,
            InputTokens = input,
        });

    private static PipelineStepExecution Step(string id, StepKind kind, string model, long input, long output)
        => new()
        {
            StepId = id,
            Kind = kind,
            Status = PipelineStepStatus.Passed,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
        };

    private static PipelineExecutionRecord Record(string jobId, DateTime at, params PipelineStepExecution[] steps)
        => new()
        {
            PipelineId = PipelineCatalogue.StandardPipelineId,
            JobId = jobId,
            Project = "P",
            StartedAt = at,
            CompletedAt = at,
            Steps = steps.Select(step => step with { StartedAt = at, CompletedAt = at }).ToList(),
        };
}
