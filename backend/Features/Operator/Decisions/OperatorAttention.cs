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
    string? Title = null,
    IReadOnlyList<OperatorOwnerOption>? OwnerOptions = null)
{
    public bool IsDecision => Type is OperatorDecisionTypes.HumanReviewReady or OperatorDecisionTypes.EscalationOpened
        or OperatorDecisionTypes.DecisionCardOpened or OperatorDecisionTypes.ElevationRateEnded or OperatorDecisionTypes.AuditAlert
        or OperatorDecisionTypes.PushBlocked or OperatorDecisionTypes.BatchRecordUnreadable || OwnerOptions is { Count: > 0 };
    public bool CanDismiss { get; init; }
}

/// <summary>
/// Answer of <c>GET /api/operator/attention</c>. <see cref="Count"/> is the
/// number of <see cref="Items"/>, with separate counts for decisions and notices.
/// </summary>
public sealed record OperatorAttentionResponse(IReadOnlyList<OperatorAttentionItem> Items, int Count)
{
    public int DecisionCount => Items.Count(item => item.IsDecision);
    public int NoticeCount => Count - DecisionCount;
}

/// <summary>
/// Pure projection from the facts the decision stream currently sees to the
/// current rows in the reader's scope. Card occurrences use the live board
/// and post-merge receipts share one row per project integration line.
/// </summary>
public static class OperatorAttention
{
    public static OperatorAttentionResponse Project(
        IEnumerable<OperatorDecisionFact> facts, OperatorDecisionReader reader,
        Func<string, bool>? activeProject = null, OperatorAttentionBoard? board = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = facts
            .Where(fact => fact.Type != OperatorDecisionTypes.WatcherHeartbeat
                && fact.Type != OperatorDecisionTypes.DeployCompleted
                && OperatorDecisionPolicy.Shows(fact.Type, fact.Project, reader)
                && (fact.Project is null || activeProject is null || activeProject(fact.Project))
                && seen.Add(fact.Key))
            .Where(fact => board?.IsOpen(fact) != false)
            // Receipts cover the single integration line recorded for each project.
            .OrderByDescending(fact => fact.OwnerOptions is { Count: > 0 })
            .ThenByDescending(fact => fact.ObservedAtUtc)
            .ThenBy(fact => fact.Key, StringComparer.Ordinal)
            .DistinctBy(fact => fact.Type is OperatorDecisionTypes.PostMergeRedIncident or OperatorDecisionTypes.PostMergeRunsMissing
                ? ("branch", fact.Project) : ("occurrence", fact.Key))
            .OrderBy(fact => fact.Type == OperatorDecisionTypes.EnvironmentIncidentOpen ? 0 : 1)
            .ThenBy(fact => fact.ObservedAtUtc)
            .ThenBy(fact => fact.Key, StringComparer.Ordinal)
            .Select(fact => new OperatorAttentionItem(fact.Key, fact.Type, fact.Project, fact.CardKey,
                fact.Cards ?? (fact.CardKey is { } card ? [card] : []), fact.Reason, fact.ObservedAtUtc,
                OperatorOperations.For(fact.Type), fact.Evidence, fact.Title, fact.OwnerOptions))
            .ToArray();
        return new OperatorAttentionResponse(items, items.Length);
    }
}
