using System.Text;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

/// <summary>
/// Backup and volume alerts of managed databases (AGT-3878) on the AGT-3223
/// path: an alert that opens creates one Backlog card tagged
/// <see cref="HealthAlerts.Tag"/> and one Activity alert; when the condition
/// ends, the card gets a comment and Activity a recovery entry. A condition
/// that returns while its earlier card is still open comments on that card
/// instead of creating another. Card creation is idempotent by slug, and a
/// failed card or Activity write is retried on the next tick. Studio never
/// moves the cards.
/// </summary>
public sealed class ManagedDatabaseAlerts(TaskScannerService scanner, TaskMutationService mutations, TimelineLog timeline,
    OrchestratorLog activity, ProjectEnvironmentService environments, TimeProvider clock, ILogger<ManagedDatabaseAlerts> logger)
{
    public const string CreationSource = "managed-database";
    private static readonly string[] TerminalLanes = [TaskStates.Completed, TaskStates.Archive];

    /// <returns>The number of alerts opened in this pass.</returns>
    public int Run()
    {
        var opened = 0;
        foreach (var watch in scanner.GetWatchPaths().DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                opened += RunProject(watch.Name, watch.Path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "managed-database alerts failed project={Project}; the next tick retries", watch.Name);
            }
        }
        return opened;
    }

    private int RunProject(string project, string watchPath)
    {
        var opened = 0;
        foreach (var (environment, record) in environments.DatabasesToWatch(project))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var conditions = environment is null ? []
                : ManagedDatabaseAlertPolicy.Conditions(record, environment.EffectiveDatabasePolicy, now);
            var alerts = new List<ManagedDatabaseAlert>();
            foreach (var (kind, summary) in conditions)
            {
                var existing = record.Alerts.FirstOrDefault(item => item.Kind == kind);
                var alert = existing is { ClosedAt: null } ? existing : new(kind, now, summary);
                if (alert.CardId is null || !alert.OpenedReported)
                {
                    alert = Open(project, watchPath, environment!, record, alert, existing?.ClosedAt is null ? null : existing.CardId);
                    if (alert.OpenedReported) opened++;
                }
                alerts.Add(alert);
            }
            foreach (var other in record.Alerts.Where(item => conditions.All(condition => condition.Kind != item.Kind)))
            {
                if (other.ClosedAt is null) Close(project, watchPath, environment, record, other, now);
                // A closed alert keeps its card, so the same condition later comments there while the card is open.
                if (other.CardId is not null) alerts.Add(other with { ClosedAt = other.ClosedAt ?? now });
            }
            environments.SetDatabaseAlerts(project, record.EnvironmentId, record.Name, record.HostId, alerts);
        }
        return opened;
    }

    private ManagedDatabaseAlert Open(string project, string watchPath, EnvironmentDeclaration environment,
        ManagedDatabaseRecord record, ManagedDatabaseAlert alert, string? earlierCardId)
    {
        var label = $"{environment.Name} database {record.Name}";
        if (alert.CardId is null)
        {
            var slug = $"database-{record.EnvironmentId}-{record.Name.Replace('_', '-')}-{alert.Kind}-{alert.Since:yyyyMMddHHmmss}";
            // The open card of an earlier alert of the same kind is reused; a crashed tick finds its own card by slug.
            var earlier = earlierCardId is null ? null
                : scanner.FindJob(earlierCardId, watchPath) is { } found && !TerminalLanes.Contains(found.State) ? found : null;
            string? cardId;
            if (earlier is not null)
            {
                cardId = Comment(earlier, $"{Title(alert.Kind)} again since {ManagedDatabaseAlertPolicy.Stamp(alert.Since)}: {alert.Summary}",
                    record, alert.Kind, "reopened") ? earlier.Id : null;
            }
            else
            {
                cardId = scanner.FindJob(slug, watchPath)?.Id ?? mutations.CreateJob(new CreateTaskRequest
                {
                    Id = slug,
                    Title = $"{Title(alert.Kind)}: {label}",
                    PromptMarkdown = Prompt(project, environment, record, alert),
                    WatchPath = watchPath,
                    TargetState = TaskStates.Backlog,
                    TaskType = alert.Kind == "volume-threshold" ? TaskTypes.Chore : TaskTypes.Bug,
                    Tags = [HealthAlerts.Tag],
                    CreationSource = CreationSource,
                    CreatedBy = TimelineActors.System,
                });
            }
            if (cardId is null)
                logger.LogWarning("managed-database alert card creation failed project={Project} database={Database} kind={Kind}",
                    project, record.Name, alert.Kind);
            alert = alert with { CardId = cardId };
        }
        if (!alert.OpenedReported && alert.CardId is not null)
            alert = alert with
            {
                OpenedReported = activity.Append(watchPath, new OrchestratorLogEntry
                {
                    Ts = clock.GetUtcNow().UtcDateTime,
                    Kind = OrchestratorLogKinds.Alert,
                    Topic = OrchestratorLogTopics.EnvironmentIncidentOpened,
                    Summary = $"{project} {label}: {Title(alert.Kind).ToLowerInvariant()}",
                    Reasoning = alert.Summary,
                    JobId = alert.CardId,
                }),
            };
        return alert;
    }

    private void Close(string project, string watchPath, EnvironmentDeclaration? environment, ManagedDatabaseRecord record,
        ManagedDatabaseAlert alert, DateTime now)
    {
        var why = environment is null || !record.Declared || record.DeletedAt is not null
            ? "closed because the database is no longer declared"
            : "recovered";
        var text = $"{Title(alert.Kind)} {why} {ManagedDatabaseAlertPolicy.Stamp(now)}, open since {ManagedDatabaseAlertPolicy.Stamp(alert.Since)}.";
        if (alert.CardId is not null && scanner.FindJob(alert.CardId, watchPath) is { } card)
            Comment(card, text, record, alert.Kind, "recovered");
        if (!alert.OpenedReported) return;
        activity.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = now,
            Kind = OrchestratorLogKinds.Observation,
            Topic = OrchestratorLogTopics.EnvironmentIncidentRecovered,
            Summary = $"{project} {environment?.Name ?? record.EnvironmentId} database {record.Name}: {text}",
            Reasoning = alert.Summary,
            JobId = alert.CardId,
        });
    }

    private bool Comment(TaskInfo card, string summary, ManagedDatabaseRecord record, string kind, string change)
        => timeline.Append(card.FolderPath, new TimelineEvent
        {
            Ts = clock.GetUtcNow().UtcDateTime,
            Kind = TimelineEventKinds.EnvironmentHealthUpdated,
            Actor = TimelineActors.System,
            Summary = summary,
            Details = new() { ["environmentId"] = record.EnvironmentId, ["database"] = record.Name, ["alert"] = kind,
                ["change"] = change },
        });

    private static string Title(string kind) => kind switch
    {
        "backup-overdue" => "Database backup overdue",
        "backup-failed" => "Database backup failed",
        _ => "Database volume above its alert threshold",
    };

    private static string Prompt(string project, EnvironmentDeclaration environment, ManagedDatabaseRecord record,
        ManagedDatabaseAlert alert)
    {
        var policy = environment.EffectiveDatabasePolicy;
        var text = new StringBuilder();
        text.Append($"# {Title(alert.Kind)}: {environment.Name} database {record.Name}\n\n");
        text.Append(alert.Summary).Append("\n\n");
        text.Append($"- Project: `{project}`\n- Environment: `{environment.Id}` ({environment.Kind}) on run host `{record.HostId}`\n");
        text.Append($"- Database: `{record.Name}`, PostgreSQL {record.RunningVersion ?? record.Version}, size {record.Size}\n");
        text.Append($"- Since: {ManagedDatabaseAlertPolicy.Stamp(alert.Since)}\n");
        text.Append($"- Policy: a backup every {policy.BackupIntervalHours} h, kept {policy.BackupRetentionDays} days, at least {policy.BackupsKept} kept\n");
        text.Append($"- Last backup: {(record.LastBackupAt is { } last ? ManagedDatabaseAlertPolicy.Stamp(last) : "none")}\n");
        if (record.LastBackupError is { } error) text.Append($"- Last error: {error}\n");
        if (record.VolumeBytes is { } bytes)
            text.Append($"- Data directory: {bytes} bytes; alert threshold {policy.VolumeAlertFor(record.Size)} bytes\n");
        text.Append("\nThe run host's log names each attempt. The restore runbook and the backup directory are described in "
            + "`docs/operations/managed-databases/README.md`. The alert comments on this card when it ends; move the card yourself.\n");
        return text.ToString();
    }
}
