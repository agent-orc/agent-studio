using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// AGT-2817 - the grounds on which a card is allowed to claim completion.
/// A move into <c>6-completed</c> records exactly one of these on the card, so
/// "delivered" can be read back as a checkable statement instead of a lane
/// position. Kept as string constants, matching
/// <see cref="IntegrationStatuses"/>, so the wire and <c>task.json</c> format
/// stays a readable literal.
/// </summary>
public static class CompletionClaimBases
{
    /// <summary>
    /// The card's effective delivery commit is contained in the project's
    /// integration branch. Decided by Git ancestry, never by a stored record.
    /// </summary>
    public const string IntegratedDelivery = "integrated-delivery";

    /// <summary>
    /// The card declares a deliverable without code (concept, planning,
    /// research, documentation) and names it - for a concept card the Dossier
    /// path and key.
    /// </summary>
    public const string DeliverableWithoutCode = "deliverable-without-code";

    /// <summary>
    /// An operator completed the card against the contract, with a written
    /// reason that is stored here and shown wherever the card claims
    /// completion.
    /// </summary>
    public const string OperatorOverride = "operator-override";

    public static readonly string[] All = [IntegratedDelivery, DeliverableWithoutCode, OperatorOverride];

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        foreach (var basis in All)
            if (string.Equals(basis, trimmed, StringComparison.OrdinalIgnoreCase)) return basis;
        return null;
    }
}

/// <summary>
/// Typed reasons the completion contract refuses a move into
/// <c>6-completed</c>. Each one is answerable by the operator: integrate the
/// delivery, name the deliverable, or override with a written reason.
/// </summary>
public static class CompletionRefusalCodes
{
    /// <summary>The card expects code and its delivery is not contained in the integration branch.</summary>
    public const string UnintegratedDelivery = "unintegrated-delivery";

    /// <summary>
    /// Every attributed delivery is marked superseded and no successor is
    /// recorded, so the card would claim a delivery that does not exist.
    /// </summary>
    public const string SupersededOnlyDelivery = "superseded-only-delivery";

    /// <summary>A code-free card names no deliverable.</summary>
    public const string UndeclaredDeliverable = "undeclared-deliverable";

    /// <summary>An operator override arrived without a written reason.</summary>
    public const string OverrideWithoutReason = "override-without-reason";
}

/// <summary>
/// The recorded completion claim. Written once per accepted move into
/// <c>6-completed</c> and replaced (not appended) when the card is completed
/// again after a reopen, so the card always states its current grounds.
/// </summary>
public sealed record TaskCompletionClaim
{
    /// <summary>One of <see cref="CompletionClaimBases"/>.</summary>
    public string Basis { get; init; } = CompletionClaimBases.OperatorOverride;

    /// <summary>Human-readable evidence for <see cref="Basis"/>, shown next to the completion claim.</summary>
    public string Evidence { get; init; } = "";

    /// <summary>
    /// Verbatim operator reason. Required for
    /// <see cref="CompletionClaimBases.OperatorOverride"/>, null otherwise.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Who moved the card into the delivered lane.</summary>
    public string? Actor { get; init; }

    /// <summary>Delivery commit that proved containment. Null unless <see cref="Basis"/> is integrated.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Integration branch the containment answer was computed against.</summary>
    public string? IntegrationBranch { get; init; }

    /// <summary>Repository-relative Dossier path for a code-free deliverable.</summary>
    public string? DeliverablePath { get; init; }

    /// <summary>Dossier or workbench key for a code-free deliverable.</summary>
    public string? DeliverableKey { get; init; }

    [JsonPropertyName("recordedAt")]
    public DateTime RecordedAtUtc { get; init; }
}
