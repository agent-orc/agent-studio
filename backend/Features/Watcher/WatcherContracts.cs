using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Detector classes of the Watcher dossier section 10.2. The class is part of
/// every fingerprint, so a repetition finding and a contradiction finding about
/// the same subject stay two distinct cases with two distinct terminals.
/// </summary>
public static class WatcherDetectorClasses
{
    /// <summary>The same failure fingerprint recurs without a state change between occurrences.</summary>
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

    /// <summary>
    /// Classes the dossier section 10.4 promotion rule may ever auto-approve.
    /// Contradiction and drift stay human-approved regardless of acceptance
    /// history. Recorded here so the promotion evaluation has one owner.
    /// </summary>
    public static readonly string[] PromotableToAutoApproval = [Hygiene, Repetition];

    public static bool IsValid(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// Durable case states. Every case reaches one of the terminal values; a case
/// that merely stops being observed is not silently dropped, it is resolved
/// with an explicit reason.
/// </summary>
public static class WatcherCaseStates
{
    /// <summary>Seen at least once, not yet persistent across two sweeps.</summary>
    public const string Open = "open";
    /// <summary>Persistent and analysed, waiting for its proposal.</summary>
    public const string Analysed = "analysed";
    /// <summary>A proposal exists and awaits an operator decision.</summary>
    public const string Proposed = "proposed";
    /// <summary>Backlogged because the contingent is exhausted. Counting continues.</summary>
    public const string Backlogged = "backlogged";

    // Terminals.

    /// <summary>The signal stopped and the case closed without a proposal.</summary>
    public const string Resolved = "resolved";
    /// <summary>The fingerprint is on the suppression list.</summary>
    public const string Suppressed = "suppressed";
    /// <summary>Bounded attempts exhausted; the missing authority or evidence is named.</summary>
    public const string GaveUp = "gave-up";

    public static readonly string[] Terminals = [Resolved, Suppressed, GaveUp];

    public static bool IsTerminal(string? state)
        => state is not null && Terminals.Contains(state, StringComparer.Ordinal);
}

/// <summary>Operator answers on a Watcher proposal (dossier section 10.4 review mode).</summary>
public static class WatcherProposalDecisions
{
    public const string Pending = "pending";
    /// <summary>Card moves to Ready with the recommended model.</summary>
    public const string Approved = "approved";
    /// <summary>Approved after the operator changed the draft. Counted separately for promotion evidence.</summary>
    public const string Edited = "edited";
    /// <summary>Folded into an existing card instead of standing alone.</summary>
    public const string Merged = "merged";
    /// <summary>Declined with a reason that feeds the suppression list.</summary>
    public const string Rejected = "rejected";

    public static readonly string[] All = [Pending, Approved, Edited, Merged, Rejected];

    public static bool IsValid(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>How a proposal reaches the board.</summary>
public static class WatcherProposalKinds
{
    /// <summary>A new card draft in the proposal state.</summary>
    public const string NewCard = "new-card";
    /// <summary>A comment on the open card that already carries this fingerprint.</summary>
    public const string Comment = "comment";
}

/// <summary>
/// Tag vocabulary the Watcher writes onto proposal cards. The fingerprint tag
/// is how the next sweep recognises that a fingerprint already has an open
/// card and must comment instead of creating a duplicate.
/// </summary>
public static class WatcherTags
{
    /// <summary>Marks a card as a Watcher proposal awaiting review.</summary>
    public const string Proposal = "watcher-proposal";

    public static string DetectorClass(string detectorClass) => $"watcher-{detectorClass}";

    /// <summary>Prefix that identifies a fingerprint tag without knowing the digest.</summary>
    public const string FingerprintPrefix = "watcher-fp-";

    public static string Fingerprint(string digest) => FingerprintPrefix + digest;
}

/// <summary>
/// One named fact inside an evidence pack. Absent evidence is recorded
/// explicitly with <see cref="Available"/> false rather than substituted with
/// a guess (dossier section 3).
/// </summary>
public sealed record WatcherEvidenceItem(string Label, string Value, string Source)
{
    public bool Available { get; init; } = true;

    public static WatcherEvidenceItem Missing(string label, string source)
        => new(label, "(not available)", source) { Available = false };
}

/// <summary>
/// What one detector concluded from one sweep. Findings are transient; the
/// durable record is the <see cref="WatcherCase"/> they fold into.
/// </summary>
public sealed record WatcherFinding
{
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    /// <summary>The dossier rule text that fired, quoted so the proposal can cite it.</summary>
    public required string DetectorRule { get; init; }
    public required string Title { get; init; }
    /// <summary>Null for workspace-wide findings, matching the bus project convention.</summary>
    public string? Project { get; init; }
    public required DateTime FirstSeenAtUtc { get; init; }
    public required DateTime LastSeenAtUtc { get; init; }
    /// <summary>How many raw signals the rule counted. Not the sweep count.</summary>
    public int Occurrences { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
    /// <summary>
    /// True when the detector cannot name a single cause from mechanical facts
    /// alone. Only a declared uncertainty may spend a strong model call
    /// (dossier section 5).
    /// </summary>
    public bool UncertainCause { get; init; }
}

/// <summary>
/// The durable Watcher record. One case per fingerprint, deduplicated across
/// sweeps and across restarts.
/// </summary>
public sealed record WatcherCase
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
    /// <summary>Short stable digest of the fingerprint, used in card tags.</summary>
    public required string FingerprintDigest { get; init; }
    public required string DetectorClass { get; init; }
    public required string DetectorRule { get; init; }
    public required string Title { get; init; }
    public string? Project { get; init; }
    public required DateTime FirstSeenAtUtc { get; init; }
    public required DateTime LastSeenAtUtc { get; init; }
    public int Occurrences { get; init; }
    /// <summary>
    /// Distinct sweeps that observed this fingerprint. A case needs two before
    /// it may be analysed, so a single transient blip never becomes a ticket.
    /// </summary>
    public int SweepCount { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
    public required string EvidenceDigest { get; init; }
    public bool UncertainCause { get; init; }
    public string State { get; init; } = WatcherCaseStates.Open;
    public string? ProposalId { get; init; }
    /// <summary>Set together with a terminal state. Never left empty on a terminal.</summary>
    public string? TerminalReason { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

/// <summary>The card draft a proposal carries, in the house prompt style.</summary>
public sealed record WatcherCardDraft
{
    public required string Title { get; init; }
    /// <summary>Context with timestamps and paths, Changes, Acceptance.</summary>
    public required string PromptMarkdown { get; init; }
    public required string TaskType { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Cards this proposal overlaps, written into task references.</summary>
    public IReadOnlyList<string> RelatedTo { get; init; } = [];
}

/// <summary>Model route recommended for the proposed card, taken from the routing policy.</summary>
public sealed record WatcherModelRecommendation
{
    public required string Tier { get; init; }
    public required string Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public required string PolicyVersion { get; init; }
    public int Score { get; init; }
    public string? CorrectnessFloorTier { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// One bounded model call the Watcher spent on a case. Kept per proposal so
/// the audit line of section 10.4 can show cost next to the decision.
/// </summary>
public sealed record WatcherModelCall
{
    public required string Purpose { get; init; }
    public required string Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    /// <summary>Null when the price catalogue has no entry. Never rendered as zero.</summary>
    public decimal? CostUsd { get; init; }
    public bool PriceKnown { get; init; }
    public DateTime AtUtc { get; init; }
}

/// <summary>Purposes a Watcher model call may serve. Detection never spends one.</summary>
public static class WatcherModelCallPurposes
{
    /// <summary>Mini/high bounded evidence compression when the pack exceeds its limit.</summary>
    public const string Compression = "compression";
    /// <summary>Sol/medium analysis, admitted only on a declared uncertainty.</summary>
    public const string Analysis = "analysis";
}

/// <summary>Operator answer recorded on a proposal.</summary>
public sealed record WatcherProposalDecision
{
    public string State { get; init; } = WatcherProposalDecisions.Pending;
    public DateTime? DecidedAtUtc { get; init; }
    public string? DecidedBy { get; init; }
    public string? Reason { get; init; }
    /// <summary>Set when the operator merged the proposal into an existing card.</summary>
    public string? MergedIntoTaskKey { get; init; }
}

/// <summary>
/// A ticket proposal: the W2 output. Proposals never enter Ready by
/// themselves; they land in the proposal state and wait for a decision.
/// </summary>
public sealed record WatcherProposal
{
    public required string Id { get; init; }
    public required string CaseId { get; init; }
    public required string Fingerprint { get; init; }
    public required string FingerprintDigest { get; init; }
    public required string DetectorClass { get; init; }
    public required string DetectorRule { get; init; }
    public string? Project { get; init; }
    public required string Kind { get; init; }
    public required WatcherCardDraft Draft { get; init; }
    public required WatcherModelRecommendation Recommendation { get; init; }
    public required string EvidenceDigest { get; init; }
    public IReadOnlyList<WatcherEvidenceItem> Evidence { get; init; } = [];
    public IReadOnlyList<WatcherModelCall> ModelCalls { get; init; } = [];
    /// <summary>Task key of the card the Watcher created, when Kind is new-card.</summary>
    public string? CreatedTaskKey { get; init; }
    /// <summary>Task key the Watcher commented on, when Kind is comment.</summary>
    public string? CommentedOnTaskKey { get; init; }
    public WatcherProposalDecision Decision { get; init; } = new();
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

/// <summary>
/// A rejected fingerprint, suppressed for a bounded and visible period.
/// Suppression always expires; a permanently silent detector is not a product.
/// </summary>
public sealed record WatcherSuppression
{
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    public required string Reason { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public string? CreatedBy { get; init; }

    public bool IsActiveAt(DateTime nowUtc) => nowUtc < ExpiresAtUtc;
}

/// <summary>Stable identifier and digest helpers shared by cases and proposals.</summary>
public static class WatcherIdentity
{
    /// <summary>
    /// Compose a fingerprint from ordered parts. The detector class leads so
    /// two classes reasoning about one subject never collide.
    /// </summary>
    public static string Fingerprint(string detectorClass, params string?[] parts)
    {
        var normalized = parts
            .Select(Normalize)
            .Where(part => part.Length > 0);
        return string.Join('|', new[] { Normalize(detectorClass) }.Concat(normalized));
    }

    /// <summary>Short stable digest of any text, safe for tags and ids.</summary>
    public static string Digest(string value, int length = 8)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexStringLower(bytes)[..Math.Clamp(length, 4, 32)];
    }

    /// <summary>
    /// Digest over the evidence items in their recorded order. Two packs with
    /// the same facts produce the same digest, so a repeat sweep does not look
    /// like new evidence and cannot unlock a second strong model call.
    /// </summary>
    public static string EvidenceDigest(IEnumerable<WatcherEvidenceItem> evidence)
    {
        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.Append(item.Label).Append('\u001f')
                   .Append(item.Available ? item.Value : "(missing)").Append('\u001f')
                   .Append(item.Source).Append('\u001e');
        }
        return Digest(builder.ToString(), 16);
    }

    /// <summary>Case id derived from the fingerprint, so a restart reconstructs the same id.</summary>
    public static string CaseId(string fingerprint) => $"WCH-{Digest(fingerprint, 10)}";

    /// <summary>Proposal id derived from the case id and the evidence digest.</summary>
    public static string ProposalId(string caseId, string evidenceDigest)
        => $"WPR-{Digest(caseId + "|" + evidenceDigest, 10)}";

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '/' || ch == ':')
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }
        return builder.ToString().Trim('-');
    }
}
