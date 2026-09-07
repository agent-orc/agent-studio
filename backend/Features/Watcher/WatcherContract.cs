using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Watcher;

/// <summary>
/// The five detector classes of the Watcher dossier (AGT-W15) section 10.2.
/// Every class is decided by deterministic code over facts the Task Server
/// already holds. No model participates in detection.
/// </summary>
public enum WatcherDetectorClass
{
    /// <summary>Same failure fingerprint recurs N times in a window with no state change between occurrences.</summary>
    Repetition,

    /// <summary>Two projections of the same fact disagree.</summary>
    Contradiction,

    /// <summary>An expected signal stopped arriving beyond its declared cadence.</summary>
    Silence,

    /// <summary>An installed tool changed and a dependent probe or parser failed afterwards.</summary>
    Drift,

    /// <summary>Validation errors the product already computes, older than a grace period.</summary>
    Hygiene,
}

/// <summary>
/// Case lifecycle of dossier section 4. W1 and W2 run in shadow mode, so
/// <see cref="ActionRunning"/> is part of the versioned contract but is never
/// produced by this slice: no recovery recipe executes before W3.
/// </summary>
public enum WatcherCaseState
{
    /// <summary>Acute and unresolved. Projects to an Activity <c>alert</c> presented as Problem.</summary>
    Open,

    /// <summary>Waiting for an attributable operator answer. Projects to Activity <c>decision</c>.</summary>
    DecisionRequired,

    /// <summary>Reserved for W3. A recipe is executing under an authority class.</summary>
    ActionRunning,

    /// <summary>Terminal. The finding was verified as no longer present.</summary>
    Resolved,

    /// <summary>Terminal. Bounded attempts exhausted; names the missing authority or evidence.</summary>
    GaveUp,
}

/// <summary>Operator answer on a ticket proposal (dossier section 10.4 review mode).</summary>
public enum WatcherProposalDecision
{
    /// <summary>Not yet decided. The proposal sits in the Watcher inbox.</summary>
    Pending,

    /// <summary>Approved. The card moves to Ready with the recommended model.</summary>
    Approved,

    /// <summary>Approved after the operator edited the draft. Counted separately for the promotion rule.</summary>
    Edited,

    /// <summary>Folded into an existing card instead of becoming a new one.</summary>
    Merged,

    /// <summary>Rejected with a reason. Feeds the expiring suppression list.</summary>
    Rejected,
}

/// <summary>
/// Bus topics the Watcher publishes under participant
/// <see cref="ParticipantId"/>. Dossier section 4a deliberately reuses the
/// stable bus kinds; no Watcher-only kind or competing feed is introduced.
/// </summary>
public static class WatcherBusTopics
{
    public const string ParticipantId = "orchestrator:global-watcher";

    public const string FindingRaised = "watcher-finding-raised";
    public const string AnalysisComplete = "watcher-analysis-complete";
    public const string DecisionRequired = "watcher-decision-required";
    public const string RecipeApplied = "watcher-recipe-applied";
}

/// <summary>
/// Tags and lane the review mode uses. Proposals never enter Ready by
/// themselves: they land in <see cref="ProposalLane"/> carrying
/// <see cref="ProposalTag"/> plus the detector-class tag.
/// </summary>
public static class WatcherProposalConventions
{
    /// <summary>Proposal inbox lane. Matches <c>TaskStates.Preparation</c>.</summary>
    public const string ProposalLane = "1-preparation";

    /// <summary>Lane an approved proposal is promoted to. Matches <c>TaskStates.Ready</c>.</summary>
    public const string ApprovedLane = "2-ready";

    public const string ProposalTag = "watcher-proposal";

    /// <summary>Per-class tag so the operator can filter the inbox by detector.</summary>
    public static string ClassTag(WatcherDetectorClass detectorClass)
        => "watcher-" + detectorClass.ToString().ToLowerInvariant();
}

/// <summary>
/// One piece of named evidence inside a case. The collector records absent
/// evidence explicitly rather than substituting a guess, so a null
/// <see cref="Value"/> is a fact and not a formatting accident.
/// </summary>
public sealed record WatcherEvidenceItem(string Label, string? Value, string? Source = null);

/// <summary>
/// A repeated failure fingerprint observed across cycles. The producers of
/// these occurrences own their own thresholds; the Watcher only counts.
/// <paramref name="StateToken"/> is the "did anything change between
/// occurrences" discriminator: a repetition only counts while it stays equal.
/// </summary>
public sealed record WatcherRepetitionSignal(
    string Fingerprint,
    string Source,
    string Message,
    IReadOnlyList<DateTime> Occurrences,
    string StateToken,
    IReadOnlyList<string> AffectedCards,
    string? Project = null);

/// <summary>Two sources that should agree about one fact but do not.</summary>
public sealed record WatcherProjectionPair(
    string Fact,
    string LeftSource,
    string? LeftValue,
    string RightSource,
    string? RightValue,
    IReadOnlyList<string> AffectedCards,
    string? Project = null);

/// <summary>
/// An expected recurring signal with its declared cadence. Silence is measured
/// against the producer's cadence, never against a Watcher-invented timer.
/// </summary>
public sealed record WatcherExpectedSignal(
    string SignalName,
    DateTime? LastSeenAt,
    TimeSpan ExpectedCadence,
    IReadOnlyList<string> AffectedCards,
    string? Project = null);

/// <summary>An installed tool whose version changed, plus the first dependent failure observed after it.</summary>
public sealed record WatcherToolVersionChange(
    string Tool,
    string? PreviousVersion,
    string CurrentVersion,
    DateTime ChangedAt,
    DateTime? FirstDependentFailureAt,
    string? DependentFailureMessage,
    IReadOnlyList<string> AffectedCards,
    string? Project = null);

/// <summary>A validation error the product already computes on every list.</summary>
public sealed record WatcherValidationError(
    string Subject,
    string Message,
    DateTime FirstObservedAt,
    string? Project = null);

/// <summary>
/// Read-only snapshot of everything one sweep looks at. Mirrors the
/// <c>SupervisorObservation</c> shape so detectors stay pure functions over
/// data and never reach for the filesystem themselves.
/// </summary>
public sealed record WatcherObservation(
    DateTime CapturedAt,
    IReadOnlyList<WatcherRepetitionSignal> Repetitions,
    IReadOnlyList<WatcherProjectionPair> Projections,
    IReadOnlyList<WatcherExpectedSignal> ExpectedSignals,
    IReadOnlyList<WatcherToolVersionChange> ToolChanges,
    IReadOnlyList<WatcherValidationError> ValidationErrors)
{
    public static WatcherObservation Empty(DateTime capturedAt)
        => new(capturedAt, [], [], [], [], []);
}

/// <summary>
/// One detector output before deduplication. A finding becomes a
/// <see cref="WatcherCase"/> only through the ledger, which owns identity,
/// counting, and persistence across sweeps.
/// </summary>
public sealed record WatcherFinding(
    DateTime ObservedAt,
    WatcherDetectorClass DetectorClass,
    string Fingerprint,
    string DetectorRule,
    string Summary,
    IReadOnlyList<string> AffectedCards,
    IReadOnlyList<WatcherEvidenceItem> Evidence,
    string? Project = null);

/// <summary>
/// Durable Watcher case. Identity is
/// <c>project + detector class + evidence fingerprint</c>: the section 1 case
/// key narrowed to the facts the section 10 detectors actually carry, since a
/// stale probe or an invalid descriptor has no task key or attempt chain.
/// </summary>
public sealed record WatcherCase
{
    public required string CaseId { get; init; }
    public required WatcherDetectorClass DetectorClass { get; init; }
    public required string Fingerprint { get; init; }
    public string? Project { get; init; }
    public required string DetectorRule { get; init; }
    public required string Summary { get; init; }
    public required DateTime FirstSeenAt { get; init; }
    public required DateTime LastSeenAt { get; init; }

    /// <summary>How many detections have been folded into this case.</summary>
    public int Occurrences { get; init; }

    /// <summary>
    /// How many distinct sweeps saw this fingerprint. The dossier requires two
    /// sweeps before analysis or a proposal, so this is the persistence check
    /// and not a duplicate of <see cref="Occurrences"/>.
    /// </summary>
    public int SweepCount { get; init; }

    /// <summary>
    /// Sequence number of the last sweep that detected this fingerprint.
    /// Quiet sweeps are derived from it rather than kept as a second counter,
    /// so a restart that replays one sweep cannot silently age a case out.
    /// </summary>
    public long LastSweepId { get; init; }

    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];

    /// <summary>Digest over the evidence pack. One strong call per digest (section 5 spend control).</summary>
    public required string EvidencePackDigest { get; init; }

    public WatcherCaseState State { get; init; } = WatcherCaseState.Open;

    /// <summary>Set when <see cref="State"/> is a terminal. Never null on a terminal case.</summary>
    public string? TerminalReason { get; init; }

    /// <summary>Set once a proposal has been drafted for this case.</summary>
    public string? ProposalId { get; init; }

    /// <summary>True once the case has been seen by at least two sweeps.</summary>
    public bool IsPersistent => SweepCount >= WatcherCasePolicy.PersistenceSweeps;

    public bool IsTerminal => State is WatcherCaseState.Resolved or WatcherCaseState.GaveUp;
}

/// <summary>Thresholds that govern case progression. Kept next to the contract so tests can address them by name.</summary>
public static class WatcherCasePolicy
{
    /// <summary>Sweeps a fingerprint must survive before analysis or a proposal (dossier section 10.3).</summary>
    public const int PersistenceSweeps = 2;

    /// <summary>Sweeps without a new detection after which an open case resolves itself.</summary>
    public const int QuietSweepsToResolve = 2;
}

/// <summary>
/// The card draft a proposal would create. Rendered in the house prompt style:
/// context with timestamps and paths, changes, acceptance.
/// </summary>
public sealed record WatcherCardDraft(
    string Title,
    string PromptMarkdown,
    string TaskType,
    string TargetState,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RelatedCards,
    string? Project);

/// <summary>Model route recommended for the drafted card, taken from the routing policy registry.</summary>
public sealed record WatcherModelRecommendation(
    string Model,
    string ThinkingLevel,
    string Tier,
    string PolicyVersion,
    string Reason);

/// <summary>
/// A ticket proposal produced by W2. It is a draft plus a recommendation plus
/// an operator decision. It is not a card until the operator approves it.
/// </summary>
public sealed record WatcherProposal
{
    public required string ProposalId { get; init; }
    public required string CaseId { get; init; }
    public required WatcherCardDraft CardDraft { get; init; }
    public required WatcherModelRecommendation Recommendation { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>Digest of the evidence pack the draft was built from. Carried for audit (section 10.4).</summary>
    public required string EvidencePackDigest { get; init; }

    /// <summary>The detector rule that produced the case. Carried for audit.</summary>
    public required string DetectorRule { get; init; }

    public WatcherProposalDecision Decision { get; init; } = WatcherProposalDecision.Pending;
    public string? RejectionReason { get; init; }
    public DateTime? DecidedAt { get; init; }

    /// <summary>
    /// Set when the proposal became a card. Null while pending, and null
    /// forever for a rejected proposal. A proposal never carries a Ready lane
    /// by itself.
    /// </summary>
    public string? SpawnedTaskKey { get; init; }

    /// <summary>
    /// When the fingerprint already has an open card, the proposal is a note on
    /// that card rather than a new one (dossier section 10.3).
    /// </summary>
    public string? CommentOnCard { get; init; }

    public bool IsComment => !string.IsNullOrWhiteSpace(CommentOnCard);
}

/// <summary>
/// Stable fingerprints and case ids. Digests are truncated SHA-256 over
/// normalized text so the same finding produces the same identity across
/// restarts and across hosts.
/// </summary>
public static class WatcherFingerprint
{
    /// <summary>Hex characters retained from the digest. Wide enough to avoid collisions, short enough to read in a log.</summary>
    public const int DigestLength = 16;

    /// <summary>Separates parts so that Of("ab","c") cannot collide with Of("a","bc").</summary>
    private const string PartSeparator = "\u001f";

    /// <summary>
    /// Digest over raw signal text. Parts are normalized first, so the same
    /// fault reported with a different id or timestamp still produces one
    /// fingerprint. A null part is kept as an explicit empty slot rather than
    /// skipped, because a missing field is itself part of the identity.
    /// </summary>
    public static string Of(params string?[] parts)
        => Digest(string.Join(PartSeparator, parts.Select(Normalize)));

    /// <summary>
    /// Case identity: detector class, project, and evidence fingerprint. Two
    /// detections that agree on all three are the same case, which is what
    /// makes the ledger idempotent under at-least-once input.
    /// </summary>
    /// <remarks>
    /// The fingerprint is already a stable digest, so it is composed verbatim.
    /// Passing it through <see cref="Normalize"/> would collapse it to the
    /// hex-run placeholder and give every case of one project the same id.
    /// </remarks>
    public static string CaseId(WatcherDetectorClass detectorClass, string? project, string fingerprint)
    {
        var scope = Digest(string.Join(
            PartSeparator,
            (project ?? "").Trim().ToLowerInvariant(),
            fingerprint.Trim().ToLowerInvariant()));
        return $"{detectorClass.ToString().ToLowerInvariant()}-{scope}";
    }

    /// <summary>Digest over the ordered evidence pack of a case.</summary>
    public static string EvidenceDigest(IReadOnlyList<WatcherEvidenceItem> evidence)
        => Of([.. evidence.SelectMany(item => new[] { item.Label, item.Value, item.Source })]);

    /// <summary>
    /// Collapses volatile detail so the same fault produces one fingerprint.
    /// Long hex runs (SHAs, run ids, guid fragments) become a placeholder,
    /// digit runs collapse, and whitespace normalizes. Those three are what
    /// made the 2026-09-06 findings look like many distinct errors rather than
    /// one that kept happening.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var collapsed = string.Join(' ', value.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return DigitRun.Replace(HexRun.Replace(collapsed, "<id>"), "#");
    }

    /// <summary>A run of eight or more hex characters is an id, a SHA, or a guid fragment.</summary>
    private static readonly Regex HexRun = new(@"\b[0-9a-f]{8,}\b", RegexOptions.Compiled);

    /// <summary>Any remaining digit run is a count, a port, or a timestamp component.</summary>
    private static readonly Regex DigitRun = new(@"[0-9]+", RegexOptions.Compiled);

    private static string Digest(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..DigestLength];
}
