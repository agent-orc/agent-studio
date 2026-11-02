using AgentStudio.Git;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3434: the tree the per-task fallback gate tests. The gate used to test
/// the delivery on the base it was written on. AGT-3408 failed there for 56
/// minutes on tests the integration tip had already fixed, and the merge path
/// then ran its own gate on the merged tree. The gate now tests the delivery
/// merged onto the current tip; the merge path reuses that verdict while the
/// tip has not moved.
/// </summary>
public sealed partial class BatchGatePilotService
{
    private PerTaskFallbackSubject PrepareFallbackSubject(BatchGatePendingRecord pending,
        BatchGateOwnership? ownership, bool ownBase, CancellationToken ct)
    {
        var resultSha = pending.Subject.ResultSha;
        if (ownBase) return PerTaskFallbackPolicy.Choose(resultSha, true, null, null, null);
        var repo = pending.RepositoryPath;
        var tip = _git.FetchBatchIntegrationTip(repo, pending.Subject.IntegrationBranch, ct);
        BatchReplayResult? merge = null;
        string? tree = null;
        if (tip is not null)
        {
            // A resumed gate keeps its candidate while the tip stands, so the
            // exact-SHA verdict cache still recognizes it.
            if (ownership is { FallbackGateActive: true, FallbackCandidateSha: { } previous,
                    FallbackCandidateTreeSha: { } previousTree }
                && ownership.ReviewAttemptId == pending.ReviewAttemptId
                && string.Equals(ownership.FallbackBaseSha, tip, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_git.GetCommitTree(repo, previous), previousTree,
                    StringComparison.OrdinalIgnoreCase))
            {
                merge = new BatchReplayResult(true, previous, [], [], null, BatchReplayRoute.Merge);
                tree = previousTree;
            }
            else
            {
                merge = _git.MergeDeliveryOntoTip(repo, pending.Subject.ResultRef, resultSha, tip);
                if (merge is { Success: true, TipSha: { } candidate })
                    tree = _git.GetCommitTree(repo, candidate);
            }
        }
        return PerTaskFallbackPolicy.Choose(resultSha, false, tip, merge, tree);
    }

    /// <summary>
    /// The delivery conflicts with the integration tip. No gate runs: the card
    /// returns to its agent for one merge round whose prompt names the
    /// conflicted files. When that round cannot start, the card escalates with
    /// the same files.
    /// </summary>
    private async Task StartFallbackMergeRoundAsync(TaskInfo task, BatchGatePendingRecord pending,
        PerTaskFallbackSubject subject, bool recordInStore, CancellationToken ct)
    {
        var branch = pending.Subject.IntegrationBranch;
        var files = string.Join(", ", subject.ConflictedFiles);
        _logger.LogWarning(
            "batch-fallback-merge-conflict task={TaskKey} attempt={AttemptId} tip={Tip} files={Files}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, subject.BaseSha, files);
        Lane(pending, "ejected", outcome: "Conflict", reason: "per-task-fallback: " + subject.Reason);
        var conflict = MergeIntoIntegrationResult.Conflicted(subject.ConflictedFiles,
            $"The delivery conflicts with {branch} at {subject.BaseSha}; no gate ran. "
            + $"Merge {branch} into the delivery and resolve: {files}.");
        var request = new RemoteDeliveryIntegrationRequest(task.ProjectName, task.Id,
            task.FolderPath, task.WatchPath, branch, pending.IntegrationStrategy,
            pending.PipelineType, pending.Subject.ReviewCompletedAtUtc);
        var rounds = _services?.GetService<IntegrationAgentRoundService>();
        var round = rounds is null
            ? new IntegrationAgentRoundStartResult(false, "No automatic merge round is configured.")
            : await rounds.TryStartAsync(request, conflict).ConfigureAwait(false);
        RecordFallbackSuites(pending, round.Started ? "merge-round" : "escalated");
        if (!round.Started)
        {
            await EscalateFallbackAsync(task, pending,
                $"Merge conflict with {branch} at {subject.BaseSha} in {files}; no gate ran. "
                + $"The automatic merge round did not start: {round.Reason}",
                infrastructure: false, recordInStore, ct).ConfigureAwait(false);
            return;
        }
        // The merge round supersedes this delivery; its review generation
        // leaves the batch queue with the ownership marker.
        BatchGateOwnershipStore.ClearIfReviewAttempt(
            _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath)?.FolderPath ?? task.FolderPath,
            pending.ReviewAttemptId);
        if (recordInStore || _store.IsQueued(pending.ReviewAttemptId))
            _store.ResolvePending(pending.ReviewAttemptId, "merge-round");
        _store.ClearQueueWait(pending.ReviewAttemptId);
    }

    /// <summary>
    /// Whether a deterministic red of a merged candidate sits on a tip that is
    /// red on its own. The durable base verdict is shared with batches on the
    /// same tip; only the first caller runs it. An unknown verdict keeps the
    /// red result.
    /// </summary>
    private async Task<bool> TipIsRedAsync(TaskInfo task, BatchGatePendingRecord pending,
        PerTaskFallbackSubject subject, BuildTestGateResult gate, CancellationToken ct)
    {
        if (!PerTaskFallbackPolicy.RetestOnOwnBase(subject, pending.Subject.ResultSha, gate, "fail")) return false;
        try
        {
            var settings = _settings.Get(pending.Subject.Project);
            var verdict = await BaseVerdictAsync(pending.Subject.Project, subject.BaseSha!,
                GateProfile(settings.BuildProfile, pending.RepositoryPath), pending.RepositoryPath,
                Path.Combine(TaskPaths.LogsDir(task.FolderPath), FallbackTipEvidenceName(pending.ReviewAttemptId)),
                null, ct).ConfigureAwait(false);
            return PerTaskFallbackPolicy.RetestOnOwnBase(subject, pending.Subject.ResultSha, gate, verdict.Outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "batch-fallback-tip-verdict-unavailable task={TaskKey} attempt={AttemptId} tip={Tip}",
                pending.Subject.TaskKey, pending.ReviewAttemptId, subject.BaseSha);
            return false;
        }
    }

    /// <summary>
    /// Counts the suites this review generation ran in the per-task path for
    /// the batch report: every recorded execution of its per-task gates, plus
    /// the publish gate when the merge ran its suite.
    /// </summary>
    private void RecordFallbackSuites(BatchGatePendingRecord pending, string outcome,
        MergeIntoIntegrationResult? publish = null)
    {
        var attempt = pending.ReviewAttemptId;
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            $"batch-fallback-{attempt}.json",
            FallbackOwnBaseEvidenceName(attempt),
            FallbackTipEvidenceName(attempt),
        };
        var perTask = _store.ListExecutions(pending.Subject.Project)
            .Count(fact => names.Contains(Path.GetFileName(fact.EvidencePath)));
        _store.RecordFallbackSuites(new BatchGateFallbackSuiteFact(
            pending.Subject.Project, pending.Subject.TaskKey, attempt, outcome, perTask,
            publish?.GateExecuted == true ? 1 : 0, publish?.GateVerdictReused == true,
            _time.GetUtcNow()));
    }

    private static string FallbackOwnBaseEvidenceName(string reviewAttemptId)
        => $"batch-fallback-{reviewAttemptId}-own-base.json";

    private static string FallbackTipEvidenceName(string reviewAttemptId)
        => $"batch-fallback-{reviewAttemptId}-tip.json";

    private static string FallbackPassedReason(BatchGateOwnership? ownership)
        => ownership?.FallbackBaseSha is { } tip
            ? $"Per-task fallback gate passed on the delivery merged onto the integration tip {tip}."
            : "Per-task fallback gate passed on the immutable result SHA.";
}
