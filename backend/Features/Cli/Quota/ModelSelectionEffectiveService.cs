using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;

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
        var hostDefault = ReviewAspectModelPolicy.ResolveDefault(
            configuration[ReviewAspectModelPolicy.CliSetting] ?? PipelineStepModelDefaults.DefaultCli,
            configuration[ReviewAspectModelPolicy.ModelSetting]);

        foreach (var step in pipeline.AllSteps.Where(PipelineStepModelDefaults.UsesModel))
        {
            var consumer = step.Kind == StepKind.Aspect ? "review-aspect"
                : step.Id == PipelineCatalogue.CodeReviewGradeStepId ? "review-grade"
                : step.Id == PipelineCatalogue.ConflictResolutionStepId ? "merge-resolver"
                : "one-shot";
            string cli;
            string model;
            string? thinking;
            var gradeDefault = AgentStudio.Review.CodeReviewGradeModelSelector.Resolve(
                configuration["CodeReviewStep:DefaultModel"], configuration["CodeReviewStep:DefaultCli"]);
            var runtimeModel = step.Id == PipelineCatalogue.CodeReviewGradeStepId
                ? gradeDefault.Model : PipelineStepModelDefaults.RuntimeDefaultFor(step)!;
            var resolution = PipelineStepConfigResolver.ResolveModelWithSource(settings, step, runtimeModel);
            if (step.Kind == StepKind.Aspect)
            {
                var choice = ReviewAspectModelPolicy.ResolveStep(hostDefault, step.Id,
                    PipelineStepConfigResolver.ResolveCliType(settings, step), resolution);
                cli = choice.CliType;
                model = choice.Model;
                thinking = PipelineStepConfigResolver.ResolveThinkingLevel(settings, step, cli, model,
                    PipelineStepModelDefaults.SupportThinkingLevel);
            }
            else
            {
                cli = PipelineStepConfigResolver.ResolveCliType(settings, step)
                    ?? (step.Id == PipelineCatalogue.CodeReviewGradeStepId
                        ? gradeDefault.Cli : PipelineStepModelDefaults.RuntimeDefaultCliFor(step)!);
                model = resolution.Model;
                thinking = PipelineStepConfigResolver.ResolveThinkingLevel(settings, step, cli, model,
                    PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(step));
            }
            rows.Add(Select(consumer, step.Id, cli, model, thinking, project,
                resolution.Source != PipelineStepConfigResolver.ModelSourceRuntime));
        }

        var deepSetting = settings?.PipelineSteps?.GetValueOrDefault("post-deep-review");
        var deepModel = deepSetting?.Model ?? AgentStudio.OrchestrationDecisions.DeepReviewPolicy.Model;
        var deepCli = deepSetting?.CliType
            ?? (deepModel.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? CliTypes.Claude : CliTypes.Codex);
        rows.Add(Select("review-deep", "post-deep-review", deepCli, deepModel,
            deepSetting?.ThinkingLevel ?? "medium",
            project, deepSetting is not null));
        rows.Add(Select("chat", null, CliTypes.Codex,
            ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), null, project, false));
        rows.Add(Select("one-shot:title", "title-generation", CliTypes.Claude,
            configuration["TitleGeneration:Model"] ?? configuration["ClaudeCli:SummaryModel"]
                ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku),
            null, project, false));
        rows.Add(Select("one-shot:tag-maintenance", "periodic-tag-maintenance", CliTypes.Claude,
            ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet), "high", project, false));
        var autoTag = AgentStudio.Tags.AutoTagClassifier.ResolveRoute(
            string.IsNullOrWhiteSpace(project) ? null : projects.Get(project));
        rows.Add(Select("one-shot:auto-tag", AgentStudio.Tags.AutoTagClassifier.StepId,
            autoTag.Cli, autoTag.Model, null, project, false));
        rows.Add(Select("card-creation", null, CliTypes.Claude,
            ModelMetadataRegistry.DefaultForCli(CliTypes.Claude), null, project, false));
        rows.Add(Select("coding", null, CliTypes.Claude,
            ModelMetadataRegistry.DefaultForCli(CliTypes.Claude), null, project, false));
        return rows;
    }

    private EffectiveModelSelection Select(string consumer, string? stepId, string cli,
        string? model, string? thinking, string? project, bool pinned)
    {
        var configured = new EffectiveModelRoute(cli, model, thinking);
        var plan = admission.Plan(new QuotaAdmissionRequest(cli, model, thinking, project, 0,
            $"effective:{consumer}", SameClassOnly: consumer.StartsWith("review-", StringComparison.Ordinal)));
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
