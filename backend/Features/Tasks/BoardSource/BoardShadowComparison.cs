using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>One field on which the two board sources disagree, named by card id and field.</summary>
public sealed record BoardShadowDifference(
    string Project,
    string TaskKey,
    string Field,
    string? WorkspaceValue,
    string? TaskServerValue)
{
    public string Fingerprint => $"{Project}|{TaskKey}|{Field}";
}

/// <summary>
/// Pure comparison of one project's workspace card core with the Task Server
/// board read model. Live cards are compared field by field; the archive is
/// compared by count, because the board read model only counts it.
/// </summary>
public static class BoardShadowComparison
{
    public const string ProjectField = "project";
    public const string PresenceField = "presence";
    public const string TitleField = "title";
    public const string LaneField = "lane";
    public const string OrderField = "order";
    public const string CountField = "count";
    public const string ArchivedCountField = "archived-count";
    public const string AnyCard = "*";

    public static IReadOnlyList<BoardShadowDifference> Compare(
        string project,
        IReadOnlyDictionary<string, BoardCardCore> workspace,
        BoardReadModelResponse? taskServer)
    {
        if (taskServer is null)
            return [new BoardShadowDifference(project, AnyCard, ProjectField, "present", "missing")];

        var differences = new List<BoardShadowDifference>();
        var workspaceLive = workspace.Values
            .Where(card => card.State != TaskStates.Archive)
            .ToDictionary(card => card.TaskKey, StringComparer.Ordinal);
        var serverLive = taskServer.Lanes
            .SelectMany(lane => lane.Cards)
            .GroupBy(card => card.TaskKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var key in workspaceLive.Keys.Union(serverLive.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var mine = workspaceLive.GetValueOrDefault(key);
            var theirs = serverLive.GetValueOrDefault(key);
            if (mine is null || theirs is null)
            {
                differences.Add(new BoardShadowDifference(project, key, PresenceField,
                    mine is null ? "absent" : mine.State, theirs is null ? "absent" : theirs.State));
                continue;
            }
            if (!string.Equals(mine.State, theirs.State, StringComparison.Ordinal))
                differences.Add(new BoardShadowDifference(project, key, LaneField, mine.State, theirs.State));
            if (!string.Equals(mine.Title, theirs.Title, StringComparison.Ordinal))
                differences.Add(new BoardShadowDifference(project, key, TitleField, mine.Title, theirs.Title));
            if (mine.Rank != theirs.Rank)
                differences.Add(new BoardShadowDifference(project, key, OrderField,
                    mine.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    theirs.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var workspaceCounts = workspaceLive.Values
            .GroupBy(card => card.State, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var serverCounts = taskServer.Lanes.ToDictionary(lane => lane.State, lane => lane.Count, StringComparer.Ordinal);
        foreach (var lane in workspaceCounts.Keys.Union(serverCounts.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var mine = workspaceCounts.GetValueOrDefault(lane);
            var theirs = serverCounts.GetValueOrDefault(lane);
            if (mine != theirs)
                differences.Add(new BoardShadowDifference(project, $"lane:{lane}", CountField,
                    mine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    theirs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var archived = workspace.Count - workspaceLive.Count;
        if (archived != taskServer.ArchivedCount)
            differences.Add(new BoardShadowDifference(project, AnyCard, ArchivedCountField,
                archived.ToString(System.Globalization.CultureInfo.InvariantCulture),
                taskServer.ArchivedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return differences;
    }
}
