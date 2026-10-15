namespace AgentStudio.OrchestrationDecisions;

/// <summary>Facts of one open cause breaker whose cause card the sweep looked up.</summary>
/// <param name="Fingerprint">The cause fingerprint of the breaker.</param>
/// <param name="CauseCardState">The cause card's lane, or null when the card is not found.</param>
public sealed record CauseBreakerOperatorFacts(string Fingerprint, string? CauseCardState);

/// <summary>
/// When an open cause breaker closes on the cause-card side (AGT-W57 §5 E1;
/// formerly <c>CauseBreakerPolicy.Close</c> in the monolith). Only Delivered
/// counts as integrated; an archived or missing cause card proves nothing
/// (D5 fail-closed), so the breaker stays open until an operator probe or a
/// green review closes it.
/// </summary>
public static class CauseBreakerClosePolicy
{
    public const string CauseIntegrated = "cause-integrated";
    public const string CauseNotIntegrated = "cause-not-integrated";

    public static bool Closes(CauseBreakerOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return string.Equals(facts.CauseCardState, TaskStates.Completed, StringComparison.OrdinalIgnoreCase);
    }
}
