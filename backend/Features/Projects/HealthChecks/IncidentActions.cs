using AgentStudio.Deploy;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

public sealed record IncidentActionLink(string IncidentId, string CardId);

/// <summary>Connects an operation to the incident that was open when it was requested.</summary>
public sealed class IncidentActions(HealthCheckStore health, TaskScannerService scanner, TimelineLog timeline,
    TimeProvider clock)
{
    public IncidentActionLink? Open(string project, string environmentId)
    {
        var incident = health.Load(project).Environments.GetValueOrDefault(environmentId)?.OpenIncident;
        return incident?.CardId is { } cardId ? new(incident.Id, cardId) : null;
    }

    public IncidentActionLink? At(string project, string environmentId, DateTime atUtc)
    {
        var state = health.Load(project).Environments.GetValueOrDefault(environmentId);
        var at = new DateTimeOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc));
        var incident = state?.OpenIncident is { } open && open.StartedAt <= at ? open
            : state?.IncidentHistory.LastOrDefault(item => item.StartedAt <= at && item.EndedAt >= at);
        return incident?.CardId is { } cardId ? new(incident.Id, cardId) : null;
    }

    public void Report(string project, string? cardId, string? incidentId, string operationId,
        string action, string actor, string? release, string result)
    {
        if (cardId is null || incidentId is null) return;
        var watch = scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));
        var card = watch is null ? null : scanner.FindJob(cardId, watch.Path);
        if (card is null) return;
        timeline.Append(card.FolderPath, new TimelineEvent
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = TimelineEventKinds.EnvironmentHealthUpdated,
            Actor = actor,
            Summary = $"{action} by {actor}: release {release ?? "unknown"}, {result}.",
            Details = new() { ["incidentId"] = incidentId, ["operationId"] = operationId,
                ["action"] = action, ["release"] = release ?? "unknown", ["result"] = result,
                ["change"] = "incident-action" },
        });
    }
}
