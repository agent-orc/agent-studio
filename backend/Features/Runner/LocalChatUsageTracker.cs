namespace AgentStudio.Runner;

/// <summary>In-process activity and completed usage for local interactive turns.</summary>
public sealed class LocalChatUsageTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveTurn> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Tokens, decimal? Cost)> _completed =
        new(StringComparer.OrdinalIgnoreCase);

    public void Start(string turnId, string project, DateTime queuedAt, DateTime startedAt)
    {
        lock (_gate) _active[turnId] = new ActiveTurn(project, queuedAt, startedAt);
    }

    public void Complete(string turnId, OrchestratorTokenUsage? usage, string? model, DateTime finishedAt)
    {
        lock (_gate)
        {
            if (!_active.Remove(turnId, out var active) || usage is null) return;
            var estimate = TokenPricing.Estimate(usage.Model ?? model,
                usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens,
                usage.CacheCreationTokens, finishedAt);
            var hasPrevious = _completed.TryGetValue(active.Project, out var previous);
            var tokens = (long)usage.InputTokens + usage.CacheReadTokens
                + usage.CacheCreationTokens + usage.OutputTokens;
            _completed[active.Project] = (
                previous.Tokens + tokens,
                estimate.ModelKnown && (!hasPrevious || previous.Cost.HasValue)
                    ? (previous.Cost ?? 0) + estimate.Total : null);
        }
    }

    public IReadOnlyList<RemoteChatUsage> GetUsage(DateTime? now = null)
    {
        lock (_gate)
        {
            var at = now ?? DateTime.UtcNow;
            return _active.Values.Select(turn => turn.Project)
                .Union(_completed.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(project =>
                {
                    var active = _active.Values.Where(turn =>
                        string.Equals(turn.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var completed = _completed.GetValueOrDefault(project);
                    return new RemoteChatUsage(
                        Environment.MachineName, project, active.Length,
                        active.Count(turn => at - turn.StartedAt >= TimeSpan.FromSeconds(30)),
                        null, completed.Tokens, completed.Cost);
                })
                .OrderBy(row => row.ProjectName)
                .ToArray();
        }
    }

    private sealed record ActiveTurn(string Project, DateTime QueuedAt, DateTime StartedAt);
}
