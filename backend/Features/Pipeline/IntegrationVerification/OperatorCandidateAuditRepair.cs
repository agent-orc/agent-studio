using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Completes the audit half of an immutable operator receipt after a crash.</summary>
public sealed class OperatorCandidateAuditRepair(
    TaskScannerService scanner, TimelineLog timeline, LaneMutexRegistry lanes,
    ILogger<OperatorCandidateAuditRepair> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RepairAll(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void RepairAll(CancellationToken cancellationToken = default)
    {
        foreach (var snapshot in scanner.ScanAllJobsWithArchive())
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!snapshot.IntegrationRecords.Any(IsOperatorCandidate)) continue;
            using var lane = lanes.Acquire(snapshot.WatchPath, Timeout.InfiniteTimeSpan);
            scanner.InvalidateCache();
            var matches = scanner.ScanAllJobsWithArchive()
                .Where(task => task.WatchPath == snapshot.WatchPath
                    && task.Id == snapshot.Id
                    && task.IntegrationRecords.Any(record => snapshot.IntegrationRecords.Any(prior =>
                        IsOperatorCandidate(prior) && SameRecord(record, prior))))
                .ToList();
            var current = matches.FirstOrDefault(task => task.FolderPath == snapshot.FolderPath)
                ?? (matches.Count == 1 ? matches[0] : null);
            if (current is null) continue;
            lock (TaskJsonFile.SyncRoot(current.FolderPath))
            {
                foreach (var record in current.IntegrationRecords.Where(IsOperatorCandidate))
                    EnsureAudit(current.FolderPath, record);
            }
        }
    }

    public bool EnsureAudit(string folderPath, TaskIntegrationRecord record)
    {
        if (!IsOperatorCandidate(record) || !File.Exists(Path.Combine(folderPath, "task.json"))) return false;
        bool? found;
        try
        {
            found = OperatorCandidateAuditLogSearch.ContainsRecord(
                TaskPaths.TimelineLog(folderPath), record.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Operator candidate audit lookup deferred for {RecordId} in {Folder}", record.Id, folderPath);
            return false;
        }
        if (found is null) return false;
        if (found.Value) return true;
        var written = timeline.Append(folderPath, new TimelineEvent
        {
            Ts = record.RecordedAtUtc,
            Kind = TimelineEventKinds.IntegrationVerificationRecorded,
            Actor = record.Actor ?? TimelineActors.System,
            Summary = record.Evidence,
            Details = new Dictionary<string, string>
            {
                ["state"] = IntegrationVerificationStates.Verified,
                ["sha"] = record.IntegrationSha ?? "",
                ["evidence"] = "operator-candidate",
                ["recordId"] = record.Id,
            },
        });
        if (!written)
            logger.LogWarning("Operator candidate audit repair deferred for {RecordId} in {Folder}", record.Id, folderPath);
        return written;
    }

    private static bool IsOperatorCandidate(TaskIntegrationRecord record)
        => record.Classification == IntegrationRecordClasses.IntegratedVerified
           && record.Id.StartsWith("operator-candidate-", StringComparison.Ordinal);

    private static bool SameRecord(TaskIntegrationRecord current, TaskIntegrationRecord prior)
        => IsOperatorCandidate(current)
           && current.Id == prior.Id
           && current.IntegrationSha == prior.IntegrationSha
           && current.SourceSha == prior.SourceSha
           && current.DeliveryEpoch == prior.DeliveryEpoch
           && current.RecordedAtUtc == prior.RecordedAtUtc
           && current.Actor == prior.Actor;
}
