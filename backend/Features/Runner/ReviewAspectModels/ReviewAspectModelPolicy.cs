using AgentStudio.Pipeline;

namespace AgentStudio.Runner;

/// <summary>
/// The CLI and model a sealed review aspect runs with. <see cref="Fault"/>
/// names the setting whose pair the model metadata registry rejected; the
/// pair itself is then already the valid fallback, never the rejected one.
/// </summary>
public sealed record ReviewAspectModelChoice(string CliType, string Model, string? Fault = null);

/// <summary>
/// AGT-3420: a review aspect model always belongs to the review CLI that runs
/// it. <c>ReviewDecisionOrchestrator:AspectModel</c> and
/// <c>ReviewDecisionOrchestrator:Cli</c> used to resolve independently, so a
/// host with the Claude CLI sealed plans with a GPT model; every aspect exited
/// after about 2.5 seconds as <c>ToolUnavailable</c> and the card escalated
/// after three retries (AOW-15, 2026-11-02). This pure policy derives the
/// default model from the CLI through <see cref="ModelMetadataRegistry"/> and
/// turns a pair the registry rejects into a planning fault that names the
/// setting, so a wrong pair never reaches a sealed plan.
/// </summary>
public static class ReviewAspectModelPolicy
{
    public const string CliSetting = "ReviewDecisionOrchestrator:Cli";
    public const string ModelSetting = "ReviewDecisionOrchestrator:AspectModel";

    /// <summary>
    /// The default aspect model of a review CLI: the bounded support model
    /// when it belongs to the CLI, otherwise the registry default of the CLI.
    /// </summary>
    public static string DefaultModelFor(string cliType)
    {
        var support = PipelineStepModelDefaults.SupportModel;
        return ModelMetadataRegistry.IsCompatibleWithCli(cliType, support)
            ? support
            : ModelMetadataRegistry.DefaultForCli(cliType) ?? support;
    }

    /// <summary>
    /// The host default pair. A blank model takes the CLI default; a model the
    /// registry rejects for the CLI is a planning fault naming
    /// <see cref="ModelSetting"/>, and the CLI default model replaces it.
    /// </summary>
    public static ReviewAspectModelChoice ResolveDefault(string? configuredCli, string? configuredModel)
    {
        var cli = ReviewDecisionOrchestrator.NormalizeReviewCliType(configuredCli);
        var model = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel.Trim();
        if (model is null)
            return new(cli, DefaultModelFor(cli));
        return ModelMetadataRegistry.IsCompatibleWithCli(cli, model)
            ? new(cli, model)
            : new(cli, DefaultModelFor(cli), PairFault(cli, model, ModelSetting, CliSetting));
    }

    /// <summary>
    /// The pair one aspect step runs with. A step CLI without an explicit model
    /// takes that CLI's default model. An explicit model the registry rejects
    /// for the CLI falls back to the valid host default pair, with a fault
    /// that names the project setting the model came from.
    /// </summary>
    public static ReviewAspectModelChoice ResolveStep(
        ReviewAspectModelChoice hostDefault,
        string stepId,
        string? stepCli,
        PipelineStepConfigResolver.ModelResolution model)
    {
        var cli = string.IsNullOrWhiteSpace(stepCli)
            ? hostDefault.CliType
            : ReviewDecisionOrchestrator.NormalizeReviewCliType(stepCli);
        if (model.Source == PipelineStepConfigResolver.ModelSourceRuntime)
        {
            return string.Equals(cli, hostDefault.CliType, StringComparison.OrdinalIgnoreCase)
                ? hostDefault with { Fault = null }
                : new(cli, DefaultModelFor(cli));
        }
        if (ModelMetadataRegistry.IsCompatibleWithCli(cli, model.Model))
            return new(cli, model.Model);
        var cliSetting = string.IsNullOrWhiteSpace(stepCli) ? CliSetting : $"pipelineSteps.{stepId}.cliType";
        return hostDefault with { Fault = PairFault(cli, model.Model, ModelSettingFor(stepId, model.Source), cliSetting) };
    }

    /// <summary>
    /// The refusal the settings route returns for an explicit step pair the
    /// registry rejects, or null when the pair is valid or incomplete.
    /// </summary>
    public static string? SettingsRefusal(string stepId, string? cliType, string? model)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(model)
            || !CliTypes.IsValid(cliType))
            return null;
        var cli = CliTypes.Normalize(cliType);
        return ModelMetadataRegistry.IsCompatibleWithCli(cli, model.Trim())
            ? null
            : PairFault(cli, model.Trim(), $"pipelineSteps.{stepId}.model", $"pipelineSteps.{stepId}.cliType");
    }

    private static string ModelSettingFor(string stepId, string source) => source switch
    {
        PipelineStepConfigResolver.ModelSourceStep => $"pipelineSteps.{stepId}.model",
        PipelineStepConfigResolver.ModelSourceProject => "orchestratorModel",
        PipelineStepConfigResolver.ModelSourceCatalogue => $"pipeline catalogue model of {stepId}",
        _ => "the global model default",
    };

    private static string PairFault(string cli, string model, string modelSetting, string cliSetting)
        => $"Review aspect model '{model}' from {modelSetting} does not belong to the review CLI '{cli}' "
           + $"from {cliSetting}. Set {modelSetting} to a {cli} model or change {cliSetting}.";
}
