namespace AgentStudio.Pipeline;

/// <summary>Selects the follow-up task synthesis defaults.</summary>
public static class TaskSpawnerModelSelector
{
    public const string DefaultCli = PipelineStepModelDefaults.DefaultCli;
    public const string DefaultModel = PipelineStepModelDefaults.QualityModel;
    public const string DefaultThinkingLevel = PipelineStepModelDefaults.QualityThinkingLevel;

    public static (string Model, string Cli, string ThinkingLevel) Resolve(
        string? configuredModel, string? configuredCli, string? configuredThinkingLevel)
    {
        var cli = string.IsNullOrWhiteSpace(configuredCli) ? DefaultCli : configuredCli.Trim();
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? (CliTypes.Normalize(cli) == DefaultCli ? DefaultModel : ModelMetadataRegistry.DefaultForCli(cli) ?? DefaultModel)
            : configuredModel.Trim();
        var thinking = string.IsNullOrWhiteSpace(configuredThinkingLevel)
            ? DefaultThinkingLevel
            : configuredThinkingLevel.Trim();
        return (model, cli, thinking);
    }
}
