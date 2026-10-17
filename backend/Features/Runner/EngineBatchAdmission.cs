namespace AgentStudio.Runner;

/// <summary>How one engine batch admission ended.</summary>
public enum EngineBatchAdmissionOutcome
{
    /// <summary>The batch gate owns the settled review: ownership and a pending record exist.</summary>
    Enqueued,

    /// <summary>The queue refused the review, and the per-task emergency gate settled the card.</summary>
    PerTaskGate,

    /// <summary>Nothing is owed: the card left its source lane or the review is no longer current.</summary>
    Skipped,
}

/// <summary>
/// Batch admission of an engine DeferToBatch review decision. The decision has
/// no lane effect, so this admission is its whole application. It mirrors the
/// monolith review report: the review verification minutes are recorded first,
/// the settled review enters the durable batch queue at the accepted report's
/// receive time, and a refused admission runs the per-task emergency gate. An
/// admission that owes nothing records its reason in the operator feed and lets
/// the occurrence complete. Any other unfinished admission throws, so the
/// occurrence stays applying and the recovery loop retries it.
/// </summary>
public sealed class EngineBatchAdmission(
    BatchGatePilotService batches,
    AttemptAuthorityService authority,
    TaskScannerService scanner,
    OrchestratorLog operatorLog,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<EngineBatchAdmission> logger)
{
    public async Task<EngineBatchAdmissionOutcome> AdmitAsync(TaskInfo task,
        MonolithOrchestrationOccurrence occurrence)
    {
        var review = authority.GetReview(occurrence.AuthorityAttemptId)
            ?? throw new InvalidOperationException("The reviewed attempt was not found.");
        if (task.State != occurrence.ExpectedSourceState)
            return Skip(task, occurrence,
                $"The card left {occurrence.ExpectedSourceState} for {task.State} before the batch admission.");
        if (!IsCurrentGeneration(review))
            return Skip(task, occurrence, $"Review {review.AttemptId} is no longer the card's current review.");
        var source = authority.GetRun(review.SourceRunAttemptId)
            ?? throw new InvalidOperationException("The batch source run was not found.");
        var reviewedAt = ReviewedAt(review, occurrence);
        // Count the review-side build and test minutes before admission, so a
        // failed admission still reports them. A replay finds the recorded fact.
        if (RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId).Entry is { } settlement)
            batches.RecordReviewVerification(task.ProjectName, review, settlement.Report.Commands);
        // As in the report request, a per-task gate is not cancelled by the
        // caller going away; only a host shutdown stops it.
        var stopping = lifetime.ApplicationStopping;
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
