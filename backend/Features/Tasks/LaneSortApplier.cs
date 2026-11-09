
namespace AgentStudio.Tasks;

/// <summary>
/// F35: applies a per-project lane sort strategy to a flat list of jobs that
/// may span multiple projects. The kanban grouped endpoint mixes projects
/// inside one lane, so we group by project, sort each project's jobs using
/// the strategy resolved from that project's <see cref="ProjectSettings"/>,
/// and concatenate the groups in alphabetical project order for a stable
/// global result. Pure function; called from <c>/api/tasks/grouped</c>.
/// </summary>
public static class LaneSortApplier
{
    public static IEnumerable<TaskInfo> Sort(
        IEnumerable<TaskInfo> jobs,
        string lane,
        Func<string, ProjectSettings> settingsResolver)
    {
        var byProject = jobs.GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var result = new List<TaskInfo>();
        foreach (var group in byProject)
        {
            var settings = settingsResolver(group.Key);
            var strategy = LaneSortStrategies.Resolve(settings, lane);
            var comparer = LaneSortStrategies.GetComparer(strategy);
            var sorted = group.ToList();
            sorted.Sort(comparer);
            result.AddRange(sorted);
        }
        return result;
    }

    /// <summary>
    /// AGT-3587: cross-project pickup order for the remote claim. Each
    /// project's Ready cards keep the order the board shows and the local
    /// runner uses (<see cref="Sort"/>), so a project's top card is always its
    /// next card. Across projects the cards interleave by their position in
    /// their own queue, then by <c>order</c> and creation time, so every
    /// project's head is offered before any project's second card.
    /// </summary>
    public static IReadOnlyList<TaskInfo> PickupOrder(
        IEnumerable<TaskInfo> jobs,
        Func<string, ProjectSettings> settingsResolver)
    {
        return jobs.GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => Sort(group, TaskStates.Ready, settingsResolver)
                .Select((job, rank) => (Job: job, Rank: rank)))
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Job.Order)
            .ThenBy(item => item.Job.CreatedAt)
            .Select(item => item.Job)
            .ToList();
    }
}
