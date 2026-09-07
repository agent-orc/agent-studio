namespace AgentStudio.Watcher;

/// <summary>
/// One observed CLI quota probe cycle. The probe itself owns its schedule; the
/// Watcher only reads what the cycle recorded.
/// </summary>
public sealed record WatcherProbeSignal
{
    public required string CliType { get; init; }
    public DateTime ObservedAtUtc { get; init; }

    /// <summary>Version the CLI reported on this cycle, when it answered at all.</summary>
    public string? CliVersion { get; init; }

    /// <summary>Set when the cycle failed. A null value is a healthy cycle.</summary>
    public DateTime? ProbeFailedAtUtc { get; init; }

    /// <summary>Verbatim probe error. Identical text across cycles is the repetition signal.</summary>
    public string? Error { get; init; }

    public bool Failed => ProbeFailedAtUtc is not null;
}

/// <summary>
/// Age of a runner capability snapshot together with the Ready cards that
/// depend on it. Without dependent cards a quiet runner is not a problem.
/// </summary>
public sealed record WatcherCapabilitySnapshotSignal
{
    public required string RunnerId { get; init; }
    public required string Project { get; init; }

    /// <summary>Null when the runner has never reported a snapshot.</summary>
    public DateTime? LastSnapshotAtUtc { get; init; }

    /// <summary>Cadence the runner itself promises. The Watcher does not invent a threshold.</summary>
    public TimeSpan ExpectedCadence { get; init; }

    /// <summary>Ready cards that target this runner and therefore cannot start.</summary>
    public IReadOnlyList<string> ReadyCardsTargeting { get; init; } = [];
}

/// <summary>
/// A recorded completion together with the Git facts that should back it. The
/// contradiction is between the lane projection and the delivery evidence.
/// </summary>
public sealed record WatcherCompletionSignal
{
    public required string Project { get; init; }
    public required string TaskKey { get; init; }
    public DateTime CompletedAtUtc { get; init; }

    /// <summary>Typed run outcome that preceded the completion, for example <c>CliCrash</c>.</summary>
    public string? PrecedingTypedOutcome { get; init; }

    /// <summary>True when the completion arrived out of band rather than from the run loop.</summary>
    public bool ExternalCompletion { get; init; }

    public string? BaseSha { get; init; }
    public string? ResultSha { get; init; }
    public int AttributedCommits { get; init; }

    /// <summary>Lane the card landed in, so the report can name what the operator sees.</summary>
    public string? LandedState { get; init; }
}

/// <summary>
/// Review attempts collapsed per subject SHA. Many passes that never change the
/// integration state are the repetition signal, not the individual pass.
/// </summary>
public sealed record WatcherReviewAttemptSignal
{
    public required string Project { get; init; }
    public required string TaskKey { get; init; }
    public required string SubjectSha { get; init; }
    public int Attempts { get; init; }

    /// <summary>Highest single-day attempt count inside the window, for the report.</summary>
    public int PeakAttemptsPerDay { get; init; }

    /// <summary>True when every attempt returned the same verdict.</summary>
    public bool AllSameVerdict { get; init; }

    public string? Verdict { get; init; }

    /// <summary>False when the attempts never moved the card or produced an integration.</summary>
    public bool StateChanged { get; init; }

    public DateTime FirstAttemptAtUtc { get; init; }
    public DateTime LastAttemptAtUtc { get; init; }
}

/// <summary>One integration failure with the fingerprint that makes it comparable across cards.</summary>
public sealed record WatcherIntegrationFailureSignal
{
    public required string Project { get; init; }
    public required string TaskKey { get; init; }

    /// <summary>Normalized failure reason, for example <c>refusing-to-fast-forward-dirty</c>.</summary>
    public required string FailureFingerprint { get; init; }

    public required string Message { get; init; }
    public DateTime ObservedAtUtc { get; init; }
}

/// <summary>
/// Two counts of the same fact taken from two sources. The detector compares
/// them; it does not decide which source is authoritative.
/// </summary>
public sealed record WatcherProjectionContradictionSignal
{
    public required string Project { get; init; }
    public required string TaskKey { get; init; }

    /// <summary>What is being counted, for example <c>review rounds</c>.</summary>
    public required string Subject { get; init; }

    public required string ArtifactSource { get; init; }
    public int ArtifactCount { get; init; }

    public required string ProjectionSource { get; init; }
    public int ProjectedCount { get; init; }

    public DateTime ObservedAtUtc { get; init; }
}

/// <summary>A validation error the product already computes, with the moment it first appeared.</summary>
public sealed record WatcherValidationErrorSignal
{
    /// <summary>Owning project, or null for a workspace-wide catalogue.</summary>
    public string? Project { get; init; }

    /// <summary>What the validator names, for example a descriptor path.</summary>
    public required string Subject { get; init; }

    public required string Message { get; init; }
    public DateTime FirstSeenAtUtc { get; init; }
}

/// <summary>An installed tool changed version. On its own this is not a finding.</summary>
public sealed record WatcherToolVersionSignal
{
    public required string Tool { get; init; }
    public string? PreviousVersion { get; init; }
    public required string NewVersion { get; init; }
    public DateTime ChangedAtUtc { get; init; }
    public string? Project { get; init; }
}

/// <summary>
/// A failure attributed to a tool. Paired with a version change that precedes
/// it, this is the drift signal.
/// </summary>
public sealed record WatcherDependentFailureSignal
{
    public required string Tool { get; init; }
    public required string FailureFingerprint { get; init; }
    public required string Message { get; init; }
    public DateTime FirstFailureAtUtc { get; init; }
    public DateTime LastFailureAtUtc { get; init; }
    public int Occurrences { get; init; }
    public string? Project { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];

    /// <summary>Source-specific facts, for example the gate cache markers behind the failure.</summary>
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
}

/// <summary>
/// Everything one sweep looks at, normalized. The collector maps live sources
/// into this shape and a fixture supplies it verbatim, so detection is the same
/// code in production and in replay.
/// </summary>
public sealed record WatcherSweepInput
{
    public DateTime NowUtc { get; init; }
    public IReadOnlyList<WatcherProbeSignal> Probes { get; init; } = [];
    public IReadOnlyList<WatcherCapabilitySnapshotSignal> CapabilitySnapshots { get; init; } = [];
    public IReadOnlyList<WatcherCompletionSignal> Completions { get; init; } = [];
    public IReadOnlyList<WatcherReviewAttemptSignal> ReviewAttempts { get; init; } = [];
    public IReadOnlyList<WatcherIntegrationFailureSignal> IntegrationFailures { get; init; } = [];
    public IReadOnlyList<WatcherProjectionContradictionSignal> Projections { get; init; } = [];
    public IReadOnlyList<WatcherValidationErrorSignal> ValidationErrors { get; init; } = [];
    public IReadOnlyList<WatcherToolVersionSignal> ToolVersions { get; init; } = [];
    public IReadOnlyList<WatcherDependentFailureSignal> DependentFailures { get; init; } = [];

    public static WatcherSweepInput Empty(DateTime nowUtc) => new() { NowUtc = nowUtc };
}

/// <summary>
/// One raw detector output, before deduplication against the case store. The
/// detector names the class, the rule, and the evidence; it never decides
/// whether a proposal follows.
/// </summary>
public sealed record WatcherFinding
{
    public required string DetectorClass { get; init; }
    public required string DetectorRule { get; init; }
    public required string Fingerprint { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>Owning project, or null for a workspace-wide finding.</summary>
    public string? Project { get; init; }

    public int Occurrences { get; init; }
    public DateTime FirstSeenAtUtc { get; init; }
    public DateTime LastSeenAtUtc { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];

    /// <summary>
    /// Task type the drafted card should carry. Derived from the class, not
    /// from a model.
    /// </summary>
    public string TaskType { get; init; } = TaskTypes.Bug;
}
