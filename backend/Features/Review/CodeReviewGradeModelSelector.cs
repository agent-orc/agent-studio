
namespace AgentStudio.Review;

/// <summary>Resolves the standard Codex route for the automatic review grade.</summary>
public static class CodeReviewGradeModelSelector
{
    public const string DefaultModel = ModelIds.Gpt62Sol;
    public const string DefaultCli = CliTypes.Codex;
    public static string? DefaultThinkingLevel =>
        ModelMetadataRegistry.DefaultThinkingLevelForCli(DefaultCli, DefaultModel);

    /// <summary>
    /// Resolve the effective (model, cli) for the grade pass, layering the
    /// per-deployment config over the standard defaults. A blank or
    /// whitespace-only override falls back to the default rather than running
    /// the grade on an empty model id.
    /// </summary>
    public static (string Model, string Cli) Resolve(string? configuredModel, string? configuredCli)
    {
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultModel
            : configuredModel.Trim();
        var cli = string.IsNullOrWhiteSpace(configuredCli)
            ? DefaultCli
            : configuredCli.Trim();
        return (model, cli);
    }
}
