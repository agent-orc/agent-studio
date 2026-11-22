namespace AgentStudio.TaskServer.Contracts;

/// <summary>One exact-tip T3 check. A skipped check is evidence but does not change the verdict.</summary>
public sealed record PostMergeRunRecord(
    string Id, string Project, string Kind, string TipSha, string? WindowFromSha,
    DateTimeOffset? WindowOpenedAtUtc, DateTimeOffset? WindowClosedAtUtc, int MergeCount,
    IReadOnlyList<string> Commands, IReadOnlyList<PostMergeCommandVerdict> Verdicts,
    string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    string? Reason = null,
    PostMergeFailureEvidence? Failure = null,
    double? StartLoadPerCore = null,
    int? StartOccupiedSlots = null)
{
    public IReadOnlyList<string> FlakyTests => Verdicts.SelectMany(verdict => verdict.FlakyTests ?? [])
        .Distinct(StringComparer.Ordinal).ToArray();
    public int FlakyCount => FlakyTests.Count;
}

public sealed record PostMergeCommandVerdict(string Command, int ExitCode, bool TimedOut,
    string? OutputTail = null, IReadOnlyList<string>? FailedTests = null,
    IReadOnlyList<string>? FlakyTests = null,
    bool Retried = false);

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
    string? AttributedTaskId = null,
    bool NoRelevantMerges = false);

public sealed record PostMergeProjectState(
    string Project, string DevelopState, string? LastGreenSha, string? LastWindowTipSha,
    PostMergeRunRecord? ActiveRun, IReadOnlyList<PostMergeRunRecord> Runs,
    IReadOnlyList<PostMergeIncident>? Incidents = null)
{
    /// <summary>Repair card escalated for this red develop incident. Its current lane decides the fallback.</summary>
    public string? AttributionEscalatedTaskId { get; init; }
    public IReadOnlyList<PostMergeFlakyOccurrence>? FlakyHistory { get; init; }
    public int FlakyCount => Runs.LastOrDefault(run => run.Status is "green" or "red")?.FlakyCount ?? 0;
    /// <summary>Post-merge window sizing derived from the project's look-ahead settings.</summary>
    public int? WindowMaxMerges { get; init; }
    public int? WindowMinutes { get; init; }
    public PostMergeWindowObservation? PendingWindow { get; init; }
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

/// <summary>Registry-derived execution target for the host-owned T3 scheduler.</summary>
public sealed record PostMergeProjectTarget(string ProjectId, string Project, string RepositoryUrl, string Branch);

/// <summary>Fetched branch facts, reported even when execution admission is closed.</summary>
public sealed record PostMergeWindowObservation(string TipSha, DateTimeOffset? OpenedAtUtc,
    DateTimeOffset ObservedAtUtc, int WindowMinutes, string? AdmissionReason)
{
    public bool Starved => OpenedAtUtc is { } opened && AdmissionReason is not null
        && (ObservedAtUtc - opened).TotalMinutes > WindowMinutes;
}

/// <summary>One flaky test per run, retained independently of the bounded receipt list.</summary>
public sealed record PostMergeFlakyOccurrence(string Test, string RunId, DateTimeOffset AtUtc);
public sealed record PostMergeFlakyTest(string Test, int Runs, DateTimeOffset LastSeenAtUtc);
