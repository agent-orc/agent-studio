using System.Globalization;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2856 - pure policy that keeps the acceptance rail from repeating an
/// attempt that already failed on unchanged inputs.
///
/// <para>
/// A refused acceptance is a decision, not a symptom: as long as the card, its
/// Git-derived integration verdict, and the action the rail would take are all
/// unchanged, repeating the move produces the same refusal and the same log
/// line. The rail therefore fingerprints the facts it acted on and only acts
/// again once a new fact arrives - a push that lands the delivery, a new
/// delivery, an operator move, or a failure classification change. The
/// fingerprint is derived only from its arguments, so the same facts always
/// yield the same verdict.
/// </para>
/// </summary>
public static class AcceptanceRailAttemptPolicy
{
    private const char Separator = '\u001f';

    /// <summary>
    /// Identity of one rail attempt: the card facts, the Git-derived verdict,
    /// and the action those facts produced. Two attempts with the same
    /// fingerprint have the same inputs and therefore the same outcome.
    /// </summary>
    public static string Fingerprint(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        AcceptanceRailDecision decision,
        string? integrationAttemptReason = null)
        => string.Join(
            Separator,
            task.State,
            task.EnteredLaneAt.ToString("O", CultureInfo.InvariantCulture),
            decision.Action.ToString(),
            decision.Reason,
            integration?.Status ?? "unknown",
            integration?.Sha ?? string.Empty,
            integration?.DeliveryRef ?? string.Empty,
            integration?.Detail ?? string.Empty,
            integration?.Failure?.Code ?? string.Empty,
            integration?.Failure?.FailureSignature ?? string.Empty,
            integrationAttemptReason ?? string.Empty);

    /// <summary>
    /// Whether the rail may act on <paramref name="fingerprint"/>. False only
    /// when the very same fingerprint was already refused, which is the tight
    /// retry loop this policy removes.
    /// </summary>
    public static bool ShouldAttempt(string fingerprint, string? lastRefusedFingerprint)
        => !string.Equals(fingerprint, lastRefusedFingerprint, StringComparison.Ordinal);
}
