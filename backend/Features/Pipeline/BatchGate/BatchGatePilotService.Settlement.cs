using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    private const int PublicationRecoveryAttempts = 3;

    private Func<BatchGateSubject, bool> SettledMembers()
    {
        var integrated = _store.ListAllPending().Where(item =>
                _store.ReadPendingOutcome(item.ReviewAttemptId) is "published" or "already-integrated" or "per-task-gate" or "strategy-handoff-integrated" or "member-already-settled")
            .Select(item => (item.Subject.TaskKey, item.Subject.RunAttempt)).ToHashSet();
        var cache = new Dictionary<(string, string), bool>();
        return member =>
        {
            var key = (member.TaskKey, member.RunAttempt);
            if (cache.TryGetValue(key, out var settled)) return settled;
            var task = _scanner.FindJob(member.TaskKey);
            var ownership = task is null ? null : BatchGateOwnershipStore.Read(task.FolderPath);
            settled = task?.State is TaskStates.Completed or TaskStates.Archive
                || integrated.Contains(key)
                || task?.State == TaskStates.HumanReview
                    && ownership?.Subject.RunAttempt == member.RunAttempt && ownership.FallbackIntegrated;
            cache[key] = settled;
            return settled;
        };
    }

    // Runs before ownership repair and formation on the first startup tick and
    // remains idempotent when cards finish while the host stays up.
    private void SettleCompletedPending()
    {
        var settled = SettledMembers();
        foreach (var pending in _store.ListPending().Where(item => settled(item.Subject)))
        {
            _store.ResolvePending(pending.ReviewAttemptId, "member-already-settled");
            _store.ClearQueueWait(pending.ReviewAttemptId);
            var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
            if (task is not null)
                BatchGateOwnershipStore.ClearIfReviewAttempt(task.FolderPath, pending.ReviewAttemptId);
        }
    }

    private void SettlePublicationRecovery(BatchGateManifest manifest, int attempts, string reason)
    {
        _store.RecordPublicationRecovery(manifest.BatchId,
            new(attempts, reason, _time.GetUtcNow(), true));
        _logger.Log(reason.StartsWith("terminal-", StringComparison.Ordinal) ? LogLevel.Information : LogLevel.Error,
            "batch-gate-publication-recovery-settled batch={BatchId} project={Project} reason={Reason} attempts={Attempts}",
            manifest.BatchId, manifest.Scope.Project, reason, attempts);
    }
}
