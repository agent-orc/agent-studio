namespace AgentStudio.OrchestrationDecisions;

/// <param name="Used">Automatic rounds this card has already spent, across every review-attempt epoch.</param>
/// <param name="Allowed">Rounds the card may spend in its lifetime.</param>
public sealed record CardRoundBudgetState(int Used, int Allowed)
{
    public int Remaining => Math.Max(0, Allowed - Used);
    public bool Exhausted => Used >= Allowed;
}

public enum CardRoundBudgetAction { OpenRound, DegradeToFollowUp, Escalate }

public static class CardRoundBudgetPolicy
{
    public static CardRoundBudgetAction Decide(
        CardRoundBudgetState budget, IEnumerable<string> blockingAspects)
    {
        if (!budget.Exhausted) return CardRoundBudgetAction.OpenRound;
        var aspects = blockingAspects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return aspects.Overlaps(["build-tests", "tests-and-evidence", "review-verification"])
            ? CardRoundBudgetAction.Escalate
            : CardRoundBudgetAction.DegradeToFollowUp;
    }

    /// <summary>
    /// The review-plane question, shared by Remote Review and the local
    /// orchestrator: a spent budget only intervenes when the review would open
    /// another round, a block (finding round) or a concern round. A passing
    /// report whose concerns open no round keeps its Pass without a follow-up.
    /// </summary>
    public static CardRoundBudgetAction DecideForReview(
        CardRoundBudgetState budget, IReadOnlyCollection<string> blockingAspects, bool opensConcernRound)
        => blockingAspects.Count > 0 || opensConcernRound
            ? Decide(budget, blockingAspects)
            : CardRoundBudgetAction.OpenRound;

}
