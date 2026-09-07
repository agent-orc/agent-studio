namespace AgentStudio.Watcher;

/// <summary>
/// The five W1 detector classes from the orchestrator-waechter dossier §10.2.
/// Closed by convention, like <see cref="AgentStudio.Shared.TimelineEventKinds"/>:
/// a new class needs a probe, a fixture, and a fingerprint short-code together.
/// </summary>
public static class WatcherDetectorClasses
{
    /// <summary>Same failure fingerprint recurs N times with no state change between occurrences.</summary>
    public const string Repetition = "repetition";

    /// <summary>Two projections disagree (artifact count vs. rendered count, lane vs. integration truth, ...).</summary>
    public const string Contradiction = "contradiction";

    /// <summary>An expected signal stops arriving beyond its producer's own cadence.</summary>
    public const string Silence = "silence";

    /// <summary>An installed tool version changed and a dependent probe or parser failed afterward.</summary>
    public const string Drift = "drift";

    /// <summary>Validation errors the product already computes, older than a grace period.</summary>
    public const string Hygiene = "hygiene";

    public static readonly string[] All = [Repetition, Contradiction, Silence, Drift, Hygiene];
}

/// <summary>
/// Durable state of one <see cref="WatcherCase"/>. Terminals per dossier §4;
/// <c>action-running</c> is omitted because W1+W2 never mutate task or Git
/// state outside proposal creation and comments (W3 scope).
/// </summary>
public static class WatcherCaseStates
{
    /// <summary>Acute, evidence-linked, not yet persisted across two sweeps.</summary>
    public const string Open = "open";

    /// <summary>A proposal exists and awaits an attributable operator decision.</summary>
    public const string DecisionRequired = "decision-required";

    /// <summary>Verified quiet: the fingerprint stopped recurring and no proposal was needed.</summary>
    public const string Resolved = "resolved";

    /// <summary>Bounded attempts exhausted (e.g. contingent stayed empty) with an exact missing reason.</summary>
    public const string GaveUp = "gave-up";

    /// <summary>An operator rejected the fingerprint; new occurrences are counted but not proposed.</summary>
    public const string Suppressed = "suppressed";

    public static readonly string[] All = [Open, DecisionRequired, Resolved, GaveUp, Suppressed];
}

/// <summary>
/// One normalized, model-free signal collected by a detector probe during a
/// sweep. Multiple observations sharing the same
/// (<see cref="DetectorClass"/>, <see cref="Project"/>, <see cref="FingerprintKey"/>)
/// collapse into one <see cref="WatcherCase"/>.
/// </summary>
public sealed record WatcherSignalObservation
{
    public required string DetectorClass { get; init; }
    public required string Project { get; init; }

    /// <summary>Detector-specific stable key used to compute the fingerprint (e.g. an integration failure code, a probe error text, a descriptor path).</summary>
    public required string FingerprintKey { get; init; }

    public required string Summary { get; init; }
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
    public List<string> AffectedCards { get; init; } = [];
    public Dictionary<string, string> Details { get; init; } = [];
    public List<string> SourcePaths { get; init; } = [];
}

/// <summary>
/// Durable, restart-safe record of one correlated Watcher case. Keyed by
/// <see cref="Fingerprint"/> (detector class + project + fingerprint key).
/// One file per case under <c>logs/watcher/cases/&lt;fingerprint&gt;.json</c>.
/// </summary>
public sealed record WatcherCase
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    public required string Project { get; init; }
    public List<string> AffectedCards { get; init; } = [];
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }

    /// <summary>Total observations folded into this case across every sweep.</summary>
    public int OccurrenceCount { get; init; }

    /// <summary>Distinct sweeps in which this fingerprint was observed. Two sweeps is the W2 analysis admission threshold (§10.3).</summary>
    public int SweepCount { get; init; }

    /// <summary>Sweep id last counted toward <see cref="SweepCount"/>, so re-observing within the same tick never double-counts.</summary>
    public string? LastSweepId { get; init; }

    public string State { get; init; } = WatcherCaseStates.Open;
    public string LastSummary { get; init; } = "";
    public Dictionary<string, string> LastDetails { get; init; } = [];
    public List<string> SourcePaths { get; init; } = [];
    public string? EvidenceDigest { get; init; }
    public string? ProposalId { get; init; }
    public string? ProposalJobId { get; init; }
    public bool IsCommentOnly { get; init; }
    public string? GaveUpReason { get; init; }
}

/// <summary>One bounded, size-limited evidence item collected for a case. Absent evidence is recorded explicitly (§3).</summary>
public sealed record WatcherEvidenceItem(string Label, string Value, DateTime? At, string? SourcePath);

/// <summary>
/// The immutable, size-bounded evidence pack built for a case that survives
/// two sweeps (§3, §10.3). Compression and analysis both read from this pack;
/// neither mutates it.
/// </summary>
public sealed record WatcherEvidencePack
{
    public required string CaseId { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public List<WatcherEvidenceItem> Signals { get; init; } = [];

    /// <summary>Facts the pack could not establish, recorded instead of guessed (§3).</summary>
    public List<string> MissingEvidence { get; init; } = [];

    public string DigestSha256 { get; init; } = "";
}

/// <summary>Structured receipt from the bounded strong-model analysis call, when one was warranted (§3 "Analysis receipt").</summary>
public sealed record WatcherAnalysisReceipt
{
    public required string CaseId { get; init; }
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> Unknowns { get; init; } = [];
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// A ticket proposal produced by W2 for a case, written through the normal
/// task API into the proposal state (§10.3). One proposal per case; a
/// fingerprint with an already-open proposal card yields a comment instead
/// (<see cref="IsComment"/>).
/// </summary>
public sealed record WatcherProposal
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string DetectorClass { get; init; }
    public required string Fingerprint { get; init; }
    public required string Project { get; init; }

    /// <summary>Id of the created proposal card. Null when <see cref="IsComment"/> is true.</summary>
    public string? JobId { get; init; }

    public bool IsComment { get; init; }

    /// <summary>Id of the pre-existing open card a repeat fingerprint was appended to as a comment.</summary>
    public string? CommentedJobId { get; init; }

    public string Title { get; init; } = "";
    public string RecommendedModel { get; init; } = "";
    public string RecommendedThinkingLevel { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public WatcherProposalDecisionRecord? Decision { get; init; }
}

/// <summary>Operator decision on a <see cref="WatcherProposal"/> (§10.4 review mode).</summary>
public sealed record WatcherProposalDecisionRecord
{
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
    public string? MergedIntoJobId { get; init; }
    public DateTime DecidedAtUtc { get; init; } = DateTime.UtcNow;
    public string DecidedBy { get; init; } = "";
}

/// <summary>The closed set of decision outcomes a proposal can receive.</summary>
public static class WatcherProposalOutcomes
{
    public const string Approved = "approved";
    public const string Edited = "edited";
    public const string Merged = "merged";
    public const string Rejected = "rejected";

    public static readonly string[] All = [Approved, Edited, Merged, Rejected];
}

/// <summary>A rejected fingerprint's expiring suppression entry (§10.4).</summary>
public sealed record WatcherSuppression
{
    public required string Fingerprint { get; init; }
    public required string Reason { get; init; }
    public DateTime SuppressedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; }
}

/// <summary>Per-class acceptance tracking used to evaluate the §10.4 promotion rule later (ten unedited acceptances, hygiene/repetition only). Read-only bookkeeping; W1+W2 never auto-approve.</summary>
public sealed record WatcherClassAcceptanceStats
{
    public required string DetectorClass { get; init; }
    public int AcceptedWithoutEdit { get; init; }
    public int AcceptedWithEdit { get; init; }
    public int Rejected { get; init; }
}
