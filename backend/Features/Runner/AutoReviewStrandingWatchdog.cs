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
    private readonly ILogger<AutoReviewStrandingWatchdog> _logger;

    public AutoReviewStrandingWatchdog(TaskScannerService scanner, AttemptAuthorityService authority,
        ReviewAttemptTaskLifecycleService reviews, BatchGateStore batches, BatchGatePilotService batchPilot,
        ProjectSettingsService settings,
        PipelineExecutionLog pipeline, AutoReviewPostProcessingQueue queue, TaskTransitionService transitions,
        AutoReviewDeliveryResumeService resume, HumanReviewEscalation escalation,
        StallInterventionStore records, StallInterventionReceipts receipts, IConfiguration configuration,
        ILogger<AutoReviewStrandingWatchdog> logger)
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
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (MonolithTransitionSwitch.For(_configuration, "ReviewReport") == OrchestrationExecutionMode.Engine
            || !MonolithTransitionSwitch.OwnsAutoReview(_configuration)) return 0;
        var threshold = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewStranding:ThresholdMinutes") ?? 30, 1, 1440));
        var now = DateTime.UtcNow;
        var pending = _batches.ListPending();
        var activeBatchMembers = _batches.ListManifests()
            .Where(IsActiveManifest)
            .SelectMany(manifest => manifest.Members.Select(member =>
                manifest.Scope.Project + "\u001f" + member.TaskKey))
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
                var review = projection.CurrentReviewAttempt;
                var run = projection.CurrentRunAttempt;
                var writePrefix = $"auto-review-stranding:{card.ProjectName}:{key}:{card.EnteredLaneAt.Ticks}:";
                var previous = interventions.Where(item => item.WriteKey.StartsWith(writePrefix, StringComparison.Ordinal)
                    && item.Status == StallInterventionStatuses.Verified).ToArray();
                var latest = previous.LastOrDefault();
                var facts = new AutoReviewStrandingFacts(now - card.EnteredLaneAt, threshold,
                    _pipeline.Read(card.FolderPath)?.Steps.Any(step => step.Status == PipelineStepStatus.Running) == true
                        || IntegrationGateJournal.Read(card.FolderPath) is not null,
                    review?.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased,
                    pending.Any(item => item.Subject.Project == card.ProjectName
                        && item.Subject.TaskKey == key)
                        || activeBatchMembers.Contains(card.ProjectName + "\u001f" + key),
                    card.PendingIntent is not null,
                    _queue.PositionOf(card.ProjectName, card.Id) is not null,
                    TaskModes.IsReportOnly(card.Mode),
                    run is { State: AttemptLifecycleState.Completed, ResultEnvelope: not null },
                    review is not null,
                    review is { Outcome: ReviewTerminalOutcome.Pass },
                    latest is null ? null : now - latest.DecidedAtUtc,
                    previous.Any(item => item.Action == AutoReviewStrandingAction.StartStep.ToString()),
                    previous.Any(item => item.Action == AutoReviewStrandingAction.ReenterBatch.ToString()));
                var action = MonolithTransitionAdapter.AutoReviewStranding(facts);
                if (action == AutoReviewStrandingAction.Leave) continue;
                // A batch can form after the pass snapshot. Recheck before any
                // side effect so formation never races a recovery intervention.
                if (IsBatchMember(card.ProjectName, key, review?.AttemptId)) continue;
                var writeKey = NextWriteKey(writePrefix, interventions);
                var repeated = action == AutoReviewStrandingAction.Escalate
                    && (facts.StartedStepBefore || facts.ReenteredBatchBefore);
                var (done, deferred, detail) = repeated
                    ? (false, false, "The previous automatic recovery did not keep an owner for this card.")
                    : await InterveneAsync(card, action, review, run, ct);
                if (deferred) continue;
                var escalated = false;
                if (!done)
                {
                    var parked = await _escalation.EscalateAsync(card.Id, card.WatchPath,
                        card.ProjectName, HumanReviewEscalationCategories.Environmental,
                        $"Auto Review had no active owner for {facts.InLane.TotalMinutes:0} minutes. {detail}",
                        ct, expectedSourceState: TaskStates.AutoReview);
                    done = parked.Status == MoveJobStatus.Success;
                    escalated = done;
                }
                interventions.Add(Publish(card, writeKey, action, done, escalated, detail, now));
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "auto-review-stranding-intervention-failed task={TaskKey}", card.TaskKey);
            }
        }
        return count;
    }

    /// <summary>Immediate handoff after a stale acceptance or integration verdict.</summary>
    public bool RequestFreshReview(TaskInfo card)
    {
        if (card.State != TaskStates.AutoReview) return false;
        var projection = _authority.GetTaskProjection(CardKey(card));
        return CreateReview(card, projection.CurrentReviewAttempt, projection.CurrentRunAttempt,
            "delivery-verdict-stale");
    }

    private async Task<(bool Done, bool Deferred, string Detail)> InterveneAsync(TaskInfo card,
        AutoReviewStrandingAction action, ReviewAttemptDto? review, RunAttemptDto? run,
        CancellationToken ct)
    {
        if (action == AutoReviewStrandingAction.StartStep)
        {
            var started = TaskModes.IsReportOnly(card.Mode)
                ? _transitions.RequeueAutoReviewPostProcessing(card, "auto-review-stranding")
                : CreateReview(card, review, run, "auto-review-stranding");
            return (started, false, started ? "Started the missing review step." : "The missing review step could not start.");
        }
        if (action == AutoReviewStrandingAction.ReenterBatch && review is not null && run is not null)
        {
            var resumed = await _resume.ResumeAsync(card, "auto-review-stranding", ct);
            if (resumed.Resumed) return (true, false, "Resumed delivery integration: " + resumed.Reason + ".");
            if (resumed.Reason is AutoReviewDeliveryResumeService.Reasons.IntegrationInFlight
                or AutoReviewDeliveryResumeService.Reasons.IntegrationOwnedWait
                or "delivery-resume-backoff" or AutoReviewResumePolicy.Reasons.GateRecoveryPending)
                return (false, true, "Delivery integration is owned: " + resumed.Reason + ".");
            try
            {
                if (_settings.Get(card.ProjectName).BatchGate.Enabled
                    && review.TerminalAt is { } reviewedAt)
                {
                    await _batchPilot.EnqueueAsync(card, review, run, new DateTimeOffset(reviewedAt), ct);
                    if (_batches.ListPending().Any(item => item.ReviewAttemptId == review.AttemptId))
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
        return (false, false, "There is no completed delivery or review step to recover.");
    }

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

    private bool IsBatchMember(string project, string taskKey, string? reviewAttemptId)
    {
        if (_batches.ListPending().Any(item => item.Subject.Project == project
            && item.Subject.TaskKey == taskKey
            || item.ReviewAttemptId == reviewAttemptId)) return true;
        return _batches.ListManifests().Any(manifest =>
            manifest.Scope.Project == project
            && manifest.Members.Any(member => member.TaskKey == taskKey)
            && IsActiveManifest(manifest));
    }

    private bool IsActiveManifest(BatchGateManifest manifest)
    {
        var stateFolder = Path.Combine(_batches.BatchDirectory(manifest.BatchId), "state");
        return !Directory.Exists(stateFolder)
            || IsActiveBatchPhase(_batches.LatestState(manifest.BatchId).Phase);
    }

    internal static bool IsActiveBatchPhase(BatchPhase phase)
        => phase is BatchPhase.Formed or BatchPhase.Assembling or BatchPhase.Assembled
            or BatchPhase.Running or BatchPhase.Green or BatchPhase.Publishing;

    internal static string NextWriteKey(string prefix, IReadOnlyCollection<StallInterventionRecord> interventions)
        => prefix + interventions.Count(item => item.WriteKey.StartsWith(prefix, StringComparison.Ordinal));

    private StallInterventionRecord Publish(TaskInfo card, string writeKey, AutoReviewStrandingAction action,
        bool succeeded, bool escalated, string detail, DateTime now)
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
            Done = detail, Why = "No running step, review, batch gate entry, or queued round owned the card.",
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
