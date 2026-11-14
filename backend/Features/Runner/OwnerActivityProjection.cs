using AgentStudio.Docs;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Read-only cross-project projection of durable owner activity.</summary>
public static class OwnerActivityProjection
{
    public sealed record Entry(
        string Project, string? WatchPath, DateTime Ts, string Kind,
        string Topic, string Summary, string? Reasoning, string? JobId,
        string? DossierId);

    public static List<Entry> Read(
        IEnumerable<TaskInfo> tasks, TimelineLog timeline,
        WorkbenchCatalogueService workbenches, IReadOnlySet<string> allowedProjects)
    {
        var entries = new List<Entry>();
        foreach (var task in tasks.Where(task => !task.Fixture && allowedProjects.Contains(task.ProjectName)))
            entries.AddRange(FromTimeline(task, timeline.ReadAll(task.FolderPath)));

        foreach (var project in allowedProjects)
            foreach (var dossier in workbenches.List(project, includeHistory: true)?.Items ?? [])
                if (FromDossier(project, dossier) is { } entry) entries.Add(entry);

        return entries.OrderByDescending(entry => entry.Ts).Take(500).ToList();
    }

    public static IEnumerable<Entry> FromTimeline(TaskInfo task, IEnumerable<TimelineEvent> events)
    {
        foreach (var evt in events)
        {
                if (!TimelineActors.IsHuman(evt.Actor) && !TimelineActors.IsHuman(evt.DelegatedBy)
                    && !(evt.Kind == TimelineEventKinds.PromptCreated
                         && evt.Details?.GetValueOrDefault("creationSource") == "human"))
                    continue;
                var topic = evt.Kind switch
                {
                    TimelineEventKinds.LaneChanged => "lane-move",
                    TimelineEventKinds.OwnerMoveRefused => "refusal",
                    TimelineEventKinds.PromptCreated => "card-created",
                    TimelineEventKinds.DecisionDecided => "decision-card",
                    _ => null,
                };
                if (topic is null) continue;
                var summary = $"{task.TaskKey}: {evt.Summary}";
                if (evt.Kind == TimelineEventKinds.PromptCreated)
                    summary = $"Created {task.TaskKey}: {task.Title}";
                else if (evt.Kind == TimelineEventKinds.LaneChanged
                         && evt.Details?.GetValueOrDefault("to") == TaskStates.Completed)
                    summary = $"Accepted {task.TaskKey}: {evt.Summary}";
                else if (evt.Kind == TimelineEventKinds.LaneChanged
                         && evt.Details?.GetValueOrDefault("to") == TaskStates.Ready
                         && evt.Details?.GetValueOrDefault("from") is TaskStates.HumanReview or TaskStates.Escalated)
                    summary = $"Reissued {task.TaskKey}: {evt.Summary}";
                yield return new Entry(task.ProjectName, task.WatchPath, evt.Ts,
                    "owner_action", topic, summary,
                    evt.Details?.GetValueOrDefault("reason") ?? evt.Details?.GetValueOrDefault("rationale"),
                    task.Id, null);
        }
    }

    public static Entry? FromDossier(string project, WorkbenchListItem dossier)
    {
        var decision = dossier.Decision;
        var at = decision?.DecidedAt ?? decision?.ConfirmedAt;
        if (decision is null || string.IsNullOrWhiteSpace(at)
            || !DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            return null;
        var created = decision.SpawnedTaskKeys.Length == 0 ? ""
            : $"; created {string.Join(", ", decision.SpawnedTaskKeys)}";
        return new Entry(project, null, ts.ToUniversalTime(), "owner_action",
            "dossier-decision", $"Decided {dossier.Key ?? dossier.Id}: {decision.Outcome}{created}",
            decision.Reason, null, dossier.Id);
    }
}
