namespace AgentStudio.TaskServer.Contracts;

/// <summary>One exact-tip T3 check. A skipped check is evidence but does not change the verdict.</summary>
public sealed record PostMergeRunRecord(
    string Id, string Project, string Kind, string TipSha, string? WindowFromSha,
    DateTimeOffset? WindowOpenedAtUtc, DateTimeOffset? WindowClosedAtUtc, int MergeCount,
    IReadOnlyList<string> Commands, IReadOnlyList<PostMergeCommandVerdict> Verdicts,
    string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    string? Reason = null,
    PostMergeFailureEvidence? Failure = null);

public sealed record PostMergeCommandVerdict(string Command, int ExitCode, bool TimedOut,
    string? OutputTail = null, IReadOnlyList<string>? FailedTests = null);

/// <summary>Runner-collected facts from the exact fetched tip; no host chooses the repair action.</summary>
public sealed record PostMergeFailureEvidence(
    string Fingerprint,
    IReadOnlyList<string> FailingTests,
    string LogExcerpt,
    IReadOnlyList<string> FailingPaths,
    IReadOnlyList<string> SuspectMerges,
    string? AttributedMerge,
    bool RevertAppliesCleanly,
    bool LaterMergeDependsOnSuspect,
    bool AllFailuresQuarantined = false,
    string? AttributedTaskId = null);

public sealed record PostMergeProjectState(
    string Project, string DevelopState, string? LastGreenSha, string? LastWindowTipSha,
    PostMergeRunRecord? ActiveRun, IReadOnlyList<PostMergeRunRecord> Runs,
    IReadOnlyList<PostMergeIncident>? Incidents = null)
{
    public static PostMergeProjectState Empty(string project) => new(project, "green", null, null, null, [], []);
}

public sealed record PostMergeIncident(string Fingerprint, string RepairCardId, string Action,
    DateTimeOffset FirstRedAtUtc, DateTimeOffset? ResolvedAtUtc, string? AttributedMerge,
    IReadOnlyList<string> FailingTests, IReadOnlyList<string> FailingPaths,
    bool Escalated = false,
    bool OriginalReopened = false,
    string? OriginalTaskId = null)
{
    public double? RedMinutes => ResolvedAtUtc is { } resolved
        ? Math.Max(0, (resolved - FirstRedAtUtc).TotalMinutes) : null;
}
