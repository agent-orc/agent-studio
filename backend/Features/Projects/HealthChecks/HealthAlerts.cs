using System.Globalization;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

/// <summary>
/// Bounded side effects of the health checks (IP-14): one typed Activity entry per incident
/// change, and one card per incident that later incidents of the same check update while it
/// is still open. Every write is idempotent so a retried tick cannot duplicate a card.
/// </summary>
public sealed class HealthAlerts(TaskScannerService scanner, TaskMutationService mutations, TimelineLog timeline,
    OrchestratorLog activity, TimeProvider clock, ILogger<HealthAlerts> logger)
{
    public const string Tag = "environment-health";
    private static readonly string[] TerminalLanes = [TaskStates.Completed, TaskStates.Archive];

    /// <returns>The incident with its card and event recorded, as far as they succeeded.</returns>
    public HealthIncident ReportOpened(string project, EnvironmentDeclaration environment, HealthCheckDefinition check,
        HealthIncident incident, HealthCheckState state, IReadOnlyList<HealthSample> evidence)
    {
        var watchPath = WatchPath(project);
        if (watchPath is null) return incident;
        if (incident.CardId is null)
        {
            var reusable = OpenCard(state.IncidentCardId, watchPath);
            if (reusable is not null)
            {
                Comment(reusable, $"Incident reopened: {check.Id} failed {incident.FailedProbes} times in a row since {Stamp(incident.StartedAt)}.",
                    incident, "reopened");
                incident = incident with { CardId = reusable.Id };
            }
            else
            {
                var slug = $"health-{environment.Id}-{check.Id}-{incident.StartedAt:yyyyMMddHHmm}";
                // A tick that created the card but crashed before saving the id finds it by slug.
                var cardId = scanner.FindJob(slug, watchPath)?.Id ?? mutations.CreateJob(new CreateTaskRequest
                {
                    Id = slug,
                    Title = $"Health incident: {environment.Name} {check.Id} is failing",
                    PromptMarkdown = IncidentPrompt(project, environment, check, incident, evidence),
                    WatchPath = watchPath,
                    TargetState = TaskStates.Backlog,
                    TaskType = TaskTypes.Bug,
                    Tags = [Tag],
                    CreationSource = "environment-health",
                    CreatedBy = TimelineActors.System,
                });
                if (cardId is null)
                    logger.LogWarning("environment-health card creation failed project={Project} incident={Incident}",
                        project, incident.Id);
                incident = incident with { CardId = cardId };
            }
            if (incident.CardId is not null) state.IncidentCardId = incident.CardId;
        }
        if (!incident.OpenedEventRaised)
        {
            var raised = activity.Append(watchPath, new OrchestratorLogEntry
            {
                Ts = clock.GetUtcNow().UtcDateTime,
                Kind = OrchestratorLogKinds.Alert,
                Topic = OrchestratorLogTopics.EnvironmentIncidentOpened,
                Summary = $"{project} {environment.Name}: {check.Id} failed {incident.FailedProbes} times in a row ({incident.FirstError})",
                Reasoning = $"Incident {incident.Id} started {Stamp(incident.StartedAt)}. Target {check.Target}.",
                JobId = incident.CardId,
            });
            incident = incident with { OpenedEventRaised = raised };
        }
        return incident;
    }

    public HealthIncident ReportRecovered(string project, EnvironmentDeclaration environment, HealthCheckDefinition check,
        HealthIncident incident)
    {
        if (incident.RecoveryReported || incident.EndedAt is not { } ended) return incident;
        var watchPath = WatchPath(project);
        if (watchPath is null) return incident;
        var duration = ended - incident.StartedAt;
        var summary = $"{project} {environment.Name}: {check.Id} recovered after {Duration(duration)} ({incident.FailedProbes} failed probes)";
        var card = incident.CardId is null ? null : scanner.FindJob(incident.CardId, watchPath);
        if (card is not null)
            Comment(card, $"Recovered {Stamp(ended)} after {Duration(duration)}; {incident.FailedProbes} failed probes since {Stamp(incident.StartedAt)}.",
                incident, "recovered");
        var raised = activity.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Observation,
            Topic = OrchestratorLogTopics.EnvironmentIncidentRecovered,
            Summary = summary,
            Reasoning = $"Incident {incident.Id} closed.",
            JobId = incident.CardId,
        });
        return incident with { RecoveryReported = raised };
    }

    /// <returns>True when the warning reached the Activity stream.</returns>
    public bool ReportCertificateExpiry(string project, EnvironmentDeclaration environment, HealthCheckDefinition check,
        DateTimeOffset notAfter, int thresholdDays, HealthCheckState state)
    {
        var watchPath = WatchPath(project);
        if (watchPath is null) return false;
        var daysLeft = Math.Max(0, (int)Math.Floor((notAfter - clock.GetUtcNow()).TotalDays));
        var text = $"{project} {environment.Name}: TLS certificate of {check.Target} expires {Stamp(notAfter)} ({daysLeft} days left)";
        var card = OpenCard(state.CertificateCardId, watchPath);
        if (card is not null)
            timeline.Append(card.FolderPath, new TimelineEvent
            {
                Ts = clock.GetUtcNow().UtcDateTime,
                Kind = TimelineEventKinds.EnvironmentHealthUpdated,
                Actor = TimelineActors.System,
                Summary = $"Certificate warning at {thresholdDays} days: expires {Stamp(notAfter)}.",
                Details = new() { ["environmentId"] = environment.Id, ["checkId"] = check.Id,
                    ["certificateNotAfter"] = notAfter.ToString("O", CultureInfo.InvariantCulture), ["change"] = "certificate-warning" },
            });
        else
        {
            var slug = $"certificate-{environment.Id}-{check.Id}-{notAfter:yyyyMMdd}";
            state.CertificateCardId = scanner.FindJob(slug, watchPath)?.Id ?? mutations.CreateJob(new CreateTaskRequest
            {
                Id = slug,
                Title = $"Renew TLS certificate: {environment.Name} {check.Target}",
                PromptMarkdown = $"# Renew TLS certificate\n\n{text}.\n\n- Project: `{project}`\n- Environment: `{environment.Id}`\n"
                    + $"- Check: `{check.Id}` ({check.Target})\n- Not after: {Stamp(notAfter)}\n- Warning threshold: {thresholdDays} days\n",
                WatchPath = watchPath,
                TargetState = TaskStates.Backlog,
                TaskType = TaskTypes.Chore,
                Tags = [Tag],
                CreationSource = "environment-health",
                CreatedBy = TimelineActors.System,
            });
        }
        return activity.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Alert,
            Topic = OrchestratorLogTopics.EnvironmentCertificateExpiring,
            Summary = text,
            Reasoning = $"Warning threshold {thresholdDays} days.",
            JobId = state.CertificateCardId,
        });
    }

    private TaskInfo? OpenCard(string? cardId, string watchPath)
        => cardId is null ? null
            : scanner.FindJob(cardId, watchPath) is { } card && !TerminalLanes.Contains(card.State) ? card : null;

    private void Comment(TaskInfo card, string summary, HealthIncident incident, string change)
        => timeline.Append(card.FolderPath, new TimelineEvent
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = TimelineEventKinds.EnvironmentHealthUpdated,
            Actor = TimelineActors.System,
            Summary = summary,
            Details = new() { ["incidentId"] = incident.Id, ["environmentId"] = incident.EnvironmentId,
                ["checkId"] = incident.CheckId, ["change"] = change },
        });

    private string? WatchPath(string project)
        => scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;

    private static string IncidentPrompt(string project, EnvironmentDeclaration environment, HealthCheckDefinition check,
        HealthIncident incident, IReadOnlyList<HealthSample> evidence)
    {
        var text = new StringBuilder();
        text.Append($"# Health incident: {environment.Name} {check.Id}\n\n");
        text.Append($"The control plane health check `{check.Id}` of environment `{environment.Id}` in project `{project}` ");
        text.Append($"failed {incident.FailedProbes} times in a row (threshold {check.FailureThreshold}).\n\n");
        text.Append($"- Incident: `{incident.Id}`\n- Started: {Stamp(incident.StartedAt)}\n");
        text.Append($"- Check: {check.Kind} `{check.Target}`");
        if (check.Kind == "http") text.Append($", expects status {check.ExpectedStatus}");
        if (check.BodyContains is not null) text.Append(" and a body marker");
        text.Append($", timeout {check.TimeoutSeconds} s, interval {check.IntervalSeconds} s\n");
        text.Append($"- Environment address: {environment.Address} on host `{environment.TargetHostId}`\n\n");
        text.Append("## Recent probes\n\n| Time (UTC) | Result | Status | Latency | Error |\n|---|---|---|---|---|\n");
        foreach (var sample in evidence.TakeLast(10))
            text.Append($"| {Stamp(sample.At)} | {(sample.Ok ? "ok" : "failed")} | {sample.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"} | "
                + $"{(sample.LatencyMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " ms" : "-")} | {sample.Error ?? "-"} |\n");
        text.Append("\nRecovery closes the incident and comments on this card. A later incident of the same check updates this card while it is open.\n");
        return text.ToString();
    }

    private static string Stamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours} h {value.Minutes} min"
            : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))} min";
}
