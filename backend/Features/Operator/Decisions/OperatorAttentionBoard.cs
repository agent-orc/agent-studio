using AgentStudio.Shared;

namespace AgentStudio.Operator;

/// <summary>Joins card-backed occurrences to the same live cards visible on the board.</summary>
public sealed class OperatorAttentionBoard
{
    private readonly Dictionary<string, Dictionary<string, TaskInfo>> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TaskInfo, DateTime?> _lastRun;
    private readonly Func<TaskInfo, string?> _waitingBatch;
    private readonly Dictionary<TaskInfo, string?> _owners = new(ReferenceEqualityComparer.Instance);

    public OperatorAttentionBoard(IEnumerable<TaskInfo> cards, Func<TaskInfo, DateTime?> lastRun,
        Func<TaskInfo, string?> waitingBatch, IEnumerable<ProjectRecord>? projects = null)
    {
        _lastRun = lastRun;
        _waitingBatch = waitingBatch;
        foreach (var card in cards.Where(card => !card.Fixture && card.State != TaskStates.Archive))
        {
            if (!_projects.TryGetValue(card.ProjectName, out var aliases))
                _projects[card.ProjectName] = aliases = new(StringComparer.OrdinalIgnoreCase);
            aliases[card.Id] = card;
            if (!string.IsNullOrEmpty(card.Key)) aliases[card.Key] = card;
            if (!string.IsNullOrEmpty(card.TaskKey)) aliases[card.TaskKey] = card;
        }
        foreach (var project in projects ?? [])
        {
            if (!_projects.TryGetValue(project.DisplayName, out var aliases)) continue;
            _projects.TryAdd(project.Id, aliases);
            if (!string.IsNullOrEmpty(project.ShortCode)) _projects.TryAdd(project.ShortCode, aliases);
        }
    }

    public bool IsOpen(OperatorDecisionFact fact)
    {
        if (fact.Project is null) return true;
        var cards = _projects.GetValueOrDefault(fact.Project);
        var card = fact.CardKey is { } key ? cards?.GetValueOrDefault(key) : null;
        return fact.Type switch
        {
            OperatorDecisionTypes.HumanReviewReady => card?.State == TaskStates.HumanReview,
            OperatorDecisionTypes.EscalationOpened => card?.State == TaskStates.Escalated,
            OperatorDecisionTypes.DecisionCardOpened => card is not null && card.State != TaskStates.Completed
                && card.Decision is { } decision && DecisionStatuses.IsOpen(decision.Status),
            OperatorDecisionTypes.OverlapStarved => card?.State == TaskStates.Ready,
            OperatorDecisionTypes.PushPendingOverdue => card is not null,
            OperatorDecisionTypes.PushBlocked => card?.State == TaskStates.AutoReview,
            OperatorDecisionTypes.QuotaFallbackActive => card?.State == TaskStates.Progress,
            OperatorDecisionTypes.QuotaExhausted when fact.CardKey is not null => card is not null
                && card.State != TaskStates.Completed && !(_lastRun(card) > fact.ObservedAtUtc),
            OperatorDecisionTypes.BatchRed => fact.BatchId is not null && (fact.Cards ?? []).Any(member =>
                cards?.GetValueOrDefault(member) is { State: TaskStates.AutoReview } waiting
                && WaitingBatch(waiting) == fact.BatchId),
            _ => true,
        };
    }

    private string? WaitingBatch(TaskInfo card)
    {
        if (!_owners.TryGetValue(card, out var owner)) _owners[card] = owner = _waitingBatch(card);
        return owner;
    }
}
