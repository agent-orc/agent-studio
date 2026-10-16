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
                using var document = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(task.FolderPath, "task.json")));
                if (document.RootElement.TryGetProperty("state", out var state))
                    physicalLane = state.GetString();
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
