namespace AgentStudio.Scenario;

public enum ScenarioStepStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>
/// Outcome of one step. <see cref="Evidence"/> holds report-relative paths so a
/// Markdown report links to files that travel with it. <see cref="Facts"/> holds
/// everything the action observed, including facts no expectation covers, so a
/// value the scenario deliberately reports instead of asserting is still visible
/// in the report and its drift can be tracked across runs.
/// </summary>
public sealed record ScenarioStepResult(
    string Id,
    string Title,
    string Action,
    ScenarioStepStatus Status,
    ScenarioStepDisposition Disposition,
    TimeSpan Duration,
    IReadOnlyDictionary<string, ScenarioFactValue> Facts,
    IReadOnlyList<ScenarioAssertionOutcome> Assertions,
    IReadOnlyList<string> Evidence,
    string? Failure)
{
    private static readonly Dictionary<string, ScenarioFactValue> NoFacts =
        new(StringComparer.Ordinal);

    public static ScenarioStepResult Skipped(PlannedScenarioStep planned)
        => new(
            planned.Step.Id,
            planned.Step.Title,
            planned.Step.Action,
            ScenarioStepStatus.Skipped,
            planned.Disposition,
            TimeSpan.Zero,
            NoFacts,
            [],
            [],
            planned.Reason);
}

public sealed record ScenarioRunResult(
    string ScenarioId,
    string Title,
    ScenarioTargetKind Target,
    ScenarioLevel Level,
    string ServerVersion,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ScenarioStepResult> Steps)
{
    public int Failures => Steps.Count(step => step.Status == ScenarioStepStatus.Failed);
    public int SkippedCount => Steps.Count(step => step.Status == ScenarioStepStatus.Skipped);
    public int PassedCount => Steps.Count(step => step.Status == ScenarioStepStatus.Passed);
    public bool Passed => Failures == 0;
}
