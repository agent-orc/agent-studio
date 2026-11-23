namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Frozen facts for one post-merge failure. The host gathers these facts before asking for a repair.
/// <paramref name="IncidentAge"/> is the age of this incident since its first red receipt, not the
/// age of the project's red develop. <paramref name="RepairState"/> is the repair card's lane, or null
/// while no repair card exists yet. Auto Review covers the gate queue, the gate execution and the
/// wait for integration, because all of them keep the card in that lane.
/// </summary>
public sealed record RedDevelopRepairFacts(
    int SuspectCount,
    bool RevertAppliesCleanly,
    bool LaterMergeDependsOnSuspect,
    TimeSpan IncidentAge,
    string? RepairState = null);

/// <summary>
/// RT-1 (AGT-3988 RT-D7 B): the decision vocabulary. None of these moves a card that is in
/// Auto Review, Human Review or Completed; <see cref="AskOwner"/> raises one owner item.
/// </summary>
public enum RedDevelopRepairAction { Revert, FixForward, Hold, AskOwner }

/// <summary>The answers the owner item offers. Switching to strict stays an owner action.</summary>
public enum RedDevelopOwnerOption { RevertAttributedMerge, KeepFixingForward, SwitchProjectToStrict }

/// <summary>The owner question of an <see cref="RedDevelopRepairAction.AskOwner"/> decision.</summary>
public sealed record RedDevelopOwnerQuestion(
    IReadOnlyList<RedDevelopOwnerOption> Options,
    RedDevelopOwnerOption Recommended,
    string RecommendationReason);

/// <summary>
/// <see cref="Repair"/> is the coding route of the repair card (Revert or FixForward) and is set on
/// every decision, so a host creating the card for a new incident never has to ask twice.
/// </summary>
public sealed record RedDevelopRepairDecision(RedDevelopRepairAction Action, string Reason,
    RedDevelopRepairAction Repair, RedDevelopOwnerQuestion? Question = null);

/// <summary>What a host does with an earlier repair card of the same fingerprint when a new incident opens.</summary>
public enum RecurringRepairCardAction
{
    /// <summary>Move the card back to Preparation with the new evidence.</summary>
    Reopen,
    /// <summary>Link the incident to the card and leave its lane and attempt alone.</summary>
    Attach,
    /// <summary>The earlier card is settled; create a new repair card.</summary>
    CreateNew,
}

/// <summary>
/// D2 A with RT-1: one clean, independent merge is reverted, otherwise the failure is fixed forward.
/// A repair that review, a gate, integration or a settled delivery owns is held. After two hours of
/// incident age the owner is asked once; there is no automatic project-wide fallback and no lane move.
/// </summary>
public static class RedDevelopRepairPolicy
{
    /// <summary>The incident age at which the owner is asked. The only copy of this bound.</summary>
    public static readonly TimeSpan OwnerAskAge = TimeSpan.FromHours(2);

    public static RedDevelopRepairDecision Decide(RedDevelopRepairFacts facts)
    {
        var repair = ChooseRepair(facts);
        if (facts.IncidentAge >= OwnerAskAge)
        {
            var recommended = repair == RedDevelopRepairAction.Revert
                ? RedDevelopOwnerOption.RevertAttributedMerge
                : RedDevelopOwnerOption.KeepFixingForward;
            return new(RedDevelopRepairAction.AskOwner,
                "This incident has been red for two hours; ask the owner once and leave every lane alone.",
                repair,
                new([RedDevelopOwnerOption.RevertAttributedMerge, RedDevelopOwnerOption.KeepFixingForward,
                        RedDevelopOwnerOption.SwitchProjectToStrict],
                    recommended,
                    repair == RedDevelopRepairAction.Revert
                        ? "Exactly one merge is attributed and an independent revert applies cleanly."
                        : "No single clean independent suspect exists, so a revert is not safe."));
        }
        if (RepairOwnedElsewhere(facts.RepairState))
            return new(RedDevelopRepairAction.Hold,
                "The repair belongs to review, a gate, integration or a settled delivery; leave its lane alone.",
                repair);
        return new(repair, repair == RedDevelopRepairAction.Revert
                ? "One merge is attributed and an independent revert applies cleanly."
                : "The failure needs a coding repair because a safe independent revert is unavailable.",
            repair);
    }

    /// <summary>Revert for exactly one clean, independent suspect; otherwise fix forward.</summary>
    public static RedDevelopRepairAction ChooseRepair(RedDevelopRepairFacts facts)
        => RedTestTriagePolicy.CanProposeRevert(facts.SuspectCount, facts.RevertAppliesCleanly, facts.LaterMergeDependsOnSuspect)
            ? RedDevelopRepairAction.Revert
            : RedDevelopRepairAction.FixForward;

    /// <summary>
    /// A recurring fingerprint never pulls a card out of Progress, Auto Review, Human Review,
    /// Completed or Archive. A settled card stays settled and a new card carries the repair.
    /// </summary>
    public static RecurringRepairCardAction RecurringCard(string? lane) => lane switch
    {
        TaskStates.Completed or TaskStates.Archive or null => RecurringRepairCardAction.CreateNew,
        TaskStates.Progress or TaskStates.AutoReview or TaskStates.HumanReview => RecurringRepairCardAction.Attach,
        _ => RecurringRepairCardAction.Reopen,
    };

    public static bool MayMerge(IReadOnlyCollection<string> changedPaths,
        IReadOnlyCollection<string> failingArea)
        => HoldScopePolicy.MayMergeFiles(changedPaths, failingArea);

    public static bool MayCreateRelease(bool developRed) => !developRed;

    private static bool RepairOwnedElsewhere(string? lane)
        => lane is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive;
}
