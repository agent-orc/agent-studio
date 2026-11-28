using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Deletes old product-owned evidence and rotated logs only after references settle.</summary>
public static partial class HostStorageRetention
{
    /// <summary>Idle instance directories must be at least this many days old.</summary>
    public const int InstanceRetentionDays = 14;
    private const int InstanceEntryLimit = 10_000;

    /// <summary>
    /// Removes only idle, empty instances, allowing the origin metadata written
    /// by Prepare. Any other file is retained, including unreadable or unknown
    /// formats. Nonrecursive deletion refuses evidence written during the scan.
    /// Open directory handles anchor inspection and removal across path changes.
    /// Only the resolved local state root can authorize instance cleanup.
    /// </summary>
    public static int SweepInstances(string currentRoot, DateTimeOffset now,
        int retentionDays, Action<string> log)
        => SweepInstances(currentRoot, now, retentionDays, log, beforeDelete: null);

    internal static int SweepInstances(string currentRoot, DateTimeOffset now,
        int retentionDays, Action<string> log, Action<string>? beforeDelete)
    {
        currentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRoot));
        var stateRoot = Path.TrimEndingDirectorySeparator(LocalStateRoot.Resolve());
        var instances = Path.Combine(stateRoot, "agentstudio", "instances");
        // Fail closed on any alternate spelling or location. A matching parent
        // directory name does not establish ownership of its contents.
        if (!string.Equals(Path.GetDirectoryName(currentRoot), instances, StringComparison.Ordinal))
        {
            log($"instance-retention-skipped path={currentRoot} reason=outside-instance-root");
            return 0;
        }
        try
        {
            // Check from the configured root downward, before enumerating any
            // siblings or accessing a child through a linked ancestor.
            foreach (var directory in new[]
                     { stateRoot, Path.Combine(stateRoot, "agentstudio"), instances, currentRoot })
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    log($"instance-retention-skipped path={currentRoot} reason=linked-instance-root");
                    return 0;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    log($"instance-retention-skipped path={currentRoot} reason=unavailable-instance-root");
                    return 0;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log($"instance-retention-skipped path={currentRoot} reason=unavailable-instance-root");
            return 0;
        }
        PinnedRetentionDirectory anchor;
        try { anchor = PinnedRetentionDirectory.OpenRoot(instances); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            log($"instance-retention-skipped path={currentRoot} reason=unavailable-instance-anchor");
            return 0;
        }
        using var pinnedInstances = anchor;
        using var deletion = new InstanceDeletionWorker(pinnedInstances.AncestorDescriptors.ToArray());
        if (!deletion.Available)
        {
            log($"instance-retention-skipped path={currentRoot} reason=unavailable-deletion-confinement");
            return 0;
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var removed = 0;
        var reported = 0;
        foreach (var entry in Directory.EnumerateDirectories(pinnedInstances.IoPath))
        {
            var name = Path.GetFileName(entry);
            var candidate = Path.Combine(instances, name);
            var reason = "current-identity";
            var directories = new List<PinnedRetentionDirectory>();
            try
            {
                if (!FileSystemPathComparer.Instance.Equals(candidate, currentRoot))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        reason = "linked-state";
                    else
                    {
                        var pinnedCandidate = pinnedInstances.OpenChild(name);
                        directories.Add(pinnedCandidate);
                        reason = InspectInstance(pinnedCandidate, now.AddDays(-Math.Max(1, retentionDays)),
                            directories, out var hasOrigin);
                        if (reason == "empty")
                        {
                            beforeDelete?.Invoke(candidate);
                            deletion.Run(() =>
                            {
                                // Check every location before the first mutation. Linux also
                                // enforces the root in the kernel if a directory moves later.
                                foreach (var directory in directories) directory.VerifyLocation();
                                for (var i = directories.Count - 1; i > 0; i--)
                                    directories[i].DeleteEmpty();
                                if (hasOrigin) pinnedCandidate.DeleteOrigin();
                                pinnedCandidate.DeleteEmpty();
                            });
                            removed++;
                            reason = "deleted";
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reason = "unreadable-or-changed";
            }
            finally
            {
                for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
            }
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
            if (reported++ < 100)
                log($"instance-retention path={candidate} reason={reason}");
        }
        foreach (var (reason, count) in counts.OrderBy(item => item.Key, StringComparer.Ordinal))
            log($"instance-retention-summary reason={reason} count={count} retentionDays={Math.Max(1, retentionDays)}");
        return removed;
    }

    private static string InspectInstance(PinnedRetentionDirectory root, DateTimeOffset deadline,
        List<PinnedRetentionDirectory> directories, out bool hasOrigin)
    {
        hasOrigin = false;
        var pending = new Stack<(PinnedRetentionDirectory Directory, string Relative)>();
        pending.Push((root, ""));
        var visited = 0;
        var recent = false;
        while (pending.TryPop(out var item))
        {
            recent |= item.Directory.LastWriteTimeUtc > deadline.UtcDateTime;
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Directory.IoPath))
            {
                if (++visited > InstanceEntryLimit) return "inspection-limit";
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return "linked-state";
                var name = Path.GetFileName(entry);
                var relative = item.Relative + name;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var child = item.Directory.OpenChild(name);
                    directories.Add(child);
                    pending.Push((child, relative + "/"));
                    continue;
                }
                if (name == "manifest.json") return "batch-manifest";
                if (relative.StartsWith("batch-push-reservations/", StringComparison.Ordinal))
                    return "pending-reservation";
                if (relative.StartsWith("batch-gates/pending/", StringComparison.Ordinal)
                    || name.Contains("evidence", StringComparison.OrdinalIgnoreCase))
                    return "pending-evidence";
                if (relative != "batch-state-origin") return "unrecognized-content";
                hasOrigin = true;
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
        // Only the normal store folder can authorize sibling cleanup.
        // SweepInstances also verifies the resolved state root and its links.
        var storage = new DirectoryInfo(Path.GetFullPath(store.StorageRoot));
        if (string.Equals(storage.Name, "batch-gates", StringComparison.Ordinal)
            && storage.Parent is { } current)
            removed += SweepInstances(current.FullName, now, InstanceRetentionDays, log);
        return removed;
    }
}
