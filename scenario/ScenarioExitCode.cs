namespace AgentStudio.Scenario;

/// <summary>
/// Stable process exit codes. CI gates, the card build gate, and operator
/// scripts branch on these values, so a code never changes meaning. New
/// outcomes take the next free number instead of reusing one.
/// </summary>
public static class ScenarioExitCode
{
    /// <summary>Every planned step passed.</summary>
    public const int Passed = 0;

    /// <summary>At least one planned step failed an assertion or threw.</summary>
    public const int StepFailed = 1;

    /// <summary>Command line arguments were missing, unknown, or contradictory.</summary>
    public const int UsageError = 2;

    /// <summary>The scenario document was missing, unreadable, or invalid.</summary>
    public const int DocumentInvalid = 3;

    /// <summary>The target could not be brought up, so no step was evaluated.</summary>
    public const int TargetUnavailable = 4;
}
