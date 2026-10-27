using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// Card core of one workspace task, keyed the way the Task Server keys it:
/// the upper-cased task key, falling back to the folder id as the legacy
/// importer does.
/// </summary>
public sealed record BoardCardCore(string Project, string TaskKey, string Title, string State, long Rank)
{
    public static BoardCardCore From(TaskInfo task)
    {
        var key = (string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key).Trim().ToUpperInvariant();
        var title = string.IsNullOrWhiteSpace(task.Title) ? key : task.Title.Trim();
        return new BoardCardCore(task.ProjectName, key, title, task.State, task.Order);
    }

    public BoardWorkspaceCard ToWorkspaceCard() => new(TaskKey, Title, State, Rank);

    /// <summary>Project name to task key to card core, over every lane including the archive.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>> Group(IEnumerable<TaskInfo> tasks)
    {
        var projects = new Dictionary<string, Dictionary<string, BoardCardCore>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ProjectName)) continue;
            var core = From(task);
            if (core.TaskKey.Length == 0) continue;
            if (!projects.TryGetValue(core.Project, out var cards))
                projects[core.Project] = cards = new Dictionary<string, BoardCardCore>(StringComparer.Ordinal);
            cards[core.TaskKey] = core;
        }
        return projects.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, BoardCardCore>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record BoardMirrorProjectDelta(
    string Project,
    IReadOnlyList<BoardCardCore> Upserts,
    IReadOnlyList<string> Removals);

/// <summary>
/// Pure delta between the card core last accepted by the Task Server and the
/// current workspace publication. A project never synced is sent whole; a
/// project that vanished from the workspace sends removals for its cards.
/// </summary>
public static class BoardMirrorPlan
{
    public static IReadOnlyList<BoardMirrorProjectDelta> Create(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>> synced,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>> current)
    {
        var deltas = new List<BoardMirrorProjectDelta>();
        foreach (var project in current.Keys.Union(synced.Keys, StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var now = current.GetValueOrDefault(project) ?? new Dictionary<string, BoardCardCore>();
            var before = synced.GetValueOrDefault(project);
            var upserts = now.Values
                .Where(card => before is null || !before.TryGetValue(card.TaskKey, out var known) || known != card)
                .OrderBy(card => card.TaskKey, StringComparer.Ordinal)
                .ToList();
            var removals = before is null
                ? []
                : before.Keys.Where(key => !now.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
            if (before is null || upserts.Count > 0 || removals.Count > 0)
                deltas.Add(new BoardMirrorProjectDelta(project, upserts, removals));
        }
        return deltas;
    }
}
