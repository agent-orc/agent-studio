namespace AgentStudio.Watcher;

/// <summary>
/// Named producers of Watcher signals. The source is part of every
/// fingerprint, so the same error text seen by two subsystems stays two cases.
/// </summary>
public static class WatcherSignalSources
{
    public const string CliQuotaProbe = "cli-quota-probe";
    public const string RunnerCapability = "runner-capability";
    public const string TaskCompletion = "task-completion";
    public const string ReviewAttempt = "review-attempt";
    public const string Integration = "integration";
    public const string Gate = "gate";
    public const string ReviewProjection = "review-projection";
    public const string DossierDescriptor = "dossier-descriptor";
}

/// <summary>
/// One occurrence of a mechanical failure. The collector normalises the raw
/// error into <see cref="Fingerprint"/> so a varying timestamp or path suffix
/// does not defeat the repetition count.
/// </summary>
/// <param name="Source">One of <see cref="WatcherSignalSources"/>.</param>
/// <param name="Subject">What failed: a probe surface, a subject SHA, a checkout.</param>
/// <param name="Fingerprint">Normalised failure text, stable across occurrences.</param>
/// <param name="ObservedAtUtc">When this occurrence was recorded.</param>
/// <param name="Summary">One line for the feed, already free of secrets.</param>
public sealed record WatcherFailureSignal(
    string Source,
    string Subject,
    string Fingerprint,
    DateTime ObservedAtUtc,
    string Summary)
{
    public string? Project { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];

    /// <summary>
    /// True when something changed between this occurrence and the previous
    /// one (a new attempt chain, a new subject SHA, a recovered probe). The
    /// repetition rule only counts occurrences since the last state change,
    /// which is what "with no state change between occurrences" means.
    /// </summary>
    public bool StateChanged { get; init; }

    /// <summary>
    /// Tool subject this failure depends on, when the collector knows it. Set
    /// it to let the drift rule tie a failure to a version change.
    /// </summary>
    public string? DependsOnTool { get; init; }

    /// <summary>Extra facts recorded verbatim into the evidence pack.</summary>
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
}

/// <summary>
/// Two projections of the same fact that disagree. The collector supplies both
/// values and both sources; the detector never decides which one is right.
/// </summary>
public sealed record WatcherContradictionSignal(
    string Source,
    string Subject,
    string LeftSource,
    string LeftValue,
    string RightSource,
    string RightValue,
    DateTime ObservedAtUtc,
    string Summary)
{
    public string? Project { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
}

/// <summary>
/// An expected signal and when it was last seen. The collector emits one only
/// for signals that currently matter, for example a runner snapshot while
/// Ready cards target that runner.
/// </summary>
public sealed record WatcherPresenceSignal(
    string Source,
    string Subject,
    DateTime? LastSeenAtUtc,
    TimeSpan ExpectedCadence,
    DateTime ObservedAtUtc,
    string Summary)
{
    public string? Project { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
}

/// <summary>An installed tool version, with the value it replaced.</summary>
public sealed record WatcherToolVersionSignal(
    string Source,
    string Subject,
    string? PreviousVersion,
    string CurrentVersion,
    DateTime ChangedAtUtc)
{
    public string? Project { get; init; }
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];

    public bool Changed => !string.IsNullOrWhiteSpace(PreviousVersion)
        && !string.Equals(PreviousVersion, CurrentVersion, StringComparison.Ordinal);
}

/// <summary>
/// One validation error the product already computes. The Watcher does not
/// re-run the validator; it only notices that an error has outlived its grace
/// period.
/// </summary>
public sealed record WatcherValidationSignal(
    string Source,
    string Subject,
    string Message,
    DateTime FirstSeenAtUtc,
    DateTime ObservedAtUtc)
{
    public string? Project { get; init; }
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
}

/// <summary>
/// Everything one sweep looks at. Building this record is the only part that
/// touches the world; detection over it is pure, which is what makes the
/// dossier fixtures replayable.
/// </summary>
public sealed record WatcherSweepInput
{
    public required DateTime NowUtc { get; init; }
    public IReadOnlyList<WatcherFailureSignal> Failures { get; init; } = [];
    public IReadOnlyList<WatcherContradictionSignal> Contradictions { get; init; } = [];
    public IReadOnlyList<WatcherPresenceSignal> Presence { get; init; } = [];
    public IReadOnlyList<WatcherToolVersionSignal> ToolVersions { get; init; } = [];
    public IReadOnlyList<WatcherValidationSignal> Validations { get; init; } = [];

    public int SignalCount =>
        Failures.Count + Contradictions.Count + Presence.Count + ToolVersions.Count + Validations.Count;

    /// <summary>Merge two inputs. Used to fold per-project collection into one sweep.</summary>
    public WatcherSweepInput Concat(WatcherSweepInput other) => this with
    {
        Failures = [.. Failures, .. other.Failures],
        Contradictions = [.. Contradictions, .. other.Contradictions],
        Presence = [.. Presence, .. other.Presence],
        ToolVersions = [.. ToolVersions, .. other.ToolVersions],
        Validations = [.. Validations, .. other.Validations],
    };
}
