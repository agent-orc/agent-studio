namespace AgentStudio.Publishing;

/// <summary>
/// PUB-1 board fold: builds the per-task "publishable: npm, website" chip signal
/// for accepted (6-completed) tasks. The design invariant is <b>no per-card git
/// spawn</b> - exactly the rule <see cref="BoardMergeStatusService"/> follows for
/// the merge signal. Each publish target already carries the set of pending
/// commits introduced by scoped mainline changes on the integration branch,
/// including the second-parent side of an integration merge. So per-task
/// publishability is an in-memory set-membership test against a
/// computation that is derived once per project (O(projects) git work), never per
/// card.
/// </summary>
public sealed class TaskPublishableService
{
    private readonly PublishTargetService _publish;
    private readonly ILogger<TaskPublishableService> _logger;

    public TaskPublishableService(PublishTargetService publish, ILogger<TaskPublishableService> logger)
    {
        _publish = publish;
        _logger = logger;
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> publish signal for the accepted tasks in
    /// the given board. Only 6-completed tasks with a real anchor are considered;
    /// a task that touched no target scope gets no entry and the card renders no
    /// chip. Never throws: a derivation failure yields no signal for that project.
    /// </summary>
    public Dictionary<string, TaskPublishSignal> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        var accepted = jobs
            .Where(j => j.State == TaskStates.Completed && BoardMergeStatusService.AnchorFor(j) != null)
            .GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in accepted)
        {
            ProjectPublishComputation computation;
            try { computation = _publish.GetComputation(group.Key); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Publish derivation failed for project {Project}; skipping chip fold.", group.Key);
                continue;
            }
            if (!computation.IsRepo || computation.Targets.Count == 0) continue;

            foreach (var job in group)
            {
                var shas = CandidateShas(job);
                if (shas.Count == 0) continue;

                var ids = new List<string>();
                var labels = new List<string>();
                foreach (var target in computation.Targets)
                {
                    if (target.PendingShas.Count == 0) continue;
                    if (shas.Any(s => target.PendingShas.Contains(s)))
                    {
                        ids.Add(target.Target.Id);
                        labels.Add(target.Target.Label);
                    }
                }
                if (ids.Count > 0)
                    result[job.TaskKey] = new TaskPublishSignal { TargetIds = ids, Labels = labels };
            }
        }

        return result;
    }

    /// <summary>
    /// The SHAs that could match a target's introduced-commit set: the task's
    /// anchor plus its attributed commits, covering both merges and linear
    /// history. Full SHAs; the pending set is also full SHAs.
    /// </summary>
    private static List<string> CandidateShas(TaskInfo job)
    {
        var set = new List<string>();
        var anchor = BoardMergeStatusService.AnchorFor(job);
        if (!string.IsNullOrWhiteSpace(anchor)) set.Add(anchor!);
        foreach (var c in job.Commits)
            if (!string.IsNullOrWhiteSpace(c.Sha)) set.Add(c.Sha);
        if (job.Commit is { Sha.Length: > 0 }) set.Add(job.Commit.Sha);
        return set;
    }
}
