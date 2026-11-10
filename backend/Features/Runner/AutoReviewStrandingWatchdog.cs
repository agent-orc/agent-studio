using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Watcher;

namespace AgentStudio.Runner;

/// <summary>Recovers cards that have no owner left in Auto Review.</summary>
public sealed class AutoReviewStrandingWatchdog : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly ReviewAttemptTaskLifecycleService _reviews;
    private readonly BatchGateStore _batches;
    private readonly BatchGatePilotService _batchPilot;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipeline;
    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly TaskTransitionService _transitions;
    private readonly AutoReviewDeliveryResumeService _resume;
    private readonly HumanReviewEscalation _escalation;
    private readonly StallInterventionStore _records;
    private readonly StallInterventionReceipts _receipts;
    private readonly IConfiguration _configuration;
    private readonly RemoteDeliveryIntegrationCoordinator? _integration;
    private readonly ILogger<AutoReviewStrandingWatchdog> _logger;

    public AutoReviewStrandingWatchdog(TaskScannerService scanner, AttemptAuthorityService authority,
        ReviewAttemptTaskLifecycleService reviews, BatchGateStore batches, BatchGatePilotService batchPilot,
        ProjectSettingsService settings,
        PipelineExecutionLog pipeline, AutoReviewPostProcessingQueue queue, TaskTransitionService transitions,
        AutoReviewDeliveryResumeService resume, HumanReviewEscalation escalation,
        StallInterventionStore records, StallInterventionReceipts receipts, IConfiguration configuration,
        ILogger<AutoReviewStrandingWatchdog> logger,
        RemoteDeliveryIntegrationCoordinator? integration = null)
    {
        _scanner = scanner;
        _authority = authority;
        _reviews = reviews;
        _batches = batches;
        _batchPilot = batchPilot;
        _settings = settings;
        _pipeline = pipeline;
        _queue = queue;
        _transitions = transitions;
        _resume = resume;
        _escalation = escalation;
        _records = records;
        _receipts = receipts;
        _configuration = configuration;
        _integration = integration;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        // The liveness floor applies in both modes. Record the active review
        // profile so the intervention names the owner that lost the card.
        var reviewMode = MonolithTransitionSwitch.For(_configuration, "ReviewReport");
        var threshold = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewStranding:ThresholdMinutes") ?? 30, 1, 1440));
        var now = DateTime.UtcNow;
        var pending = _batches.ListPending();
        var manifests = _batches.ListManifests();
        var activeBatchMembers = manifests
            .Where(manifest => IsActiveManifest(_batches, manifest))
            .SelectMany(manifest => manifest.Members.Select(member =>
                BatchMemberKey(member)))
            .ToHashSet(StringComparer.Ordinal);
        var interventions = _records.All().ToList();
        var count = 0;
        foreach (var card in _scanner.ScanAllAutomationJobs().Where(item =>
                     item.State == TaskStates.AutoReview && !item.Fixture))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var key = CardKey(card);
                var projection = _authority.GetTaskProjection(key);
                var run = projection.CurrentRunAttempt;
                var currentReview = ReviewForCurrentDelivery(projection.CurrentReviewAttempt, run);
                var writePrefix = $"auto-review-stranding:{card.ProjectName}:{key}:{card.EnteredLaneAt.Ticks}:";
                var previous = interventions.Where(item => item.WriteKey.StartsWith(writePrefix, StringComparison.Ordinal)
                    && item.Status == StallInterventionStatuses.Verified).ToArray();
                var latest = previous.LastOrDefault();
                var lastBatchEvent = run is null ? null : manifests
                    .Where(manifest => manifest.Scope.Project == card.ProjectName
                        && manifest.Members.Any(member => IsSubjectForDelivery(member, card.ProjectName, key, run)))
                    .Select(manifest => Directory.Exists(Path.Combine(_batches.BatchDirectory(manifest.BatchId), "state"))
                        ? (DateTime?)_batches.LatestState(manifest.BatchId).RecordedAtUtc.UtcDateTime : null)
                    .Max();
                var withoutOwner = TimeWithoutOwner(now, card.EnteredLaneAt, run, currentReview,
                    lastBatchEvent, latest?.DecidedAtUtc);
                var infraFailure = currentReview is
                    { State: AttemptLifecycleState.Failed, Outcome: ReviewTerminalOutcome.InfrastructureFailure };
                var facts = new AutoReviewStrandingFacts(withoutOwner, threshold,
                    _pipeline.Read(card.FolderPath)?.Steps.Any(step => step.Status == PipelineStepStatus.Running) == true
                        || IntegrationGateJournal.Read(card.FolderPath) is not null
                        || card.Phase == LifecyclePhases.Integrating,
                    currentReview?.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased,
                    pending.Any(item => IsPendingForDelivery(item, card.ProjectName, key,
                        run, currentReview?.AttemptId))
                        || run is not null && activeBatchMembers.Contains(
                            BatchMemberKey(card.ProjectName, key, run.AttemptId, run.ResultSha)),
                    card.PendingIntent is not null
                        || CauseWaitMarker.TryRead(card.FolderPath, _logger) is { Probe: false },
                    _queue.PositionOf(card.ProjectName, card.Id) is not null,
                    TaskModes.IsReportOnly(card.Mode),
                    run is { State: AttemptLifecycleState.Completed, ResultEnvelope: not null },
                    currentReview is not null,
                    currentReview is { Outcome: ReviewTerminalOutcome.Pass },
                    latest is null ? null : now - latest.DecidedAtUtc,
                    previous.Any(item => item.Action == AutoReviewStrandingAction.StartStep.ToString()),
                    previous.Any(item => item.Action == AutoReviewStrandingAction.ReenterBatch.ToString()),
                    infraFailure,
                    infraFailure && _authority.HasReviewInfrastructureRetryBudget(currentReview!.AttemptId),
                    infraFailure && _authority.HasScheduledReviewInfrastructureRetry(currentReview!.AttemptId));
                var action = MonolithTransitionAdapter.AutoReviewStranding(facts);
                if (action == AutoReviewStrandingAction.Leave) continue;
                // A batch can form after the pass snapshot. Recheck before any
                // side effect so formation never races a recovery intervention.
                if (IsBatchMember(_batches, card.ProjectName, key, run, currentReview?.AttemptId)) continue;
                var writeKey = NextWriteKey(writePrefix, interventions);
                var repeated = action == AutoReviewStrandingAction.Escalate
                    && !facts.InfrastructureFailure
                    && (facts.StartedStepBefore || facts.ReenteredBatchBefore);
                var (done, deferred, detail) = repeated
                    ? (false, false, "The previous automatic recovery did not keep an owner for this card.")
                    : await InterveneAsync(card, action, currentReview, run, ct);
                if (deferred) continue;
                var escalated = false;
                if (!done)
                {
                    // Formation can consume the pending entry while recovery is
                    // running. A closed batch now owns the card, even if the
                    // recovery call did not observe a pending entry itself.
                    if (IsBatchMember(_batches, card.ProjectName, key, run, currentReview?.AttemptId)) continue;
                    var parked = await _escalation.EscalateAsync(card.Id, card.WatchPath,
                        card.ProjectName, HumanReviewEscalationCategories.Environmental,
                        $"Auto Review had no active owner for {facts.WithoutOwner.TotalMinutes:0} minutes. {detail}",
                        ct, expectedSourceState: TaskStates.AutoReview);
                    done = parked.Status == MoveJobStatus.Success;
                    escalated = done;
                }
                interventions.Add(Publish(card, writeKey, action, done, escalated, detail, now, reviewMode));
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "auto-review-stranding-intervention-failed task={TaskKey}", card.TaskKey);
            }
        }
        return count;
    }

    /// <summary>Integrates the current run before exposing it to Remote Review.</summary>
    public async Task<MergeIntoIntegrationResult?> IntegrateCurrentDeliveryAsync(TaskInfo card)
    {
        if (card.State != TaskStates.AutoReview || _integration is null) return null;
        var run = _authority.GetTaskProjection(CardKey(card)).CurrentRunAttempt;
        if (run is not { State: AttemptLifecycleState.Completed, ResultEnvelope: not null, ResultSha: not null })
            return null;
        var previous = ReviewSubjectStore.Read(card.FolderPath);
        ReviewSubjectStore.Write(card.FolderPath, new ReviewSubjectRecord
        {
            TaskKey = CardKey(card), RunAttemptId = run.AttemptId,
            Project = card.ProjectName, Repository = run.ResultEnvelope.RepositoryUrl ?? string.Empty,
            ResultSha = run.ResultSha, BaseSha = run.ResultEnvelope.BaseSha,
            AttemptChainId = run.Lease?.LeaseId ?? run.AttemptId,
            Executor = run.Lease?.ExecutorId ?? "authority-recovery",
            LeaseId = run.Lease?.LeaseId ?? run.AttemptId, FencingToken = run.LastFence,
            ImmutableResultRef = run.ResultEnvelope.ImmutableRemoteRef,
            ResultRef = run.ResultEnvelope.ImmutableRemoteRef,
            IntegrationBranch = previous?.IntegrationBranch,
            CompletedAtUtc = run.TerminalAt ?? DateTimeOffset.UtcNow,
        });
        var project = _settings.Get(card.ProjectName);
        return await _integration.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            card.ProjectName, card.Id, card.FolderPath, card.WatchPath,
            TaskIntegrationBranch.Resolve(card, project.IntegrationBranch),
            project.IntegrationStrategy, PipelineTypes.Resolve(card),
            run.TerminalAt ?? DateTimeOffset.UtcNow,
            ReviewedResultSha: run.ResultSha), discardCompletedReplay: true);
    }

    /// <summary>Requests review after the current delivery has integrated.</summary>
    public bool RequestFreshReview(TaskInfo card)
    {
        if (card.State != TaskStates.AutoReview) return false;
        var projection = _authority.GetTaskProjection(CardKey(card));
        return CreateReview(card, projection.CurrentReviewAttempt, projection.CurrentRunAttempt,
            "delivery-verdict-stale");
    }

    /// <summary>A verdict can recover only the immutable result it reviewed.</summary>
    internal static ReviewAttemptDto? ReviewForCurrentDelivery(ReviewAttemptDto? review, RunAttemptDto? run)
        => run is { State: AttemptLifecycleState.Completed, ResultEnvelope: not null, ResultSha: not null }
            && review is not null
            && string.Equals(review.SourceRunAttemptId, run.AttemptId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(review.Subject.SourceRunAttemptId, run.AttemptId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(review.Subject.ExpectedResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase)
            && (review.State != AttemptLifecycleState.Completed
                || string.Equals(review.TestedResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase))
            ? review : null;

    internal static TimeSpan TimeWithoutOwner(DateTime now, DateTime enteredLaneAt,
        RunAttemptDto? run, ReviewAttemptDto? review, DateTime? lastBatchEvent,
        DateTime? lastIntervention)
    {
        var lastOwnerChange = new[] { enteredLaneAt, run?.TerminalAt, review?.TerminalAt,
                lastBatchEvent, lastIntervention }
            .OfType<DateTime>().Max();
        return now > lastOwnerChange ? now - lastOwnerChange : TimeSpan.Zero;
    }

    private async Task<(bool Done, bool Deferred, string Detail)> InterveneAsync(TaskInfo card,
        AutoReviewStrandingAction action, ReviewAttemptDto? review, RunAttemptDto? run,
        CancellationToken ct)
    {
        if (action == AutoReviewStrandingAction.StartStep)
        {
            if (review is { State: AttemptLifecycleState.Failed,
                    Outcome: ReviewTerminalOutcome.InfrastructureFailure })
            {
                var current = _authority.GetTaskProjection(CardKey(card));
                if (current.CurrentReviewAttempt?.AttemptId != review.AttemptId
                    || ReviewForCurrentDelivery(current.CurrentReviewAttempt, current.CurrentRunAttempt) is null
                    || _authority.HasScheduledReviewInfrastructureRetry(review.AttemptId))
                    return (false, true, "The review retry is already owned or the delivery changed.");
                var reason = ReviewFailureDetail(review);
                var scheduled = _authority.ScheduleReviewInfrastructureRetry(review.AttemptId, reason);
                if (!scheduled.Accepted)
                    return (false, true, "The review retry state changed during recovery.");
                return (true, false,
                    $"Scheduled infrastructure retry {scheduled.RetryNumber}/{scheduled.RetryBudget}: {reason}");
            }
            var started = TaskModes.IsReportOnly(card.Mode)
                ? _transitions.RequeueAutoReviewPostProcessing(card, "auto-review-stranding")
                : CreateReview(card, review, run, "auto-review-stranding");
            return (started, false, started ? "Started the missing review step." : "The missing review step could not start.");
        }
        if (action == AutoReviewStrandingAction.ReenterBatch && review is not null && run is not null)
        {
            // The projection can advance after the sweep snapshot. Never let a
            // passed review for an older delivery resume or enqueue this one.
            var current = _authority.GetTaskProjection(CardKey(card));
            if (current.CurrentReviewAttempt?.AttemptId != review.AttemptId
                || ReviewForCurrentDelivery(current.CurrentReviewAttempt, current.CurrentRunAttempt) is null
                || current.CurrentRunAttempt?.AttemptId != run.AttemptId
                || !_authority.IsReviewedDeliveryCurrent(review.AttemptId))
                return (false, true, "The review no longer covers the current delivery.");
            var resumed = await _resume.ResumeAsync(card, "auto-review-stranding", ct);
            if (resumed.Resumed) return (true, false, "Resumed delivery integration: " + resumed.Reason + ".");
            if (resumed.Reason is AutoReviewDeliveryResumeService.Reasons.IntegrationInFlight
                or AutoReviewDeliveryResumeService.Reasons.IntegrationOwnedWait
                or "delivery-resume-backoff" or AutoReviewResumePolicy.Reasons.GateRecoveryPending)
                return (false, true, "Delivery integration is owned: " + resumed.Reason + ".");
            try
            {
                if (!_authority.IsReviewedDeliveryCurrent(review.AttemptId))
                    return (false, true, "The reviewed delivery was superseded before batch admission.");
                if (_settings.Get(card.ProjectName).BatchGate.Enabled
                    && review.TerminalAt is { } reviewedAt)
                {
                    current = _authority.GetTaskProjection(CardKey(card));
                    if (current.CurrentReviewAttempt?.AttemptId != review.AttemptId
                        || ReviewForCurrentDelivery(current.CurrentReviewAttempt, current.CurrentRunAttempt) is null
                        || current.CurrentRunAttempt?.AttemptId != run.AttemptId
                        || !_authority.IsReviewedDeliveryCurrent(review.AttemptId))
                        return (false, true, "The review no longer covers the current delivery.");
                    await _batchPilot.EnqueueAsync(card, review, run, new DateTimeOffset(reviewedAt), ct);
                    if (IsBatchMember(_batches, card.ProjectName, CardKey(card), run, review.AttemptId))
                        return (true, false, "Re-entered the batch gate for the passed review.");
                    return (false, false,
                        "The batch gate retained only a resolved record for this review attempt.");
                }
            }
            catch (Exception ex)
            {
                return (false, false, "Batch gate re-entry failed: " + ex.Message);
            }
            return (false, false, "The passed review has no resumable integration or batch entry.");
        }
        return (false, false, review is { Outcome: ReviewTerminalOutcome.InfrastructureFailure }
            ? $"Infrastructure retry limit reached. {ReviewFailureDetail(review)}"
            : "There is no completed delivery or review step to recover.");
    }

    internal static string ReviewFailureDetail(ReviewAttemptDto review)
        => $"{ReviewAttemptChainEntry.ClassificationLabel(review.Outcome, review.FailureClassification)}: "
            + (string.IsNullOrWhiteSpace(review.TerminalReason)
                ? "No review summary was recorded." : review.TerminalReason);

    internal static bool ReviewMatchesCurrentRun(ReviewAttemptDto? review, RunAttemptDto? run)
        => review is not null && run is not null
            && review.SourceRunAttemptId == run.AttemptId
            && review.Subject.SourceRunAttemptId == run.AttemptId
            && !string.IsNullOrWhiteSpace(run.ResultSha)
            && string.Equals(review.Subject.ExpectedResultSha, run.ResultSha,
                StringComparison.OrdinalIgnoreCase)
            && (review.TestedResultSha is null
                || string.Equals(review.TestedResultSha, run.ResultSha,
                    StringComparison.OrdinalIgnoreCase));

    private bool CreateReview(TaskInfo card, ReviewAttemptDto? previous, RunAttemptDto? run, string source)
    {
        if (run is not { State: AttemptLifecycleState.Completed, ResultEnvelope: not null, ResultSha: not null })
            return false;
        var subject = previous?.Subject;
        var sameDelivery = subject is not null
            && subject.SourceRunAttemptId == run.AttemptId
            && string.Equals(subject.ExpectedResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase);
        var requirementsPath = Path.Combine(card.FolderPath, "prompt.md");
        var requirements = File.Exists(requirementsPath) ? File.ReadAllText(requirementsPath) : card.Id;
        var result = _reviews.CreateReviewAttemptInAutoReview(card, new CreateReviewAttemptRequest(
            CardKey(card), run.RepositoryId, run.ResultSha, run.AttemptId,
            sameDelivery ? subject!.TaskRequirementsHash : AttemptAuthorityService.Hash(requirements),
            sameDelivery ? subject!.ReviewPolicyHash : AttemptAuthorityService.Hash("remote-review-policy:v1"),
            run.EvidenceDigests, $"{source}:{run.AttemptId}:{card.EnteredLaneAt.Ticks}",
            RepositoryUrl: run.ResultEnvelope.RepositoryUrl,
            ResultRef: run.ResultEnvelope.ImmutableRemoteRef,
            Plan: sameDelivery ? subject!.Plan : null));
        if (!result.Accepted) return false;
        TaskJsonFile.UpdateField(card.FolderPath, "phase", LifecyclePhases.AwaitingReview, _logger);
        _scanner.InvalidateCache();
        _scanner.PublishCoreFromKnownFolder(card.FolderPath);
        return true;
    }

    internal static bool IsBatchMember(BatchGateStore batches, string project, string taskKey,
        RunAttemptDto? run, string? reviewAttemptId)
    {
        if (run is null || run.ResultSha is null) return false;
        if (batches.ListPending().Any(item => IsPendingForDelivery(item, project, taskKey,
            run, reviewAttemptId))) return true;
        return batches.ListManifests().Any(manifest =>
            manifest.Scope.Project == project
            && manifest.Members.Any(member => IsSubjectForDelivery(member, project, taskKey, run))
            && IsActiveManifest(batches, manifest));
    }

    private static bool IsPendingForDelivery(BatchGatePendingRecord pending, string project,
        string taskKey, RunAttemptDto? run, string? reviewAttemptId)
        => reviewAttemptId is not null
            && string.Equals(pending.ReviewAttemptId, reviewAttemptId, StringComparison.Ordinal)
            && run is not null && IsSubjectForDelivery(pending.Subject, project, taskKey, run);

    private static bool IsSubjectForDelivery(BatchGateSubject subject, string project,
        string taskKey, RunAttemptDto run)
        => string.Equals(subject.Project, project, StringComparison.Ordinal)
            && string.Equals(subject.TaskKey, taskKey, StringComparison.Ordinal)
            && string.Equals(subject.RunAttempt, run.AttemptId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(subject.ResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase);

    private static string BatchMemberKey(BatchGateSubject subject)
        => BatchMemberKey(subject.Project, subject.TaskKey, subject.RunAttempt, subject.ResultSha);

    private static string BatchMemberKey(string project, string taskKey, string runAttempt, string? resultSha)
        => string.Join('\u001f', project, taskKey, runAttempt.ToUpperInvariant(), resultSha?.ToUpperInvariant());

    private static bool IsActiveManifest(BatchGateStore batches, BatchGateManifest manifest)
    {
        // Until a state is visible, keep ownership with the batch so the
        // watchdog cannot send its members through review a second time.
        var phase = batches.LatestState(manifest.BatchId).Phase;
        return phase == BatchPhase.Unknown || IsActiveBatchPhase(phase);
    }

    internal static bool IsActiveBatchPhase(BatchPhase phase)
        => phase is BatchPhase.Formed or BatchPhase.Assembling or BatchPhase.Assembled
            or BatchPhase.Running or BatchPhase.Green or BatchPhase.Publishing;

    internal static string NextWriteKey(string prefix, IReadOnlyCollection<StallInterventionRecord> interventions)
        => prefix + interventions.Count(item => item.WriteKey.StartsWith(prefix, StringComparison.Ordinal));

    private StallInterventionRecord Publish(TaskInfo card, string writeKey, AutoReviewStrandingAction action,
        bool succeeded, bool escalated, string detail, DateTime now, OrchestrationExecutionMode reviewMode)
    {
        var record = new StallInterventionRecord
        {
            Id = StallInterventionStore.IdFor(writeKey), WriteKey = writeKey,
            Subject = "auto-review:" + CardKey(card), Kind = "auto-review-stranded",
            Host = "task-server", Action = action.ToString(), Autonomy = "A1",
            Disposition = "act", ReasonCode = "auto-review-no-owner",
            Status = escalated ? StallInterventionStatuses.Escalated
                : succeeded ? StallInterventionStatuses.Verified : StallInterventionStatuses.Failed,
            Project = card.ProjectName, AffectedCards = [card.Id],
            Observed = "Card had no active Auto Review owner beyond the configured threshold.",
            Done = detail, Why = $"In {reviewMode} mode, no running step, review, batch gate entry, or queued round owned the card.",
            Before = TaskStates.AutoReview, After = escalated ? TaskStates.Escalated : action.ToString(),
            Receipt = detail, DecidedAtUtc = now, ResolvedAtUtc = now,
        };
        _records.Save(record);
        _receipts.Publish(record, _records.All());
        return record;
    }

    private static string CardKey(TaskInfo card)
        => !string.IsNullOrWhiteSpace(card.Key) ? card.Key :
            !string.IsNullOrWhiteSpace(card.TaskKey) ? card.TaskKey : card.Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _scanner.WaitForFirstPublicationAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "auto-review-stranding-watchdog-failed"); }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
