using AgentStudio.Pipeline;

namespace AgentStudio.Review;

/// <summary>Selects the automatic grade and manual review defaults.</summary>
public static class CodeReviewGradeModelSelector
{
    public const string DefaultCli = PipelineStepModelDefaults.DefaultCli;
    public const string DefaultModel = PipelineStepModelDefaults.QualityModel;
    public const string DefaultThinkingLevel = PipelineStepModelDefaults.QualityThinkingLevel;

    public static string? ThinkingDefault(string cli, string? configuredThinking) =>
        string.IsNullOrWhiteSpace(configuredThinking)
            ? (CliTypes.Normalize(cli) == CliTypes.Codex ? DefaultThinkingLevel : null)
            : configuredThinking.Trim();

    public static (string Model, string Cli) Resolve(string? configuredModel, string? configuredCli)
    {
        var cli = string.IsNullOrWhiteSpace(configuredCli) ? DefaultCli : configuredCli.Trim();
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? (CliTypes.Normalize(cli) == DefaultCli ? DefaultModel : ModelMetadataRegistry.DefaultForCli(cli) ?? DefaultModel)
            : configuredModel.Trim();
        return (model, cli);
    }
}
