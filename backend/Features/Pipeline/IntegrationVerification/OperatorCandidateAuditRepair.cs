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
            var current = scanner.FindJob(snapshot.Id, snapshot.WatchPath);
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
        if (timeline.ReadAll(folderPath).Any(item =>
                item.Kind == TimelineEventKinds.IntegrationVerificationRecorded
                && item.Details?.GetValueOrDefault("recordId") == record.Id)) return true;
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
}
