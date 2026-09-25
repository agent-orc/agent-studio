namespace AgentStudio.Tasks;

/// <summary>Pure admission rule for a decision card and work waiting on one.</summary>
public static class DecisionLaneGuard
{
    public static string? Refusal(TaskInfo task, string targetState, TaskReferenceIndex index)
    {
        if (TaskKinds.IsDecision(task.Kind))
        {
            if (targetState == TaskStates.Preparation) return null;
            if (targetState is TaskStates.Completed or TaskStates.Archive)
                return task.Decision?.Status == DecisionStatuses.Decided
                    ? null : "A pending decision cannot be completed or archived.";
            return "Decision cards cannot enter a runner lane.";
        }

        if (targetState is not (TaskStates.Ready or TaskStates.Progress)) return null;
        var pending = task.References?.DependsOn
            .Select(edge => index.Resolve(edge.Key))
            .Where(target => target is not null && TaskKinds.IsDecision(target.Kind)
                && DecisionStatuses.IsOpen(target.Decision?.Status))
            .Select(target => target!.Key ?? target.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        return pending.Length == 0 ? null
            : $"Task is blocked by pending decision {string.Join(", ", pending)}.";
    }
}
