using AgentStudio.Pipeline;

namespace AgentStudio.Review;

/// <summary>Input, output and cache tokens of one review step execution.</summary>
public sealed record ReviewStepTokens(long Input, long Output, long CacheRead, long CacheCreation)
{
    /// <summary>Everything the model read: uncached input plus both cache dimensions.</summary>
    public long Prompt => Input + CacheRead + CacheCreation;
}

/// <summary>
/// One finished execution of a review step (Dossier AGT-3400, slice S2).
/// <see cref="Tokens"/> and <see cref="WallMinutes"/> are null when the row
/// does not carry them, which is unknown and never zero.
/// </summary>
public sealed record ReviewStepSample(
    string JobId, string RoundId, string Step, DateTime CompletedAt,
    double? WallMinutes, ReviewStepTokens? Tokens, bool Passed,
    string? ReviewedResultSha, int? FilesReviewed);

/// <summary>Review step names and the review tier each one belongs to under PK4 R-B.</summary>
public static class ReviewSteps
{
    public const string ToolCommands = "tool commands";
    public const string Grade = "grade";
    public const string Decision = "decision";
    public const string RequirementFit = "requirement-fit";

    public const string CardCheckTier = "card-check";
    public const string PackageTier = "package";
    public const string MemberTier = "member";
    public const string OtherTier = "other";

    /// <summary>The four catalogue aspects whose share of reviewOther is the Dossier's <c>a</c>.</summary>
    public static readonly IReadOnlySet<string> CatalogueAspects = PipelineCatalogue.AspectStepIds
        .Select(id => id["aspect-".Length..]).ToHashSet(StringComparer.Ordinal);

    /// <summary>The review step a pipeline step id belongs to, or null for a non-review step.</summary>
    public static string? Of(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return null;
        var id = stepId.Trim().ToLowerInvariant();
        if (id.StartsWith("aspect-", StringComparison.Ordinal)) return id["aspect-".Length..];
        return id switch
        {
            "post-deep-review" => "deep-review",
            PipelineCatalogue.CodeReviewGradeStepId => Grade,
            PipelineCatalogue.OrchestratorReviewStepId or PipelineCatalogue.OrchestratorDecisionStepId => Decision,
            PipelineCatalogue.BuildTestGateStepId => ToolCommands,
            _ => null,
        };
    }

    /// <summary>
    /// The tier of a step under PK4 R-B: requirement fit and the tool commands
    /// stay in the per-card check, the context-heavy aspects and the grade move
    /// to the package, and the engine decision stays per member.
    /// </summary>
    public static string Tier(string step) => step switch
    {
        ToolCommands or RequirementFit => CardCheckTier,
        "code-quality" or "documentation-impact" or "tests-and-evidence" or Grade => PackageTier,
        Decision => MemberTier,
        _ => OtherTier,
    };
}

/// <summary>
/// Reads the review step executions of one job from its pipeline record:
/// every attempt, every earlier run of a step and the current row, each as
/// <see cref="StepCostMeasurement.Executions"/> lists them.
/// </summary>
public static class ReviewStepSampleReader
{
    public static IReadOnlyList<ReviewStepSample> Read(string jobId, PipelineExecutionRecord record, ITokenPricing pricing)
    {
        var samples = new List<ReviewStepSample>();
        foreach (var run in record.PreviousAttempts.Append(record))
        foreach (var step in run.Steps)
        {
            if (ReviewSteps.Of(step.StepId) is not { } name) continue;
            var modelBacked = StepCostMeasurement.IsModelBacked(step.StepId, step.Kind);
            var executions = StepCostMeasurement.Executions(pricing, step);
            for (var index = 0; index < executions.Count; index++)
            {
                var execution = executions[index];
                var current = index == executions.Count - 1 && executions.Count > (step.EarlierRuns?.Count ?? 0);
                if (execution.CompletedAt is not { } completed || execution.Status == PipelineStepStatus.Running) continue;
                if (CarriedOver(step, execution, current)) continue;
                samples.Add(new ReviewStepSample(jobId,
                    execution.ExecutionAttemptId ?? $"{jobId}:run:{run.Attempt}", name,
                    completed.ToUniversalTime(), WallMinutes(execution, modelBacked),
                    Tokens(execution, modelBacked), execution.Status == PipelineStepStatus.Passed,
                    execution.ReviewedResultSha, execution.ReviewedFileCount));
            }
        }
        return samples;
    }

    /// <summary>
    /// Tokens of one execution. A positive count is a measurement; a step that
    /// calls no model, or a row stamped deterministic, is a measured zero. A
    /// model-backed row with no counts is a legacy row or a gap: unknown.
    /// </summary>
    internal static ReviewStepTokens? Tokens(PipelineStepRunSummary execution, bool modelBacked)
    {
        var tokens = new ReviewStepTokens(execution.InputTokens, execution.OutputTokens,
            execution.CacheReadTokens, execution.CacheCreationTokens);
        if (tokens.Prompt + tokens.Output > 0) return tokens;
        return !modelBacked || execution.CostBasis is StepCostBasis.Deterministic or StepCostBasis.NotRun
            ? tokens : null;
    }

    /// <summary>
    /// Wall minutes from the recorded duration, else from start and finish. A
    /// model-backed row without either is unknown; a rule step may take zero.
    /// </summary>
    internal static double? WallMinutes(PipelineStepRunSummary execution, bool modelBacked)
    {
        if (execution.DurationMs > 0) return execution.DurationMs / 60000d;
        if (execution.StartedAt is { } started && execution.CompletedAt is { } completed)
        {
            var minutes = (completed - started).TotalMinutes;
            if (minutes > 0 || (minutes == 0 && !modelBacked)) return minutes;
        }
        return null;
    }

    // A verdict carried from an earlier round ran nothing in this round. The
    // current row names it; an archived run is recognised by its empty shape.
    private static bool CarriedOver(PipelineStepExecution step, PipelineStepRunSummary execution, bool current)
        => current && string.Equals(step.ExecutionLocation, "carried-over", StringComparison.Ordinal)
           || step.Kind == StepKind.Aspect && string.IsNullOrWhiteSpace(execution.Model)
              && execution.DurationMs == 0 && execution.StartedAt == execution.CompletedAt
              && StepCostMeasurement.Tokens(execution) == 0;
}
