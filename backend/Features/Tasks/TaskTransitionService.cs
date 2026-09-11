

namespace AgentStudio.Tasks;

/// <summary>
/// Application-owned job state transitions that need side effects around the
/// raw folder move. Manual API moves and automatic runner completion both use
/// this service so lifecycle policy stays in code, not in agent prompts.
/// </summary>
public sealed class TaskTransitionService
{
    internal const string ResultScaffoldMarker = "<!-- agent-studio:result-scaffold -->";
    private const string OperatorBackfillMarker = "<!-- agent-studio:operator-result-backfill -->";
    private static readonly HashSet<string> ResultRequiredStates = new(StringComparer.Ordinal)
    {
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<TaskTransitionService> _logger;
    private readonly TaskSessionLog? _sessions;
    private readonly CompletedPushQueue? _pushQueue;
    private readonly AgentStudio.Drift.DriftPostStepRunner? _driftRunner;
    private readonly CliRouter? _cliRouter;
    private readonly IAutoReviewPostProcessingQueue? _autoReviewQueue;
    private readonly TaskProvenanceService? _provenance;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;
    private readonly TaskIntegrationStatusService? _integrationStatus;
    private readonly TimelineLog? _timeline;
    private readonly OperatorReviewRequeueService? _operatorReviewRequeue;
    private readonly AgentStudio.Pipeline.PipelineExecutionLog? _pipelineLog;
    private readonly AttemptAuthorityService? _attemptAuthority;
    private readonly ReviewAttemptTaskLifecycleService? _reviewAttemptLifecycle;
    private readonly ResultVersionStore? _resultVersions;
    private long _resultScaffoldCreatedCount;

    /// <summary>
    /// Process-local recurrence counter for honest result scaffolds. Each
    /// successful creation is also written to the structured application log
    /// with its provenance so operators can spot a transport regression.
    /// Refreshing an already-owned scaffold does not increment the counter.
    /// </summary>
    public long ResultScaffoldCreatedCount => Interlocked.Read(ref _resultScaffoldCreatedCount);

    /// <summary>
    /// Fires after a successful folder move with the resolved project name,
    /// the job id, the source state (the lane the job was in before the move),
    /// and the target state. Subscribers must be cheap and side-effect-only;
    /// the move itself is already on disk by the time this fires. The
    /// load-bearing subscriber is the runner-active-state clearer wired in
    /// <c>Program.cs</c>: when the moved job was the active one for that
    /// project, the runner's in-memory <c>_activeJobId</c> is reconciled
    /// atomically so the next pickup tick is unblocked.
    /// </summary>
    public event Action<string, string, string, string>? OnJobMoved;

    public TaskTransitionService(
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<TaskTransitionService> logger,
        TaskSessionLog? sessions = null,
        CompletedPushQueue? pushQueue = null,
        AgentStudio.Drift.DriftPostStepRunner? driftRunner = null,
        CliRouter? cliRouter = null,
        IAutoReviewPostProcessingQueue? autoReviewQueue = null,
        TaskProvenanceService? provenance = null,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null,
        TaskIntegrationStatusService? integrationStatus = null,
        TimelineLog? timeline = null,
        OperatorReviewRequeueService? operatorReviewRequeue = null,
        AgentStudio.Pipeline.PipelineExecutionLog? pipelineLog = null,
        AttemptAuthorityService? attemptAuthority = null,
        ReviewAttemptTaskLifecycleService? reviewAttemptLifecycle = null,
        ResultVersionStore? resultVersions = null)
    {
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
        _sessions = sessions;
        _pushQueue = pushQueue;
        _driftRunner = driftRunner;
        _cliRouter = cliRouter;
        _autoReviewQueue = autoReviewQueue;
        _provenance = provenance;
        _bus = bus;
        _integrationStatus = integrationStatus;
        _timeline = timeline;
        _operatorReviewRequeue = operatorReviewRequeue;
        _pipelineLog = pipelineLog;
        _attemptAuthority = attemptAuthority;
        _reviewAttemptLifecycle = reviewAttemptLifecycle;
        _resultVersions = resultVersions;
    }

    /// <summary>
    /// Moves a job between states. When auto-commit is enabled and the
    /// transition is <c>3-progress -> 4-auto-review</c>, commits the target
    /// working tree first and stamps the produced SHA onto the moved job.
    /// (Post-ADR-0025 the lane is 4-auto-review; the orchestrator's
    /// review-decision pass then decides whether to promote to
    /// 5-human-review.)
    /// </summary>
    /// <param name="transitionCause">
    /// Why the lane changes, one of <see cref="LaneChangeCauses"/>; stamped onto
    /// the <c>lane_changed</c> ledger row (see <see cref="TaskStateMachine.MoveJob"/>).
    /// </param>
    /// <param name="transitionDetail">Short qualifier for <paramref name="transitionCause"/>.</param>
    public async Task<MoveJobOutcome> MoveAsync(
        string jobId,
        string targetState,
        string? watchPath,
        CancellationToken ct = default,
        int? targetIndex = null,
        string? cause = null,
        string? reason = null,
        AttemptWriteReference? authorityWrite = null,
        bool suppressProductExecution = false,
        string? expectedSourceState = null,
        bool suppressIntegrationTrigger = false,
        bool operatorOverride = false,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
        if (operatorOverride && targetState != TaskStates.Completed)
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                "operatorOverride is valid only for a move to 6-completed.");
        }

        var settledRunRecovery = PrepareSettledRunRecovery(info, targetState, cause);
        if (settledRunRecovery.Error is not null)
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                settledRunRecovery.Error,
                info.FolderPath);
        }
        if (settledRunRecovery.Run is not null)
        {
            targetState = TaskStates.AutoReview;
            authorityWrite = new AttemptWriteReference(
                settledRunRecovery.Run.AttemptId,
                settledRunRecovery.Run.LastFence,
                settledRunRecovery.Run.AuthorityEpoch,
                $"settled-run-recovery:{settledRunRecovery.Run.AttemptId}");
            reason = $"Recovered completed RunAttempt {settledRunRecovery.Run.AttemptId} from its immutable result envelope; requeue was suppressed.";
            // The requested requeue became a delivery hand-off: the ledger names
            // the hand-off, not the cause the caller had in mind for the requeue.
            transitionCause = LaneChangeCauses.Delivered;
            transitionDetail = "settled-run-recovery";
        }

        var settings = _settings.Get(info.ProjectName);
        var autoPushStrategy = AutoPushStrategies.Normalize(settings.AutoPushStrategy);

        // Result is a transition invariant, not a runner or endpoint concern.
        // Materialize the fallback before the folder move so it travels with the
        // task atomically. If the task store is not writable, refuse the move:
        // landing a review/terminal card without a Result is not a valid state.
        if (ResultRequiredStates.Contains(targetState)
            && !TryEnsureResultDocument(
                info,
                targetState,
                operatorBackfill: false,
                refreshOwnedScaffold: true,
                DateTime.UtcNow,
                integrationReferenceOverride: null,
                out var resultError))
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                $"Cannot move task to {targetState} because status.md could not be ensured: {resultError}");
        }

        // Read-only modes (planning / research) skip every git side effect on the
        // transition: no auto-commit, no immediate push, no commit-attribution,
        // no completed-push. Such a run produces only a report; the runner
        // reports any stray working-tree diff as a containment violation rather
        // than committing it. See TaskModes / ParallelSlotPolicy and ADR-0052.
        var isReadOnly = TaskModes.IsReadOnly(info.Mode);
        var integrationRequired = AcceptanceIntegrationPolicy.IsIntegrationRequired(info);

        // Auto-commit is a per-project operator setting. Read it from the live
        // ProjectSettingsService cache for every transition so a UI toggle
        // affects the next move without a backend restart. TryAutoCommitAsync
        // still self-gates on a clean tree and scopes dirty paths to this task.
        var shouldAutoCommit =
            !suppressProductExecution &&
            settledRunRecovery.Run is null &&
            !isReadOnly &&
            info.State == TaskStates.Progress &&
            targetState == TaskStates.AutoReview &&
            settings.AutoCommit;

        TaskCommitInfo? commitToStamp = null;
        if (shouldAutoCommit)
        {
            commitToStamp = await TryAutoCommitAsync(jobId, watchPath, ct);
            if (commitToStamp != null
                && autoPushStrategy == AutoPushStrategies.AlwaysImmediate
                && _pushQueue == null)
            {
                // Compatibility fallback for isolated fixtures. Production always
                // supplies the queue so network work never blocks this transition.
                await TryPushCommitAsync(commitToStamp.Sha, info.WatchPath, info.ProjectName, jobId, "auto-commit", ct);
            }
        }

        var fromState = info.State;
        var projectName = info.ProjectName;

        // Human acceptance is a quality decision, never an integration trigger.
        // Coding deliveries must already be present in the effective integration
        // branch. This check retains current-attempt lineage validation and
        // refuses a stale, failed, or pending delivery without invoking Git.
        if (!suppressIntegrationTrigger
            && !suppressProductExecution
            && integrationRequired
            && !operatorOverride
            && fromState == TaskStates.HumanReview
            && targetState == TaskStates.Completed)
        {
            var integrationAdmission = ValidateIntegratedAcceptance(info, settings);
            if (integrationAdmission is not null) return integrationAdmission;
        }

        if (fromState == TaskStates.Escalated
            && targetState == TaskStates.Completed
            && !CanCompleteEscalatedJob(info, settings))
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                $"Escalated tasks can only be accepted after their latest task commit is integrated into "
                + $"{ResolveIntegrationBranch(info, settings)}.");
        }

        ReleaseCliOutputResourcesBeforeMove(info);
        MoveJobOutcome MoveCore() => _states.MoveJob(
                jobId,
                targetState,
                watchPath,
                cause,
                authorityWrite,
                expectedSourceState,
                reason,
                transitionCause,
                transitionDetail);
        var outcome = _reviewAttemptLifecycle is not null
                      && targetState is TaskStates.Completed or TaskStates.Archive
            ? _reviewAttemptLifecycle.ExecuteTerminalTransition(info, targetState, MoveCore)
            : MoveCore();
        var operatorRequeue = outcome.Status == MoveJobStatus.Success
            && OperatorReviewRequeueService.IsOperatorRequeue(fromState, targetState, cause);
        var supersedeFailedDelivery = operatorRequeue && HasFailedIntegrationRound(
            outcome.NewFolderPath ?? info.FolderPath);
        if (operatorRequeue && _operatorReviewRequeue != null)
        {
            var movedFolder = outcome.NewFolderPath
                ?? _scanner.FindJob(jobId, watchPath)?.FolderPath
                ?? info.FolderPath;
            _operatorReviewRequeue.Apply(
                movedFolder,
                jobId,
                projectName,
                fromState,
                targetState,
                reason,
                cause!);
            _scanner.InvalidateCache();
        }
        if (supersedeFailedDelivery)
        {
            var movedFolder = outcome.NewFolderPath
                ?? _scanner.FindJob(jobId, watchPath)?.FolderPath
                ?? info.FolderPath;
            var supersession = _mutations.SupersedeCurrentDeliveryOnFolder(
                movedFolder,
                TaskCommitSupersession.PendingAttempt);
            if (!supersession.Succeeded)
            {
                _logger.LogError(
                    "integration-requeue commit supersession failed project={Project} job={JobId} folder={Folder}",
                    projectName,
                    jobId,
                    movedFolder);
            }
            else if (supersession.MarkedCommits > 0)
            {
                _logger.LogInformation(
                    "integration-requeue superseded commits project={Project} job={JobId} count={Count}",
                    projectName,
                    jobId,
                    supersession.MarkedCommits);
            }
        }
        if (outcome.Status == MoveJobStatus.Success && commitToStamp != null)
        {
            var moved = _scanner.FindJob(jobId, watchPath);
            if (moved != null)
            {
                _mutations.SetJobCommitOnFolder(moved.FolderPath, commitToStamp);
                if (autoPushStrategy == AutoPushStrategies.AlwaysImmediate && _pushQueue != null)
                {
                    var stamped = _scanner.FindJob(jobId, watchPath) ?? moved;
                    if (!_pushQueue.Enqueue(new CompletedPushRequest(
                            stamped,
                            autoPushStrategy,
                            RequireCompletedState: false)))
                    {
                        _logger.LogWarning(
                            "Immediate auto-push enqueue failed for {JobId}; completion backstop will retry",
                            jobId);
                    }
                }
            }
        }

        if (outcome.Status == MoveJobStatus.Success && ResultRequiredStates.Contains(targetState))
        {
            // Rebuild only an application-owned scaffold after the move. The
            // target-lane TaskInfo lets the accepted-card integration projection
            // contribute its computed status. A real generated status.md is
            // never overwritten.
            var moved = _scanner.FindJob(jobId, watchPath);
            if (moved != null
                && !TryEnsureResultDocument(
                    moved,
                    targetState,
                    operatorBackfill: false,
                    refreshOwnedScaffold: true,
                    DateTime.UtcNow,
                    integrationReferenceOverride: null,
                    out var refreshError))
            {
                // The pre-move scaffold already preserves the invariant. Keep the
                // landed transition, but make the failed enrichment visible.
                _logger.LogError(
                    "result-scaffold-refresh-failed project={Project} job={JobId} state={State} error={Error}",
                    moved.ProjectName,
                    moved.Id,
                    targetState,
                    refreshError);
            }
        }

        if (outcome.Status == MoveJobStatus.Success
            && string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal)
            && !string.Equals(targetState, TaskStates.AutoReview, StringComparison.Ordinal))
        {
            var terminalFolder = outcome.NewFolderPath ?? info.FolderPath;
            var completed = targetState is TaskStates.HumanReview or TaskStates.Completed;
            PostProcessingLifecycleStore.Terminalize(
                terminalFolder,
                DateTime.UtcNow,
                failed: !completed,
                detail: completed
                    ? $"Post Processing completed and moved the task to {targetState}."
                    : $"Post Processing stopped and moved the task to {targetState}.",
                _logger);
        }

        // Deterministic commit-attribution post-step (ADR "Commit-Attribution-Regel").
        // Runs on every 3-progress -> 4-auto-review transition, after the
        // auto-commit stamp, so the task's commit set is pinned to its own
        // work and noise that landed in its run windows (crash-recovery for
        // another task, update-stable bumps, merges) is excluded before the
        // review lane renders it. No LLM, no tokens; same git + windows in,
        // same result out, so re-running is a no-op.
        if (outcome.Status == MoveJobStatus.Success
            && targetState == TaskStates.AutoReview
            && (info.State == TaskStates.Progress || operatorRequeue)
            && (!suppressProductExecution || settledRunRecovery.Run is not null))
        {
            var attributed = _scanner.FindJob(jobId, watchPath);
            if (attributed != null) EnterPostProcessingPhase(attributed);
            // Commit-attribution is a git step; skip it for read-only runs (they
            // produce no commits to attribute). Drift below is not a git step and
            // self-gates, so it still runs.
            if (attributed != null && !isReadOnly && info.State == TaskStates.Progress)
                RunCommitAttribution(attributed, watchPath);

            // DRIFT Nachtrag: fire the enabled drift dimensions as automatic
            // post-steps once the task has settled into auto-review. Fire-and-
            // forget and fully guarded - a drift failure (or the absence of any
            // enabled dimension; the runner self-gates default-OFF) must never
            // affect the lane transition that already completed above.
            if (attributed != null && _driftRunner != null)
            {
                TriggerDriftPostSteps(
                    attributed,
                    AgentStudio.Pipeline.PipelineTypeSettings.ForTask(settings, attributed)!);
            }

            if (attributed != null)
            {
                EnqueueAutoReviewPostProcessing(attributed);
            }
        }

        if (outcome.Status == MoveJobStatus.Success && settledRunRecovery.Run is not null)
        {
            var recovered = _scanner.FindJob(jobId, watchPath);
            if (recovered is not null)
            {
                _timeline?.Append(
                    recovered.FolderPath,
                    TimelineEventKinds.SettledRunRecovered,
                    TimelineActors.System,
                    $"Recovered completed run {settledRunRecovery.Run.AttemptId}; no replacement run was queued.",
                    runId: settledRunRecovery.Run.AttemptId,
                    details: new Dictionary<string, string>
                    {
                        ["requestedTarget"] = TaskStates.Ready,
                        ["recoveryTarget"] = TaskStates.AutoReview,
                        ["trigger"] = string.IsNullOrWhiteSpace(cause) ? TimelineActors.System : cause,
                        ["resultSha"] = settledRunRecovery.Run.ResultSha!,
                        ["resultEnvelopeDigest"] = settledRunRecovery.Run.ResultEnvelopeDigest!,
                    });
            }
        }

        // Drag-and-drop carries a desired slot in the target lane. Without
        // it the moved folder keeps its source-lane order value, so the
        // card snaps to whatever position that value sorts to in the new
        // lane - not where the user dropped it. Apply the slot after the
        // folder is on disk so the lane scan sees the moved job in its
        // new state when it rewrites every sibling's order field.
        if (outcome.Status == MoveJobStatus.Success && targetIndex.HasValue && fromState != targetState)
        {
            _states.SetOrderInLane(jobId, watchPath, targetIndex.Value);
        }

        if (outcome.Status == MoveJobStatus.Success && fromState != targetState)
        {
            if (TaskModes.IsConcept(info.Mode)
                && fromState == TaskStates.HumanReview
                && targetState == TaskStates.Completed)
            {
                RecordConceptSightReviewCompletion(info, watchPath, cause);
            }

            if (fromState == TaskStates.HumanReview && targetState == TaskStates.Completed)
            {
                var accepted = _scanner.FindJob(jobId, watchPath);
                if (accepted != null)
                {
                    if (operatorOverride)
                        RecordOperatorIntegrationOverride(accepted, cause, reason);
                    else
                        ClearAcceptanceIntegrationMarkers(accepted);
                }
            }

            // ASS-1724: the ONE commit-provenance recording hook. Anchor the
            // task/<id> tip + integration head at this lane crossing so the board
            // can graph "where does this work live" historically. Best-effort and
            // fully guarded inside the service - it runs after the move has landed,
            // so it can never undo the transition. Re-find post-move so the record
            // is written to the folder's new location with fresh provenance.
            if (_provenance != null && !suppressProductExecution)
            {
                var anchored = _scanner.FindJob(jobId, watchPath);
                if (anchored != null) _provenance.RecordTransition(anchored, targetState);
            }

            if (targetState == TaskStates.Completed && autoPushStrategy != AutoPushStrategies.Never && !isReadOnly)
            {
                var moved = _scanner.FindJob(jobId, watchPath);
                if (moved != null)
                {
                    // Offload the network push (git fetch + git push, ~2-3 s)
                    // to the background worker so the move-to-complete request
                    // returns immediately. The SHAs are immutable, so the
                    // deferred push is always still correct; the periodic
                    // backstop covers anything dropped on shutdown. When no
                    // queue is wired (unit-test fixtures) we fall back to the
                    // synchronous push so the behaviour is still exercised.
                    if (_pushQueue != null)
                        _pushQueue.Enqueue(new CompletedPushRequest(moved, autoPushStrategy));
                    else
                        await PushCompletedJobCommitsAsync(moved, autoPushStrategy, ct);
                }
            }

            try
            {
                OnJobMoved?.Invoke(projectName, jobId, fromState, targetState);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnJobMoved subscriber threw for {JobId} ({From} -> {To})", jobId, fromState, targetState);
            }
        }

        return outcome;
    }

    private bool HasFailedIntegrationRound(string folderPath)
    {
        if (_pipelineLog is null) return false;
        try
        {
            var step = _pipelineLog.Read(folderPath)?.Steps.LastOrDefault(candidate =>
                string.Equals(
                    candidate.StepId,
                    AgentStudio.Pipeline.PipelineCatalogue.MergeIntoDevelopStepId,
                    StringComparison.Ordinal));
            return step is not null
                && AgentStudio.Pipeline.AcceptedIntegrationFailurePolicy.Classify(
                    step.Status,
                    step.Verdict,
                    step.Reason,
                    step.VerdictSummary,
                    step.FailureCode) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "integration-requeue could not inspect the prior integration round folder={Folder}",
                folderPath);
            return false;
        }
    }

    /// <summary>
    /// BP-09 guard for every Progress-to-Ready requeue, regardless of whether
    /// the caller is an operator, a periodic sweep, or a liveness detector.
    /// Authority, not lane residue, decides whether the coding run already
    /// completed. A valid immutable envelope is driven forward through the
    /// idempotent review handoff instead of allowing a second coding attempt.
    /// </summary>
    private SettledRunRecoveryPreparation PrepareSettledRunRecovery(
        TaskInfo info,
        string targetState,
        string? trigger)
    {
        if (_attemptAuthority is null
            || !string.Equals(info.State, TaskStates.Progress, StringComparison.Ordinal)
            || !string.Equals(targetState, TaskStates.Ready, StringComparison.Ordinal))
        {
            return SettledRunRecoveryPreparation.None;
        }

        var taskKey = !string.IsNullOrWhiteSpace(info.Key)
            ? info.Key
            : !string.IsNullOrWhiteSpace(info.TaskKey)
                ? info.TaskKey
                : info.Id;
        var run = _attemptAuthority.GetTaskProjection(taskKey).CurrentRunAttempt;
        if (run is not { State: AttemptLifecycleState.Completed, ResultEnvelope: not null })
            return SettledRunRecoveryPreparation.None;
        if (!IsRecoverableSettledRun(run))
        {
            return new SettledRunRecoveryPreparation(
                null,
                $"Completed run {run.AttemptId} has a result envelope, so requeue is forbidden, but the immutable envelope or its digest is invalid.");
        }

        try
        {
            if (!TaskModes.IsReportOnly(info.Mode))
            {
                var requirementsPath = Path.Combine(info.FolderPath, "prompt.md");
                var requirements = File.Exists(requirementsPath)
                    ? File.ReadAllText(requirementsPath)
                    : info.Id;
                var envelope = run!.ResultEnvelope!;
                var review = _attemptAuthority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                    taskKey,
                    run.RepositoryId,
                    run.ResultSha!,
                    run.AttemptId,
                    AttemptAuthorityService.Hash(requirements),
                    AttemptAuthorityService.Hash("remote-review-policy:v1"),
                    run.EvidenceDigests,
                    $"review-subject:{run.AttemptId}:{run.ResultSha}",
                    RepositoryUrl: envelope.RepositoryUrl,
                    ResultRef: envelope.ImmutableRemoteRef));
                if (!review.Accepted)
                {
                    return new SettledRunRecoveryPreparation(
                        null,
                        $"Completed run {run.AttemptId} has an immutable result envelope, so requeue is forbidden, but review recovery failed: {review.Status} {review.Message}");
                }

                var existing = AgentStudio.Pipeline.ReviewSubjectStore.Read(info.FolderPath);
                if (existing is null
                    || !string.Equals(existing.ResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.ImmutableResultRef, envelope.ImmutableRemoteRef, StringComparison.Ordinal))
                {
                    AgentStudio.Pipeline.ReviewSubjectStore.Write(
                        info.FolderPath,
                        new AgentStudio.Pipeline.ReviewSubjectRecord
                        {
                            TaskKey = taskKey,
                            RunAttemptId = run.AttemptId,
                            Project = info.ProjectName,
                            Repository = envelope.RepositoryUrl ?? string.Empty,
                            ResultSha = run.ResultSha!,
                            AttemptChainId = run.Lease?.LeaseId ?? run.AttemptId,
                            Executor = run.Lease?.ExecutorId ?? "authority-recovery",
                            LeaseId = run.Lease?.LeaseId ?? run.AttemptId,
                            FencingToken = run.LastFence,
                            ImmutableResultRef = envelope.ImmutableRemoteRef,
                            ResultRef = envelope.ImmutableRemoteRef,
                            IntegrationBranch = existing?.IntegrationBranch,
                            CompletedAtUtc = run.TerminalAt ?? DateTimeOffset.UtcNow,
                        });
                }
            }

            _logger.LogWarning(
                "settled-run-requeue-suppressed project={Project} task={TaskKey} attempt={AttemptId} resultSha={ResultSha} trigger={Trigger}",
                info.ProjectName,
                taskKey,
                run!.AttemptId,
                run.ResultSha,
                string.IsNullOrWhiteSpace(trigger) ? TimelineActors.System : trigger);
            return new SettledRunRecoveryPreparation(run, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "settled-run-recovery-failed project={Project} task={TaskKey} attempt={AttemptId}",
                info.ProjectName,
                taskKey,
                run!.AttemptId);
            return new SettledRunRecoveryPreparation(
                null,
                $"Completed run {run.AttemptId} has an immutable result envelope, so requeue is forbidden, but its recovery handoff failed: {ex.Message}");
        }
    }

    private static bool IsRecoverableSettledRun(RunAttemptDto? run)
    {
        if (run is not
            {
                State: AttemptLifecycleState.Completed,
                ResultEnvelope: not null,
                ResultEnvelopeDigest: not null,
                ResultSha: not null,
            })
        {
            return false;
        }

        try
        {
            AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Validate(run.ResultEnvelope);
            return string.Equals(run.ResultEnvelope.SourceRunAttemptId, run.AttemptId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(run.ResultEnvelope.RepositoryId, run.RepositoryId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(run.ResultEnvelope.ResultSha, run.ResultSha, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(run.ResultEnvelope),
                    run.ResultEnvelopeDigest,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private sealed record SettledRunRecoveryPreparation(RunAttemptDto? Run, string? Error)
    {
        public static SettledRunRecoveryPreparation None { get; } = new(null, null);
    }

    /// <summary>
    /// One-time, idempotent operator repair for legacy accepted cards whose
    /// Result tab is empty. Existing non-empty status documents are preserved.
    /// The startup caller invokes this once per process; subsequent boots are
    /// no-ops because every repaired card now owns a marked status.md.
    /// </summary>
    public ResultDocumentBackfillOutcome BackfillMissingResultDocuments(DateTime? nowUtc = null)
    {
        var repaired = 0;
        var failures = new List<string>();
        var missing = new List<TaskInfo>();
        var candidates = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => task.State is
                TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive)
            .OrderBy(task => task.EnteredLaneAt)
            .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var at = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();

        foreach (var task in candidates)
        {
            var path = Path.Combine(task.FolderPath, "status.md");
            try
            {
                if (File.Exists(path) && !string.IsNullOrWhiteSpace(File.ReadAllText(path)))
                    continue;
            }
            catch (Exception ex)
            {
                failures.Add($"{task.TaskKey}: {ex.Message}");
                continue;
            }

            missing.Add(task);
        }

        var integrationByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_integrationStatus != null && missing.Count > 0)
        {
            try
            {
                foreach (var (key, status) in _integrationStatus.BuildLookup(missing))
                    integrationByKey[key] = FormatIntegrationReference(status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "result-document-backfill integration batch failed");
            }
        }

        foreach (var task in missing)
        {
            var integration = integrationByKey.GetValueOrDefault(
                task.TaskKey,
                _integrationStatus == null
                    ? "Not computed (integration projection unavailable)"
                    : "Unknown (integration projection unavailable for this card)");
            if (TryEnsureResultDocument(
                    task,
                    task.State,
                    operatorBackfill: true,
                    refreshOwnedScaffold: false,
                    at,
                    integration,
                    out var error))
            {
                repaired++;
            }
            else
            {
                failures.Add($"{task.TaskKey}: {error}");
            }
        }

        if (repaired > 0)
        {
            _logger.LogInformation(
                "result-document-backfill repaired={Repaired} scanned={Scanned}",
                repaired,
                candidates.Count);
        }
        foreach (var failure in failures)
            _logger.LogWarning("result-document-backfill-failed task={Failure}", failure);

        return new ResultDocumentBackfillOutcome(candidates.Count, repaired, failures);
    }

    private bool TryEnsureResultDocument(
        TaskInfo task,
        string targetState,
        bool operatorBackfill,
        bool refreshOwnedScaffold,
        DateTime atUtc,
        string? integrationReferenceOverride,
        out string? error)
    {
        var path = Path.Combine(task.FolderPath, "status.md");
        try
        {
            var refreshingOwnedScaffold = false;
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (!ResultScaffoldPolicy.ShouldWrite(existing, refreshOwnedScaffold))
                {
                    error = null;
                    return true;
                }
                refreshingOwnedScaffold = !string.IsNullOrWhiteSpace(existing)
                    && existing.Contains(ResultScaffoldMarker, StringComparison.Ordinal);
            }

            Directory.CreateDirectory(task.FolderPath);
            var scaffold = BuildResultScaffold(
                    task,
                    targetState,
                    operatorBackfill,
                    atUtc,
                    integrationReferenceOverride);
            if (_resultVersions is not null)
            {
                _resultVersions.Replace(
                    task.FolderPath,
                    scaffold,
                    ResultProducer.Scaffold(),
                    targetState,
                    atUtc,
                    TimelineActors.System);
            }
            else
            {
                File.WriteAllText(
                    path,
                    scaffold,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            if (refreshingOwnedScaffold)
            {
                _logger.LogDebug(
                    "result-scaffold-refreshed project={Project} job={JobId} state={State}",
                    task.ProjectName,
                    task.Id,
                    targetState);
            }
            else
            {
                var provenance = operatorBackfill
                    ? "operator-backfill"
                    : string.Equals(task.State, targetState, StringComparison.Ordinal)
                        ? "transition-landed"
                        : "transition-preflight";
                var count = Interlocked.Increment(ref _resultScaffoldCreatedCount);
                _logger.LogWarning(
                    "result-scaffold-created project={Project} job={JobId} state={State} provenance={Provenance} count={Count}",
                    task.ProjectName,
                    task.Id,
                    targetState,
                    provenance,
                    count);
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "result-scaffold-write-failed project={Project} job={JobId} state={State} path={Path}",
                task.ProjectName,
                task.Id,
                targetState,
                path);
            error = ex.Message;
            return false;
        }
    }

    private string BuildResultScaffold(
        TaskInfo task,
        string targetState,
        bool operatorBackfill,
        DateTime atUtc,
        string? integrationReferenceOverride)
    {
        var landed = string.Equals(task.State, targetState, StringComparison.Ordinal);
        var result = ResolveScaffoldResult(task, targetState);
        var grade = ResolveGradeReference(task);
        var deliverables = ResolveDeliverablesReference(task);
        // Do not put a cold Git projection on the transition's preflight path.
        // Before the move, the target-lane projection is not authoritative
        // anyway. The post-move refresh and the startup backfill run against the
        // landed state and write the actual computed integration verdict.
        var integration = integrationReferenceOverride
            ?? (landed || operatorBackfill
                ? ResolveIntegrationReference(task)
                : "Pending target-lane computation");
        var provenance = operatorBackfill
            ? $"Operator backfill on {atUtc:yyyy-MM-ddTHH:mm:ssZ} from existing task artifacts."
            : landed
                ? $"Synthesized by Agent Studio after entering `{targetState}` because no generated status.md was available."
                : $"Prepared by Agent Studio for a transition into `{targetState}` because no generated status.md was available.";
        var title = SingleLine(task.Title);
        if (title.Length == 0) title = task.TaskKey;
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append(ResultScaffoldMarker).Append(nl);
        if (operatorBackfill) sb.Append(OperatorBackfillMarker).Append(nl);
        sb.Append("# Status").Append(nl).Append(nl);
        sb.Append("- Result: ").Append(result).Append(nl);
        sb.Append("- Case: ").Append(result is "Success" or "NoOp" ? "generic" : "blocked").Append(nl);
        sb.Append("- Grade: ").Append(grade).Append(nl);
        sb.Append("- Deliverables: ").Append(deliverables).Append(nl);
        sb.Append("- Integration: ").Append(integration).Append(nl);
        sb.Append("- Provenance: ").Append(provenance).Append(nl).Append(nl);
        sb.Append("## Overview").Append(nl).Append(nl);
        sb.Append("- Problem: `status.md` was missing ")
            .Append(landed ? "when" : "before")
            .Append(" task `")
            .Append(SingleLine(task.TaskKey))
            .Append(landed ? "` reached `" : "` could move into `")
            .Append(targetState)
            .Append("`.").Append(nl);
        sb.Append("- Solution: This honest scaffold exposes the recorded outcome and existing evidence for ")
            .Append(title)
            .Append(".").Append(nl).Append(nl);
        sb.Append("## What Was Done").Append(nl).Append(nl);
        sb.Append(landed ? "- The task reached `" : "- A transition is pending into `")
            .Append(targetState)
            .Append("`.").Append(nl);
        sb.Append("- Grade, deliverables, and integration facts are linked or stated above when available.")
            .Append(nl).Append(nl);
        sb.Append("## Open Items").Append(nl).Append(nl);
        sb.Append("- None recorded in this synthesized scaffold.").Append(nl).Append(nl);
        sb.Append("## Notes").Append(nl).Append(nl);
        sb.Append("- This document does not infer work that is absent from task.json and the task artifact folder.")
            .Append(nl);
        return sb.ToString();
    }

    private static string ResolveScaffoldResult(TaskInfo task, string targetState)
    {
        if (targetState == TaskStates.Escalated) return "NeedsInput";

        try
        {
            var logPath = TaskPaths.CliOutputLog(task.FolderPath);
            if (File.Exists(logPath))
            {
                var classified = TerminalRunOutcomeClassifier.TryClassifyRenderedLog(
                    File.ReadAllText(logPath));
                var result = classified?.Outcome.ProtocolResult;
                if (result is "Success" or "Failed" or "NoOp" or "Blocked" or "NeedsInput" or "Partial")
                {
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskTransitionService: scaffold outcome log read is best-effort");
            // The lane fallback below is deliberately conservative.
        }

        return targetState == TaskStates.Completed ? "Success" : "Partial";
    }

    private static string ResolveGradeReference(TaskInfo task)
    {
        var gradeTag = (task.Tags ?? [])
            .FirstOrDefault(tag =>
                tag.StartsWith("code-review:grade-", StringComparison.OrdinalIgnoreCase));
        var grade = gradeTag?["code-review:grade-".Length..].Trim().ToUpperInvariant();
        string? artifact = null;
        try
        {
            artifact = Directory.EnumerateFiles(
                    task.FolderPath,
                    "code-review-grade*.md",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFileName)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskTransitionService: scaffold grade artifact enumeration is best-effort");
            // A tag still supplies the recorded grade when enumeration fails.
        }

        if (!string.IsNullOrWhiteSpace(grade) && !string.IsNullOrWhiteSpace(artifact))
            return $"{grade} ([{artifact}]({artifact}))";
        if (!string.IsNullOrWhiteSpace(grade))
            return $"{grade} (grade artifact not found)";
        if (!string.IsNullOrWhiteSpace(artifact))
            return $"See [{artifact}]({artifact})";
        return "Not recorded";
    }

    private static string ResolveDeliverablesReference(TaskInfo task)
    {
        // Research cards deliver one primary HTML report (AGT-2417 convention);
        // the scaffold links it so the Result tab names the actual deliverable.
        var report = Path.Combine(TaskPaths.ResultsDir(task.FolderPath), "report.html");
        if (TaskModes.IsReportOnly(task.Mode) && File.Exists(report))
            return "[results/report.html](results/report.html)";

        var path = Path.Combine(TaskPaths.ResultsDir(task.FolderPath), "deliverables.md");
        return File.Exists(path)
            ? "[results/deliverables.md](results/deliverables.md)"
            : "Not recorded";
    }

    private string ResolveIntegrationReference(TaskInfo task)
    {
        if (_integrationStatus == null)
            return "Not computed (integration projection unavailable)";

        try
        {
            var lookup = _integrationStatus.BuildLookup([task]);
            if (!lookup.TryGetValue(task.TaskKey, out var status))
                return $"Not applicable in `{task.State}`";
            return FormatIntegrationReference(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "result-scaffold-integration-read-failed project={Project} job={JobId}",
                task.ProjectName,
                task.Id);
            return "Unknown (integration projection failed)";
        }
    }

    private static string FormatIntegrationReference(TaskIntegrationStatus status)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('`').Append(SingleLine(status.Status)).Append("` on `")
            .Append(SingleLine(status.IntegrationBranch)).Append('`');
        if (!string.IsNullOrWhiteSpace(status.Sha))
            sb.Append(" at `").Append(SingleLine(status.Sha)).Append('`');
        if (!string.IsNullOrWhiteSpace(status.Detail))
            sb.Append(" (").Append(SingleLine(status.Detail)).Append(')');
        return sb.ToString();
    }

    private static string SingleLine(string? value)
        => (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private MoveJobOutcome? ValidateIntegratedAcceptance(
        TaskInfo reviewed,
        ProjectSettings settings)
    {
        var reviewSubject = AgentStudio.Pipeline.ReviewSubjectStore.Read(reviewed.FolderPath);
        if (reviewSubject is not null
            && _attemptAuthority is not null
            && !AgentStudio.Pipeline.ReviewSubjectStore.TryValidateCurrentAttempt(
                reviewed.FolderPath,
                reviewSubject,
                _attemptAuthority,
                out var subjectError))
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                subjectError ?? "The review subject does not belong to the current run attempt.");
        }

        if (IsAlreadyIntegrated(reviewed)) return null;

        var integrationBranch = ResolveIntegrationBranch(reviewed, settings);
        var status = _integrationStatus?.BuildLookup([reviewed])
            .GetValueOrDefault(reviewed.TaskKey);
        var detail = string.IsNullOrWhiteSpace(status?.Detail)
            ? "The current delivery is not an ancestor of the integration branch."
            : status.Detail;
        return new MoveJobOutcome(
            MoveJobStatus.IntegrationFailed,
            $"Acceptance does not integrate deliveries. The task remains in Human Review because its current delivery is "
            + $"'{status?.Status ?? IntegrationStatuses.Pending}' on '{integrationBranch}'. {detail}",
            reviewed.FolderPath);
    }

    private void ClearAcceptanceIntegrationMarkers(TaskInfo accepted)
    {
        _mutations.SetJobPhase(accepted.FolderPath, null);
        var tags = (accepted.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count != (accepted.Tags?.Count ?? 0))
            _mutations.SetJobTags(accepted.Id, tags, accepted.WatchPath);
        TryClearAcceptanceIntegrationStatus(accepted);
    }

    private bool IsAlreadyIntegrated(TaskInfo job)
    {
        if (_integrationStatus == null) return false;
        var lookup = _integrationStatus.BuildLookup([job]);
        return lookup.TryGetValue(job.TaskKey, out var status)
               && status.Status == IntegrationStatuses.Integrated;
    }

    private string ResolveIntegrationBranch(TaskInfo job, ProjectSettings settings)
    {
        var candidate = TaskIntegrationBranch.Name(
            settings.IntegrationBranch,
            fallback: string.Empty);
        var repoRoot = _git.ResolveRepoRootForWatchPath(job.WatchPath)
            ?? (string.IsNullOrWhiteSpace(job.WatchPath) ? null : job.WatchPath);
        return string.IsNullOrWhiteSpace(repoRoot)
            ? candidate
            : _git.ResolveIntegrationBranch(repoRoot, candidate);
    }

    private void RecordOperatorIntegrationOverride(
        TaskInfo accepted,
        string? actor,
        string? reason)
    {
        var tags = (accepted.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count != (accepted.Tags?.Count ?? 0))
        {
            _mutations.SetJobTags(accepted.Id, tags, accepted.WatchPath);
            accepted = _scanner.FindJob(accepted.Id, accepted.WatchPath) ?? accepted;
        }

        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(accepted.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Skipped,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "operator-override",
            VerdictSummary = "Operator explicitly completed acceptance without integration.",
            Reason = reason,
        });
        try
        {
            AcceptanceIntegrationStatusDocument.WriteOperatorOverride(
                accepted.FolderPath,
                reason,
                now);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "acceptance-integration-status-write-failed project={Project} job={JobId} outcome=OperatorOverride",
                accepted.ProjectName,
                accepted.Id);
        }
        RecordIntegrationEvent(
            accepted,
            TimelineEventKinds.IntegrationOverridden,
            actor,
            "Operator explicitly completed acceptance without integration.",
            "OperatorOverride",
            reason);
    }

    private void TryClearAcceptanceIntegrationStatus(TaskInfo task)
    {
        try
        {
            AcceptanceIntegrationStatusDocument.Clear(task.FolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "acceptance-integration-status-clear-failed project={Project} job={JobId}",
                task.ProjectName,
                task.Id);
        }
    }

    private void RecordIntegrationEvent(
        TaskInfo job,
        string kind,
        string? actor,
        string summary,
        string outcome,
        string? detail = null)
    {
        _timeline?.Append(
            job.FolderPath,
            new TimelineEvent
            {
                Ts = DateTime.UtcNow,
                Kind = kind,
                Actor = string.IsNullOrWhiteSpace(actor) ? TimelineActors.System : actor,
                Summary = summary,
                Details = new Dictionary<string, string>
                {
                    ["outcome"] = outcome,
                    ["integrationBranch"] = ResolveIntegrationBranch(
                        job,
                        _settings.Get(job.ProjectName)),
                    ["detail"] = detail ?? string.Empty,
                },
            });
    }

    private void RecordConceptSightReviewCompletion(
        TaskInfo original,
        string? watchPath,
        string? cause)
    {
        var moved = _scanner.FindJob(original.Id, watchPath);
        var folder = moved?.FolderPath;
        if (string.IsNullOrWhiteSpace(folder)) return;

        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(folder, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.ConceptSightReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "sight-review-approved",
            VerdictSummary = "Human sight review approved the concept.",
        });
        if (!string.Equals(cause, "concept-sight-review-approved", StringComparison.Ordinal))
        {
            _pipelineLog?.RecordStep(folder, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.ConceptPromotionStepId,
                Kind = StepKind.Tool,
                Status = PipelineStepStatus.Skipped,
                StartedAt = now,
                CompletedAt = now,
                Verdict = "no-implementation",
                VerdictSummary = "Sight review completed without promoting implementation cards.",
                Reason = "The operator completed the concept without promotion.",
            });
        }
        _pipelineLog?.Complete(folder, now);
        SteerPendingMarker.Clear(folder, _logger);
    }

    private bool CanCompleteEscalatedJob(TaskInfo info, ProjectSettings settings)
    {
        // Read-only/no-code escalations have nothing to integrate; they can be
        // accepted once the operator has made the decision.
        if (TaskModes.IsReadOnly(info.Mode)) return true;
        if (info.Commits.Count == 0 && !info.CodeActivityDetected) return true;
        if (string.IsNullOrWhiteSpace(info.WatchPath) || !Directory.Exists(info.WatchPath)) return false;

        var latest = info.Commits.LastOrDefault()?.Sha ?? info.Commit?.Sha;
        if (string.IsNullOrWhiteSpace(latest)) return false;

        // The task's commits live in the project's CODE repository, which is not
        // necessarily WatchPath: in the dogfooding split WatchPath is the workspace
        // task-store (a separate git repo that does not contain the code SHA).
        // Passing WatchPath straight to IsAncestor checks the wrong repo, so the
        // ancestor probe always fails (rc=128, SHA absent) and NO escalated coding
        // task can ever be accepted. Resolve the real code repo root first.
        var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath) ?? info.WatchPath;
        var integrationBranch = _git.ResolveIntegrationBranch(
            repoRoot,
            settings.IntegrationBranch);
        return _git.IsAncestor(repoRoot, latest, integrationBranch);
    }

    private void EnterPostProcessingPhase(TaskInfo info)
    {
        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);
        try
        {
            var now = DateTime.UtcNow;
            PostProcessingLifecycleStore.BeginPostProcessing(
                info.FolderPath,
                now,
                "orchestrator-post-processing",
                "Task execution finished; orchestrator post-processing is running before human review.",
                _logger,
                replaceChecks: true);
            PostProcessingOutcomeLog.Append(info.FolderPath, new PostProcessingOutcomeRecord
            {
                At = now,
                JobId = info.Id,
                Project = info.ProjectName,
                Outcome = PostProcessingOutcomes.FindingsAdded,
                Performer = PostProcessingPerformers.Orchestrator,
                StepId = AgentStudio.Pipeline.PipelineCatalogue.GitCommitAttributionStepId,
                Summary = "Entered orchestrator post-processing after task execution.",
                EvidenceRef = "lifecycle.json"
            }, _logger);
            _logger.LogInformation(
                "post-processing-entered project={Project} job={JobId} performer={Performer}",
                info.ProjectName, info.Id, PostProcessingPerformers.Orchestrator);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write post-processing lifecycle evidence for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Per-item-atomic batch move. Each item independently routes through
    /// <see cref="MoveAsync"/>, so a failure on one item never rolls back
    /// items that already moved. The result list mirrors the input order
    /// one-for-one with a typed per-item status (<c>moved</c> / <c>not-found</c>
    /// / <c>conflict</c> / <c>rejected</c> / <c>failed</c>). This is the
    /// endpoint backing <c>POST /api/tasks/batch-move</c> and exists so the
    /// LLM never has to fall back to shell <c>mv</c> for a multi-job
    /// restore - the 2026-05-08 incident that motivated this method.
    /// </summary>
    public async Task<IReadOnlyList<BatchMoveItemResult>> BatchMoveAsync(
        IEnumerable<BatchMoveItem> items,
        CancellationToken ct = default,
        string? cause = null)
    {
        var results = new List<BatchMoveItemResult>();
        foreach (var item in items)
        {
            results.Add(await MoveBatchItemAsync(item, ct, cause).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// Executes one independently reportable item from a batch. Both the
    /// compatibility <see cref="BatchMoveAsync"/> loop and the background-job
    /// worker use this boundary so validation, outcome mapping and failure
    /// isolation cannot drift between synchronous service callers and HTTP
    /// jobs.
    /// </summary>
    public async Task<BatchMoveItemResult> MoveBatchItemAsync(
        BatchMoveItem item,
        CancellationToken ct = default,
        string? cause = null)
    {
        if (string.IsNullOrWhiteSpace(item.JobId))
        {
            return new BatchMoveItemResult
            {
                JobId = item.JobId ?? "",
                Status = "rejected",
                Message = "jobId is required"
            };
        }

        if (!TaskStates.All.Contains(item.TargetState))
        {
            var msg = TaskStates.NumberedLegacyMap.TryGetValue(item.TargetState, out var renamed)
                ? $"Lane '{item.TargetState}' was renamed in ADR-0025. Use '{renamed}' instead."
                : $"Invalid state. Allowed: {string.Join(", ", TaskStates.All)}";
            return new BatchMoveItemResult
            {
                JobId = item.JobId,
                Status = "rejected",
                Message = msg
            };
        }

        MoveJobOutcome outcome;
        try
        {
            outcome = await MoveAsync(
                item.JobId,
                item.TargetState,
                item.WatchPath,
                ct,
                item.TargetIndex,
                cause: string.IsNullOrWhiteSpace(cause) ? TimelineActors.Human("") : cause,
                reason: item.Reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch move item failed for {JobId} -> {Target}", item.JobId, item.TargetState);
            return new BatchMoveItemResult
            {
                JobId = item.JobId,
                Status = "failed",
                Message = ex.Message
            };
        }

        return outcome.Status switch
        {
            MoveJobStatus.Success =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "moved" },
            MoveJobStatus.NotFound =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "not-found" },
            MoveJobStatus.TargetFolderExists =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "conflict", Message = outcome.Message },
            MoveJobStatus.DirectoryLocked =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "locked", Message = outcome.Message },
            MoveJobStatus.IntegrationFailed =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "integration-failed", Message = outcome.Message },
            _ =>
                new BatchMoveItemResult { JobId = item.JobId, Status = "failed", Message = outcome.Message }
        };
    }

    private void ReleaseCliOutputResourcesBeforeMove(TaskInfo info)
    {
        if (_cliRouter == null) return;
        try
        {
            _cliRouter.Get(info.CliType).ReleaseOutputResources(info.TaskKey);
            _logger.LogDebug(
                "task-transition-released-cli-output jobId={JobId} cliType={CliType} target={TaskKey}",
                info.Id, info.CliType, info.TaskKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "task-transition-release-cli-output-failed jobId={JobId} cliType={CliType}",
                info.Id, info.CliType);
        }
    }

    /// <summary>
    /// Deterministic commit-attribution post-step. Gathers the commits in the
    /// task's run SHA-windows (plus the platform auto-commit), runs the pure
    /// <see cref="CommitAttributionService"/> rule engine, and persists the
    /// attributed chain + exclusions via <see cref="TaskMutationService"/>
    /// (never a direct file write). Best-effort: any failure is logged and
    /// swallowed so attribution never blocks the lane transition.
    /// </summary>
    private void RunCommitAttribution(TaskInfo moved, string? watchPath)
    {
        try
        {
            if (_sessions == null) return;

            // Pre-attribution candidate set: the aggregate the runner builds is
            // the raw union of run-range commits and the just-stamped
            // auto-commit - exactly the noisy input the rule engine is meant to
            // clean up.
            var result = CommitAttributionRunner.Run(moved, watchPath, _sessions, _git);
            if (result == null) return;

            _mutations.SetCommitAttributionOnFolder(moved.FolderPath, result.Attributed);
            _logger.LogInformation(
                "commit-attribution jobId={JobId} attributed={Attributed} excluded={Excluded}",
                moved.Id, result.Attributed.Count, result.Excluded.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "commit-attribution post-step failed for {JobId}", moved.Id);
        }
    }

    /// <summary>
    /// Fire-and-forget trigger for the opt-in drift post-steps (DRIFT Nachtrag).
    /// The transition has already committed on disk, so this runs on a detached
    /// task and swallows every failure: a drift run is an expensive extra pass
    /// whose outcome must never feed back into the lane move. The runner itself
    /// self-gates (default-OFF), so when no drift dimension is enabled for the
    /// project this returns almost immediately.
    /// </summary>
    private void TriggerDriftPostSteps(TaskInfo moved, ProjectSettings settings)
    {
        var runner = _driftRunner;
        if (runner == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(moved.ProjectName, moved.Id, moved.FolderPath, settings, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "drift post-step trigger failed for {JobId}", moved.Id);
            }
        });
    }

    /// <summary>
    /// Re-drives auto-review post-processing for a card the startup-recovery
    /// scan found parked in <c>4-auto-review</c> with unfinished post-processing.
    /// The queue is process-local, so a backend restart can lose its item after
    /// the durable lane transition. The downstream worker re-scans and
    /// self-gates, which makes a redundant recovery enqueue harmless.
    /// </summary>
    public bool RequeueAutoReviewPostProcessing(
        TaskInfo info,
        string source = "startup-recovery",
        DateTime? restartedAtUtc = null)
    {
        if (info.Fixture || !string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
            return false;

        var startedAt = (restartedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        if (!PostProcessingLifecycleStore.BeginPostProcessing(
                info.FolderPath,
                startedAt,
                "orchestrator-post-processing",
                "Post Processing restarted after backend recovery.",
                _logger,
                replaceChecks: true))
        {
            return false;
        }
        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);

        try
        {
            var accepted = EnqueueAutoReviewPostProcessing(info, source);
            if (!accepted)
                FailRecoveredPostProcessingAttempt(info, startedAt);
            return accepted;
        }
        catch
        {
            FailRecoveredPostProcessingAttempt(info, startedAt);
            throw;
        }
    }

    /// <summary>
    /// Repairs the durable lifecycle projection when the startup sweep finds a
    /// terminal decision outcome but an older active Post Processing phase or
    /// check survived the process restart.
    /// </summary>
    internal bool ReconcileCompletedAutoReviewPostProcessing(
        TaskInfo info,
        PostProcessingOutcomeRecord decision)
    {
        if (info.Fixture || !string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
            return false;

        var failed = !string.Equals(
            decision.Outcome,
            PostProcessingOutcomes.PassToHumanReview,
            StringComparison.Ordinal);
        var finishedAt = decision.At == default ? DateTime.UtcNow : decision.At;
        var detail = string.IsNullOrWhiteSpace(decision.Summary)
            ? $"Recovered terminal Post Processing outcome {decision.Outcome}."
            : decision.Summary;
        var updated = PostProcessingLifecycleStore.Terminalize(
            info.FolderPath,
            finishedAt,
            failed,
            detail!,
            _logger);
        if (updated)
        {
            _mutations.SetJobPhase(
                info.FolderPath,
                failed ? LifecyclePhases.PostProcessingBlocked : LifecyclePhases.AwaitingReview);
        }
        return updated;
    }

    private void FailRecoveredPostProcessingAttempt(TaskInfo info, DateTime startedAtUtc)
    {
        var finishedAt = DateTime.UtcNow < startedAtUtc ? startedAtUtc : DateTime.UtcNow;
        const string detail = "Post Processing recovery could not enqueue a replacement attempt.";
        PostProcessingLifecycleStore.Terminalize(
            info.FolderPath,
            finishedAt,
            failed: true,
            detail,
            _logger,
            onlyWhenActive: true);
        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingBlocked);
    }

    private bool EnqueueAutoReviewPostProcessing(TaskInfo moved, string source = "progress-to-auto-review")
    {
        var queue = _autoReviewQueue;
        if (queue == null) return false;

        var accepted = queue.Enqueue(new AutoReviewPostProcessingRequest(
            ProjectName: moved.ProjectName,
            JobId: moved.Id,
            WatchPath: moved.WatchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: source));

        if (accepted)
        {
            _logger.LogInformation(
                "auto-review-postprocessing-enqueued project={Project} job={JobId} source={Source}",
                moved.ProjectName, moved.Id, source);
        }
        else
        {
            _logger.LogWarning(
                "auto-review-postprocessing-enqueue-failed project={Project} job={JobId} source={Source}",
                moved.ProjectName, moved.Id, source);
        }
        return accepted;
    }

    private async Task<TaskCommitInfo?> TryAutoCommitAsync(string jobId, string? watchPath, CancellationToken ct)
    {
        try
        {
            // Pre-flight attribution scoping. In a sequential run (maxParallelism
            // ==1) the agent works in the SHARED main checkout, so the dirty tree
            // can mix this task's edits with leftover changes from an operator or
            // an earlier task that never committed. A blanket `git add -A` would
            // sweep all of them into this task's commit (the mega-blob / mis-
            // attribution bug). We classify each dirty path by mtime against the
            // task's first CLI run and commit only the ones authored during it.
            // Gated on TaskSessionLog so unit-test fixtures without it keep the
            // legacy whole-tree behavior.
            var plan = PlanAutoCommitScope(jobId, watchPath);
            if (plan.Scope == AutoCommitScope.None)
            {
                _logger.LogInformation(
                    "Auto-commit skipped for {JobId}: working-tree dirty paths predate the task's first run",
                    jobId);
                return null;
            }

            var pathspecs = plan.Scope == AutoCommitScope.Scoped ? plan.Paths : null;
            if (pathspecs != null)
                _logger.LogInformation(
                    "Auto-commit for {JobId}: scoping commit to {Count} task-attributable path(s); foreign dirty changes left untouched",
                    jobId, pathspecs.Count);

            var (result, message) = await _git.AutoCommitAsync(jobId, watchPath, ct, pathspecs);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                _logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                return null;
            }

            var files = _git.GetCommitFiles(jobId, watchPath, result.Sha);
            return new TaskCommitInfo
            {
                Sha = result.Sha,
                ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                Message = message,
                FilesChanged = files.Count,
                Files = files.Select(f => f.Path).ToList(),
                At = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-commit threw for {JobId}. Moving without a recorded SHA.", jobId);
            return null;
        }
    }

    public async Task<int> PushCompletedJobCommitsAsync(TaskInfo job, string strategy, CancellationToken ct = default)
        => await PushJobCommitsAsync(job, strategy, requireCompletedState: true, ct);

    public async Task<int> PushJobCommitsAsync(
        TaskInfo job,
        string strategy,
        bool requireCompletedState,
        CancellationToken ct = default)
    {
        if (AutoPushStrategies.Normalize(strategy) == AutoPushStrategies.Never) return 0;
        if (requireCompletedState && job.State != TaskStates.Completed) return 0;

        var commits = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];

        var pushed = 0;
        var reason = requireCompletedState ? "completed" : "auto-commit";
        foreach (var commit in commits.Where(c => !string.IsNullOrWhiteSpace(c.Sha)).OrderBy(c => c.At))
        {
            if (await TryPushCommitAsync(commit.Sha, job.WatchPath, job.ProjectName, job.Id, reason, ct))
                pushed++;
        }
        return pushed;
    }

    private enum AutoCommitScope
    {
        /// <summary>Commit the whole tree (legacy / unknown - no session window to scope against).</summary>
        All,
        /// <summary>Nothing is attributable to this task; skip the commit entirely.</summary>
        None,
        /// <summary>Commit only <see cref="AutoCommitPlan.Paths"/>.</summary>
        Scoped,
    }

    private sealed record AutoCommitPlan(AutoCommitScope Scope, IReadOnlyList<string> Paths);

    /// <summary>
    /// Classifies the project's dirty working tree against this task's first
    /// recorded CLI run so the auto-commit can stage only the task's own work.
    /// A dirty path is attributable when its last-write time is at or after the
    /// task's first session event (the agent touched it during the run) or when
    /// its timestamp cannot be read (deletion / rename / permission error - we
    /// defer to commit so a real agent-driven change is never lost). Paths whose
    /// mtime predates the run are leftover from another context (operator edit,
    /// an earlier task that never committed) and are excluded.
    ///
    /// <para>
    /// Returns <see cref="AutoCommitScope.All"/> (commit the whole tree, legacy
    /// <c>git add -A</c> path) when there is no window to scope against:
    /// <list type="bullet">
    /// <item>No <see cref="TaskSessionLog"/> is wired (legacy fixtures).</item>
    /// <item>The task has no recorded session events (first-ever pickup).</item>
    /// <item>The working tree is clean or the repo root can't be resolved -
    ///   <see cref="GitService.AutoCommitAsync"/> short-circuits on its own.</item>
    /// </list>
    /// Returns <see cref="AutoCommitScope.None"/> when every dirty path predates
    /// the run, and <see cref="AutoCommitScope.Scoped"/> with the attributable
    /// subset otherwise.
    /// </para>
    /// </summary>
    private AutoCommitPlan PlanAutoCommitScope(string jobId, string? watchPath)
    {
        var all = new AutoCommitPlan(AutoCommitScope.All, []);
        if (_sessions == null) return all;

        List<SessionEvent> events;
        try { events = _sessions.ReadSessionEvents(jobId, watchPath); }
        catch { return all; }
        if (events.Count == 0) return all;

        var firstActivityUtc = events.Min(e => e.Ts);
        if (firstActivityUtc == default) return all;

        var status = _git.GetStatus(jobId, watchPath);
        if (!status.IsRepo || status.Files.Count == 0) return all;

        var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return all;

        var scoped = new List<string>();
        foreach (var file in status.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var fullPath = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(fullPath))
                {
                    // Deletion or rename - we cannot timestamp it. Treat as
                    // attributable so a real agent-driven delete is still
                    // committed, but keep it scoped to this path; never widen
                    // back to a whole-tree `git add -A`.
                    scoped.Add(file.Path);
                    continue;
                }
                if (File.GetLastWriteTimeUtc(fullPath) >= firstActivityUtc)
                    scoped.Add(file.Path);
            }
            catch
            {
                // Unreadable mtime: defer to commit, but still scoped to the path.
                scoped.Add(file.Path);
            }
        }

        if (scoped.Count == 0) return new AutoCommitPlan(AutoCommitScope.None, []);
        return new AutoCommitPlan(AutoCommitScope.Scoped, scoped);
    }

    private async Task<bool> TryPushCommitAsync(string sha, string watchPath, string project, string jobId, string reason, CancellationToken ct)
    {
        try
        {
            var result = await _git.PushShaAsync(sha, watchPath, ct);
            if (result.Success)
            {
                _logger.LogInformation("Auto-push {Status} for {JobId} at {Sha} ({Reason})", result.Status, jobId, sha, reason);
                return result.Status == "pushed";
            }

            _logger.LogWarning("Auto-push skipped for {JobId} at {Sha} ({Reason}): {Status} {Error}",
                jobId, sha, reason, result.Status, result.Error);
            if (_bus != null)
                await _bus.EmitManagedRepoPushFailureAsync(
                    project, jobId, watchPath, "main", result.Status, result.Error, 1, ct);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-push threw for {JobId} at {Sha} ({Reason})", jobId, sha, reason);
            if (_bus != null)
                await _bus.EmitManagedRepoPushFailureAsync(
                    project, jobId, watchPath, "main", "error", ex.Message, 1, ct);
            return false;
        }
    }
}

public sealed record ResultDocumentBackfillOutcome(
    int Scanned,
    int Repaired,
    IReadOnlyList<string> Failures);
