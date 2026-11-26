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
    /// <summary>
    /// RT-3 (AGT-3988): one verdict per test that failed in this run, with every
    /// attempt (initial, same-SHA repeat, last-green control, bisect points).
    /// Null on receipts of older runners.
    /// </summary>
    public IReadOnlyList<PostMergeTestVerdict>? TestVerdicts { get; init; }
    /// <summary>RT-3: every focused run (repeat, control, bisect) and test-project build with its duration.</summary>
    public IReadOnlyList<PostMergeFocusedRun>? FocusedRuns { get; init; }
    /// <summary>RT-3 (RT-D10 B, fingerprint part): the test envelope the run's commands saw.</summary>
    public PostMergeEnvelopeFingerprint? Envelope { get; init; }
    public ReviewSuiteSelectionDto? SuiteSelection { get; init; }
    /// <summary>Measured envelope checks when available. Null keeps the RT-3 single-envelope assumption until RT-6.</summary>
    public bool? EnvelopeCanaryPassed { get; init; }
    public bool? FingerprintMatchesReference { get; init; }
    public IReadOnlyList<string> FlakyTests => Verdicts.SelectMany(verdict => verdict.FlakyTests ?? [])
        .Distinct(StringComparer.Ordinal).ToArray();
    public int FlakyCount => FlakyTests.Count;
}

public sealed record PostMergeCommandVerdict(string Command, int ExitCode, bool TimedOut,
    string? OutputTail = null, IReadOnlyList<string>? FailedTests = null,
    IReadOnlyList<string>? FlakyTests = null,
    bool Retried = false)
{
    /// <summary>Wall-clock duration of the command's first run; null on older receipts.</summary>
    public long? DurationMs { get; init; }
    /// <summary>The executed suite, including shard commands whose filter was rewritten.</summary>
    public string? SuiteId { get; init; }
}

/// <summary>Values of <see cref="PostMergeTestAttempt.Phase"/> and <see cref="PostMergeFocusedRun.Phase"/>.</summary>
public static class PostMergeRunPhases
{
    public const string Initial = "initial";
    public const string Repeat = "repeat";
    public const string Control = "control";
    public const string Bisect = "bisect";
    public const string ControlBuild = "control-build";
    public const string BisectBuild = "bisect-build";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Initial, Repeat, Control, Bisect, ControlBuild, BisectBuild };
}

/// <summary>Values of a test attempt result and of an attribution control or point run.</summary>
public static class PostMergeTestResults
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Unknown = "unknown";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pass, Fail, Unknown };
}

/// <summary>
/// The RT-2 classification names (<c>RedTestClassification</c>) as they appear on
/// a receipt. The runner's value is a hint: the server applies the triage policy.
/// </summary>
public static class PostMergeClassificationHints
{
    public const string Flaky = "flaky";
    public const string Environment = "environment";
    public const string Regression = "regression";
    public const string WrongAttribution = "wrong-attribution";
    public const string Ambiguous = "ambiguous";
    public const string Inconclusive = "inconclusive";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Flaky, Environment, Regression, WrongAttribution, Ambiguous, Inconclusive };
}

/// <summary>
/// RT-3: one failed test of a post-merge run. <see cref="CoveringFolders"/> are
/// the repository folders whose type names the test source file mentions (a
/// static name match, not runtime coverage); <see cref="CoveringFallback"/> is
/// true when the hold must fall back to the test project's area.
/// </summary>
public sealed record PostMergeTestVerdict(string Test, string SuiteId, string SuiteCategory,
    IReadOnlyList<PostMergeTestAttempt> Attempts, string ClassificationHint)
{
    public bool Quarantined { get; init; }
    public string? TestFile { get; init; }
    public IReadOnlyList<string>? CoveringFolders { get; init; }
    public bool? CoveringFallback { get; init; }
}

/// <summary>
/// One attempt of a test. <see cref="DurationMs"/> is the duration of the run
/// that produced it: a focused run for repeat, control and bisect attempts; null
/// for the initial attempt inside a whole suite (the suite's duration is on its verdict).
/// </summary>
public sealed record PostMergeTestAttempt(string Phase, string Result, long? DurationMs, string? Sha);

/// <summary>RT-3: one focused test run or test-project build, recorded so the protocol's cost is measured.</summary>
public sealed record PostMergeFocusedRun(string Phase, string Sha, int Tests, int ExitCode, bool TimedOut,
    long DurationMs);

/// <summary>
/// RT-3 (RT-D10 B, fingerprint part only). Digests are SHA-256 hex prefixes and
/// carry no values. <see cref="CgroupKind"/> is <c>worker</c>, <c>daemon</c>,
/// <c>process</c> or <c>none</c>. <see cref="PeakLoadPerCore"/> is the highest of
/// the 15-second load samples taken while the commands ran.
/// </summary>
public sealed record PostMergeEnvelopeFingerprint(string AllowlistDigest, string DeclaredEnvironmentDigest,
    string CgroupKind, string? CpuMax, int ProcessorCount, string? SdkVersion, string ShardPlan,
    double? StartLoadPerCore, double? PeakLoadPerCore);

/// <summary>
/// RT-3 (RT-D9 B): the attribution protocol's evidence. Results are
/// <see cref="PostMergeTestResults"/> values of the focused runs of the failing
/// tests; <see cref="Classification"/> is the bisect verdict of the RT-2 policy.
/// </summary>
public sealed record PostMergeAttributionEvidence(string? LastGreenSha, IReadOnlyList<string> ControlResults,
    IReadOnlyList<PostMergeBisectPointEvidence> Points, string Classification, string? StopReason = null)
{
    /// <summary>The partial build used at the control and every point, for example <c>dotnet build x.csproj --no-restore</c>.</summary>
    public string? BuildCommand { get; init; }
}

/// <summary>One tested bisect point, in candidate order, with the verdict of its two runs.</summary>
public sealed record PostMergeBisectPointEvidence(string Sha, IReadOnlyList<string> Results, string Verdict)
{
    public IReadOnlyList<string>? ParentResults { get; init; }
}

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
    bool NoRelevantMerges = false)
{
    /// <summary>RT-3: control and bisect evidence; null when no protocol ran or on older receipts.</summary>
    public PostMergeAttributionEvidence? Attribution { get; init; }
}

public sealed record PostMergeProjectState(
    string Project, string DevelopState, string? LastGreenSha, string? LastWindowTipSha,
    PostMergeRunRecord? ActiveRun, IReadOnlyList<PostMergeRunRecord> Runs,
    IReadOnlyList<PostMergeIncident>? Incidents = null)
{
    /// <summary>
    /// Repair card that its own run path escalated during this red develop.
    /// Evidence only: since RT-1 (AGT-3988) it no longer changes the project's
    /// test placement. Kept readable for compatibility.
    /// </summary>
    public string? AttributionEscalatedTaskId { get; init; }
    public IReadOnlyList<PostMergeFlakyOccurrence>? FlakyHistory { get; init; }
    public int FlakyCount => Runs.LastOrDefault(run => run.Status is "green" or "red")?.FlakyCount ?? 0;
    /// <summary>Post-merge window sizing derived from the project's look-ahead settings.</summary>
    public int? WindowMaxMerges { get; init; }
    public int? WindowMinutes { get; init; }
    public PostMergeWindowObservation? PendingWindow { get; init; }
    /// <summary>
    /// What a merge-window run selects, from the project's testing strategy
    /// (AGT-3965): <see cref="PostMergeWindowSuiteModes.Affected"/> (fast) or
    /// <see cref="PostMergeWindowSuiteModes.Routine"/> (balanced). Null keeps
    /// the affected selection.
    /// </summary>
    public string? WindowSuites { get; init; }
    public string? LineStop { get; init; }
    public string? InconclusiveRunId { get; init; }
    public IReadOnlyList<ProjectTestQuarantine> Quarantines { get; init; } = [];
    public IReadOnlyList<TestQuarantineHoldFact> Holds { get; init; } = [];
    public int QuarantinedCount => Quarantines.Count;
    public int HeldCount => Quarantines.Count(entry => entry.Kind == "held");
    public static PostMergeProjectState Empty(string project) => new(project, "green", null, null, null, [], []);
}

public sealed record PostMergeIncident(string Fingerprint, string RepairCardId, string Action,
    DateTimeOffset FirstRedAtUtc, DateTimeOffset? ResolvedAtUtc, string? AttributedMerge,
    IReadOnlyList<string> FailingTests, IReadOnlyList<string> FailingPaths,
    bool Escalated = false,
    bool OriginalReopened = false,
    string? OriginalTaskId = null,
    DateTimeOffset? OwnerAskedAtUtc = null,
    PostMergeOwnerQuestion? OwnerQuestion = null)
{
    public string? TestId { get; init; }
    public string? SuiteId { get; init; }
    public string? SuiteCategory { get; init; }
    public string? ModelRung { get; init; }
    public string? LineStopReason { get; init; }
    public IReadOnlyList<PostMergeOwnerQuestion> OwnerQuestions { get; init; } = [];
    public double? RedMinutes => ResolvedAtUtc is { } resolved
        ? Math.Max(0, (resolved - FirstRedAtUtc).TotalMinutes) : null;
}

/// <summary>
/// RT-1 (AGT-3988): the one owner item of an incident that stayed red for two
/// hours. <see cref="Options"/> and <see cref="Recommended"/> are the decision
/// library's option names; <see cref="RedRunId"/> and <see cref="RedTipSha"/>
/// name the latest red receipt the question was asked on.
/// </summary>
public sealed record PostMergeOwnerQuestion(IReadOnlyList<string> Options, string Recommended,
    string RecommendationReason, string? RedRunId, string? RedTipSha)
{
    public string? Reason { get; init; }
    public DateTimeOffset? AskedAtUtc { get; init; }
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
