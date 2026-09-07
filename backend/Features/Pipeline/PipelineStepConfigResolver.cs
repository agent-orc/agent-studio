
namespace AgentStudio.Pipeline;

/// <summary>
/// Pure resolver for the per-project pipeline-step overrides stored in
/// <see cref="ProjectSettings.PipelineSteps"/>. It is the single place
/// that turns "what did the operator configure for this project" into the
/// concrete <c>enabled</c> / <c>model</c> / <c>mode</c> a step runs with.
///
/// <para>
/// Lookup is tolerant: a step can be addressed by its full pipeline id
/// (<c>aspect-code-quality</c>, <c>post-lint-scss</c>) or by the bare
/// suffix (<c>code-quality</c>, <c>lint-scss</c>) so per-project config
/// stays terse, matching the same convenience the per-task
/// <see cref="PostStepConfigResolver"/> already offers. All lookups are
/// case-insensitive.
/// </para>
///
/// <para>
/// Resolution order:
/// <list type="bullet">
///   <item><c>enabled</c>: step override -&gt; <c>true</c> (steps run by default).</item>
///   <item><c>model</c>: step override -&gt; the step's own catalogue
///         <see cref="PipelineStep.Model"/> -&gt; project
///         <see cref="ProjectSettings.OrchestratorModel"/> -&gt; the
///         caller-supplied runtime default.</item>
///   <item><c>mode</c>: step override -&gt; caller-supplied built-in default.</item>
/// </list>
/// </para>
/// </summary>
public static class PipelineStepConfigResolver
{
    public const string ModelSourceStep = "step";
    public const string ModelSourceProject = "project";
    public const string ModelSourceGlobal = "global";
    public const string ModelSourceCatalogue = "catalogue";
    public const string ModelSourceRuntime = "runtime";

    public sealed record ModelResolution(
        string Model,
        string Source,
        string? StepOverride,
        string? ProjectOverride,
        string? GlobalDefault,
        string? CatalogueDefault,
        string RuntimeDefault);

    public sealed record ThinkingLevelResolution(
        string? ThinkingLevel,
        string Source,
        string? StepOverride,
        string? ProjectOverride,
        string? GlobalDefault,
        string? ModelDefault);

    /// <summary>Prefixes the catalogue uses; stripped to support bare-suffix lookup.</summary>
    private static readonly string[] StepIdPrefixes = { "aspect-", "post-", "pre-" };

    /// <summary>
    /// Resolve the per-step override for a given step id, accepting either
    /// the full pipeline id or the bare suffix. Returns null when the
    /// project has no override for the step.
    /// </summary>
    public static PipelineStepSetting? Lookup(ProjectSettings? settings, string stepId)
    {
        if (IsRepositoryOwnedAnalysisStep(stepId)) return null;
        var map = settings?.PipelineSteps;
        if (map == null || map.Count == 0 || string.IsNullOrWhiteSpace(stepId)) return null;

        if (TryGet(map, stepId, out var direct)) return direct;

        // full id -> bare suffix (e.g. "aspect-code-quality" -> "code-quality")
        foreach (var prefix in StepIdPrefixes)
        {
            if (stepId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var bare = stepId.Substring(prefix.Length);
                if (TryGet(map, bare, out var bareHit)) return bareHit;
            }
        }

        // bare suffix -> full id (e.g. "code-quality" -> "aspect-code-quality")
        foreach (var prefix in StepIdPrefixes)
        {
            if (TryGet(map, prefix + stepId, out var prefixedHit)) return prefixedHit;
        }

        return null;
    }

    /// <summary>
    /// True unless the project explicitly disabled the step. This overload has
    /// no catalogue step in hand, so an absent override defaults to on - the
    /// right behavior for the always-on aspect / tool / orchestrator steps.
    /// For an opt-in step (drift) prefer the <see cref="IsEnabled(ProjectSettings?, PipelineStep)"/>
    /// overload so the step's own <see cref="PipelineStep.DefaultEnabled"/> is honored.
    /// </summary>
    public static bool IsEnabled(ProjectSettings? settings, string stepId)
        => Lookup(settings, stepId)?.Enabled ?? true;

    /// <summary>
    /// True when the step should run for this project: an explicit project
    /// override wins, otherwise the step's own <see cref="PipelineStep.DefaultEnabled"/>.
    /// This is what makes the drift post-steps default off (opt-in) while every
    /// other step stays on by default.
    /// </summary>
    public static bool IsEnabled(ProjectSettings? settings, PipelineStep step)
        => Lookup(settings, step.Id)?.Enabled ?? step.DefaultEnabled;

    /// <summary>
    /// Whether an operator may disable this catalogue step. The core agent run
    /// and Dossier contract are mandatory, and the pre-loop guard mirrors an
    /// always-on safety circuit breaker, so none may expose an enable/disable
    /// control.
    /// </summary>
    public static bool CanDisable(PipelineStep step)
        => step.Kind is not (StepKind.Core or StepKind.Analysis)
           && !string.Equals(step.Id, PipelineCatalogue.LoopGuardStepId, StringComparison.Ordinal)
           && !string.Equals(step.Id, PipelineCatalogue.DossierMaintenanceStepId, StringComparison.Ordinal)
           && !string.Equals(step.Id, PipelineCatalogue.UiIterationArtifactStepId, StringComparison.Ordinal)
           && !string.Equals(step.Id, PipelineCatalogue.UiVisualCaptureStepId, StringComparison.Ordinal)
           && !string.Equals(step.Id, PipelineCatalogue.UiVisualVerdictStepId, StringComparison.Ordinal)
           && !string.Equals(step.Id, PipelineCatalogue.UiHumanReviewGateStepId, StringComparison.Ordinal);

    /// <summary>
    /// Quality analysis activation belongs exclusively to the versioned
    /// repository policy. Central project settings deliberately ignore and
    /// reject these ids.
    /// </summary>
    public static bool IsRepositoryOwnedAnalysisStep(string stepId) =>
        PipelineCatalogue.QualityAnalysisStepIds.Contains(stepId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve and clamp the UI iteration cap from the named routing step.</summary>
    public static int ResolveUiMaxIterations(ProjectSettings? settings)
        => Math.Clamp(
            Lookup(settings, PipelineCatalogue.UiPipelineRoutingStepId)?.MaxIterations
                ?? AgentStudio.Runner.UiIterationGate.DefaultMaxIterations,
            AgentStudio.Runner.UiIterationGate.MinimumIterations,
            AgentStudio.Runner.UiIterationGate.MaximumIterations);

    /// <summary>
    /// Resolve the model for a step addressed by id, with no catalogue
    /// step in hand (the aspect runner only knows the bare aspect id).
    /// </summary>
    public static string ResolveModel(
        ProjectSettings? settings,
        string stepId,
        string runtimeDefault,
        string? globalDefault = null)
    {
        return ResolveModelWithSource(settings, stepId, runtimeDefault, globalDefault).Model;
    }

    /// <summary>
    /// Resolve the model for a catalogue step, layering the step's own
    /// default <see cref="PipelineStep.Model"/> between the project
    /// override and the project orchestrator model.
    /// </summary>
    public static string ResolveModel(
        ProjectSettings? settings,
        PipelineStep step,
        string runtimeDefault,
        string? globalDefault = null)
    {
        return ResolveModelWithSource(settings, step, runtimeDefault, globalDefault).Model;
    }

    public static ModelResolution ResolveModelWithSource(
        ProjectSettings? settings,
        string stepId,
        string runtimeDefault,
        string? globalDefault = null)
    {
        return ResolveModelCore(
            stepOverride: Lookup(settings, stepId)?.Model,
            catalogueDefault: null,
            projectOverride: settings?.OrchestratorModel,
            globalDefault: globalDefault,
            runtimeDefault: runtimeDefault);
    }

    public static ModelResolution ResolveModelWithSource(
        ProjectSettings? settings,
        PipelineStep step,
        string runtimeDefault,
        string? globalDefault = null)
    {
        return ResolveModelCore(
            stepOverride: Lookup(settings, step.Id)?.Model,
            catalogueDefault: step.Model,
            projectOverride: settings?.OrchestratorModel,
            globalDefault: globalDefault,
            runtimeDefault: runtimeDefault);
    }

    public static string? ResolveThinkingLevel(
        ProjectSettings? settings,
        string stepId,
        string? cliType,
        string? resolvedModel,
        string? globalDefault = null)
        => ResolveThinkingLevelWithSource(settings, stepId, cliType, resolvedModel, globalDefault).ThinkingLevel;

    public static string? ResolveThinkingLevel(
        ProjectSettings? settings,
        PipelineStep step,
        string? cliType,
        string? resolvedModel,
        string? globalDefault = null)
        => ResolveThinkingLevelWithSource(settings, step, cliType, resolvedModel, globalDefault).ThinkingLevel;

    public static ThinkingLevelResolution ResolveThinkingLevelWithSource(
        ProjectSettings? settings,
        string stepId,
        string? cliType,
        string? resolvedModel,
        string? globalDefault = null)
        => ResolveThinkingLevelCore(
            stepOverride: Lookup(settings, stepId)?.ThinkingLevel,
            projectOverride: settings?.OrchestratorThinkingLevel,
            globalDefault: globalDefault,
            cliType: cliType,
            resolvedModel: resolvedModel);

    public static ThinkingLevelResolution ResolveThinkingLevelWithSource(
        ProjectSettings? settings,
        PipelineStep step,
        string? cliType,
        string? resolvedModel,
        string? globalDefault = null)
        => ResolveThinkingLevelCore(
            stepOverride: Lookup(settings, step.Id)?.ThinkingLevel,
            projectOverride: settings?.OrchestratorThinkingLevel,
            globalDefault: globalDefault,
            cliType: cliType,
            resolvedModel: resolvedModel);

    private static ModelResolution ResolveModelCore(
        string? stepOverride,
        string? catalogueDefault,
        string? projectOverride,
        string? globalDefault,
        string runtimeDefault)
    {
        var runtime = string.IsNullOrWhiteSpace(runtimeDefault) ? "" : runtimeDefault.Trim();
        var step = Normalize(stepOverride);
        var project = Normalize(projectOverride);
        var global = Normalize(globalDefault);
        var catalogue = Normalize(catalogueDefault);

        if (step is not null) return new(step, ModelSourceStep, step, project, global, catalogue, runtime);
        if (project is not null) return new(project, ModelSourceProject, null, project, global, catalogue, runtime);
        if (global is not null) return new(global, ModelSourceGlobal, null, null, global, catalogue, runtime);
        if (catalogue is not null) return new(catalogue, ModelSourceCatalogue, null, null, null, catalogue, runtime);
        return new(runtime, ModelSourceRuntime, null, null, null, null, runtime);
    }

    private static ThinkingLevelResolution ResolveThinkingLevelCore(
        string? stepOverride,
        string? projectOverride,
        string? globalDefault,
        string? cliType,
        string? resolvedModel)
    {
        var step = Normalize(stepOverride);
        var project = Normalize(projectOverride);
        var global = Normalize(globalDefault);
        var modelDefault = ModelMetadataRegistry.DefaultThinkingLevelForCli(cliType, resolvedModel);

        if (step is not null)
            return new(ModelMetadataRegistry.ResolveThinkingLevel(cliType, resolvedModel, step), ModelSourceStep, step, project, global, modelDefault);
        if (project is not null)
            return new(ModelMetadataRegistry.ResolveThinkingLevel(cliType, resolvedModel, project), ModelSourceProject, null, project, global, modelDefault);
        if (global is not null)
            return new(ModelMetadataRegistry.ResolveThinkingLevel(cliType, resolvedModel, global), ModelSourceGlobal, null, null, global, modelDefault);
        return new(modelDefault, ModelSourceCatalogue, null, null, null, modelDefault);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Resolve the per-step run condition for a step addressed by id. Returns
    /// null when the project set no condition (interpreted as "always run").
    /// </summary>
    public static PipelineStepCondition? ResolveCondition(ProjectSettings? settings, string stepId)
        => Lookup(settings, stepId)?.Condition;

    /// <summary>
    /// Resolve the per-step run condition for a catalogue step. Returns null
    /// when the project set no condition (interpreted as "always run").
    /// </summary>
    public static PipelineStepCondition? ResolveCondition(ProjectSettings? settings, PipelineStep step)
        => Lookup(settings, step.Id)?.Condition;

    /// <summary>
    /// Resolve an optional LLM prompt override. Null means the runner should use
    /// the catalogue prompt template or its runtime-built default.
    /// </summary>
    public static string? ResolvePrompt(ProjectSettings? settings, string stepId)
        => Normalize(Lookup(settings, stepId)?.Prompt);

    /// <summary>
    /// Resolve an optional LLM prompt override for a catalogue step.
    /// </summary>
    public static string? ResolvePrompt(ProjectSettings? settings, PipelineStep step)
        => ResolvePrompt(settings, step.Id);

    /// <summary>
    /// Resolve an optional per-step CLI override. Null means the runner should
    /// keep the catalogue or runtime default CLI for that step.
    /// </summary>
    public static string? ResolveCliType(ProjectSettings? settings, string stepId)
        => Normalize(Lookup(settings, stepId)?.CliType);

    /// <summary>
    /// Resolve an optional per-step CLI override for a catalogue step.
    /// </summary>
    public static string? ResolveCliType(ProjectSettings? settings, PipelineStep step)
        => ResolveCliType(settings, step.Id);

    /// <summary>
    /// Whether a catalogue step should actually run for this task run: the step
    /// must be enabled (honouring its <see cref="PipelineStep.DefaultEnabled"/>)
    /// and its configured run condition must match the run facts in
    /// <paramref name="ctx"/>. A step with no condition runs whenever enabled.
    /// </summary>
    public static bool ShouldRun(ProjectSettings? settings, PipelineStep step, PipelineStepConditionContext ctx)
        => IsEnabled(settings, step)
           && PipelineStepConditionEvaluator.Matches(ResolveCondition(settings, step), ctx);

    /// <summary>
    /// Whether a step addressed by id should run for this task run. Uses the
    /// id-only enablement default (absent override = on), so prefer the
    /// <see cref="ShouldRun(ProjectSettings?, PipelineStep, PipelineStepConditionContext)"/>
    /// overload for opt-in steps whose default-off lives on the catalogue step.
    /// </summary>
    public static bool ShouldRun(ProjectSettings? settings, string stepId, PipelineStepConditionContext ctx)
        => IsEnabled(settings, stepId)
           && PipelineStepConditionEvaluator.Matches(ResolveCondition(settings, stepId), ctx);

    /// <summary>
    /// Resolve the gate mode for a step. Project override wins; otherwise
    /// the caller-supplied built-in default (today
    /// <see cref="PostStepConfigResolver.BuiltInDefault"/>).
    /// </summary>
    public static PostStepMode ResolveMode(ProjectSettings? settings, string stepId, PostStepMode builtInDefault)
    {
        var raw = Lookup(settings, stepId)?.Mode;
        return PostStepConfigResolver.ParseMode(raw) ?? builtInDefault;
    }

    private static bool TryGet(IReadOnlyDictionary<string, PipelineStepSetting> map, string key, out PipelineStepSetting? value)
    {
        // The map is deserialized from project-settings.json with default
        // (ordinal) comparison, so do an explicit case-insensitive scan
        // rather than trusting the dictionary's comparer.
        foreach (var kv in map)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}
