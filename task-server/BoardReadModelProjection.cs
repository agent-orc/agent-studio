using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Pure projection of one project's task rows into the board read model.
/// Live lanes follow <see cref="TaskStates.All"/>; a state outside that list
/// gets its own lane after them instead of disappearing from the board, and
/// the terminal archive lane is only counted.
/// </summary>
public static class BoardReadModelProjection
{
    /// <summary>Bumped when the projection changes shape, so an old validator never matches.</summary>
    public const int Version = 1;

    public static string FormatETag(long generation) => $"\"board-v{Version}-g{generation}\"";

    public static BoardReadModelResponse Project(
        string projectId,
        string projectName,
        long generation,
        long? workspaceSourceGeneration,
        IEnumerable<BoardCardDto> cards)
    {
        var byState = new Dictionary<string, List<BoardCardDto>>(StringComparer.Ordinal);
        var archived = 0;
        foreach (var card in cards)
        {
            if (string.Equals(card.State, TaskStates.Archive, StringComparison.Ordinal))
            {
                archived++;
                continue;
            }
            if (!byState.TryGetValue(card.State, out var lane))
                byState[card.State] = lane = [];
            lane.Add(card);
        }

        var lanes = new List<BoardLaneDto>();
        foreach (var state in TaskStates.All)
        {
            if (state == TaskStates.Archive) continue;
            lanes.Add(Lane(state, byState.GetValueOrDefault(state)));
        }
        foreach (var state in byState.Keys
                     .Where(state => !TaskStates.All.Contains(state, StringComparer.Ordinal))
                     .Order(StringComparer.Ordinal))
            lanes.Add(Lane(state, byState[state]));

        return new BoardReadModelResponse(
            projectId,
            projectName,
            generation,
            FormatETag(generation),
            lanes,
            lanes.Sum(lane => lane.Count),
            archived,
            workspaceSourceGeneration);
    }

    private static BoardLaneDto Lane(string state, List<BoardCardDto>? cards)
    {
        var ordered = (cards ?? [])
            .OrderBy(card => card.Rank)
            .ThenBy(card => card.TaskKey, StringComparer.Ordinal)
            .ToList();
        return new BoardLaneDto(state, ordered.Count, ordered);
    }
}
