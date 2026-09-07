using System.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>
/// Completes legacy acceptance transactions already queued by an older backend
/// process. New acceptance requests never enqueue work here. The runner owns
/// merge/gate/rollback serialization and hands a released SHA to the existing
/// <see cref="IntegrationPushWorker"/>. Successful recovery moves the card from
/// Human Review to Completed; decided failures return it to ordinary review.
/// </summary>
public sealed class AcceptedIntegrationWorker : BackgroundService
{
    private readonly AcceptedIntegrationQueue _queue;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskProvenanceService _provenance;
    private readonly ILogger<AcceptedIntegrationWorker> _logger;
    private readonly TaskTransitionService? _transitions;
    private readonly TimelineLog? _timeline;

    public AcceptedIntegrationWorker(
        AcceptedIntegrationQueue queue,
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskProvenanceService provenance,
        ILogger<AcceptedIntegrationWorker> logger,
        TaskTransitionService? transitions = null,
        TimelineLog? timeline = null)
    {
        _queue = queue;
        _runner = runner;
        _scanner = scanner;
        _mutations = mutations;
        _provenance = provenance;
        _logger = logger;
        _transitions = transitions;
        _timeline = timeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(
                ex,
                "AcceptedIntegrationWorker: graceful shutdown; queued items are recovered by the durable backstop.");
        }
    }

    /// <summary>
    /// Processes one accepted delivery. Exposed internally for deterministic
    /// worker tests without starting a hosted-service loop.
    /// </summary>
    internal async Task<MergeIntoIntegrationResult> ProcessAsync(AcceptedIntegrationRequest request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Deliberately not the host stopping token. Once merge/gate/rollback
            // starts it must reach a consistent terminal state. A process exit
            // can still interrupt it; the accepted-integration backstop then
            // reconstructs the work from the durable lane and pipeline facts.
            var result = await _runner.RunAsync(
                request.Project,
                request.JobId,
                request.JobFolderPath,
                request.WatchPath,
                request.IntegrationBranch,
                CancellationToken.None,
                request.IntegrationStrategy).ConfigureAwait(false);

            if (result.Outcome.IsSuccessfulIntegration())
            {
                await FinalizeAcceptedTaskAsync(request, result).ConfigureAwait(false);
            }
            else if (result.GateEnvironmentFailure)
            {
                KeepPendingForGateEnvironmentRetry(request, result);
            }
            else
            {
                await ReturnToReviewWithFailureAsync(request, result).ConfigureAwait(false);
            }

            sw.Stop();
            _logger.LogInformation(
                "Accepted-integration worker completed {JobId} with {Outcome} in {ElapsedMs}ms",
                request.JobId, result.Outcome, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errored = MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: ex.Message);
            await ReturnToReviewWithFailureAsync(request, errored).ConfigureAwait(false);
            _logger.LogWarning(
                ex,
                "Accepted-integration worker failed for {JobId} after {ElapsedMs}ms; the task returned to Human Review",
                request.JobId, sw.ElapsedMilliseconds);
            return errored;
        }
    }

    private async Task FinalizeAcceptedTaskAsync(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job?.State == TaskStates.HumanReview && _transitions != null)
        {
            var moved = await _transitions.MoveAsync(
                request.JobId,
                TaskStates.Completed,
                request.WatchPath,
                CancellationToken.None,
                request.CompletedLaneIndex,
                request.Cause ?? TimelineActors.System,
                request.Reason,
                expectedSourceState: TaskStates.HumanReview,
                suppressIntegrationTrigger: true,
                transitionCause: LaneChangeCauses.Accepted,
                transitionDetail: result.Outcome.ToString()).ConfigureAwait(false);
            if (moved.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "Integrated {JobId}, but finalizing Completed failed with {Status}: {Message}",
                    request.JobId,
                    moved.Status,
                    moved.Message);
                return;
            }
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
            _mutations.SetJobPhase(job.FolderPath, null);
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        CompletePendingMarker(request, result);
        if (job != null)
        {
            TryClearAcceptanceIntegrationStatus(job);
            _timeline?.Append(
                job.FolderPath,
                TimelineEventKinds.IntegrationSucceeded,
                TimelineActors.System,
                $"Integration into {request.IntegrationBranch} succeeded; acceptance completed.",
                details: new Dictionary<string, string>
                {
                    ["outcome"] = result.Outcome.ToString(),
                    ["integrationBranch"] = request.IntegrationBranch,
                });
        }
    }

    /// <summary>
    /// AGT-2720: the gate died in its own toolchain before the first test, so
    /// this attempt decided nothing. The card keeps its lane, its integrating
    /// phase, and its pending marker, which is exactly what makes the
    /// accepted-integration backstop pick it up and run the same integration
    /// again on its next sweep. Returning it to Human Review instead would ask
    /// an operator to fix a delivery no suite ever ran against - the state
    /// CAC-18 sat in for four weeks.
    /// </summary>
    private void KeepPendingForGateEnvironmentRetry(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        _logger.LogWarning(
            "accepted_integration_gate_environment project={Project} job_id={JobId} integration={Integration} reason={Reason}",
            request.Project,
            request.JobId,
            request.IntegrationBranch,
            result.Error ?? "gate environment");

        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job == null) return;
        _timeline?.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            "The gate failed in its own toolchain before the first test; the integration stays pending and retries.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
                ["failureCode"] = AcceptedIntegrationFailureCodes.GateEnvironment,
                ["detail"] = result.Error ?? string.Empty,
            });
    }

    private async Task ReturnToReviewWithFailureAsync(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job == null) return;

        var decision = AcceptanceIntegrationPolicy.Decide(
            result.Outcome,
            integrationRequired: AcceptanceIntegrationPolicy.IsIntegrationRequired(job));
        if (decision == AcceptedIntegrationLaneDecision.Complete)
        {
            await FinalizeBranchlessTaskAsync(request, job, result).ConfigureAwait(false);
            return;
        }

        if (job.State == TaskStates.Completed && _transitions != null)
        {
            var moveReason = $"Acceptance integration ended with {result.Outcome}: "
                             + (result.Error ?? "The delivery was not integrated.");
            var moved = await _transitions.MoveAsync(
                request.JobId,
                TaskStates.HumanReview,
                request.WatchPath,
                CancellationToken.None,
                cause: TimelineActors.System,
                reason: moveReason,
                expectedSourceState: TaskStates.Completed,
                suppressIntegrationTrigger: true,
                transitionCause: LaneChangeCauses.AcceptanceIntegrationFailed,
                transitionDetail: result.Outcome.ToString()).ConfigureAwait(false);
            if (moved.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "Integration failed for {JobId}, but returning to Human Review failed with {Status}: {Message}",
                    request.JobId,
                    moved.Status,
                    moved.Message);
                return;
            }
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        if (job.State != TaskStates.HumanReview) return;

        _mutations.SetJobPhase(job.FolderPath, null);
        job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        TryWriteAcceptanceIntegrationFailure(job, request, result);
        _timeline?.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Integration failed ({result.Outcome}); the task is in Human Review.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = result.Error ?? string.Empty,
            });
    }

    private async Task FinalizeBranchlessTaskAsync(
        AcceptedIntegrationRequest request,
        TaskInfo job,
        MergeIntoIntegrationResult result)
    {
        if (job.State == TaskStates.HumanReview && _transitions != null)
        {
            var moved = await _transitions.MoveAsync(
                request.JobId,
                TaskStates.Completed,
                request.WatchPath,
                CancellationToken.None,
                request.CompletedLaneIndex,
                request.Cause ?? TimelineActors.System,
                request.Reason,
                expectedSourceState: TaskStates.HumanReview,
                suppressIntegrationTrigger: true,
                transitionCause: LaneChangeCauses.Accepted,
                transitionDetail: "no-delivery-branch").ConfigureAwait(false);
            if (moved.Status != MoveJobStatus.Success) return;
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        _mutations.SetJobPhase(job.FolderPath, null);
        CompletePendingMarker(request, result);
        TryClearAcceptanceIntegrationStatus(job);
        _timeline?.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationSucceeded,
            TimelineActors.System,
            "Acceptance completed without integration because the card expects no delivery branch.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
            });
    }

    private void TryWriteAcceptanceIntegrationFailure(
        TaskInfo job,
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        try
        {
            AcceptanceIntegrationStatusDocument.WriteFailure(
                job.FolderPath,
                result.Outcome.ToString(),
                result.Error,
                request.IntegrationBranch);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "acceptance-integration-status-write-failed project={Project} job={JobId} outcome={Outcome}",
                job.ProjectName,
                job.Id,
                result.Outcome);
        }
    }

    private void TryClearAcceptanceIntegrationStatus(TaskInfo job)
    {
        try
        {
            AcceptanceIntegrationStatusDocument.Clear(job.FolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "acceptance-integration-status-clear-failed project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
        }
    }

    private void CompletePendingMarker(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job == null) return;

        if (result.Outcome.IsFreshMerge()
            && !string.IsNullOrWhiteSpace(result.MergedSha))
        {
            _provenance.RecordMerge(job, result.MergedSha);
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        var tags = (job.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count != (job.Tags?.Count ?? 0))
        {
            _mutations.SetJobTags(job.Id, tags, job.WatchPath);
        }
    }
}
