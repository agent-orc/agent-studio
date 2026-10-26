using System.Globalization;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

/// <summary>A check with an open incident, with the probes that show it.</summary>
public sealed record FailingCheck(HealthCheckDefinition Check, HealthIncident Incident, IReadOnlyList<HealthSample> Evidence);

/// <summary>
/// Bounded side effects of the health checks (IP-14): one typed Activity entry per environment
/// incident change, and one card per environment incident that the checks of that incident
/// comment on and that a later incident of the same environment updates while it is still open.
/// Card creation is idempotent so a retried tick cannot duplicate a card. Targets appear
/// without user info and query strings.
/// </summary>
public sealed class HealthAlerts(TaskScannerService scanner, TaskMutationService mutations, TimelineLog timeline,
    OrchestratorLog activity, TimeProvider clock, ILogger<HealthAlerts> logger)
{
    public const string Tag = "environment-health";
    private static readonly string[] TerminalLanes = [TaskStates.Completed, TaskStates.Archive];

    /// <returns>The incident with its card and event recorded, as far as they succeeded.</returns>
    public EnvironmentIncident ReportOpened(string project, EnvironmentDeclaration environment, EnvironmentIncident incident,
        EnvironmentHealthState state, IReadOnlyList<FailingCheck> failing)
    {
        var watchPath = WatchPath(project);
        if (watchPath is null) return incident;
        if (incident.CardId is null)
        {
            var reusable = OpenCard(state.IncidentCardId, watchPath);
            if (reusable is not null)
            {
                Comment(reusable, $"Incident reopened since {Stamp(incident.StartedAt)}: {Describe(failing)}.", incident, null, "reopened");
                incident = incident with { CardId = reusable.Id };
            }
            else
            {
                var slug = $"health-{environment.Id}-{incident.StartedAt:yyyyMMddHHmmss}";
                // A tick that created the card but crashed before saving the id finds it by slug.
                var cardId = OpenCard(slug, watchPath)?.Id ?? mutations.CreateJob(new CreateTaskRequest
                {
                    Id = slug,
                    Title = $"Health incident: {environment.Name} is failing",
                    PromptMarkdown = IncidentPrompt(project, environment, incident, failing),
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
                Summary = $"{project} {environment.Name}: {Describe(failing)}",
                Reasoning = $"Incident {incident.Id} started {Stamp(incident.StartedAt)}. Targets: "
                    + string.Join(", ", failing.Select(item => HealthTargets.Display(item.Check.Target))) + ".",
                JobId = incident.CardId,
            });
            incident = incident with { OpenedEventRaised = raised };
        }
        return incident;
    }

    /// <returns>True when the card of the environment incident shows that one more check fails.</returns>
    public bool ReportCheckFailing(string project, EnvironmentIncident incident, FailingCheck failing)
        => Card(project, incident.CardId) is { } card && Comment(card,
            $"Also failing: {Describe([failing])}, target {HealthTargets.Display(failing.Check.Target)}.",
            incident, failing.Incident.CheckId, "check-failing");

    /// <returns>True when the card shows that the check no longer fails while others still do.</returns>
    public bool ReportCheckRecovered(string project, EnvironmentIncident incident, HealthIncident check)
        => Card(project, incident.CardId) is { } card && Comment(card,
            $"{Ended(check)} Other checks are still failing.", incident, check.CheckId, "check-recovered");

    public EnvironmentIncident ReportRecovered(string project, EnvironmentIncident incident,
        IReadOnlyList<HealthIncident> checks)
    {
        if ((incident.RecoveryReported && incident.RecoveryCommentReported != false)
            || incident.EndedAt is not { } ended) return incident;
        var watchPath = WatchPath(project);
        if (watchPath is null) return incident;
        var duration = Duration(ended - incident.StartedAt);
        var removed = checks.Count > 0 && checks.All(check => check.EndReason == "check-removed");
        var how = removed ? "closed because its failing checks were removed from the declaration" : "recovered";
        var probes = checks.Sum(check => check.FailedProbes);
        if (incident.RecoveryCommentReported != true)
        {
            var card = Card(project, incident.CardId);
            incident = incident with { RecoveryCommentReported = card is null || Comment(card,
                $"Environment {how} {Stamp(ended)} after {duration}; {probes} failed probes since "
                + $"{Stamp(incident.StartedAt)}. {string.Join(" ", checks.Select(Ended))}", incident, null, "recovered") };
        }
        var raised = incident.RecoveryReported || activity.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = OrchestratorLogKinds.Observation,
            Topic = OrchestratorLogTopics.EnvironmentIncidentRecovered,
            Summary = $"{project} {incident.EnvironmentName}: {how} after {duration} ({probes} failed probes)",
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
        var target = HealthTargets.Display(check.Target);
        var text = $"{project} {environment.Name}: TLS certificate of {target} expires {Stamp(notAfter)} ({daysLeft} days left)";
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
                Title = $"Renew TLS certificate: {environment.Name} {target}",
                PromptMarkdown = $"# Renew TLS certificate\n\n{text}.\n\n- Project: `{project}`\n- Environment: `{environment.Id}`\n"
                    + $"- Check: `{check.Id}` ({target})\n- Not after: {Stamp(notAfter)}\n- Warning threshold: {thresholdDays} days\n",
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

    private TaskInfo? Card(string project, string? cardId)
        => cardId is not null && WatchPath(project) is { } watchPath ? scanner.FindJob(cardId, watchPath) : null;

    private bool Comment(TaskInfo card, string summary, EnvironmentIncident incident, string? checkId, string change)
    {
        var details = new Dictionary<string, string> { ["incidentId"] = incident.Id,
            ["environmentId"] = incident.EnvironmentId, ["change"] = change };
        if (checkId is not null) details["checkId"] = checkId;
        return timeline.Append(card.FolderPath, new TimelineEvent
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = TimelineEventKinds.EnvironmentHealthUpdated,
            Actor = TimelineActors.System,
            Summary = summary,
            Details = details,
        });
    }

    private string? WatchPath(string project)
        => scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;

    private static string Describe(IReadOnlyList<FailingCheck> failing)
        => string.Join("; ", failing.Select(item =>
            $"{item.Incident.CheckId} failed {item.Incident.FailedProbes} times in a row ({item.Incident.FirstError})"));

    private static string Ended(HealthIncident check)
        => check.EndReason == "check-removed"
            ? $"Check {check.CheckId} was removed from the declaration; its incident closed {Stamp(check.EndedAt ?? check.StartedAt)}."
            : $"Check {check.CheckId} recovered {Stamp(check.EndedAt ?? check.StartedAt)} after {check.FailedProbes} failed probes.";

    private static string IncidentPrompt(string project, EnvironmentDeclaration environment, EnvironmentIncident incident,
        IReadOnlyList<FailingCheck> failing)
    {
        var text = new StringBuilder();
        text.Append($"# Health incident: {environment.Name}\n\n");
        text.Append($"Control plane health checks of environment `{environment.Id}` in project `{project}` are failing.\n\n");
        text.Append($"- Incident: `{incident.Id}`\n- Started: {Stamp(incident.StartedAt)}\n");
        text.Append($"- Environment address: {HealthTargets.Display(environment.Address)} on host `{environment.TargetHostId}`\n");
        foreach (var (check, checkIncident, evidence) in failing)
        {
            text.Append($"\n## Check `{check.Id}`\n\n");
            text.Append($"Failed {checkIncident.FailedProbes} times in a row (threshold {check.FailureThreshold}) since {Stamp(checkIncident.StartedAt)}.\n\n");
            text.Append($"- Check: {check.Kind} `{HealthTargets.Display(check.Target)}`");
            if (check.Kind == "http") text.Append($", expects status {check.ExpectedStatus}");
            if (check.BodyContains is not null) text.Append(" and a body marker");
            text.Append($", timeout {check.TimeoutSeconds} s, interval {check.IntervalSeconds} s\n\n");
            text.Append("| Time (UTC) | Result | Status | Latency | Error |\n|---|---|---|---|---|\n");
            foreach (var sample in evidence.TakeLast(10))
                text.Append($"| {Stamp(sample.At)} | {(sample.Ok ? "ok" : "failed")} | {sample.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"} | "
                    + $"{(sample.LatencyMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " ms" : "-")} | {sample.Error ?? "-"} |\n");
        }
        text.Append("\nOther checks of this environment that fail during the incident and their recoveries comment on this card. ");
        text.Append("The incident closes when every check has recovered; a later incident of this environment updates this card while it is open.\n");
        return text.ToString();
    }

    private static string Stamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours} h {value.Minutes} min"
            : $"{Math.Max(1, (int)Math.Round(value.TotalMinutes))} min";
}
