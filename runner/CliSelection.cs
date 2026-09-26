namespace AgentRunner;

/// <summary>
/// Resolves the typed provider selection handed to CodingAgentRunner. This type
/// deliberately owns no argv construction or process launch; those are CAR
/// responsibilities.
/// </summary>
public static class CliSelection
{
    public sealed record Selection(
        string FileName,
        string CliType,
        string? Model,
        string? ThinkingLevel,
        bool SpecApplied,
        string Source,
        string? Note = null);

    public const string ClaudeCli = "claude";
    public const string CodexCli = "codex";

    public static Selection Resolve(RunnerOptions options, RunSpecDto? runSpec)
    {
        var configuredType = NormalizeCliType(options.CliType)
            ?? throw new ArgumentException($"Unsupported RUNNER_CLI_TYPE '{options.CliType}'.");
        var requestedType = NormalizeCliType(runSpec?.CliType);
        var cliType = requestedType ?? configuredType;
        var fileName = cliType == CodexCli ? options.CodexCliBin : options.ClaudeCliBin;
        var model = string.IsNullOrWhiteSpace(runSpec?.Model) ? null : runSpec!.Model!.Trim();
        var thinking = string.IsNullOrWhiteSpace(runSpec?.ThinkingLevel)
            ? null
            : runSpec!.ThinkingLevel!.Trim();
        var specApplied = requestedType is not null || model is not null || thinking is not null;

        return new Selection(
            fileName,
            cliType,
            model,
            thinking,
            specApplied,
            specApplied ? "card" : "runner-options");
    }

    public static string? NormalizeCliType(string? cliType) => cliType?.Trim().ToLowerInvariant() switch
    {
        ClaudeCli => ClaudeCli,
        CodexCli => CodexCli,
        _ => null,
    };
}
