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

    /// <summary>
    /// Owner rows of the timeline and of the Dossier decisions.
    /// <paramref name="dossiers"/> answers from the published Wiki snapshot: a
    /// live catalogue resolved the Wiki source and walked docs/ of every
    /// project on each feed poll, about 50 Git processes per request (AGT-3793).
    /// </summary>
    public static List<Entry> Read(
        IEnumerable<TaskInfo> tasks, TimelineLog timeline,
        Func<string, IEnumerable<WorkbenchListItem>> dossiers, IReadOnlySet<string> allowedProjects)
    {
        var entries = new List<Entry>();
        foreach (var task in tasks.Where(task => !task.Fixture && allowedProjects.Contains(task.ProjectName)))
            entries.AddRange(FromTimeline(task, timeline.ReadAll(task.FolderPath)));

        foreach (var project in allowedProjects)
            foreach (var dossier in dossiers(project))
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
                var displayKey = task.Key ?? task.Id;
                var summary = $"{displayKey}: {evt.Summary}";
                if (evt.Kind == TimelineEventKinds.PromptCreated)
                    summary = $"Created {displayKey}: {task.Title}";
                else if (evt.Kind == TimelineEventKinds.LaneChanged
                         && evt.Details?.GetValueOrDefault("to") == TaskStates.Completed)
                    summary = $"Accepted {displayKey}: {evt.Summary}";
                else if (evt.Kind == TimelineEventKinds.LaneChanged
                         && evt.Details?.GetValueOrDefault("to") == TaskStates.Ready
                         && evt.Details?.GetValueOrDefault("from") is TaskStates.HumanReview or TaskStates.Escalated)
                    summary = $"Reissued {displayKey}: {evt.Summary}";
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
