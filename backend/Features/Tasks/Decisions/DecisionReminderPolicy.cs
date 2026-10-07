namespace AgentStudio.Tasks;

/// <summary>A card that may wait on a decision, as the reminder policy sees it.</summary>
public sealed record DecisionWaitingCard(string Key, string State, bool DependsOnDecision);

/// <summary>
/// Pure reminder rule for pending decision cards (Dossier decision-cards
/// D5=A): once a pending decision passes its due date, remind once per pending
/// cycle and name the cards it blocks. The due date defaults to three days
/// after the decision was requested or last reopened.
/// </summary>
public static class DecisionReminderPolicy
{
    public static readonly TimeSpan DefaultDueAfter = TimeSpan.FromDays(3);

    /// <summary>Start of the current pending cycle: the last reopen, else the request.</summary>
    public static DateTime PendingSince(DecisionContent content, DateTime requestedAtUtc) =>
        (content.History ?? [])
            .Where(entry => entry.Status == DecisionStatuses.Reopened)
            .Select(entry => (DateTime?)entry.At.ToUniversalTime())
            .LastOrDefault() ?? requestedAtUtc.ToUniversalTime();

    /// <summary>
    /// The due instant for the current cycle. An explicit due date counts while
    /// it lies inside the cycle; a reopened decision whose due date already
    /// passed in an earlier cycle falls back to the default window.
    /// </summary>
    public static DateTime DueAt(DecisionContent content, DateTime requestedAtUtc)
    {
        var since = PendingSince(content, requestedAtUtc);
        var reopened = (content.History ?? []).Any(entry => entry.Status == DecisionStatuses.Reopened);
        if (content.DueDate is { } due && (!reopened || due.ToUniversalTime() > since))
            return due.ToUniversalTime();
        return since + DefaultDueAfter;
    }

    public static bool IsDue(DecisionContent content, DateTime requestedAtUtc, DateTime nowUtc) =>
        DecisionStatuses.IsOpen(content.Status)
        && content.RemindedAt is null
        && nowUtc.ToUniversalTime() >= DueAt(content, requestedAtUtc);

    /// <summary>
    /// The cards the decision blocks: the declared dependants and linked
    /// implementation cards, plus every card whose <c>dependsOn</c> names the
    /// decision. Cards already completed or archived are not waiting.
    /// </summary>
    public static IReadOnlyList<string> BlockedCards(
        DecisionContent content, IReadOnlyList<DecisionWaitingCard> cards)
    {
        var byKey = cards
            .GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var declared = (content.Dependants ?? []).Concat(content.AppliesTo ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim());
        var discovered = cards.Where(card => card.DependsOnDecision).Select(card => card.Key);
        return declared.Concat(discovered)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => !byKey.TryGetValue(key, out var card) || !IsTerminal(card.State))
            .ToList();
    }

    private static bool IsTerminal(string state) =>
        state is TaskStates.Completed or TaskStates.Archive;
}
