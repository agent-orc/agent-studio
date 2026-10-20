namespace AgentStudio.Pipeline;

public sealed partial class MergeIntoDevelopRunner
{
    private sealed record CancellationSubject(string? Sha, string? Lane, bool ContinuePending, string Folder);

    private CancellationSubject CaptureCancellationSubject(
        string jobId, string? watchPath, string folder, string? reviewedSha)
    {
        var task = _taskScanner?.FindJob(jobId, watchPath);
        string? physicalLane = null;
        string? physicalFolder = null;
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            var entry = _taskScanner?.GetWatchPaths().FirstOrDefault(item =>
                AgentStudio.Shared.WatchPathComparison.PathsEqual(item.Path, watchPath));
            var foundPhysical = false;
            foreach (var state in TaskStates.All)
            {
                var candidate = Path.Combine(watchPath, state, jobId);
                if (!File.Exists(Path.Combine(candidate, "task.json"))) continue;
                if (entry is not null)
                    task = _taskScanner!.ScanJobFolder(candidate, entry, state) ?? task;
                physicalLane = state;
                physicalFolder = candidate;
                foundPhysical = true;
                break;
            }
            if (!foundPhysical && task is not null
                && File.Exists(Path.Combine(task.FolderPath, "task.json")))
            {
                if (entry is not null)
                    task = _taskScanner!.ScanJobFolder(task.FolderPath, entry, task.State) ?? task;
                // A concurrent write can leave task.json unreadable for a
                // moment; the scanned state then decides the lane.
                if (AgentStudio.Tasks.TaskJsonFile.TryReadStringField(
                        task.FolderPath, "state", _logger, out var flatState))
                    physicalLane = flatState;
                physicalFolder = task.FolderPath;
            }
        }
        var liveFolder = physicalFolder ?? task?.FolderPath ?? folder;
        var subject = ReviewSubjectStore.Read(liveFolder);
        var sha = subject?.ResultSha;
        if (_attemptAuthority is not null && task is not null)
            sha = _attemptAuthority.GetTaskProjection(task.Key ?? task.Id).CurrentRunAttempt?.ResultSha ?? sha;
        return new CancellationSubject(reviewedSha ?? sha, physicalLane ?? task?.State,
            task?.PendingIntent is not null, liveFolder);
    }
}
