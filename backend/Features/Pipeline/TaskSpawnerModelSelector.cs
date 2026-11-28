namespace AgentStudio.Pipeline;

/// <summary>Resolves the Codex route for the follow-up drafting step.</summary>
public static class TaskSpawnerModelSelector
{
    public const string DefaultCli = CliTypes.Codex;
    public const string DefaultModel = ModelIds.Gpt62Sol;
    public const string DefaultThinkingLevel = "medium";

    public static (string Model, string Cli, string ThinkingLevel) Resolve(
        string? configuredModel, string? configuredCli, string? configuredThinkingLevel)
    {
        var model = string.IsNullOrWhiteSpace(configuredModel) ? DefaultModel : configuredModel.Trim();
        var cli = string.IsNullOrWhiteSpace(configuredCli) ? DefaultCli : configuredCli.Trim();
        var thinking = string.IsNullOrWhiteSpace(configuredThinkingLevel)
            ? DefaultThinkingLevel
            : configuredThinkingLevel.Trim();
        return (model, cli, thinking);
    }
}
