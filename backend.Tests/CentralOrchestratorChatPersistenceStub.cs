using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

internal sealed class CentralOrchestratorChatPersistenceStub : IOrchestratorChatPersistence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<OrchestratorChatTurn>> _turns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _summaries =
        new(StringComparer.Ordinal);

    public bool IsCentralTaskServerStore => true;

    public void SeedContext(string contextKey, string summary, params OrchestratorChatTurn[] turns)
    {
        lock (_gate)
        {
            _turns[contextKey] = [.. turns];
            _summaries[contextKey] = summary;
        }
    }

    public Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        int limit,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var key = ContextKey(projectName, context);
            if (!_turns.TryGetValue(key, out var turns))
            {
                turns = [];
                _turns[key] = turns;
                _summaries[key] = context?.TaskKey ?? $"Project chat for {projectName}";
            }
            return Task.FromResult<IReadOnlyList<OrchestratorChatTurn>>(
                turns.TakeLast(Math.Clamp(limit, 1, 1000)).ToArray());
        }
    }

    public Task AppendAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        OrchestratorChatTurn turn,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var key = ContextKey(projectName, context);
            if (!_turns.TryGetValue(key, out var turns))
            {
                turns = [];
                _turns[key] = turns;
            }
            turns.Add(turn);
            if (turn.Role == OrchestratorChatRoles.User && !string.IsNullOrWhiteSpace(turn.Text))
                _summaries[key] = turn.Text;
            else if (!_summaries.ContainsKey(key))
                _summaries[key] = context?.TaskKey ?? $"Project chat for {projectName}";
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<OrchestratorContextDto>> ListContextsAsync(
        bool includeHidden,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var contexts = _turns.Keys.Select(key =>
            {
                if (!OrchestratorContextKey.TryParse(key, out var parsed))
                    throw new InvalidOperationException($"Invalid test context key '{key}'.");
                var project = parsed.ProjectId ?? string.Empty;
                return new OrchestratorContextDto(
                    key,
                    parsed.Kind,
                    project,
                    project,
                    null,
                    parsed.TaskKey,
                    _summaries.GetValueOrDefault(key) ?? parsed.TaskKey ?? $"Project chat for {project}",
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    null,
                    _turns[key].Count);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<OrchestratorContextDto>>(contexts);
        }
    }

    private static string ContextKey(string projectName, OrchestratorContextKey? context)
        => context?.Kind is OrchestratorContextKey.TaskKind or OrchestratorContextKey.WorkbenchKind
            ? context.Value
            : $"project:{projectName}";
}
