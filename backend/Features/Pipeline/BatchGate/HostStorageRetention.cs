using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Deletes old product-owned evidence and rotated logs only after references settle.</summary>
public static class HostStorageRetention
{
    public static bool CanRemoveBatchEvidence(BatchGateState state, DateTimeOffset now,
        int retentionDays, bool referenced)
        => !referenced
            && state.Phase is BatchPhase.Published or BatchPhase.Abandoned
            && state.RecordedAtUtc <= now.AddDays(-Math.Max(1, retentionDays));

    public static int Sweep(BatchGateStore store, TaskScannerService scanner,
        DateTimeOffset now, int retentionDays, Action<string> log,
        Func<string, bool>? hasActiveAttempt = null)
    {
        var manifests = store.ListManifests();
        var tasks = scanner.ScanAllJobsWithArchive().ToArray();
        var terminalCards = tasks.Where(task => task.State is TaskStates.Completed or TaskStates.Archive)
            .Select(task => task.Key ?? task.Id)
            .Where(key => hasActiveAttempt?.Invoke(key) != true)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = store.ListPending().Select(item => item.Subject.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unsettled = manifests.Where(manifest =>
            store.LatestState(manifest.BatchId).Phase is not (BatchPhase.Published or BatchPhase.Abandoned))
            .ToArray();
        var protectedBatches = unsettled.SelectMany(manifest =>
                new[] { manifest.BatchId, manifest.ParentBatchId, manifest.StackedOnBatchId })
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var protectedCards = unsettled.SelectMany(manifest => manifest.Members.Select(member => member.TaskKey))
            .Concat(pending).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var manifest in manifests)
        {
            var state = store.LatestState(manifest.BatchId);
            var referenced = protectedBatches.Contains(manifest.BatchId)
                || manifest.Members.Any(member => protectedCards.Contains(member.TaskKey)
                    || !terminalCards.Contains(member.TaskKey));
            if (!CanRemoveBatchEvidence(state, now, retentionDays, referenced)) continue;
            var file = Path.Combine(store.BatchDirectory(manifest.BatchId), "gate-evidence.jsonl");
            if (!File.Exists(file)) continue;
            if (store.ListIsolationFindings(manifest.Scope.Project).Any(finding =>
                    string.Equals(finding.EvidencePath, file, StringComparison.Ordinal))) continue;
            try { File.Delete(file); removed++; log($"gate-evidence-retention deleted batch={manifest.BatchId}"); }
            catch (IOException error) { log($"gate-evidence-retention kept batch={manifest.BatchId} error={error.Message}"); }
        }
        foreach (var task in tasks)
        {
            if (task.State is not (TaskStates.Completed or TaskStates.Archive)
                || task.EnteredLaneAt > now.UtcDateTime.AddDays(-Math.Max(1, retentionDays))
                || protectedCards.Contains(task.Key ?? task.Id)
                || hasActiveAttempt?.Invoke(task.Key ?? task.Id) == true) continue;
            var rotated = Path.Combine(TaskPaths.LogsDir(task.FolderPath), "cli-output.log.1");
            if (!File.Exists(rotated) || File.GetLastWriteTimeUtc(rotated) > now.UtcDateTime.AddDays(-Math.Max(1, retentionDays)))
                continue;
            try { File.Delete(rotated); removed++; log($"rotated-log-retention deleted task={task.Key ?? task.Id}"); }
            catch (IOException error) { log($"rotated-log-retention kept task={task.Key ?? task.Id} error={error.Message}"); }
        }
        return removed;
    }
}
