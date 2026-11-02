namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// AGT-3420: an agent aspect whose CLI refuses its model failed on the
/// project's configuration, not on the host's toolchain. The runner reports
/// it as <see cref="Classification"/> instead of <c>ToolUnavailable</c>, so it
/// drains no host capability; the plan builder and the settings route keep a
/// wrong pair out of new plans, and this covers plans sealed before that.
/// </summary>
public static class ReviewModelConfigurationPolicy
{
    public const string Classification = "ModelConfigurationFault";

    private static readonly string[] UnknownModelSignatures =
    [
        "model is not supported",
        "unknown model",
        "invalid model",
        "model not found",
        "model_not_found",
        "unsupported model",
        "model does not exist",
        "issue with the selected model",
    ];

    /// <summary>
    /// True when the failed agent call named a model the CLI does not know:
    /// the model belongs to another CLI family, or the CLI output says so.
    /// </summary>
    public static bool IsModelUnknownToCli(string? cliType, string? model, string? output)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (FamilyMismatch(cliType, model.Trim())) return true;
        return !string.IsNullOrEmpty(output)
               && UnknownModelSignatures.Any(signature =>
                   output.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }

    public static string Summary(string stepId, string? cliType, string? model)
        => $"Review aspect '{stepId}' named model '{model}', which the review CLI '{cliType}' does not know. "
           + "This is a configuration fault of the project's review step setting "
           + "(pipelineSteps.<step>.model / cliType or ReviewDecisionOrchestrator:AspectModel / Cli), "
           + "not a host tool failure.";

    private static bool FamilyMismatch(string? cliType, string model)
    {
        var cli = (cliType ?? string.Empty).Trim().ToLowerInvariant();
        var family = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? "claude"
            : model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ? "codex"
            : model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ? "gemini"
            : null;
        return family is not null
               && cli is "claude" or "codex" or "gemini"
               && !string.Equals(cli, family, StringComparison.Ordinal);
    }
}
