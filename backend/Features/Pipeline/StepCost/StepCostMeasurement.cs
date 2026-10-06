namespace AgentStudio.Pipeline;

/// <summary>Stable wire tokens for <see cref="PipelineStepExecution.CostBasis"/>.</summary>
public static class StepCostBasis
{
    /// <summary>The step called a model; tokens and price come from that call.</summary>
    public const string Model = "model";
    /// <summary>The step ran without a model call; its zero cost is a measurement.</summary>
    public const string Deterministic = "deterministic";
    /// <summary>The step was skipped, not applicable, or a planned stub.</summary>
    public const string NotRun = "not-run";
}

/// <summary>
/// Pure measurement policy for one pipeline step execution: whether the step
/// is model-backed, how a terminal row's cost is stamped, when a write starts
/// a new execution, and when a row is missing its model. Every step write
/// passes through <see cref="PipelineExecutionLog.RecordStep"/>, which applies
/// this policy, so executors report what ran and the log owns the arithmetic.
/// Measurement only: nothing here feeds a decision, budget or threshold.
/// </summary>
public static class StepCostMeasurement
{
    /// <summary>Bound on <see cref="PipelineStepExecution.EarlierRuns"/> per row.</summary>
    public const int MaxEarlierRuns = 50;

    /// <summary>
    /// Orchestrator- and drift-kind steps that decide by rule, analyse by rule
    /// or wait for a human and never call a model. They record an explicit zero.
    /// </summary>
    private static readonly HashSet<string> DeterministicDecisionSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        PipelineCatalogue.OrchestratorReviewStepId,
        PipelineCatalogue.ConceptReviewStepId,
        PipelineCatalogue.ConceptSightReviewGateStepId,
        PipelineCatalogue.UiHumanReviewGateStepId,
        PipelineCatalogue.DriftCodePatternStepId,
    };

    /// <summary>
    /// Module, tool and analysis steps that call a model. Prompt enrichment,
    /// orchestrator prep and model qualification are rule-based selectors
    /// even though they name a model, so they are not listed.
    /// </summary>
    private static readonly HashSet<string> ModelBackedAuxiliarySteps = new(StringComparer.OrdinalIgnoreCase)
    {
        PipelineCatalogue.QualityModelReviewStepId,
    };

    /// <summary>
    /// True when a run of this step is expected to call a model. Core and
    /// aspect steps always are; drift and orchestrator steps are unless they
    /// are one of the rule or human gates; module, tool and analysis steps are
    /// deterministic unless they are one of the named model-backed exceptions.
    /// </summary>
    public static bool IsModelBacked(string stepId, StepKind kind) => kind switch
    {
        StepKind.Core or StepKind.Aspect => true,
        StepKind.Drift or StepKind.Orchestrator => !DeterministicDecisionSteps.Contains(stepId),
        _ => ModelBackedAuxiliarySteps.Contains(stepId),
    };

    /// <summary>
    /// The core run carries its own accumulated tokens forward on every
    /// write, so its earlier executions are counted but not priced again.
    /// </summary>
    public static bool AccumulatesInPlace(StepKind kind) => kind == StepKind.Core;

    public static bool IsTerminal(PipelineStepStatus status) => status is
        PipelineStepStatus.Passed or PipelineStepStatus.Failed
        or PipelineStepStatus.Skipped or PipelineStepStatus.NotApplicable;

    public static long Tokens(PipelineStepExecution row)
        => row.InputTokens + row.OutputTokens + row.CacheReadTokens + row.CacheCreationTokens;

    public static long Tokens(PipelineStepRunSummary run)
        => run.InputTokens + run.OutputTokens + run.CacheReadTokens + run.CacheCreationTokens;

    /// <summary>
    /// Stamp <see cref="PipelineStepExecution.CostBasis"/>,
    /// <see cref="PipelineStepExecution.EstimatedCostUsd"/> and
    /// <see cref="PipelineStepExecution.ModelPriced"/> on a terminal row. A
    /// non-terminal row is returned unchanged. An unpriced model leaves the
    /// cost null with <c>ModelPriced = false</c>; it is never a zero.
    /// </summary>
    public static PipelineStepExecution Stamp(PipelineStepExecution row)
    {
        if (!IsTerminal(row.Status)) return row;

        var tokens = Tokens(row);
        var hasModel = !string.IsNullOrWhiteSpace(row.Model);
        var modelBacked = IsModelBacked(row.StepId, row.Kind);
        var explicitlyDeterministic = string.Equals(
            row.CostBasis, StepCostBasis.Deterministic, StringComparison.Ordinal);

        if (tokens > 0 || (modelBacked && hasModel && !explicitlyDeterministic && Ran(row.Status)))
        {
            var estimate = TokenPricing.Estimate(
                row.Model,
                row.InputTokens,
                row.OutputTokens,
                row.CacheReadTokens,
                row.CacheCreationTokens,
                row.StartedAt ?? row.CompletedAt);
            return row with
            {
                CostBasis = StepCostBasis.Model,
                ModelPriced = estimate.ModelKnown,
                EstimatedCostUsd = estimate.ModelKnown ? Round(estimate.Total) : null,
            };
        }

        if (!Ran(row.Status))
        {
            return row with
            {
                CostBasis = StepCostBasis.NotRun,
                ModelPriced = null,
                EstimatedCostUsd = 0m,
            };
        }

        if (!modelBacked || explicitlyDeterministic)
        {
            return row with
            {
                CostBasis = StepCostBasis.Deterministic,
                ModelPriced = null,
                EstimatedCostUsd = 0m,
            };
        }

        // Model-backed, ran, and the executor reported neither model nor
        // tokens: leave the gap visible instead of inventing a zero.
        return row with { CostBasis = null, ModelPriced = null, EstimatedCostUsd = null };
    }

    /// <summary>
    /// True when a terminal, executed row of a model-backed step names no
    /// model and was not explicitly recorded as a rule decision. This is the
    /// measurement defect the pipeline log warns about and tests guard.
    /// </summary>
    public static bool IsMissingModel(PipelineStepExecution row)
        => row.Status is PipelineStepStatus.Passed or PipelineStepStatus.Failed
           && IsModelBacked(row.StepId, row.Kind)
           && string.IsNullOrWhiteSpace(row.Model)
           && !string.Equals(row.CostBasis, StepCostBasis.Deterministic, StringComparison.Ordinal);

    /// <summary>
    /// Decide whether <paramref name="incoming"/> starts a new execution of the
    /// step held in <paramref name="existing"/>, and carry the run history.
    /// A new execution begins when a step enters Running from any state other
    /// than the same running execution, or when a terminal row with its own
    /// start time replaces a terminal row that started at another time. The
    /// replaced execution moves into <see cref="PipelineStepExecution.EarlierRuns"/>.
    /// </summary>
    public static PipelineStepExecution CarryRuns(PipelineStepExecution? existing, PipelineStepExecution incoming)
    {
        var earlier = existing?.EarlierRuns ?? [];
        var existingRuns = existing?.Runs ?? 0;
        // Legacy rows written before run counting carry Runs = 0 even when
        // they executed; count that execution once.
        if (existing is not null && existingRuns == 0 && Executed(existing))
            existingRuns = earlier.Count + 1;

        if (existing is null || !Executed(existing))
        {
            return incoming with
            {
                EarlierRuns = earlier.Count == 0 ? null : earlier,
                Runs = earlier.Count + (Executed(incoming) ? 1 : 0),
            };
        }

        var startsNewRun = incoming.Status == PipelineStepStatus.Running
            ? !(existing.Status == PipelineStepStatus.Running && existing.StartedAt == incoming.StartedAt)
            : existing.Status != PipelineStepStatus.Running
              && Executed(incoming)
              && incoming.StartedAt.HasValue
              && existing.StartedAt.HasValue
              && incoming.StartedAt != existing.StartedAt;

        if (!startsNewRun)
        {
            return incoming with
            {
                EarlierRuns = earlier.Count == 0 ? null : earlier,
                Runs = Math.Max(existingRuns, 1),
            };
        }

        var history = earlier.Append(Summarize(existing)).ToList();
        if (history.Count > MaxEarlierRuns)
            history = history.Skip(history.Count - MaxEarlierRuns).ToList();
        return incoming with
        {
            EarlierRuns = history,
            Runs = existingRuns + 1,
        };
    }

    /// <summary>
    /// Every execution of the row, oldest first: the earlier runs followed by
    /// the row itself when it executed. Used by the cost rollups and the
    /// transition view so a repeated decision is neither lost nor merged.
    /// </summary>
    public static IReadOnlyList<PipelineStepRunSummary> Executions(PipelineStepExecution row)
    {
        var list = new List<PipelineStepRunSummary>(row.EarlierRuns ?? []);
        if (Executed(row)) list.Add(Summarize(row, keepTokens: true));
        return list;
    }

    /// <summary>How many times the step executed in this attempt, legacy rows included.</summary>
    public static int RunCount(PipelineStepExecution row)
        => row.Runs > 0 ? row.Runs : Executions(row).Count;

    private static PipelineStepRunSummary Summarize(PipelineStepExecution row, bool keepTokens = false)
    {
        var stamped = Stamp(row);
        var dropTokens = !keepTokens && AccumulatesInPlace(row.Kind);
        return new PipelineStepRunSummary
        {
            Status = row.Status,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            DurationMs = row.DurationMs,
            Model = row.Model,
            ThinkingLevel = row.ThinkingLevel,
            ModelSource = row.ModelSource,
            InputTokens = dropTokens ? 0 : row.InputTokens,
            OutputTokens = dropTokens ? 0 : row.OutputTokens,
            CacheReadTokens = dropTokens ? 0 : row.CacheReadTokens,
            CacheCreationTokens = dropTokens ? 0 : row.CacheCreationTokens,
            CostBasis = stamped.CostBasis,
            EstimatedCostUsd = dropTokens ? null : stamped.EstimatedCostUsd,
            ModelPriced = stamped.ModelPriced,
            Verdict = row.Verdict,
            Reason = row.Reason,
            EvidenceRef = row.EvidenceRef,
        };
    }

    /// <summary>
    /// The step did work: it is running, finished with an outcome, or spent
    /// tokens even though it reports Skipped (a model that judged the step
    /// not relevant still ran).
    /// </summary>
    private static bool Executed(PipelineStepExecution row)
        => row.Status is PipelineStepStatus.Running or PipelineStepStatus.Passed or PipelineStepStatus.Failed
           || Tokens(row) > 0;

    private static bool Ran(PipelineStepStatus status)
        => status is PipelineStepStatus.Passed or PipelineStepStatus.Failed;

    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
