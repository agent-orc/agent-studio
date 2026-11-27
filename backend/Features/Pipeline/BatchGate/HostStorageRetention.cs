using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Deletes old product-owned evidence and rotated logs only after references settle.</summary>
public static class HostStorageRetention
{
    /// <summary>Idle instance directories must be at least this many days old.</summary>
    public const int InstanceRetentionDays = 14;
    private const int InstanceEntryLimit = 10_000;

    /// <summary>
    /// Removes only idle, empty instances, allowing the origin metadata written
    /// by Prepare. Any other file is retained, including unreadable or unknown
    /// formats. Nonrecursive deletion refuses evidence written during the scan.
    /// </summary>
    public static int SweepInstances(string currentRoot, DateTimeOffset now,
        int retentionDays, Action<string> log)
    {
        currentRoot = Path.GetFullPath(currentRoot);
        var instances = Path.GetDirectoryName(currentRoot)!;
        if (!Directory.Exists(instances)) return 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var removed = 0;
        var reported = 0;
        foreach (var candidate in Directory.EnumerateDirectories(instances))
        {
            var reason = "current-identity";
            try
            {
                if (!FileSystemPathComparer.Instance.Equals(candidate, currentRoot))
                {
                    reason = InspectInstance(candidate, now.AddDays(-Math.Max(1, retentionDays)),
                        out var directories, out var origin);
                    if (reason == "empty")
                    {
                        // Delete children first, without recursively deleting any
                        // unchecked content that appeared after inspection.
                        foreach (var directory in directories.AsEnumerable().Reverse())
                            Directory.Delete(directory);
                        if (origin is not null) File.Delete(origin);
                        Directory.Delete(candidate);
                        removed++;
                        reason = "deleted";
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reason = "unreadable-or-changed";
            }
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
            if (reported++ < 100)
                log($"instance-retention path={candidate} reason={reason}");
        }
        foreach (var (reason, count) in counts.OrderBy(item => item.Key, StringComparer.Ordinal))
            log($"instance-retention-summary reason={reason} count={count} retentionDays={Math.Max(1, retentionDays)}");
        return removed;
    }

    private static string InspectInstance(string root, DateTimeOffset deadline,
        out List<string> directories, out string? origin)
    {
        directories = [];
        origin = null;
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        var recent = false;
        while (pending.TryPop(out var directory))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return "linked-state";
            recent |= info.LastWriteTimeUtc > deadline.UtcDateTime;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visited > InstanceEntryLimit) return "inspection-limit";
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return "linked-state";
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(entry);
                    pending.Push(entry);
                    continue;
                }
                var name = Path.GetFileName(entry);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (name == "manifest.json") return "batch-manifest";
                if (relative.StartsWith("batch-push-reservations/", StringComparison.Ordinal))
                    return "pending-reservation";
                if (relative.StartsWith("batch-gates/pending/", StringComparison.Ordinal)
                    || name.Contains("evidence", StringComparison.OrdinalIgnoreCase))
                    return "pending-evidence";
                if (relative != "batch-state-origin") return "unrecognized-content";
                origin = entry;
                recent |= File.GetLastWriteTimeUtc(entry) > deadline.UtcDateTime;
            }
        }
        return recent ? "recent" : "empty";
    }

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
        // Only an instance-scoped store can authorize sibling cleanup. Legacy
        // stores and test stores elsewhere do not own their parent directory.
        var current = Directory.GetParent(Path.GetFullPath(store.StorageRoot));
        if (current?.Parent?.Name == "instances")
            removed += SweepInstances(current.FullName, now, InstanceRetentionDays, log);
        return removed;
    }
}
