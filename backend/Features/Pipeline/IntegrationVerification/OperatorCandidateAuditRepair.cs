using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Completes the audit half of an immutable operator receipt after a crash.</summary>
public sealed class OperatorCandidateAuditRepair(
    TaskScannerService scanner, TimelineLog timeline, LaneMutexRegistry lanes,
    ILogger<OperatorCandidateAuditRepair> logger, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() =>
        {
            try { RepairAll(lifetime.ApplicationStopping); }
            catch (Exception ex) { logger.LogWarning(ex, "Operator candidate audit repair failed"); }
        }, lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void RepairAll(CancellationToken cancellationToken = default)
    {
        scanner.InvalidateCache();
        foreach (var snapshot in scanner.ScanAllJobsWithArchive())
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!snapshot.IntegrationRecords.Any(IsOperatorCandidate)) continue;
            using var lane = lanes.Acquire(snapshot.WatchPath, Timeout.InfiniteTimeSpan);
            if (!File.Exists(Path.Combine(snapshot.FolderPath, "task.json"))) continue;
            lock (TaskJsonFile.SyncRoot(snapshot.FolderPath))
            {
                foreach (var record in snapshot.IntegrationRecords.Where(IsOperatorCandidate))
                    EnsureAudit(snapshot.FolderPath, record);
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

}
