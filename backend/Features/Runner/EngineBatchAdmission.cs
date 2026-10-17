namespace AgentStudio.Runner;

/// <summary>How one engine batch admission ended.</summary>
public enum EngineBatchAdmissionOutcome
{
    /// <summary>The batch gate owns the settled review: ownership and a pending record exist.</summary>
    Enqueued,

    /// <summary>The queue refused the review, and the per-task emergency gate settled the card.</summary>
    PerTaskGate,

    /// <summary>Nothing is owed: the card is not in Auto Review or the review is no longer current.</summary>
    Skipped,
}

/// <summary>
/// Batch admission of an engine DeferToBatch review decision. The decision has
/// no lane effect, so this admission is its whole application. It mirrors the
/// monolith review report: only a card in Auto Review is admitted, the review
/// verification minutes are recorded first, the settled review enters the
/// durable batch queue at the accepted report's receive time, and a refused
/// admission runs the per-task emergency gate. An admission that owes nothing
/// records its reason in the operator feed and lets the occurrence complete.
/// Any other unfinished admission throws, so the occurrence stays applying and
/// the recovery loop retries it.
/// </summary>
public sealed class EngineBatchAdmission(
    BatchGatePilotService batches,
    BatchGateStore store,
    AttemptAuthorityService authority,
    TaskScannerService scanner,
    OrchestratorLog operatorLog,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<EngineBatchAdmission> logger)
{
    /// <param name="hostStopping">
    /// The caller's own host stop signal, such as a hosted service's stopping
    /// token, never a request token: as in the report request, the caller going
    /// away does not cancel a per-task gate; only a host shutdown stops it.
    /// </param>
    public async Task<EngineBatchAdmissionOutcome> AdmitAsync(TaskInfo task,
        MonolithOrchestrationOccurrence occurrence, CancellationToken hostStopping = default)
    {
        if (LaneRefusal(task, occurrence) is { } refusal) return Skip(task, occurrence, refusal);
        var review = authority.GetReview(occurrence.AuthorityAttemptId)
            ?? throw new InvalidOperationException("The reviewed attempt was not found.");
        if (!IsCurrentGeneration(review))
            return Skip(task, occurrence, $"Review {review.AttemptId} is no longer the card's current review.");
        var source = authority.GetRun(review.SourceRunAttemptId)
            ?? throw new InvalidOperationException("The batch source run was not found.");
        var reviewedAt = ReviewedAt(review, occurrence);
        // Count the review-side build and test minutes before admission, so a
        // failed admission still reports them. A replay finds the recorded fact.
        if (RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId).Entry is { } settlement)
            batches.RecordReviewVerification(task.ProjectName, review, settlement.Report.Commands);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping, hostStopping);
        var stopping = shutdown.Token;
        try
        {
            await batches.EnqueueAsync(task, review, source, reviewedAt, stopping).ConfigureAwait(false);
            return EngineBatchAdmissionOutcome.Enqueued;
        }
        catch (Exception exception) when (!stopping.IsCancellationRequested)
        {
            logger.LogError(exception, "batch-gate-enqueue-failed attempt={AttemptId}", review.AttemptId);
        }
        string? state = null;
        try
        {
            await batches.RunEmergencyFallbackAsync(task, review, source, reviewedAt, stopping)
                .ConfigureAwait(false);
            scanner.InvalidateCache();
            state = scanner.FindJob(task.Id, task.WatchPath)?.State;
        }
        catch (Exception fallbackError) when (!stopping.IsCancellationRequested)
        {
            logger.LogError(fallbackError, "batch-gate-emergency-fallback-failed attempt={AttemptId}",
                review.AttemptId);
        }
        if (state is TaskStates.HumanReview or TaskStates.Escalated)
            return EngineBatchAdmissionOutcome.PerTaskGate;
        if (!IsCurrentGeneration(review))
            return Skip(task, occurrence,
                $"Review {review.AttemptId} was superseded while it entered the batch queue.");
        throw new InvalidOperationException(
            "The settled review could not enter the durable batch queue or finish its per-task gate.");
    }

    /// <summary>
    /// True while the card waits in Auto Review on the occurrence's current
    /// review, the batch queue has no entry for that review, and no ownership
    /// records progress past admission. Ownership alone is not enough: a
    /// refused admission whose per-task gate was interrupted keeps an active
    /// gate marker without a queue entry, and a host stop between the ownership
    /// write and the queue entry leaves a bare marker. Admitting again resumes
    /// either. Reads attempt authority and two small files; runs no Git.
    /// </summary>
    public bool AwaitsAdmission(TaskInfo task, MonolithOrchestrationOccurrence occurrence)
    {
        if (LaneRefusal(task, occurrence) is not null) return false;
        var review = authority.GetReview(occurrence.AuthorityAttemptId);
        if (review is null || !IsCurrentGeneration(review)
            || store.ReadPending(review.AttemptId) is not null) return false;
        return BatchGateOwnershipStore.Read(task.FolderPath) is not { } ownership
            || ownership.ReviewAttemptId != review.AttemptId
            || !RecordsProgress(ownership);
    }

    /// <summary>
    /// The monolith report path admits a deferred review only while the card
    /// sits in Auto Review. The occurrence keeps whatever lane the report found,
    /// and the decision policy assumes Auto Review, so the report lane and the
    /// current lane must both be Auto Review. Elsewhere the batch never resolves
    /// the queue entry, and the ownership refuses the card's release.
    /// </summary>
    private static string? LaneRefusal(TaskInfo task, MonolithOrchestrationOccurrence occurrence)
        => occurrence.ExpectedSourceState != TaskStates.AutoReview
            ? $"The batch gate admits only from {TaskStates.AutoReview}; the report found the card in "
              + $"{occurrence.ExpectedSourceState}."
            : task.State != TaskStates.AutoReview
                ? $"The card left {TaskStates.AutoReview} for {task.State} before the batch admission."
                : null;

    // A batch, a passed per-task gate or a containment record carries the
    // review past admission, so no repeat admission is owed.
    private static bool RecordsProgress(BatchGateOwnership ownership)
        => ownership.BatchId is not null || ownership.FallbackTestedSha is not null
            || ownership.FallbackIntegrated || ownership.ContainedInSha is not null;

    // The batch subject is current only for the current review of the current run.
    private bool IsCurrentGeneration(ReviewAttemptDto review)
    {
        var projection = authority.GetTaskProjection(review.TaskKey);
        return projection.CurrentReviewAttempt?.AttemptId == review.AttemptId
            && projection.CurrentRunAttempt?.AttemptId == review.SourceRunAttemptId;
    }

    /// <summary>
    /// The receive time of the report that prepared this occurrence, else of the
    /// last accepted report, else the review's terminal time. The batch planner
    /// orders and ages members by it, so the decision time must not replace it.
    /// </summary>
    private static DateTimeOffset ReviewedAt(ReviewAttemptDto review,
        MonolithOrchestrationOccurrence occurrence)
    {
        var accepted = review.Reports.Where(report => report.AuthorityStatus == AttemptWriteStatus.Accepted)
            .ToArray();
        var reviewedAt = accepted.LastOrDefault(report => occurrence.IdempotencyKey
                == $"review-report:{review.AttemptId}:{report.IdempotencyKey}")?.ReceivedAt
            ?? accepted.LastOrDefault()?.ReceivedAt
            ?? review.TerminalAt
            ?? throw new InvalidOperationException("The deferred review has no accepted report.");
        return new DateTimeOffset(DateTime.SpecifyKind(reviewedAt, DateTimeKind.Utc));
    }

    private EngineBatchAdmissionOutcome Skip(TaskInfo task, MonolithOrchestrationOccurrence occurrence,
        string reason)
    {
        var topic = "engine-batch-admission:" + occurrence.Run.RunId;
        if (!operatorLog.Read(task.WatchPath).Any(entry => entry.Topic == topic))
            operatorLog.Append(task.WatchPath, new OrchestratorLogEntry
            {
                Ts = time.GetUtcNow().UtcDateTime,
                Kind = OrchestratorLogKinds.Decision,
                Topic = topic,
                JobId = task.Id,
                Summary = $"{task.Key ?? task.Id} completed its engine batch decision without a batch admission.",
                Reasoning = reason,
            });
        logger.LogWarning("engine-batch-admission-skipped run={RunId} attempt={AttemptId} reason={Reason}",
            occurrence.Run.RunId, occurrence.AuthorityAttemptId, reason);
        return EngineBatchAdmissionOutcome.Skipped;
    }
}
