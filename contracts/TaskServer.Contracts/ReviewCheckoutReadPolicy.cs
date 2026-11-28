namespace AgentStudio.TaskServer.Contracts;

public static class ReviewCheckoutReadPolicy
{
    public const string FailureClassification = "ReviewerCheckoutReadUnavailable";

    public static bool IsFailure(ReviewReportRequest report)
        => report.Outcome == "ReviewInfra" && report.FailureClassification == FailureClassification;

    // Older plans have no declaration. Their agent prompts can use the
    // checkout, including legacy concept prompts upgraded by the executor.
    public static bool NeedsSandbox(ReviewCommandDto command, string? provider)
        => ReviewCommandKinds.IsAgent(command.ExecutionKind)
            && (command.RequiresCheckoutReads != false
                || string.Equals(command.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase))
            && string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase);

    public static bool Allows(ReviewCommandDto command, string? provider, IReadOnlySet<string> capabilities)
        => !NeedsSandbox(command, provider) || capabilities.Contains(ReviewCapabilities.CodexCheckoutRead);
}
