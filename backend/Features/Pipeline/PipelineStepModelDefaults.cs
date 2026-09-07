

namespace AgentStudio.Pipeline;

/// <summary>
/// Maps a catalogue <see cref="PipelineStep"/> to the runtime-default model its
/// LLM-backed work falls back to when neither a per-step nor a per-project
/// override is set. It mirrors the exact value each runtime call site already
/// passes to <see cref="PipelineStepConfigResolver.ResolveModel(ProjectSettings?, PipelineStep, string, string?)"/>,
/// so the pre-run pipeline view can show the same effective model the run would
/// actually use. Bounded supporting steps use the Codex mini family at
/// <c>high</c>; the operator-facing grade and task-spawner judgments use the
/// live-discovered
/// Codex flagship and its top advertised reasoning level.
///
/// <para>
/// Deterministic steps (loop guard, reissue check, git-commit attribution,
/// lint-scss, regression radar, the early post-core completeness gate) and the
/// core agent run do not resolve a per-step LLM model through the resolver -
/// the core run uses the task's own model and the deterministic gate rows are
/// policy code - so they return null here and the pre-run view shows no
/// resolved model for them.
/// </para>
/// </summary>
public static class PipelineStepModelDefaults
{
    public const string DefaultCli = CliTypes.Codex;
    public const string SupportThinkingLevel = "high";

    /// <summary>
    /// Bounded supporting tier: the newest member of the Codex mini family the
    /// installed CLI offers. Resolved per read (not a const) for the same reason
    /// <see cref="QualityModel"/> is - the pre-run pipeline view must show the
    /// model the run would actually use.
    /// </summary>
    public static string SupportModel => ModelFamilyResolver.Resolve(ModelFamilies.GptMini);

    public static string QualityModel =>
        ModelMetadataRegistry.DefaultForCli(DefaultCli)
        ?? ModelFamilyResolver.Resolve(ModelFamilies.GptFlagship);

    public static string QualityThinkingLevel =>
        ModelMetadataRegistry.DefaultThinkingLevelForCli(DefaultCli, QualityModel) ?? "high";

    /// <summary>
    /// The runtime-default model for a step, or null when the step does not
    /// resolve a per-step LLM model through <see cref="PipelineStepConfigResolver"/>.
    /// </summary>
    public static string? RuntimeDefaultFor(PipelineStep step) => step.Kind switch
    {
        StepKind.Aspect => SupportModel,
        StepKind.Drift => SupportModel,
        StepKind.Module when string.Equals(
            step.Id, PipelineCatalogue.PreOrchestratorPrepStepId, StringComparison.OrdinalIgnoreCase)
            => SupportModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.CodeReviewGradeStepId, StringComparison.OrdinalIgnoreCase)
            => QualityModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.OrchestratorDecisionStepId, StringComparison.OrdinalIgnoreCase)
            => SupportModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.ConflictResolutionStepId, StringComparison.OrdinalIgnoreCase)
            => SupportModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.PostAbortReviewStepId, StringComparison.OrdinalIgnoreCase)
            => SupportModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.UiVisualVerdictStepId, StringComparison.OrdinalIgnoreCase)
            => SupportModel,
        StepKind.Orchestrator when string.Equals(
            step.Id, PipelineCatalogue.TaskSpawnerStepId, StringComparison.OrdinalIgnoreCase)
            => QualityModel,
        _ => null,
    };

    /// <summary>The runtime-default CLI for an LLM-backed step.</summary>
    public static string? RuntimeDefaultCliFor(PipelineStep step) =>
        UsesModel(step) ? DefaultCli : null;

    /// <summary>The runtime-default reasoning level for an LLM-backed step.</summary>
    public static string? RuntimeDefaultThinkingLevelFor(PipelineStep step)
    {
        if (!UsesModel(step)) return null;
        return string.Equals(step.Id, PipelineCatalogue.CodeReviewGradeStepId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(step.Id, PipelineCatalogue.TaskSpawnerStepId, StringComparison.OrdinalIgnoreCase)
            ? QualityThinkingLevel
            : SupportThinkingLevel;
    }

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
