namespace AgentStudio.Tokens;

/// <summary>
/// Participant kind of a usage ledger row, read from the participant id
/// prefix: <c>agent:*</c> coding runs, <c>support:*</c> supporting loops,
/// and <c>orchestrator:*</c> project meta-turns.
/// </summary>
public static class TokenParticipants
{
    public static bool IsAgent(string? participantId)
        => HasPrefix(participantId, "agent:");

    public static bool IsSupporting(string? participantId)
        => HasPrefix(participantId, "support:");

    public static bool IsOrchestrator(string? participantId)
        => HasPrefix(participantId, "orchestrator:");

    /// <summary>Measured execution family used to reconcile receipts against pipeline rows.</summary>
    public static StepKind? ReconciliationFamily(string? participantId)
    {
        var step = ReviewStatisticsFold.StepId(participantId);
        if (IsOrchestrator(participantId)
            || step is "post-code-review-grade" or "review-decision"
                or "post-orchestrator-review" or "post-orchestrator-decision")
            return StepKind.Orchestrator;
        return IsSupporting(participantId) ? StepKind.Aspect : null;
    }

    private static bool HasPrefix(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
