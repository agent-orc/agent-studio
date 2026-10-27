namespace AgentStudio.TaskServer.Contracts;

/// <summary>One exact-tip T3 check. A skipped check is evidence but does not change the verdict.</summary>
public sealed record PostMergeRunRecord(
    string Id, string Project, string Kind, string TipSha, string? WindowFromSha,
    DateTimeOffset? WindowOpenedAtUtc, DateTimeOffset? WindowClosedAtUtc, int MergeCount,
    IReadOnlyList<string> Commands, IReadOnlyList<PostMergeCommandVerdict> Verdicts,
    string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    string? Reason = null);

public sealed record PostMergeCommandVerdict(string Command, int ExitCode, bool TimedOut);

public sealed record PostMergeProjectState(
    string Project, string DevelopState, string? LastGreenSha, string? LastWindowTipSha,
    PostMergeRunRecord? ActiveRun, IReadOnlyList<PostMergeRunRecord> Runs)
{
    /// <summary>Repair card escalated for this red develop incident. Its current lane decides the fallback.</summary>
    public string? AttributionEscalatedTaskId { get; init; }
    /// <summary>Post-merge window sizing derived from the project's look-ahead settings.</summary>
    public int? WindowMaxMerges { get; init; }
    public int? WindowMinutes { get; init; }
    public static PostMergeProjectState Empty(string project) => new(project, "green", null, null, null, []);
}
