namespace AgentStudio.Watcher;

/// <summary>
/// The five detector classes of the Watcher dossier §10.2. A class describes
/// how a finding was reached, not how bad it is: repetition and hygiene are
/// bookkeeping, contradiction and drift compare two sources, silence measures
/// an absence.
/// </summary>
public static class WatcherDetectorClasses
{
    /// <summary>Same failure fingerprint recurs N times with no state change between occurrences.</summary>
    public const string Repetition = "repetition";

    /// <summary>Two projections of the same fact disagree.</summary>
    public const string Contradiction = "contradiction";

    /// <summary>An expected signal stopped arriving beyond its cadence.</summary>
    public const string Silence = "silence";

    /// <summary>An installed tool changed and a dependent probe or parser failed afterwards.</summary>
    public const string Drift = "drift";

    /// <summary>Validation errors the product already computes, older than a grace period.</summary>
    public const string Hygiene = "hygiene";

    public static readonly string[] All = [Repetition, Contradiction, Silence, Drift, Hygiene];

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// §10.4 promotion rule: only hygiene and repetition may ever become
    /// auto-approve classes. Auto-approval itself is out of scope for W1/W2;
    /// this predicate exists so the recorded acceptance evidence is scoped to
    /// the classes the dossier allows to be promoted later.
    /// </summary>
    public static bool IsPromotable(string? detectorClass)
        => detectorClass is Repetition or Hygiene;
}

/// <summary>
/// Case lifecycle. <see cref="Open"/> and <see cref="DecisionRequired"/> are
/// acute; the rest are terminals. A case never leaves the store, so a reopened
/// fingerprint appends evidence instead of creating a second row.
/// </summary>
public static class WatcherCaseStates
{
    /// <summary>Detected, still counting. Not yet persistent enough to analyse.</summary>
    public const string Open = "open";

    /// <summary>A proposal exists and waits for an attributable operator answer.</summary>
    public const string DecisionRequired = "decision-required";

    /// <summary>The operator accepted, edited, or merged the proposal.</summary>
    public const string Resolved = "resolved";

    /// <summary>The operator rejected the proposal; the fingerprint is suppressed until its entry expires.</summary>
    public const string Suppressed = "suppressed";

    /// <summary>The signal stopped before the case produced a proposal.</summary>
    public const string GaveUp = "gave-up";

    public static readonly string[] All =
        [Open, DecisionRequired, Resolved, Suppressed, GaveUp];

    public static bool IsTerminal(string? state)
        => state is Resolved or Suppressed or GaveUp;
}

/// <summary>
/// Why a persistent case has no proposal yet. Null means "no blocker": the case
/// either already produced a proposal or has not yet survived two sweeps.
/// </summary>
public static class WatcherBacklogReasons
{
    /// <summary>The per-day or per-week contingent is used up (§10.4).</summary>
    public const string ContingentExhausted = "contingent-exhausted";

    /// <summary>The fingerprint is on the suppression list from an earlier rejection.</summary>
    public const string Suppressed = "suppressed";

    /// <summary>Detected once; a second sweep must confirm persistence (§10.3).</summary>
    public const string AwaitingPersistence = "awaiting-persistence";
}

/// <summary>
/// What a proposal asks the operator to do with the drafted work. A fingerprint
/// that already has an open card yields <see cref="Comment"/>, not a new card.
/// </summary>
public static class WatcherProposalKinds
{
    public const string NewCard = "new-card";
    public const string Comment = "comment";
}

/// <summary>Operator answer on a proposal. Review mode never auto-advances past <see cref="Pending"/>.</summary>
public static class WatcherProposalDecisions
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Edited = "edited";
    public const string Merged = "merged";
    public const string Rejected = "rejected";

    public static readonly string[] All = [Pending, Approved, Edited, Merged, Rejected];

    /// <summary>Approve and edit both promote the drafted card; merge and reject do not.</summary>
    public static bool PromotesToReady(string? decision)
        => decision is Approved or Edited;
}

/// <summary>
/// Bus topics produced by the Watcher (§4a) plus the two topics §10 adds for
/// the proposal loop. Every topic rides an existing bus kind; no new kind is
/// introduced.
/// </summary>
public static class WatcherBusTopics
{
    public const string FindingRaised = "watcher-finding-raised";
    public const string AnalysisComplete = "watcher-analysis-complete";
    public const string DecisionRequired = "watcher-decision-required";
    public const string ProposalCreated = "watcher-proposal-created";
    public const string ProposalDecided = "watcher-proposal-decided";
    public const string ContingentExhausted = "watcher-contingent-exhausted";
    public const string Heartbeat = "watcher-heartbeat";
}

/// <summary>Bus participant identity registered by the Watcher (§4a).</summary>
public static class WatcherParticipant
{
    public const string Id = "orchestrator:global-watcher";
    public const string DisplayName = "Global Watcher";
    public const string Kind = "Orchestrator";
    public const string Role = "system";
}

/// <summary>Tags a Watcher-authored card carries so the proposal inbox is queryable.</summary>
public static class WatcherTags
{
    /// <summary>Marks a card as an unapproved Watcher draft. Removed when the operator approves.</summary>
    public const string Proposal = "watcher-proposal";

    /// <summary>Stable prefix for the detector class tag, for example <c>watcher-repetition</c>.</summary>
    public const string ClassPrefix = "watcher-";

    public static string ForClass(string detectorClass) => ClassPrefix + detectorClass;
}

/// <summary>
/// One reference to evidence the collector saw. The pack carries references and
/// digests, never copied artifacts, so a case row stays small and a missing
/// source is recorded as a fact instead of a guess.
/// </summary>
public sealed record WatcherEvidenceItem
{
    /// <summary>Short stable label, for example <c>probe-error</c> or <c>gate-transcript</c>.</summary>
    public required string Label { get; init; }

    /// <summary>Where the evidence lives: a file path, a task key, or a bus correlation id.</summary>
    public required string Source { get; init; }

    /// <summary>The observed value, trimmed to the pack limit.</summary>
    public string? Value { get; init; }

    /// <summary>Timestamp the source itself reported, when it had one.</summary>
    public DateTime? ObservedAtUtc { get; init; }

    /// <summary>Set when the collector looked for this evidence and it was absent.</summary>
    public bool Missing { get; init; }
}

/// <summary>
/// The bounded, immutable evidence pack of §3, narrowed to what a detector
/// class actually needs. <see cref="Digest"/> is the audit anchor recorded on
/// both the case and the proposal.
/// </summary>
public sealed record WatcherEvidencePack
{
    public required string Digest { get; init; }
    public IReadOnlyList<WatcherEvidenceItem> Items { get; init; } = [];

    /// <summary>Set when a Mini/high compression call replaced raw text (§5).</summary>
    public string? CompressedSummary { get; init; }

    public static readonly WatcherEvidencePack Empty = new() { Digest = string.Empty };
}

/// <summary>
/// One deduplicated Watcher finding with its own lifecycle. Keyed by
/// <see cref="Fingerprint"/>; at-least-once inputs append evidence and raise
/// <see cref="Occurrences"/> instead of creating a second case.
/// </summary>
public sealed record WatcherCase
{
    public required string CaseId { get; init; }
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }

    /// <summary>Rule id inside the class, for example <c>probe-error-repeat</c>.</summary>
    public required string DetectorRule { get; init; }

    /// <summary>Owning project, or null for a workspace-wide finding.</summary>
    public string? Project { get; init; }

    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;

    public DateTime FirstSeenAtUtc { get; init; }
    public DateTime LastSeenAtUtc { get; init; }

    /// <summary>How often the underlying signal was observed, as reported by the detector.</summary>
    public int Occurrences { get; init; }

    /// <summary>How many sweeps have seen this fingerprint. A proposal needs two (§10.3).</summary>
    public int SweepsSeen { get; init; }

    public IReadOnlyList<string> AffectedCards { get; init; } = [];

    public string EvidencePackDigest { get; init; } = string.Empty;
    public WatcherEvidencePack Evidence { get; init; } = WatcherEvidencePack.Empty;

    public string State { get; init; } = WatcherCaseStates.Open;

    /// <summary>Set while the case is persistent but still has no proposal.</summary>
    public string? BacklogReason { get; init; }

    public string? ProposalId { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    public bool IsTerminal => WatcherCaseStates.IsTerminal(State);
}

/// <summary>
/// The card draft a proposal carries, written in the house prompt style:
/// context with timestamps and paths, changes, acceptance.
/// </summary>
public sealed record WatcherCardDraft
{
    public required string Title { get; init; }

    /// <summary>Full prompt body: Context / Changes / Acceptance sections.</summary>
    public required string Prompt { get; init; }

    /// <summary>One of <see cref="TaskTypes"/>.</summary>
    public required string TaskType { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Task keys this draft overlaps, rendered as references in the card.</summary>
    public IReadOnlyList<string> References { get; init; } = [];
}

/// <summary>
/// Model recommendation carried by a proposal, taken from the routing policy so
/// the operator approves a route rather than inventing one.
/// </summary>
public sealed record WatcherModelRecommendation
{
    public required string PolicyVersion { get; init; }
    public required string Tier { get; init; }
    public required string Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? CorrectnessFloorTier { get; init; }
    public int Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>One model call the Watcher made while analysing a case, for the audit trail.</summary>
public sealed record WatcherModelCall
{
    public required string Purpose { get; init; }
    public required string Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>Null when the price catalog has no entry. Never rendered as zero.</summary>
    public double? Dollars { get; init; }

    public DateTime AtUtc { get; init; }
}

/// <summary>
/// The analysis receipt of §3 as far as W2 needs it: which calls ran, what they
/// cost, and whether strong analysis was invoked at all.
/// </summary>
public sealed record WatcherAnalysisReceipt
{
    public IReadOnlyList<WatcherModelCall> Calls { get; init; } = [];

    /// <summary>Why strong analysis ran or was skipped, so the §5 boundary is auditable.</summary>
    public string Route { get; init; } = string.Empty;

    public long TotalInputTokens => Calls.Sum(call => call.InputTokens);
    public long TotalOutputTokens => Calls.Sum(call => call.OutputTokens);

    /// <summary>Null when any call had an unknown price. Unknown is never summed as zero.</summary>
    public double? TotalDollars => Calls.Count == 0
        ? 0d
        : Calls.Any(call => call.Dollars is null) ? null : Calls.Sum(call => call.Dollars!.Value);

    public static readonly WatcherAnalysisReceipt None = new() { Route = "no-model-call" };
}

/// <summary>
/// A ticket proposal: the W2 output. It is a draft plus a decision slot. It is
/// never a lane transition on its own.
/// </summary>
public sealed record WatcherProposal
{
    public required string ProposalId { get; init; }
    public required string CaseId { get; init; }
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    public required string DetectorRule { get; init; }
    public required string Project { get; init; }

    /// <summary><see cref="WatcherProposalKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Existing card the comment was appended to, when <see cref="Kind"/> is a comment.</summary>
    public string? TargetTaskKey { get; init; }

    public required WatcherCardDraft Draft { get; init; }
    public required WatcherModelRecommendation Recommendation { get; init; }

    public string EvidencePackDigest { get; init; } = string.Empty;
    public WatcherAnalysisReceipt Analysis { get; init; } = WatcherAnalysisReceipt.None;

    /// <summary>Id of the card the Watcher created in the proposal state.</summary>
    public string? CreatedTaskId { get; init; }
    public string? CreatedTaskKey { get; init; }

    public string Decision { get; init; } = WatcherProposalDecisions.Pending;
    public string? DecisionReason { get; init; }
    public string? DecidedBy { get; init; }
    public DateTime? DecidedAtUtc { get; init; }

    /// <summary>True when the operator changed the draft before approving (§10.4 promotion evidence).</summary>
    public bool Edited { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>
/// One suppressed fingerprint. Suppression is visible and expires; it is never
/// a silent mute.
/// </summary>
public sealed record WatcherSuppression
{
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    public required string Reason { get; init; }
    public string? SuppressedBy { get; init; }
    public DateTime SuppressedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }

    public bool IsActive(DateTime nowUtc) => nowUtc < ExpiresAtUtc;
}
