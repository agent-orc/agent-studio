namespace AgentStudio.Operator;

/// <summary>
/// One open fact of the decision stream as the attention list shows it
/// (AGT-3576). <see cref="Id"/> is the occurrence key, so a row keeps its
/// identity across reads until the fact clears.
/// </summary>
public sealed record OperatorAttentionItem(
    string Id,
    string Type,
    string? Project,
    string? CardKey,
    IReadOnlyList<string> Cards,
    string Reason,
    DateTime ObservedAt,
    IReadOnlyList<string> SuggestedOperations,
    IReadOnlyList<OperatorEvidenceLink> Evidence,
    string? Title = null);

/// <summary>
/// Answer of <c>GET /api/operator/attention</c>. <see cref="Count"/> is the
/// number of <see cref="Items"/>, so a header count and the list it opens
/// cannot disagree (style-guide R3).
/// </summary>
public sealed record OperatorAttentionResponse(IReadOnlyList<OperatorAttentionItem> Items, int Count);

/// <summary>
/// Pure projection from the facts the decision stream currently sees to the
/// rows of the attention list. It reads the same facts as the stream, so the
/// list wakes on exactly what <c>studio-op watch</c> wakes on, and it applies
/// the stream's project scope. It adds no threshold or rule of its own.
/// </summary>
public static class OperatorAttention
{
    public static OperatorAttentionResponse Project(
        IEnumerable<OperatorDecisionFact> facts, OperatorDecisionReader reader)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = facts
            .Where(fact => fact.Type != OperatorDecisionTypes.WatcherHeartbeat
                && OperatorDecisionPolicy.Shows(fact.Type, fact.Project, reader)
                && seen.Add(fact.Key))
            .OrderBy(fact => fact.ObservedAtUtc)
            .ThenBy(fact => fact.Key, StringComparer.Ordinal)
            .Select(fact => new OperatorAttentionItem(fact.Key, fact.Type, fact.Project, fact.CardKey,
                fact.Cards ?? (fact.CardKey is { } card ? [card] : []), fact.Reason, fact.ObservedAtUtc,
                OperatorOperations.For(fact.Type), fact.Evidence, fact.Title))
            .ToArray();
        return new OperatorAttentionResponse(items, items.Length);
    }
}
