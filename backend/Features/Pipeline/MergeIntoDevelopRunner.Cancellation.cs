namespace AgentStudio.Pipeline;

public sealed partial class MergeIntoDevelopRunner
{
    private sealed record CancellationSubject(string? Sha, string? Lane, bool ContinuePending, string Folder);

    private CancellationSubject CaptureCancellationSubject(
        string jobId, string? watchPath, string folder, string? reviewedSha)
    {
        var task = _taskScanner?.FindJob(jobId, watchPath);
        var liveFolder = task?.FolderPath ?? folder;
        var subject = ReviewSubjectStore.Read(liveFolder);
        var sha = subject?.ResultSha;
        if (_attemptAuthority is not null && task is not null)
            sha = _attemptAuthority.GetTaskProjection(task.Key ?? task.Id).CurrentRunAttempt?.ResultSha ?? sha;
        return new CancellationSubject(reviewedSha ?? sha, task?.State,
            task?.PendingIntent is not null, liveFolder);
    }
}
