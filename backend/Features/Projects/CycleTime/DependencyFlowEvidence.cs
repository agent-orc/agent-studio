namespace AgentStudio.Projects;

/// <summary>Historical waits-on evaluation for ready-lane portions of one episode.</summary>
public static class DependencyFlowEvidence
{
    public static IReadOnlyList<FlowSegment> Build(TaskInfo task, IReadOnlyList<TimelineEvent> ledger,
        Func<string, (TaskInfo Task, IReadOnlyList<TimelineEvent> Events)?> target)
    {
        if (task.References.DependsOn.Count == 0
            && !ledger.Any(e => e.Kind == TimelineEventKinds.DependencyChanged)) return [];
        var changes = ledger.Where(e => e.Kind == TimelineEventKinds.LaneChanged).OrderBy(e => e.Ts).ToList();
        var ready = new List<(DateTime Start, DateTime End)>();
        var lane = ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.PromptCreated)?.Details?.GetValueOrDefault("targetState")
            ?? changes.FirstOrDefault()?.Details?.GetValueOrDefault("from") ?? TaskStates.Backlog;
        var cursor = ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.PromptCreated)?.Ts ?? task.CreatedAt;
        foreach (var change in changes)
        {
            if (lane == TaskStates.Ready && change.Ts > cursor) ready.Add((cursor, change.Ts));
            lane = change.Details?.GetValueOrDefault("to") ?? lane;
            cursor = change.Ts;
        }
        var edits = ledger.Where(e => e.Kind == TimelineEventKinds.DependencyChanged).ToList();
        var keys = task.References.DependsOn.Select(e => e.Key)
            .Concat(edits.SelectMany(e => Split(e.Details?.GetValueOrDefault("added"))))
            .Concat(edits.SelectMany(e => Split(e.Details?.GetValueOrDefault("removed"))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var targets = keys.ToDictionary(key => key, key => target(key), StringComparer.OrdinalIgnoreCase);
        var result = new List<FlowSegment>();
        foreach (var stay in ready)
        {
            var points = new List<DateTime> { stay.Start, stay.End };
            points.AddRange(edits.Where(e => e.Ts > stay.Start && e.Ts < stay.End).Select(e => e.Ts));
            foreach (var found in targets.Values)
                if (found is not null)
                    points.AddRange(found.Value.Events.Where(e => e.Ts > stay.Start && e.Ts < stay.End
                        && (e.Kind is TimelineEventKinds.LaneChanged or TimelineEventKinds.DecisionDecided
                            or TimelineEventKinds.TaskReleased)).Select(e => e.Ts));
            points.Sort();
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var from = points[i]; var to = points[i + 1];
                if (from >= to) continue;
                var edges = EdgesAt(from);
                if (!edges.Any(edge => !Fulfilled(edge, from))) continue;
                result.Add(new FlowSegment("dependencyWait", null, null, null,
                    from, to, (to - from).TotalSeconds));
            }
        }
        return result;

        IReadOnlyList<TaskDependencyReference> EdgesAt(DateTime at)
        {
            var active = task.References.DependsOn.ToDictionary(e => e.Key, e => e,
                StringComparer.OrdinalIgnoreCase);
            foreach (var edit in edits.Where(e => e.Ts > at).OrderByDescending(e => e.Ts))
            {
                foreach (var key in Split(edit.Details?.GetValueOrDefault("added"))) active.Remove(key);
                foreach (var key in Split(edit.Details?.GetValueOrDefault("removed")))
                    active.TryAdd(key, new TaskDependencyReference(key));
            }
            return active.Values.ToList();
        }

        bool Fulfilled(TaskDependencyReference edge, DateTime at)
        {
            if (!targets.TryGetValue(edge.Key, out var found) || found is null) return false;
            var (other, events) = found.Value;
            var ordered = events.Where(e => e.Ts <= at).OrderBy(e => e.Ts).ToList();
            var firstChange = events.Where(e => e.Kind == TimelineEventKinds.LaneChanged)
                .OrderBy(e => e.Ts).FirstOrDefault();
            var initial = events.FirstOrDefault(e => e.Kind == TimelineEventKinds.PromptCreated)?
                .Details?.GetValueOrDefault("targetState")
                ?? firstChange?.Details?.GetValueOrDefault("from") ?? other.State;
            var state = ordered.Where(e => e.Kind == TimelineEventKinds.LaneChanged)
                .Select(e => e.Details?.GetValueOrDefault("to")).LastOrDefault() ?? initial;
            if (state is not (TaskStates.Completed or TaskStates.Archive)) return false;
            if (TaskKinds.IsDecision(other.Kind)
                && !ordered.Any(e => e.Kind == TimelineEventKinds.DecisionDecided)) return false;
            return !edge.ReleaseGate || ordered.Any(e => e.Kind == TimelineEventKinds.TaskReleased);
        }
    }

    private static IEnumerable<string> Split(string? raw) => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
