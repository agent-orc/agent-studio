using System.Collections.Concurrent;
using AgentStudio.Pipeline;

namespace AgentStudio.Runner;

public enum RemoteReviewSettlementReconcileStatus
{
    NoWork, PendingAuthority, PendingEvidence, Complete, Repair,
}

/// <summary>Replays only derived work belonging to the current, settled review generation.</summary>
public sealed class RemoteReviewSettlementReconciler : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly RemoteReviewEvidenceProjectionQueue _queue;
    private readonly AutoReviewDeliveryResumeService _resume;
    private readonly ILogger<RemoteReviewSettlementReconciler> _logger;

    // Keyed by task key; the value is the review generation it was observed for.
    // An archived card has no lane continuation: once its current review settled
    // (Complete, NoWork or Repair), only a new generation can change the answer,
    // so ordinary ticks skip its folder. A fresh process reconciles it once.
    private readonly ConcurrentDictionary<string, string> _settledArchive = new(StringComparer.OrdinalIgnoreCase);
    // A repair is logged once per task and review generation, not on every tick.
    private readonly ConcurrentDictionary<string, string> _reportedRepairs = new(StringComparer.OrdinalIgnoreCase);

    public RemoteReviewSettlementReconciler(
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        RemoteReviewEvidenceProjectionQueue queue,
        AutoReviewDeliveryResumeService resume,
        ILogger<RemoteReviewSettlementReconciler> logger)
    {
        _scanner = scanner;
        _authority = authority;
        _queue = queue;
        _resume = resume;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "remote-review-settlement-reconcile-failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var task in _scanner.ScanAllJobsWithArchive().Where(task => !task.Fixture))
        {
            ct.ThrowIfCancellationRequested();
            if (TaskKeyOf(task) is not { } taskKey) continue;
            var generation = _authority.GetTaskProjection(taskKey).CurrentReviewAttempt?.AttemptId ?? string.Empty;
            var archived = string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal);
            if (archived && _settledArchive.TryGetValue(taskKey, out var settled) && settled == generation) continue;
            var status = RemoteReviewSettlementReconcileStatus.Repair;
            Exception? failure = null;
            try { status = Reconcile(task); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
            if (archived && failure is null && status is RemoteReviewSettlementReconcileStatus.Complete
                    or RemoteReviewSettlementReconcileStatus.NoWork or RemoteReviewSettlementReconcileStatus.Repair)
                _settledArchive[taskKey] = generation;
            if (status != RemoteReviewSettlementReconcileStatus.Repair)
            {
                _reportedRepairs.TryRemove(taskKey, out _);
            }
            else if (!_reportedRepairs.TryGetValue(taskKey, out var reported) || reported != generation)
            {
                _reportedRepairs[taskKey] = generation;
                _logger.LogWarning(failure,
                    "remote-review-settlement-repair-required task={TaskKey} attempt={AttemptId}", taskKey, generation);
            }
        }
        await _resume.RunOnceAsync("review-settlement-reconcile", ct).ConfigureAwait(false);
    }

    private static string? TaskKeyOf(TaskInfo task)
        => new[] { task.Key, task.TaskKey, task.Id }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public RemoteReviewSettlementReconcileStatus Reconcile(TaskInfo task)
    {
        var taskKey = TaskKeyOf(task);
        if (taskKey is null) return RemoteReviewSettlementReconcileStatus.NoWork;
        var review = _authority.GetTaskProjection(taskKey).CurrentReviewAttempt;
        if (review is null) return RemoteReviewSettlementReconcileStatus.NoWork;
        var accepted = review.Reports.LastOrDefault(report => report.AuthorityStatus == AttemptWriteStatus.Accepted);
        if (accepted is null)
        {
            // An unaccepted journal binds nothing. Once the attempt is terminal no
            // report can accept it any more, so the orphan is released.
            if (review.TerminalAt is null) return RemoteReviewSettlementReconcileStatus.PendingAuthority;
            RemoteReviewSettlementJournal.Release(task.FolderPath, review.AttemptId);
            return RemoteReviewSettlementReconcileStatus.NoWork;
        }

        var read = RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId);
        if (read.Status != RemoteReviewSettlementReadStatus.Ready || read.Entry is null)
        {
            // Older reports can have fully projected evidence without this new
            // journal. An unfinished report cannot be reconstructed from a verdict.
            if (read.Status == RemoteReviewSettlementReadStatus.Missing
                && RemoteDeliverySettlementStore.Read(task.FolderPath) is { JournalRequired: false } legacy
                && legacy.ReviewAttemptId == review.AttemptId)
                return RemoteReviewSettlementReconcileStatus.NoWork;
            return read.Status == RemoteReviewSettlementReadStatus.Missing
                   && File.Exists(Path.Combine(task.FolderPath,
                       RemoteReviewReportEvidence.EvidenceFileName(review.AttemptId)))
                ? RemoteReviewSettlementReconcileStatus.Complete
                : RemoteReviewSettlementReconcileStatus.Repair;
        }
        var entry = read.Entry;
        if (!RemoteReviewSettlementPolicy.MatchesAcceptedReview(entry, review))
            return RemoteReviewSettlementReconcileStatus.Repair;

        RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task, entry);
        if (entry.Delivery is null
            && AutoReviewResumePolicy.IsAdmissibleOutcome(review.Outcome)
            && string.Equals(task.State, TaskStates.AutoReview, StringComparison.Ordinal))
            return RemoteReviewSettlementReconcileStatus.Repair;

        if (entry.RepairReason is not null) return RemoteReviewSettlementReconcileStatus.Repair;
        if (entry.EvidenceComplete && !File.Exists(Path.Combine(task.FolderPath,
                RemoteReviewReportEvidence.EvidenceFileName(review.AttemptId))))
            return RemoteReviewSettlementReconcileStatus.Repair;
        if (!entry.EvidenceComplete && entry.NextEvidenceAttemptUtc <= DateTime.UtcNow)
            _queue.Enqueue(new RemoteReviewEvidenceProjectionRequest(
                review.AttemptId, review.TaskKey, review, entry.Report,
                RemoteReviewReportEvidence.EvidenceFileName(review.AttemptId),
                entry.ReportSha256, accepted.ReceivedAt, DateTime.UtcNow));
        if (review.Outcome == ReviewTerminalOutcome.InfrastructureFailure
            && string.Equals(task.State, TaskStates.AutoReview, StringComparison.Ordinal)
            && !_authority.HasScheduledReviewInfrastructureRetry(review.AttemptId))
            return RemoteReviewSettlementReconcileStatus.Repair;
        return entry.EvidenceComplete
            ? RemoteReviewSettlementReconcileStatus.Complete
            : RemoteReviewSettlementReconcileStatus.PendingEvidence;
    }
}
