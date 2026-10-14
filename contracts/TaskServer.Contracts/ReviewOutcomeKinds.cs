using AgentStudio.Shared;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Outcome tokens persisted in review reports and completion receipts.</summary>
public static class ReviewOutcomeKinds
{
    public const string Pass = nameof(ReviewTerminalOutcome.Pass);
    public const string ProductFailure = nameof(ReviewTerminalOutcome.ProductFailure);
    public const string IntegrationBranchDefect = nameof(ReviewTerminalOutcome.IntegrationBranchDefect);
    public const string ReviewInfra = "ReviewInfra";
}

public static class CompletionOutcomeKinds
{
    public const string Done = "done";
    public const string NoOp = "noop";
    public const string Blocked = "blocked";
    public const string NeedsInput = "needsinput";
    public const string Unknown = "unknown";
    public const string EnvironmentFailure = "environmentfailure";
    public const string Stopped = "stopped";
    public const string MechanicalFallback = "mechanicalfallback";
}
