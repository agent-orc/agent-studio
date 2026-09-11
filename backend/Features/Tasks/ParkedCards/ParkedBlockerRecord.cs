using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// The closed set of machine-readable park conditions. A parked card records
/// WHAT it waits for in this vocabulary instead of only a freetext reason, so a
/// sweep can ask "is this still true?" without a human re-reading prose.
///
/// <para>New kinds land as a triple in one change: a constant here, a branch in
/// <see cref="ParkedBlockerProbe"/>, and a row in
/// <c>ParkedBlockerPolicyTests</c>. The list is deliberately small - a
/// condition that no probe can evaluate belongs to <see cref="Manual"/>, which
/// is honest about needing a person.</para>
/// </summary>
public static class ParkedBlockerConditionKinds
{
    /// <summary>No automatic check exists; only a human can clear this park.</summary>
    public const string Manual = "manual";

    /// <summary>
    /// Resolved once <see cref="ParkedBlockerParameters.Ancestor"/> is reachable
    /// from <see cref="ParkedBlockerParameters.Descendant"/> in
    /// <see cref="ParkedBlockerParameters.RepositoryRoot"/>. This is the AGT-2220
    /// condition: a baseline-comparison review cannot materialize its subject
    /// until the card branch carries the current integration branch, and the
    /// documented fix ("merge the card branch fresh onto develop") is exactly
    /// that ancestry becoming true.
    /// </summary>
    public const string GitAncestor = "git-ancestor";
}

/// <summary>Parameter keys used by <see cref="ParkedBlockerConditionKinds"/>.</summary>
public static class ParkedBlockerParameters
{
    public const string RepositoryRoot = "repositoryRoot";
    public const string Ancestor = "ancestor";
    public const string Descendant = "descendant";
}

/// <summary>Evaluation verdicts for a <see cref="ParkedBlockerCondition"/>.</summary>
public static class ParkedBlockerStatuses
{
    /// <summary>The condition still holds; the card stays parked.</summary>
    public const string Blocked = "blocked";

    /// <summary>The condition is gone. The card is REPORTED as recallable; it is
    /// never requeued automatically.</summary>
    public const string Recallable = "recallable";

    /// <summary>No probe could decide (manual condition, missing parameters, an
    /// unreadable repository). Treated exactly like <see cref="Blocked"/> for
    /// lane purposes and surfaced separately so the board can tell "still
    /// blocked" from "nobody can tell".</summary>
    public const string Undeterminable = "undeterminable";
}

/// <summary>
/// The machine-readable half of a park: which check decides whether the
/// precondition is gone.
/// </summary>
public sealed record ParkedBlockerCondition
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ParkedBlockerConditionKinds.Manual;

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>One sentence a human can read without decoding the parameters.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    public string? Parameter(string key)
        => Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

/// <summary>One probe result, persisted so the board renders the last verdict
/// without re-running git on the request path.</summary>
public sealed record ParkedBlockerEvaluation
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = ParkedBlockerStatuses.Blocked;

    /// <summary>When this verdict was first observed - NOT the last time the
    /// sweep ran. An unchanged verdict is deliberately not re-persisted, because
    /// re-writing the marker would bump the job folder's mtime and reset the
    /// card's activity age (see <c>ParkedCardRecallPolicy.NeedsPersist</c>).</summary>
    [JsonPropertyName("at")]
    public DateTime At { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";
}

/// <summary>
/// The durable park marker, written next to <c>task.json</c> as
/// <c>parked-blocker.json</c> whenever a card enters a human-decision lane.
///
/// <para>Before this record existed a park carried only the freetext
/// <c>lane_changed</c> reason. AGT-2220 was parked on 2026-07-29 with
/// "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto
/// rerun"; the precondition was cleared on 2026-08-02 and nothing noticed, because
/// nothing could: prose is not a condition anything can re-evaluate. This record
/// is the condition.</para>
/// </summary>
public sealed record ParkedBlockerRecord
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>The escalation category that parked the card (see
    /// <c>HumanReviewEscalationCategories</c>), or <c>operator-decision</c> for a
    /// human park.</summary>
    [JsonPropertyName("blockerType")]
    public string BlockerType { get; init; } = "";

    [JsonPropertyName("condition")]
    public ParkedBlockerCondition Condition { get; init; } = new();

    /// <summary>The lane the card was parked in.</summary>
    [JsonPropertyName("lane")]
    public string Lane { get; init; } = "";

    [JsonPropertyName("parkedAt")]
    public DateTime ParkedAt { get; init; }

    /// <summary>The original freetext reason, preserved verbatim.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    /// <summary>Task-relative artifact containing the full agent question.</summary>
    [JsonPropertyName("needsInputFile")]
    public string? NeedsInputFile { get; init; }

    /// <summary>The latest probe verdict, or null when no sweep has run yet.</summary>
    [JsonPropertyName("lastEvaluation")]
    public ParkedBlockerEvaluation? LastEvaluation { get; init; }

    /// <summary>When the recall was last announced on the timeline. Keeps the
    /// sweep from re-announcing the same resolved blocker on every tick.</summary>
    [JsonPropertyName("reportedRecallableAt")]
    public DateTime? ReportedRecallableAt { get; init; }
}
