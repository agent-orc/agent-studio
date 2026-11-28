using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

public sealed record EffectiveModelRoute(string CliType, string? Model, string? ThinkingLevel);

public sealed record EffectiveModelSelection(
    string Consumer, string? StepId, EffectiveModelRoute Configured,
    EffectiveModelRoute? Effective, string Rule, string? Reason);

/// <summary>Read-only projection of the same admission result used at launch.</summary>
public sealed class ModelSelectionEffectiveService(
    ProjectSettingsService projects,
    QuotaAdmissionService admission,
    IConfiguration configuration)
{
    public IReadOnlyList<EffectiveModelSelection> Get(string? project, string? pipelineType)
    {
        var settings = string.IsNullOrWhiteSpace(project)
            ? null : PipelineTypeSettings.ForType(projects.Get(project), pipelineType);
        var pipeline = PipelineCatalogue.ForType(pipelineType);
        var rows = new List<EffectiveModelSelection>();
        var hostDefault = ReviewAspectModelPolicy.ResolveHost(configuration);
        foreach (var step in pipeline.AllSteps.Where(PipelineStepModelDefaults.UsesModel))
        {
            var consumer = step.Id switch
            {
                PipelineCatalogue.RemoteConceptFitStepId => SelectionConsumers.ConceptFit,
                PipelineCatalogue.CodeReviewGradeStepId => SelectionConsumers.ReviewGrade,
                PipelineCatalogue.ConflictResolutionStepId => SelectionConsumers.MergeResolver,
                _ when step.Kind == StepKind.Aspect => SelectionConsumers.ReviewAspect,
                _ => "one-shot",
            };
            var (route, source) = PipelineStepModelDefaults.ResolveRoute(settings, step, configuration, hostDefault);
            rows.Add(Select(consumer, step.Id, route.CliType, route.Model, route.ThinkingLevel, project,
                source == PipelineStepConfigResolver.ModelSourceStep, pipelineType));
        }

        var deepSetting = settings?.PipelineSteps?.GetValueOrDefault("post-deep-review");
        var deepModel = deepSetting?.Model ?? AgentStudio.OrchestrationDecisions.DeepReviewPolicy.Model;
        var deepCli = deepSetting?.CliType
            ?? (deepModel.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? CliTypes.Claude : CliTypes.Codex);
        rows.Add(Select("review-deep", "post-deep-review", deepCli, deepModel,
            deepSetting?.ThinkingLevel ?? AgentStudio.OrchestrationDecisions.DeepReviewPolicy.DefaultThinkingLevel,
            project, deepSetting is not null, pipelineType));
        rows.Add(Select("chat", null, CliTypes.Codex,
            PipelineStepModelDefaults.QualityModel, PipelineStepModelDefaults.QualityThinkingLevel, project, false, pipelineType));
        rows.Add(Select("one-shot:title", "title-generation", CliTypes.Claude,
            configuration["TitleGeneration:Model"] ?? configuration["ClaudeCli:SummaryModel"]
                ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku),
            null, project, false, pipelineType));
        rows.Add(Select("one-shot:tags", "periodic-tag-maintenance", CliTypes.Claude,
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet), "high", project, false, pipelineType));
        var autoTag = AgentStudio.Tags.AutoTagClassifier.ResolveRoute(
            string.IsNullOrWhiteSpace(project) ? null : projects.Get(project));
        rows.Add(Select("one-shot:auto-tag", AgentStudio.Tags.AutoTagClassifier.StepId,
            autoTag.Cli, autoTag.Model, null, project, false, pipelineType));
        rows.Add(Select("card-creation", null, CliTypes.Claude,
            ModelMetadataRegistry.DefaultForCli(CliTypes.Claude), null, project, false, pipelineType));
        rows.Add(Select("coding", null, CliTypes.Claude,
            ModelMetadataRegistry.DefaultForCli(CliTypes.Claude), null, project, false, pipelineType));
        return rows;
    }

    private EffectiveModelSelection Select(string consumer, string? stepId, string cli,
        string? model, string? thinking, string? project, bool pinned, string? pipelineType)
    {
        var configured = new EffectiveModelRoute(cli, model, thinking);
        var plan = admission.Selection.Select(new ModelSelectionCall(new SelectionRequest
        {
            Consumer = consumer,
            Project = project,
            PipelineType = pipelineType,
            StepId = stepId,
            IsExplicitPin = pinned,
            Pins = new SelectionPins(cli, model, thinking),
            Required = new SelectionRequirements(SameClassOnly: SelectionConsumers.IsReview(consumer)),
        }, $"effective:{consumer}", Preview: true)).Admission;
        var rule = plan.IsFallback
            ? plan.ModelFallback?.Reason == "operator-preference" ? "operator-switch" : "quota-cap"
            : admission.IsOperatorSwitchActive(cli) ? "operator-switch"
            : !plan.ShouldLaunch ? "quota-cap"
            : pinned ? "pin" : "consumer-default";
        return new EffectiveModelSelection(consumer, stepId, configured,
            plan.ShouldLaunch ? new EffectiveModelRoute(plan.CliType, plan.Model, plan.ThinkingLevel) : null,
            rule, plan.ShouldLaunch ? plan.IsFallback ? plan.Reason : null : plan.Reason);
    }
}
