

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for the two pure helpers the pre/post-step feature adds:
/// <see cref="PipelineStepConfigResolver"/> (turns per-project overrides
/// into the concrete enabled / model / mode a step runs with) and
/// <see cref="PipelineCostCalculator"/> (derives per-step + task-total
/// cost from a recorded execution via the single price table).
/// </summary>
public class PipelineConfigAndCostTests
{
    private static ProjectSettings SettingsWith(params (string StepId, PipelineStepSetting Setting)[] steps)
    {
        var map = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, s) in steps) map[id] = s;
        return new ProjectSettings { PipelineSteps = map };
    }

    [Fact]
    public void IsEnabled_DefaultsTrue_WhenNoOverride()
    {
        Assert.True(PipelineStepConfigResolver.IsEnabled(null, "aspect-code-quality"));
        Assert.True(PipelineStepConfigResolver.IsEnabled(new ProjectSettings(), "aspect-code-quality"));
    }

    [Fact]
    public void IsEnabled_CatalogueStep_HonoursDefaultOffOptInSteps()
    {
        var drift = PipelineCatalogue.Standard.Post.Single(s => s.Id == PipelineCatalogue.DriftAdrCodeStepId);

        Assert.False(PipelineStepConfigResolver.IsEnabled(null, drift));
        Assert.False(PipelineStepConfigResolver.IsEnabled(new ProjectSettings(), drift));

        var enabled = SettingsWith((drift.Id, new PipelineStepSetting { Enabled = true }));
        Assert.True(PipelineStepConfigResolver.IsEnabled(enabled, drift));
    }

    [Fact]
    public void IsEnabled_RespectsExplicitDisable_ByFullIdOrBareSuffix()
    {
        var byFull = SettingsWith(("aspect-code-quality", new PipelineStepSetting { Enabled = false }));
        Assert.False(PipelineStepConfigResolver.IsEnabled(byFull, "aspect-code-quality"));
        // The aspect runner only knows the bare id; the resolver must find
        // the override stored under the full id.
        Assert.False(PipelineStepConfigResolver.IsEnabled(byFull, "code-quality"));

        var byBare = SettingsWith(("code-quality", new PipelineStepSetting { Enabled = false }));
        Assert.False(PipelineStepConfigResolver.IsEnabled(byBare, "aspect-code-quality"));
    }

    [Fact]
    public void CanDisable_MatchesCatalogueSafetySemantics()
    {
        var pipeline = PipelineCatalogue.Standard;

        Assert.False(PipelineStepConfigResolver.CanDisable(
            pipeline.Core.Single(s => s.Id == PipelineCatalogue.CoreAgentRunStepId)));
        Assert.False(PipelineStepConfigResolver.CanDisable(
            pipeline.Pre.Single(s => s.Id == PipelineCatalogue.LoopGuardStepId)));
        Assert.True(PipelineStepConfigResolver.CanDisable(
            pipeline.Post.Single(s => s.Id == PipelineCatalogue.LintScssStepId)));
    }

    [Fact]
    public void ResolveModel_OrdersStepOverride_Then_ProjectModel_Then_RuntimeDefault()
    {
        // 1) step override wins
        var withStep = SettingsWith(("aspect-code-quality", new PipelineStepSetting { Model = "claude-haiku-4-5" }))
            with { OrchestratorModel = "claude-sonnet-4-6" };
        Assert.Equal("claude-haiku-4-5",
            PipelineStepConfigResolver.ResolveModel(withStep, "aspect-code-quality", "claude-opus-4-8"));

        // 2) no step override -> project orchestrator model
        var projectOnly = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-6" };
        Assert.Equal("claude-sonnet-4-6",
            PipelineStepConfigResolver.ResolveModel(projectOnly, "aspect-code-quality", "claude-opus-4-8"));

        // 3) nothing configured -> runtime default
        Assert.Equal("claude-opus-4-8",
            PipelineStepConfigResolver.ResolveModel(null, "aspect-code-quality", "claude-opus-4-8"));
    }

    [Fact]
    public void ResolveThinkingLevel_OrdersStepOverride_Then_ProjectDefault_Then_GlobalDefault_Then_ModelDefault()
    {
        var withStep = SettingsWith(("aspect-code-quality", new PipelineStepSetting { ThinkingLevel = "xhigh" }))
            with { OrchestratorThinkingLevel = "medium" };
        Assert.Equal("xhigh",
            PipelineStepConfigResolver.ResolveThinkingLevel(withStep, "aspect-code-quality", "claude", "claude-opus-4-7", "low"));

        var projectOnly = new ProjectSettings { OrchestratorThinkingLevel = "medium" };
        Assert.Equal("medium",
            PipelineStepConfigResolver.ResolveThinkingLevel(projectOnly, "aspect-code-quality", "claude", "claude-opus-4-7", "low"));

        Assert.Equal("low",
            PipelineStepConfigResolver.ResolveThinkingLevel(null, "aspect-code-quality", "claude", "claude-opus-4-7", "low"));

        Assert.Equal("high",
            PipelineStepConfigResolver.ResolveThinkingLevel(null, "aspect-code-quality", "claude", "claude-opus-4-7"));
    }

    [Fact]
    public void ProjectPipelineOrder_ReordersPreAndPost_WhileCoreStaysFixed()
    {
        var settings = new ProjectSettings
        {
            PipelineStepOrder =
            [
                PipelineCatalogue.PreReissueOpenItemsStepId,
                PipelineCatalogue.LoopGuardStepId,
                PipelineCatalogue.LintScssStepId,
                "not-in-this-pipeline",
                PipelineCatalogue.BuildTestGateStepId,
            ],
        };

        var ordered = ProjectPipelineOrder.Apply(PipelineCatalogue.Standard, settings);

        Assert.Equal(PipelineCatalogue.PreReissueOpenItemsStepId, ordered.Pre[0].Id);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, ordered.Pre[1].Id);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, ordered.Core.Single().Id);
        Assert.Equal(PipelineCatalogue.LintScssStepId, ordered.Post[0].Id);
        Assert.Equal(PipelineCatalogue.BuildTestGateStepId, ordered.Post[1].Id);
        Assert.Contains(ordered.Post.Skip(2), step => step.Id == PipelineCatalogue.OrchestratorDecisionStepId);
    }

    [Fact]
    public void Summarize_EmptyRecord_IsZeroCost()
    {
        var summary = PipelineCostCalculator.Summarize(null);
        Assert.Empty(summary.Steps);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(0m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
        Assert.Equal(0, summary.UnpricedRuns);
        Assert.Empty(summary.PricingGaps);
    }

    [Fact]
    public void Summarize_PerStepAndTotalCost_FromPriceTable()
    {
        var record = new PipelineExecutionRecord
        {
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality",
                    Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", // $1 / $5 per million
                    InputTokens = 1_000_000,
                    OutputTokens = 200_000,
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-requirement-fit",
                    Kind = StepKind.Aspect,
                    Model = "claude-opus-4-8", // $5 / $25 per million
                    InputTokens = 100_000,
                    OutputTokens = 10_000,
                },
            },
        };

        var summary = PipelineCostCalculator.Summarize(record);
        Assert.Equal(2, summary.Steps.Count);

        var haiku = summary.Steps[0];
        // 1M input * $1/M + 0.2M output * $5/M = $1.00 + $1.00 = $2.00
        Assert.Equal(2.00m, haiku.CostUsd);
        Assert.Equal(1_200_000, haiku.TotalTokens);
        Assert.True(haiku.ModelKnown);

        var opus = summary.Steps[1];
        // 0.1M input * $5/M + 0.01M output * $25/M = $0.50 + $0.25 = $0.75
        Assert.Equal(0.75m, opus.CostUsd);

        Assert.Equal(1_310_000, summary.TotalTokens);
        Assert.Equal(2.75m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
    }

    [Fact]
    public void Summarize_FlagsUnknownModel_OnlyWhenStepConsumedTokens()
    {
        var record = new PipelineExecutionRecord
        {
            Steps =
            {
                // Unknown model that actually spent tokens -> flag n/a.
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality",
                    Kind = StepKind.Aspect,
                    Model = "unpriced-test-model",
                    InputTokens = 500,
                    OutputTokens = 200,
                },
                // Tool step with zero tokens and no model is NOT a pricing gap.
                new PipelineStepExecution
                {
                    StepId = "post-lint-scss",
                    Kind = StepKind.Tool,
                    Model = null,
                },
            },
        };

        var summary = PipelineCostCalculator.Summarize(record);
        Assert.True(summary.AnyModelUnknown);
        Assert.Equal(1, summary.UnpricedRuns);
        Assert.False(summary.Steps[0].ModelKnown);
        Assert.Equal(0m, summary.Steps[0].CostUsd);
        var gap = Assert.Single(summary.PricingGaps);
        Assert.Equal("unpriced-test-model", gap.ModelId);
        Assert.Equal("UnknownModel", gap.Reason);
        Assert.Equal(1, gap.AffectedRuns);
        // The zero-token tool step has an unknown (null) model but, because
        // it spent no tokens, it does not flip AnyModelUnknown on its own.
        Assert.False(summary.Steps[1].ModelKnown);
        Assert.Equal(700, summary.TotalTokens);
    }

    [Fact]
    public void SummarizeWithLedger_RepricesPreviouslyUnpricedCallWhenCatalogNowResolvesIt()
    {
        var at = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);
        var record = new PipelineExecutionRecord
        {
            StartedAt = at,
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000,
                },
            },
        };
        IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> ledger =
            new Dictionary<string, IReadOnlyList<TaskTokenCall>>
            {
                ["core-agent-run"] =
                [
                    new TaskTokenCall
                    {
                        Ts = at,
                        Model = "claude-haiku-4-5",
                        InputTokens = 1_000_000,
                        OutputTokens = 200_000,
                        EstimatedApiCostUsd = 0,
                        ModelPriced = false,
                    },
                ],
            };

        var summary = PipelineCostCalculator.SummarizeWithLedger(record, ledger);

        Assert.Equal(2.00m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
        Assert.Equal(0, summary.UnpricedRuns);
        Assert.Empty(summary.PricingGaps);
    }

    [Fact]
    public void SummarizeByModel_EmptyRecord_IsZero()
    {
        var summary = PipelineCostCalculator.SummarizeByModel(null);
        Assert.Empty(summary.Runs);
        Assert.Empty(summary.TotalByModel);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(0m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
        Assert.Equal(0, summary.UnpricedRuns);
        Assert.Empty(summary.PricingGaps);
    }

    [Fact]
    public void SummarizeByModel_GroupsStepsPerModel_BusiestFirst()
    {
        var record = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 6, 2, 10, 5, 0, DateTimeKind.Utc),
            Steps =
            {
                // Two aspect steps on Haiku ($1 / $5 per million) -> one model row.
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-requirement-fit", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                },
                // Core run on Opus ($5 / $25 per million) -> a smaller, separate row.
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-opus-4-8", InputTokens = 100_000, OutputTokens = 10_000, // $0.75
                },
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(record);

        var run = Assert.Single(summary.Runs);
        // The record carries a CompletedAt stamp, so it is the newest run but
        // not a live one.
        Assert.False(run.Current);
        Assert.Equal(1, run.Attempt);
        Assert.Equal(2, run.Models.Count);

        // Busiest model first: Haiku (2.4M tokens) before Opus (110K tokens).
        var haiku = run.Models[0];
        Assert.Equal("claude-haiku-4-5", haiku.Model);
        Assert.Equal(2, haiku.Steps);
        Assert.Equal(2_400_000, haiku.TotalTokens);
        Assert.Equal(4.00m, haiku.CostUsd); // two $2.00 steps summed
        Assert.True(haiku.ModelKnown);

        var opus = run.Models[1];
        Assert.Equal("claude-opus-4-8", opus.Model);
        Assert.Equal(0.75m, opus.CostUsd);

        Assert.Equal(2_510_000, run.TotalTokens);
        Assert.Equal(4.75m, run.TotalCostUsd);

        // Single run -> grand total equals the run.
        Assert.Equal(2_510_000, summary.TotalTokens);
        Assert.Equal(4.75m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
    }

    [Fact]
    public void SummarizeByModel_SumsModelsAcrossAllRuns_OldestFirst()
    {
        var older = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 6, 1, 9, 5, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                },
            },
        };
        var current = new PipelineExecutionRecord
        {
            Attempt = 2,
            StartedAt = new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                },
            },
            // PreviousAttempts are stored newest-first on disk.
            PreviousAttempts = { older },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(current);

        // Oldest first: Run #1 then the current Run #2.
        Assert.Equal(2, summary.Runs.Count);
        Assert.Equal(1, summary.Runs[0].Attempt);
        Assert.False(summary.Runs[0].Current);
        Assert.Equal(2, summary.Runs[1].Attempt);
        Assert.True(summary.Runs[1].Current);

        // Grand total sums the same model across both runs.
        var total = Assert.Single(summary.TotalByModel);
        Assert.Equal("claude-haiku-4-5", total.Model);
        Assert.Equal(2_400_000, total.TotalTokens);
        Assert.Equal(4.00m, total.CostUsd);
        Assert.Equal(4.00m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SummarizeByModel_RecordVariant_MarksCurrentOnlyWhileTheRunIsLive(
        bool finished,
        bool expectedCurrent)
    {
        var startedAt = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        var record = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = startedAt,
            CompletedAt = finished ? startedAt.AddMinutes(5) : null,
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000,
                },
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(record);

        var run = Assert.Single(summary.Runs);
        Assert.Equal(expectedCurrent, run.Current);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SummarizeByModel_SessionVariant_MarksCurrentOnlyWhileTheRunIsLive(
        bool finished,
        bool expectedCurrent)
    {
        var first = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var sessions = new List<SessionEvent>
        {
            new()
            {
                Ts = first,
                FinishedAt = first.AddMinutes(20),
                Kind = "start",
                Model = "claude-haiku-4-5",
                Status = "completed",
            },
            new()
            {
                Ts = first.AddHours(1),
                FinishedAt = finished ? first.AddHours(1).AddMinutes(20) : null,
                Kind = "continue",
                Model = "claude-haiku-4-5",
                Status = finished ? "completed" : "running",
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(null, sessions, null);

        Assert.Equal(2, summary.Runs.Count);
        // An older session never carries the marker, whatever the newest one does.
        Assert.False(summary.Runs[0].Current);
        Assert.Equal(expectedCurrent, summary.Runs[1].Current);
    }

    [Fact]
    public void SummarizeByModel_Gpt56SolAfterCatalogRollout_IsPricedAndHasNoGap()
    {
        var record = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "gpt-5.6-sol", InputTokens = 500_000, OutputTokens = 100_000,
                },
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(record);

        Assert.Equal(5.50m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
        Assert.Equal(0, summary.UnpricedRuns);
        Assert.Empty(summary.PricingGaps);
        var model = Assert.Single(summary.TotalByModel);
        Assert.True(model.ModelKnown);
        Assert.Equal(5.50m, model.CostUsd);
        Assert.Empty(model.PricingGaps);
    }

    [Fact]
    public void SummarizeByModel_SessionRunsUseCanonicalTaskLedgerAndMarkMissingUsage()
    {
        var first = new DateTime(2026, 8, 11, 8, 0, 0, DateTimeKind.Utc);
        var sessions = Enumerable.Range(0, 6)
            .Select(index => new SessionEvent
            {
                Ts = first.AddHours(index),
                FinishedAt = first.AddHours(index).AddMinutes(20),
                Kind = index == 0 ? "start" : "continue",
                Model = "gpt-5.6-sol",
                Status = "completed",
            })
            .ToList();
        var tokens = new TaskTokenSummary
        {
            Calls = 2,
            InputTokens = 900_000,
            OutputTokens = 110_000,
            CacheReadTokens = 300_000,
            CacheCreationTokens = 40_000,
            TotalTokens = 1_350_000,
            AllModelsPriced = true,
            LastModel = "gpt-5.6-sol",
            LastUpdate = first.AddHours(5).AddMinutes(10),
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = first.AddMinutes(10),
                    Model = "gpt-5.6-sol",
                    InputTokens = 500_000,
                    OutputTokens = 60_000,
                    CacheReadTokens = 200_000,
                    CacheCreationTokens = 10_000,
                },
                new TaskTokenCall
                {
                    Ts = first.AddHours(5).AddMinutes(10),
                    Model = "gpt-5.6-sol",
                    InputTokens = 400_000,
                    OutputTokens = 50_000,
                    CacheReadTokens = 100_000,
                    CacheCreationTokens = 30_000,
                },
            ],
        };

        var summary = PipelineCostCalculator.SummarizeByModel(null, sessions, tokens);

        Assert.Equal(6, summary.Runs.Count);
        Assert.Equal(4, summary.MissingTokenRuns);
        Assert.True(summary.Runs[0].TokenUsageAvailable);
        Assert.False(summary.Runs[1].TokenUsageAvailable);
        Assert.True(summary.Runs[5].TokenUsageAvailable);
        Assert.Equal(900_000, Assert.Single(summary.TotalByModel).InputTokens);
        Assert.Equal(110_000, summary.TotalByModel[0].OutputTokens);
        Assert.Equal(300_000, summary.TotalByModel[0].CacheReadTokens);
        Assert.Equal(40_000, summary.TotalByModel[0].CacheCreationTokens);
        Assert.Equal(1_350_000, summary.TotalTokens);
        Assert.True(summary.TotalCostUsd > 0m);
        Assert.False(summary.AnyModelUnknown);
    }

    [Fact]
    public void SummarizeByModel_SplitsOneModelIntoOneRowPerReasoningLevel()
    {
        var record = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-opus-4-8", ThinkingLevel = "medium",
                    InputTokens = 100_000, OutputTokens = 10_000,
                },
                new PipelineStepExecution
                {
                    StepId = "post-code-review", Kind = StepKind.Module,
                    Model = "claude-opus-4-8", ThinkingLevel = "high",
                    InputTokens = 40_000, OutputTokens = 4_000,
                },
                // Same model, no recorded level: its own "level unknown" row.
                new PipelineStepExecution
                {
                    StepId = "post-drift-check", Kind = StepKind.Drift,
                    Model = "claude-opus-4-8",
                    InputTokens = 10_000, OutputTokens = 1_000,
                },
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(record);

        var run = Assert.Single(summary.Runs);
        Assert.Equal(3, run.Models.Count);
        Assert.Equal(new[] { "medium", "high", null }, run.Models.Select(m => m.ThinkingLevel));
        Assert.All(run.Models, m => Assert.Equal("claude-opus-4-8", m.Model));
        // The lifetime rollup keeps the same three identities.
        Assert.Equal(3, summary.TotalByModel.Count);
        Assert.Equal(165_000, summary.TotalTokens);
        Assert.Equal(summary.TotalTokens, summary.TotalByModel.Sum(m => m.TotalTokens));
    }

    [Fact]
    public void SummarizeByModel_LedgerLevelWins_AndRunLevelOnlyFillsTheRunsOwnModel()
    {
        var first = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var sessions = new List<SessionEvent>
        {
            new()
            {
                Ts = first,
                FinishedAt = first.AddMinutes(30),
                Kind = "start",
                Model = "claude-opus-4-8",
                ThinkingLevel = "medium",
                Status = "completed",
            },
        };
        var tokens = new TaskTokenSummary
        {
            Calls = 3,
            InputTokens = 160_000,
            OutputTokens = 16_000,
            TotalTokens = 176_000,
            AllModelsPriced = true,
            LastModel = "claude-opus-4-8",
            LastUpdate = first.AddMinutes(20),
            Entries =
            [
                // Recorded level on the call wins over the run's level.
                new TaskTokenCall
                {
                    Ts = first.AddMinutes(5),
                    Model = "claude-opus-4-8",
                    ThinkingLevel = "high",
                    InputTokens = 100_000,
                    OutputTokens = 10_000,
                },
                // No recorded level, but this IS the run's model -> the run's
                // recorded level applies.
                new TaskTokenCall
                {
                    Ts = first.AddMinutes(10),
                    Model = "claude-opus-4-8",
                    InputTokens = 50_000,
                    OutputTokens = 5_000,
                },
                // A different model with no recorded level stays unknown; the
                // run's level is never borrowed for it.
                new TaskTokenCall
                {
                    Ts = first.AddMinutes(20),
                    Model = "claude-haiku-4-5",
                    InputTokens = 10_000,
                    OutputTokens = 1_000,
                },
            ],
        };

        var summary = PipelineCostCalculator.SummarizeByModel(null, sessions, tokens);

        var run = Assert.Single(summary.Runs);
        var identities = run.Models
            .Select(m => (m.Model, m.ThinkingLevel))
            .ToList();
        Assert.Contains(("claude-opus-4-8", "high"), identities);
        Assert.Contains(("claude-opus-4-8", "medium"), identities);
        Assert.Contains(("claude-haiku-4-5", (string?)null), identities);
        Assert.Equal(176_000, summary.TotalTokens);
        Assert.Equal(summary.TotalTokens, run.Models.Sum(m => m.TotalTokens));
    }

    [Fact]
    public void SummarizeByModel_SessionRunsWithoutLedgerNeverMasqueradeAsZeroUsage()
    {
        var first = new DateTime(2026, 8, 11, 8, 0, 0, DateTimeKind.Utc);
        var sessions = Enumerable.Range(0, 6)
            .Select(index => new SessionEvent
            {
                Ts = first.AddHours(index),
                FinishedAt = first.AddHours(index).AddMinutes(20),
                Kind = index == 0 ? "start" : "continue",
                Model = "gpt-5.6-sol",
                Status = "completed",
            })
            .ToList();

        var summary = PipelineCostCalculator.SummarizeByModel(null, sessions, null);

        Assert.Equal(6, summary.Runs.Count);
        Assert.Equal(6, summary.MissingTokenRuns);
        Assert.All(summary.Runs, run => Assert.False(run.TokenUsageAvailable));
        Assert.Empty(summary.TotalByModel);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(0m, summary.TotalCostUsd);
    }

    [Fact]
    public void SummarizeByModel_MixedRuns_ReportsPartialCostAndNoPriceForDateReason()
    {
        var priced = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 1, 9, 5, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000,
                },
            },
        };
        var current = new PipelineExecutionRecord
        {
            Attempt = 2,
            StartedAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "gpt-5-codex", InputTokens = 500_000, OutputTokens = 100_000,
                },
            },
            PreviousAttempts = { priced },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(current);

        Assert.Equal(2.00m, summary.TotalCostUsd);
        Assert.True(summary.AnyModelUnknown);
        Assert.Equal(1, summary.UnpricedRuns);
        var gap = Assert.Single(summary.PricingGaps);
        Assert.Equal("gpt-5-codex", gap.ModelId);
        Assert.Equal("NoPriceForDate", gap.Reason);
        Assert.Equal(1, gap.AffectedRuns);
        Assert.True(summary.Runs[1].AnyModelUnknown);
        Assert.Equal("NoPriceForDate", Assert.Single(summary.Runs[1].PricingGaps).Reason);
    }

    [Fact]
    public void SummarizeByModel_CollapsesNullModelToUnknown_AndFlagsIt()
    {
        var record = new PipelineExecutionRecord
        {
            Attempt = 1,
            StartedAt = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc),
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = null, InputTokens = 500, OutputTokens = 200,
                },
                // Zero-token step is ignored entirely.
                new PipelineStepExecution
                {
                    StepId = "post-lint-scss", Kind = StepKind.Tool, Model = null,
                },
            },
        };

        var summary = PipelineCostCalculator.SummarizeByModel(record);

        var model = Assert.Single(summary.TotalByModel);
        Assert.Equal("unknown", model.Model);
        Assert.False(model.ModelKnown);
        Assert.Equal(1, model.Steps); // the zero-token step did not count
        Assert.Equal(700, model.TotalTokens);
        Assert.Equal(0m, model.CostUsd);
        Assert.True(summary.AnyModelUnknown);
        Assert.Equal(1, summary.UnpricedRuns);
        Assert.Equal("UnknownModel", Assert.Single(summary.PricingGaps).Reason);
    }

    private static PipelineExecutionRecord RunOn(DateTime day, params PipelineStepExecution[] steps)
        => new()
        {
            JobId = Guid.NewGuid().ToString("n"),
            Project = "P",
            StartedAt = day,
            CompletedAt = day,
            Steps = steps.ToList(),
        };

    [Fact]
    public void PipelineTimeline_AggregatesPerStepKind_OverDenseDayAxis()
    {
        var now = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            // yesterday: an aspect on Haiku + the core run on Opus
            RunOn(now.AddDays(-1),
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                },
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-opus-4-8", InputTokens = 100_000, OutputTokens = 10_000, // $0.75
                }),
            // today: a second aspect run on Haiku, same cost
            RunOn(now,
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                }),
        };

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 3, nowUtc: now);

        // Dense axis: one cell per requested day even when idle.
        Assert.Equal(3, timeline.Days.Count);
        Assert.Equal(new[] { "2026-05-31", "2026-06-01", "2026-06-02" }, timeline.Days);
        Assert.True(timeline.HasData);
        Assert.Equal(2, timeline.TaskCount);

        // Two kinds present, in stable order (core before aspect).
        Assert.Equal(new[] { "core", "aspect" }, timeline.Kinds.Select(k => k.Kind).ToArray());

        var aspect = timeline.Kinds.Single(k => k.Kind == "aspect");
        Assert.Equal(4.00m, aspect.TotalCostUsd); // two $2.00 runs
        Assert.Equal(2_400_000, aspect.TotalTokens);
        // Cells align to Days: idle 05-31, $2 on 06-01, $2 on 06-02.
        Assert.Equal(0m, aspect.Cells[0].CostUsd);
        Assert.Equal(2.00m, aspect.Cells[1].CostUsd);
        Assert.Equal(2.00m, aspect.Cells[2].CostUsd);

        var core = timeline.Kinds.Single(k => k.Kind == "core");
        Assert.Equal(0.75m, core.TotalCostUsd);
        Assert.Equal(0.75m, core.Cells[1].CostUsd);

        Assert.Equal(4.75m, timeline.TotalCostUsd);
        Assert.False(timeline.AnyModelUnknown);
    }

    [Fact]
    public void PipelineTimeline_AggregatesPerStep_OverWindow_MostExpensiveFirst()
    {
        var now = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            // yesterday: the core run on Opus + an aspect on Haiku
            RunOn(now.AddDays(-1),
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-opus-4-8", InputTokens = 100_000, OutputTokens = 10_000, // $0.75
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                }),
            // today: the same aspect runs again -> its window sum doubles
            RunOn(now,
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000, // $2.00
                }),
        };

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 90, nowUtc: now);

        // Two distinct steps, summed across runs, most-expensive (by tokens) first.
        Assert.Equal(2, timeline.Steps.Count);

        var aspect = timeline.Steps[0];
        Assert.Equal("aspect-code-quality", aspect.StepId);
        Assert.Equal("aspect", aspect.Kind);
        Assert.Equal(2_400_000, aspect.TotalTokens); // two 1.2M runs
        Assert.Equal(4.00m, aspect.TotalCostUsd);
        Assert.False(aspect.AnyModelUnknown);

        var core = timeline.Steps[1];
        Assert.Equal("core-agent-run", core.StepId);
        Assert.Equal("core", core.Kind);
        Assert.Equal(110_000, core.TotalTokens);
        Assert.Equal(0.75m, core.TotalCostUsd);
    }

    [Fact]
    public void PipelineTimeline_PerStep_FlagsUnknownModel_AndIgnoresZeroTokenSteps()
    {
        var now = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            RunOn(now,
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "unpriced-test-model", InputTokens = 500, OutputTokens = 200,
                },
                // Zero-token tool step: must not produce a per-step row.
                new PipelineStepExecution
                {
                    StepId = "post-lint-scss", Kind = StepKind.Tool, Model = null,
                }),
        };

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 90, nowUtc: now);

        var step = Assert.Single(timeline.Steps);
        Assert.Equal("aspect-code-quality", step.StepId);
        Assert.Equal(700, step.TotalTokens);
        Assert.Equal(0m, step.TotalCostUsd);
        Assert.True(step.AnyModelUnknown);
        Assert.Equal(1, step.UnpricedRuns);
        Assert.Equal("UnknownModel", Assert.Single(step.PricingGaps).Reason);
    }

    [Fact]
    public void PipelineTimeline_DropsRunsOutsideWindow_AndFlagsUnknownModel()
    {
        var now = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            // Inside window, unknown model that spent tokens -> flag.
            RunOn(now,
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "unpriced-test-model", InputTokens = 500, OutputTokens = 200,
                }),
            // Outside the 2-day window -> excluded entirely.
            RunOn(now.AddDays(-10),
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000,
                }),
        };

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 2, nowUtc: now);

        Assert.Equal(1, timeline.TaskCount); // the old run dropped out
        Assert.True(timeline.AnyModelUnknown);
        Assert.Equal(0m, timeline.TotalCostUsd); // only the unpriced run survived
        Assert.Equal(700, timeline.TotalTokens);
        Assert.Equal(1, timeline.UnpricedRuns);
        Assert.Equal("UnknownModel", Assert.Single(timeline.PricingGaps).Reason);
    }

    [Fact]
    public void PipelineTimeline_MixedRuns_ReportsPartialCostAndAffectedRunCount()
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            RunOn(now.AddHours(-2),
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "claude-haiku-4-5", InputTokens = 1_000_000, OutputTokens = 200_000,
                }),
            RunOn(now.AddHours(-1),
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core,
                    Model = "gpt-5-codex", InputTokens = 500_000, OutputTokens = 100_000,
                }),
        };

        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 7, nowUtc: now);

        Assert.Equal(2.00m, timeline.TotalCostUsd);
        Assert.Equal(1, timeline.UnpricedRuns);
        var gap = Assert.Single(timeline.PricingGaps);
        Assert.Equal("gpt-5-codex", gap.ModelId);
        Assert.Equal("NoPriceForDate", gap.Reason);
        Assert.Equal(1, gap.AffectedRuns);

        var core = Assert.Single(timeline.Kinds);
        Assert.Equal(1, core.UnpricedRuns);
        Assert.Equal(1, core.Cells.Single(cell => cell.TotalTokens > 0).UnpricedRuns);
    }

    [Fact]
    public void PipelineTimeline_EmptyWhenNoRecords()
    {
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", Array.Empty<PipelineExecutionRecord>(), days: 7);
        Assert.False(timeline.HasData);
        Assert.Empty(timeline.Kinds);
        Assert.Empty(timeline.Steps);
        Assert.Equal(0, timeline.TaskCount);
        Assert.Equal(7, timeline.Days.Count); // axis still dense
    }

    [Fact]
    public void PipelineTimeline_ReceiptCallsPopulateCurrentStepKinds()
    {
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            ReceiptEntry("AGT-2542", "agent:codex", now.AddHours(-3), 1_000, 100),
            ReceiptEntry("AGT-2542", "support:code-review", now.AddHours(-2), 500, 50),
            ReceiptEntry("AGT-2542", "orchestrator:agent-taskboard", now.AddHours(-1), 200, 20),
        };

        var records = ProjectPipelineCostService.BuildReceiptRecords("P", entries);
        var timeline = ProjectPipelineCostService.BuildFromRecords("P", records, days: 7, nowUtc: now);

        Assert.Equal(1_870, timeline.TotalTokens);
        Assert.Equal(new[] { "core", "aspect", "orchestrator" }, timeline.Kinds.Select(kind => kind.Kind));
        Assert.Equal(now.AddHours(-1).ToString("o"), timeline.Freshness.AsOf);
        Assert.Equal(1, timeline.TaskCount);
    }

    private static OrchestratorLogEntry ReceiptEntry(
        string jobId,
        string participant,
        DateTime at,
        int input,
        int output) => new()
        {
            Ts = at,
            JobId = jobId,
            ParticipantId = participant,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = "gpt-5.3-codex",
                InputTokens = input,
                OutputTokens = output,
            },
        };
}
