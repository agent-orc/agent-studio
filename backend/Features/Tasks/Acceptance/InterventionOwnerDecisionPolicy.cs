namespace AgentStudio.Tasks;

/// <summary>Facts of one operator move that may be an owner decision on an intervention card.</summary>
/// <param name="TargetState">The requested lane.</param>
/// <param name="OperatorOverride">The request already carries an explicit override.</param>
/// <param name="HasOperatorAuthority">The caller holds operator authority (Owner or Admin, or the local operator).</param>
/// <param name="IsInterventionFollowUp">The failure-intervention ledger names the card as its follow-up.</param>
/// <param name="HasAttributedCommits">The card carries a repository delivery.</param>
/// <param name="HasRunAttempt">The card has a run attempt whose delivery the content check judges.</param>
public sealed record InterventionOwnerDecisionFacts(
    string TargetState,
    bool OperatorOverride,
    bool HasOperatorAuthority,
    bool IsInterventionFollowUp,
    bool HasAttributedCommits,
    bool HasRunAttempt);

/// <summary>
/// AGT-3617: an intervention card raised by the orchestrator asks the owner
/// to resolve a shared cause and "complete this task". It has no delivery, so
/// the plain "Move to Accepted" was refused for a missing delivery brief and
/// content verdict, and the cards held behind it stayed held.
/// <para>
/// A person with operator authority moving such a card to Accepted is the
/// owner's decision that the cause is resolved. The move then completes as a
/// code-free resolution whose claim records that decision and who made it.
/// Delivery evidence still outranks the intent: a card with commits or a run
/// attempt goes through the ordinary acceptance checks.
/// </para>
/// </summary>
public static class InterventionOwnerDecisionPolicy
{
    public const string RecordedReason =
        "Owner decision: the shared cause is resolved, so this intervention is accepted without a delivery.";

    public static bool Applies(InterventionOwnerDecisionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return string.Equals(facts.TargetState, TaskStates.Completed, StringComparison.Ordinal)
            && !facts.OperatorOverride
            && facts.HasOperatorAuthority
            && facts.IsInterventionFollowUp
            && !facts.HasAttributedCommits
            && !facts.HasRunAttempt;
    }

    /// <summary>The reason the claim stores: the operator's own words when usable, else the recorded decision.</summary>
    public static string Reason(string? written)
        => CompletionContractPolicy.IsUsableReason(written?.Trim()) ? written!.Trim() : RecordedReason;
}
