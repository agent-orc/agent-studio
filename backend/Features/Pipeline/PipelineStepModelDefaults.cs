namespace AgentStudio.Pipeline;

/// <summary>Built-in pipeline routes shared by preview and runtime.</summary>
public static class PipelineStepModelDefaults
{
    public const string DefaultCli = CliTypes.Codex;
    public const string SupportModel = ModelIds.Gpt62Sol;
    public const string SupportThinkingLevel = "low";
    public const string QualityModel = ModelIds.Gpt62Sol;
    public const string QualityThinkingLevel = "medium";

    /// <summary>The built-in model for a step that exposes model configuration.</summary>
    public static string? RuntimeDefaultFor(PipelineStep step) => step.Kind switch
    {
        StepKind.Orchestrator when IsSummaryAssessment(step) => ModelIds.ClaudeHaiku55,
        StepKind.Aspect => QualityModel,
        StepKind.Drift when step.Id != PipelineCatalogue.DriftCodePatternStepId => QualityModel,
        StepKind.Module when step.Id == PipelineCatalogue.PreOrchestratorPrepStepId => SupportModel,
        StepKind.Orchestrator when step.Id is PipelineCatalogue.CodeReviewGradeStepId
            or PipelineCatalogue.OrchestratorDecisionStepId
            or PipelineCatalogue.ConflictResolutionStepId
            or PipelineCatalogue.PostAbortReviewStepId
            or PipelineCatalogue.UiVisualVerdictStepId
            or PipelineCatalogue.TaskSpawnerStepId
            or PipelineCatalogue.FailureInterventionStepId => QualityModel,
        _ => null,
    };

    /// <summary>The runtime-default CLI for an LLM-backed step.</summary>
    public static string? RuntimeDefaultCliFor(PipelineStep step) =>
        !UsesModel(step) ? null : IsSummaryAssessment(step) ? CliTypes.Claude : DefaultCli;

    /// <summary>The runtime-default reasoning level for an LLM-backed step.</summary>
    public static string? RuntimeDefaultThinkingLevelFor(PipelineStep step)
    {
        if (!UsesModel(step)) return null;
        if (IsSummaryAssessment(step)) return PipelineCatalogue.SummaryAssessmentThinkingLevel;
        return step.Id == PipelineCatalogue.PreOrchestratorPrepStepId
            ? SupportThinkingLevel
            : QualityThinkingLevel;
    }

    internal static ((string CliType, string Model, string? ThinkingLevel) Route, string Source) ResolveRoute(
        ProjectSettings? settings, PipelineStep step, IConfiguration configuration, ReviewAspectModelChoice? host = null)
    {
        if (step.Kind == StepKind.Aspect)
        {
            var (choice, thinking, source) = ReviewAspectModelPolicy.ResolveRoute(host ?? ReviewAspectModelPolicy.ResolveHost(configuration), settings, step);
            return ((choice.CliType, choice.Model, thinking), source);
        }

        if (IsSummaryAssessment(step))
        {
            var summary = SummaryGenerationService.ResolveStepSelection(settings);
            return ((summary.Cli, summary.Model, summary.ThinkingLevel), summary.ModelSource);
        }

        var model = RuntimeDefaultFor(step)!;
        var cli = RuntimeDefaultCliFor(step)!;
        var thinkingDefault = RuntimeDefaultThinkingLevelFor(step);
        switch (step.Id)
        {
            case PipelineCatalogue.CodeReviewGradeStepId:
                (model, cli) = CodeReviewGradeModelSelector.Resolve(
                    configuration["CodeReviewStep:DefaultModel"], configuration["CodeReviewStep:DefaultCli"]);
                break;
            case PipelineCatalogue.TaskSpawnerStepId:
                (model, cli, thinkingDefault) = TaskSpawnerModelSelector.Resolve(
                    configuration["TaskSpawnerStep:DefaultModel"], configuration["TaskSpawnerStep:DefaultCli"],
                    configuration["TaskSpawnerStep:DefaultThinkingLevel"]);
                break;
            case PipelineCatalogue.OrchestratorDecisionStepId:
                cli = ReviewDecisionOrchestrator.NormalizeReviewCliType(configuration[ReviewAspectModelPolicy.CliSetting]);
                model = configuration["ReviewDecisionOrchestrator:Model"] ?? ReviewAspectModelPolicy.DefaultModelFor(cli);
                break;
        }

        var resolution = PipelineStepConfigResolver.ResolveModelWithSource(settings, step, model);
        cli = PipelineStepConfigResolver.ResolveCliType(settings, step) ?? cli;
        if (step.Id == PipelineCatalogue.CodeReviewGradeStepId)
            thinkingDefault = CodeReviewGradeModelSelector.ThinkingDefault(
                cli, configuration["CodeReviewStep:DefaultThinkingLevel"]);
        if (step.Id == PipelineCatalogue.OrchestratorDecisionStepId)
            thinkingDefault = configuration["ReviewDecisionOrchestrator:ThinkingLevel"]
                ?? (cli == CliTypes.Codex ? QualityThinkingLevel : null);
        var level = PipelineStepConfigResolver.ResolveThinkingLevel(settings, step, cli, resolution.Model, thinkingDefault);
        return ((cli, resolution.Model, level), resolution.Source);
    }

    private static bool IsSummaryAssessment(PipelineStep step) =>
        string.Equals(step.Id, PipelineCatalogue.SummaryAssessmentStepId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the step resolves a per-step LLM model pre-run.</summary>
    public static bool UsesModel(PipelineStep step) => RuntimeDefaultFor(step) is not null;

    /// <summary>
    /// Resolve the effective model a step would run with right now (before any
    /// run), layering the per-project + per-step overrides over the step's
    /// runtime default via <see cref="PipelineStepConfigResolver"/>. Returns null
    /// when the step resolves no per-step LLM model (see the class summary).
    /// </summary>
    public static PipelineStepConfigResolver.ModelResolution? Resolve(
        ProjectSettings? settings, PipelineStep step)
    {
        var runtimeDefault = RuntimeDefaultFor(step);
        if (runtimeDefault is null) return null;
        return PipelineStepConfigResolver.ResolveModelWithSource(settings, step, runtimeDefault);
    }
}
