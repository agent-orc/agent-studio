using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Observes changes in an existing hold decision without changing admission or retry rules.</summary>
public static class GuardianIntegrationWaitJournal
{
    public static void Record(TimelineLog timeline, string folder, bool blocked, bool pathsKnown,
        DateTimeOffset at, string delivery)
    {
        var previous = timeline.ReadAll(folder).LastOrDefault(e => e.Kind == TimelineEventKinds.IntegrationQuarantineWait);
        var state = blocked ? "blocked" : "released";
        if (previous?.Details?.GetValueOrDefault("state") == state || previous is null && !blocked) return;
        timeline.Append(folder, new TimelineEvent
        {
            Ts = at.UtcDateTime, Kind = TimelineEventKinds.IntegrationQuarantineWait, Actor = TimelineActors.System,
            Summary = blocked ? "Integration waits for a quarantine hold." : "Integration quarantine wait ended.",
            Details = new() { ["state"] = state, ["scope"] = pathsKnown ? "held-area" : "unknown", ["delivery"] = delivery },
        });
    }
}
