using AgentStudio.Shared;
using AgentStudio.Runner;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// The Human Review park reason for an immediate Remote integration result
/// (AGT-2995). A spent automatic recovery budget keeps its exact wording;
/// otherwise a failed integration reads its typed code, e.g.
/// <c>integration: source-needs-rebase</c>, and never falls back to the lane
/// ledger's bare <c>review-verdict: Error</c>.
/// </summary>
public static class RemoteDeliveryParkReason
{
    public const string Prefix = "integration: ";

    /// <summary>Null for a successful or deliberately deferred integration.</summary>
    public static string? For(MergeIntoIntegrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.IsNullOrWhiteSpace(result.AutomaticRecoveryDetail))
            return result.AutomaticRecoveryDetail;
        return AcceptedIntegrationFailureCodes.For(result) is { } code ? ForCode(code) : null;
    }

    public static string ForCode(string failureCode) => Prefix + failureCode;
}

/// <param name="ReviewAttemptId">The settled ReviewAttempt that earned this integration; null for a batch member.</param>
/// <param name="ReviewedResultSha">
/// The exact Result-SHA that review passed (AGT-3091). The merge runner refuses
/// a delivery that resolves to any other SHA; null keeps the unpinned merge.
/// </param>
public sealed record RemoteDeliveryIntegrationRequest(
    string Project,
    string JobId,
    string JobFolderPath,
    string? WatchPath,
    string IntegrationBranch,
    string IntegrationStrategy,
    string PipelineType,
    DateTimeOffset DeliveredAtUtc,
    string? ReviewAttemptId = null,
    string? ReviewedResultSha = null,
    bool MonolithOwnedResume = false);

/// <summary>
/// Serializes immediately eligible fenced deliveries per project in delivery
/// order, then delegates every mutation to <see cref="MergeIntoDevelopRunner"/>.
/// The caller awaits the result before moving the card to Human Review. This
/// is the merge every live Remote delivery takes, so it is also the
/// after-integration trigger of <see cref="BranchReclaimTriggerService"/>.
/// </summary>
public sealed class RemoteDeliveryIntegrationCoordinator
{
    private static readonly TimeSpan CompletedReplayRetention = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectQueue> _projectQueues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeliveryReplay> _deliveryReplays =
        new(StringComparer.Ordinal);
    private readonly Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> _integrate;
    private readonly Func<RemoteDeliveryIntegrationRequest, MergeIntoIntegrationResult, Task<IntegrationAgentRoundStartResult>> _startAgentRound;
    private readonly Action<RemoteDeliveryIntegrationRequest, string, string, string> _recordFailure;
    private readonly ILogger<RemoteDeliveryIntegrationCoordinator> _logger;
    private readonly Func<RemoteDeliveryIntegrationRequest, bool> _isCurrentReview;
    private readonly Func<RemoteDeliveryIntegrationRequest, bool> _isAdmissibleDelivery;
    private readonly Action<RemoteDeliveryIntegrationRequest> _reclaimAfterIntegration;
    private long _sequence;

    /// <summary>Error prefix of a review-driven delivery refused at dequeue because its card was reissued (AGT-3091).</summary>
    public const string ReviewedDeliverySupersededError = "reviewed-delivery-superseded";

    public RemoteDeliveryIntegrationCoordinator(
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskProvenanceService provenance,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        IntegrationAgentRoundService agentRounds,
        ILogger<RemoteDeliveryIntegrationCoordinator> logger,
        AttemptAuthorityService authority,
        GitService? git = null,
        BranchReclaimTriggerService? branchReclaim = null,
        IConfiguration? configuration = null)
        : this(
            request => IntegrateAndRecordAsync(
                request,
                runner,
                scanner,
                provenance,
                timeline,
                request.ReviewAttemptId is { } reviewAttemptId
                    ? apply => OwnsRequest(request, configuration)
                        && authority.TryApplyForCurrentReviewedDelivery(reviewAttemptId, apply)
                    : null),
            logger,
            (request, failureCode, summary, detail) => RecordPreReviewFailure(
                request,
                failureCode,
                summary,
                detail,
                pipelineLog,
                timeline),
            agentRounds.TryStartAsync,
            request => request.ReviewAttemptId is null
                || authority.GetReview(request.ReviewAttemptId) is { } review
                   && authority.GetTaskProjection(review.TaskKey).CurrentReviewAttempt?.AttemptId
                       == request.ReviewAttemptId,
            request => OwnsRequest(request, configuration)
                && IsReviewedDeliveryAdmissible(request, scanner, authority),
            request => ReclaimBranchesAfterIntegration(request, scanner, git, branchReclaim))
    {
    }

    private static bool OwnsRequest(RemoteDeliveryIntegrationRequest request, IConfiguration? configuration)
        => !request.MonolithOwnedResume || configuration is null
            || MonolithTransitionSwitch.OwnsAutoReview(configuration);

    /// <summary>
    /// A review-driven delivery is still owed only while its card sits in
    /// <c>4-auto-review</c> and the reviewed RunAttempt is still the task's
    /// current settled one. A concern or finding reissue changes neither the
    /// review generation nor the queued request, so the review check alone let
    /// AGT-2951's superseded delivery hold the gate for 11.6 minutes.
    /// </summary>
    internal static bool IsReviewedDeliveryAdmissible(
        RemoteDeliveryIntegrationRequest request,
        TaskScannerService scanner,
        AttemptAuthorityService authority)
        => request.ReviewAttemptId is not { } reviewAttemptId
           || scanner.FindJob(request.JobId, request.WatchPath) is { } card
              && string.Equals(card.State, TaskStates.AutoReview, StringComparison.Ordinal)
              && authority.IsReviewedDeliveryCurrent(reviewAttemptId);

    internal RemoteDeliveryIntegrationCoordinator(
        Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> integrate,
        ILogger<RemoteDeliveryIntegrationCoordinator> logger,
        Action<RemoteDeliveryIntegrationRequest, string, string, string>? recordFailure = null,
        Func<RemoteDeliveryIntegrationRequest, MergeIntoIntegrationResult, Task<IntegrationAgentRoundStartResult>>? startAgentRound = null,
        Func<RemoteDeliveryIntegrationRequest, bool>? isCurrentReview = null,
        Func<RemoteDeliveryIntegrationRequest, bool>? isAdmissibleDelivery = null,
        Action<RemoteDeliveryIntegrationRequest>? reclaimAfterIntegration = null,
        IConfiguration? configuration = null)
    {
        _integrate = integrate;
        _startAgentRound = startAgentRound ?? ((_, _) => Task.FromResult(
            new IntegrationAgentRoundStartResult(false, "No automatic agent-round boundary was configured.")));
        _recordFailure = recordFailure ?? ((_, _, _, _) => { });
        _isCurrentReview = isCurrentReview ?? (_ => true);
        _isAdmissibleDelivery = request => OwnsRequest(request, configuration)
            && (isAdmissibleDelivery?.Invoke(request) ?? true);
        _reclaimAfterIntegration = reclaimAfterIntegration ?? (_ => { });
        _logger = logger;
    }

    /// <summary>
    /// Persists a Remote delivery gate rejection before the card enters Human
    /// Review. The card therefore projects a typed failure instead of a silent
    /// integration-pending state, and acceptance has no reason to retry it.
    /// </summary>
    public void RecordGateFailure(
        RemoteDeliveryIntegrationRequest request,
        string detail)
        => _recordFailure(
            request,
            AcceptedIntegrationFailureCodes.DeliveryGateFailed,
            "Remote delivery gate failed before integration.",
            detail);

    /// <param name="request">The delivery to integrate.</param>
    /// <param name="discardCompletedReplay">
    /// True when the caller knows the last completed result for this delivery
    /// is stale - an operator moved the card out of its integration park
    /// (AGT-2995). An in-flight integration is still coalesced; only a finished
    /// one is no longer handed back.
    /// </param>
    public Task<MergeIntoIntegrationResult> EnqueueAsync(
        RemoteDeliveryIntegrationRequest request,
        bool discardCompletedReplay = false)
        => Enqueue(request, discardCompletedReplay, joinInFlight: true)!;

    /// <summary>
    /// Like <see cref="EnqueueAsync"/>, but never joins an integration of the
    /// same card that another caller is already driving (AGT-3091). Returns
    /// false and no task in that case. A recovery sweep that coalesced onto the
    /// report request's integration waited out that whole gate, and every card
    /// behind it was then judged on a lane snapshot that old.
    /// </summary>
    public bool TryEnqueueUnlessInFlight(
        RemoteDeliveryIntegrationRequest request,
        bool discardCompletedReplay,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<MergeIntoIntegrationResult>? result)
    {
        result = Enqueue(request, discardCompletedReplay, joinInFlight: false);
        return result is not null;
    }

    private Task<MergeIntoIntegrationResult>? Enqueue(
        RemoteDeliveryIntegrationRequest request,
        bool discardCompletedReplay,
        bool joinInFlight)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deliveryKey = DeliveryKey(request);
        ProjectQueue projectQueue;
        var startDrain = false;
        TaskCompletionSource<MergeIntoIntegrationResult> completion;
        long sequence;
        lock (_gate)
        {
            PruneCompletedReplays(DateTimeOffset.UtcNow);
            if (discardCompletedReplay
                && _deliveryReplays.TryGetValue(deliveryKey, out var finished)
                && finished.CompletedAtUtc is not null)
            {
                _deliveryReplays.Remove(deliveryKey);
            }
            if (!joinInFlight
                && _deliveryReplays.Values.Any(inFlight => inFlight.CompletedAtUtc is null
                                                           && IsSameCard(inFlight.Request, request)))
            {
                return null;
            }
            if (_deliveryReplays.TryGetValue(deliveryKey, out var replay))
            {
                _logger.LogInformation(
                    "remote-delivery-integration replay coalesced project={Project} job={JobId} sequence={Sequence}",
                    request.Project,
                    request.JobId,
                    replay.Sequence);
                return replay.Completion.Task;
            }

            completion = new TaskCompletionSource<MergeIntoIntegrationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sequence = NextSequence();
            _deliveryReplays[deliveryKey] = new DeliveryReplay(
                sequence,
                completion,
                CompletedAtUtc: null,
                request);
            if (!_projectQueues.TryGetValue(request.Project, out projectQueue!))
            {
                projectQueue = new ProjectQueue();
                _projectQueues[request.Project] = projectQueue;
            }
            projectQueue.Pending.Add(new QueuedDelivery(
                request,
                deliveryKey,
                sequence,
                completion));
            if (!projectQueue.Running)
            {
                projectQueue.Running = true;
                startDrain = true;
            }
        }

        var reenter = discardCompletedReplay && IntegrationLaneEvents.Read(request.JobFolderPath)
            .Any(e => e.Kind == "ejected" && e.Outcome is ("AgentRoundRequired" or "Conflict"));
        IntegrationLaneEvents.Append(request.JobFolderPath, request.Project, request.JobId,
            reenter ? "agent-round-re-entry" : "enqueued",
            sequence: sequence, deliveredAtUtc: request.DeliveredAtUtc);
        if (startDrain) _ = DrainProjectAsync(request.Project, projectQueue);
        return completion.Task;
    }

    private async Task DrainProjectAsync(string project, ProjectQueue projectQueue)
    {
        while (true)
        {
            QueuedDelivery delivery;
            lock (_gate)
            {
                if (projectQueue.Pending.Count == 0)
                {
                    projectQueue.Running = false;
                    if (_projectQueues.TryGetValue(project, out var current)
                        && ReferenceEquals(current, projectQueue))
                    {
                        _projectQueues.Remove(project);
                    }
                    return;
                }

                var nextIndex = 0;
                for (var index = 1; index < projectQueue.Pending.Count; index++)
                {
                    if (QueuedDeliveryComparer.Instance.Compare(
                            projectQueue.Pending[index],
                            projectQueue.Pending[nextIndex]) < 0)
                    {
                        nextIndex = index;
                    }
                }
                delivery = projectQueue.Pending[nextIndex];
                projectQueue.Pending.RemoveAt(nextIndex);
            }

            try
            {
                if (!_isCurrentReview(delivery.Request))
                {
                    CompleteDelivery(delivery, MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: MergeIntoDevelopRunner.SupersededReviewGenerationError));
                    AppendSuperseded(delivery);
                    continue;
                }
                // AGT-3091: a delivery may wait here behind a whole gate. A
                // reissue in that time moves the card and leases a new run
                // without a new review generation, so both are re-read now.
                if (delivery.Request.ReviewAttemptId is not null
                    && !_isAdmissibleDelivery(delivery.Request))
                {
                    _logger.LogInformation(
                        "remote-delivery-integration skipped project={Project} job={JobId} sequence={Sequence} reason={Reason}",
                        delivery.Request.Project,
                        delivery.Request.JobId,
                        delivery.Sequence,
                        ReviewedDeliverySupersededError);
                    CompleteDelivery(delivery, MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: $"{ReviewedDeliverySupersededError}: the card left Auto Review or its reviewed RunAttempt is no longer current; nothing was merged."));
                    AppendSuperseded(delivery);
                    continue;
                }
                _logger.LogInformation(
                    "remote-delivery-integration started project={Project} job={JobId} sequence={Sequence} deliveredAt={DeliveredAt}",
                    delivery.Request.Project,
                    delivery.Request.JobId,
                    delivery.Sequence,
                    delivery.Request.DeliveredAtUtc);
                IntegrationLaneEvents.Append(delivery.Request.JobFolderPath,
                    delivery.Request.Project, delivery.Request.JobId, "dequeued",
                    sequence: delivery.Sequence);
                MergeIntoIntegrationResult result;
                using (IntegrationLaneEvents.BeginDelivery(delivery.Sequence))
                    result = await _integrate(delivery.Request).ConfigureAwait(false);
                // The runner's publication fence already decided a merge; its
                // result stays true. Only an unmerged delivery could still start
                // a stale repair round, so recheck card and profile ownership.
                if (result.Outcome is MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict
                    && (!_isCurrentReview(delivery.Request) || !_isAdmissibleDelivery(delivery.Request)))
                {
                    _logger.LogInformation(
                        "remote-delivery-integration continuation project={Project} job={JobId} started=False reason={Reason}",
                        delivery.Request.Project,
                        delivery.Request.JobId,
                        MergeIntoDevelopRunner.SupersededReviewGenerationError);
                }
                else if (result.Outcome is MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict)
                {
                    var continuation = await _startAgentRound(
                        delivery.Request,
                        result).ConfigureAwait(false);
                    if (continuation.BudgetExhausted)
                    {
                        result = result with
                        {
                            AutomaticRecoveryDetail = continuation.Reason,
                            AutomaticRecoveryBudgetUsed = continuation.BudgetUsed,
                            AutomaticRecoveryBudgetLimit = continuation.BudgetLimit,
                        };
                    }
                    _logger.LogInformation(
                        "remote-delivery-integration continuation project={Project} job={JobId} started={Started} reason={Reason}",
                        delivery.Request.Project,
                        delivery.Request.JobId,
                        continuation.Started,
                        continuation.Reason);
                }
                IntegrationLaneEvents.Append(delivery.Request.JobFolderPath,
                    delivery.Request.Project, delivery.Request.JobId,
                    result.Outcome.IsSuccessfulIntegration() ? "ready-to-publish" : "ejected",
                    sequence: delivery.Sequence, outcome: result.Outcome.ToString(),
                    reason: result.Error,
                    receipt: result.Outcome.IsSuccessfulIntegration() ? result.MergedSha : null);
                CompleteDelivery(delivery, result);
                if (result.Outcome.IsSuccessfulIntegration()) ReclaimAfterIntegration(delivery);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "remote-delivery-integration failed project={Project} job={JobId} sequence={Sequence}",
                    delivery.Request.Project,
                    delivery.Request.JobId,
                    delivery.Sequence);
                _recordFailure(
                    delivery.Request,
                    AcceptedIntegrationFailureCodes.IntegrationError,
                    "Immediate Remote delivery integration failed.",
                    ex.Message);
                CompleteDelivery(delivery, MergeIntoIntegrationResult.Failed(
                    AcceptedIntegrationFailureCodes.IntegrationError,
                    ex.Message));
                IntegrationLaneEvents.Append(delivery.Request.JobFolderPath,
                    delivery.Request.Project, delivery.Request.JobId, "ejected",
                    sequence: delivery.Sequence, outcome: "error", reason: ex.Message);
            }
        }
    }

    /// <summary>
    /// Runs after <see cref="CompleteDelivery"/>, so the caller's move to Human
    /// Review never waits on ref deletion, and still inside the per-project
    /// drain, so the reclaim never races the next merge into the same
    /// repository. Only a landed or already-contained merge reaches here; a
    /// gate failure, conflict, agent round, or superseded review never does.
    /// A reclaim failure is logged and retried by the periodic sweep; it never
    /// becomes an integration failure.
    /// </summary>
    private void ReclaimAfterIntegration(QueuedDelivery delivery)
    {
        try
        {
            _reclaimAfterIntegration(delivery.Request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "remote-delivery-integration branch reclaim failed project={Project} job={JobId}",
                delivery.Request.Project,
                delivery.Request.JobId);
        }
    }

    private static void ReclaimBranchesAfterIntegration(
        RemoteDeliveryIntegrationRequest request,
        TaskScannerService scanner,
        GitService? git,
        BranchReclaimTriggerService? branchReclaim)
    {
        if (git is null || branchReclaim is null) return;
        var job = scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null) return;
        var repoPath = git.ResolveRepoRootForWatchPath(job.WatchPath);
        if (string.IsNullOrWhiteSpace(repoPath)) return;
        var taskKey = !string.IsNullOrWhiteSpace(job.Key)
            ? job.Key!
            : !string.IsNullOrWhiteSpace(job.TaskKey)
                ? job.TaskKey
                : job.Id;
        branchReclaim.ReclaimAfterIntegration(
            request.Project, repoPath, taskKey, request.IntegrationBranch, job.FolderPath);
    }

    private void CompleteDelivery(
        QueuedDelivery delivery,
        MergeIntoIntegrationResult result)
    {
        delivery.Completion.TrySetResult(result);
        lock (_gate)
        {
            if (_deliveryReplays.TryGetValue(delivery.DeliveryKey, out var replay)
                && ReferenceEquals(replay.Completion, delivery.Completion))
            {
                _deliveryReplays[delivery.DeliveryKey] = replay with
                {
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }
    }

    private void PruneCompletedReplays(DateTimeOffset nowUtc)
    {
        foreach (var key in _deliveryReplays
                     .Where(pair => pair.Value.CompletedAtUtc is { } completedAt
                                    && nowUtc - completedAt >= CompletedReplayRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _deliveryReplays.Remove(key);
        }
    }

    private static string DeliveryKey(RemoteDeliveryIntegrationRequest request)
        => string.Join(
            '\u001f',
            request.Project,
            request.JobId,
            request.IntegrationBranch,
            request.IntegrationStrategy,
            request.PipelineType,
            request.ReviewAttemptId ?? string.Empty,
            request.ReviewedResultSha?.ToLowerInvariant() ?? string.Empty,
            request.DeliveredAtUtc.ToUniversalTime().Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsSameCard(RemoteDeliveryIntegrationRequest left, RemoteDeliveryIntegrationRequest right)
        => string.Equals(left.Project, right.Project, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.JobId, right.JobId, StringComparison.Ordinal);

    // A delivery dropped before it reached the runner still ends its lane
    // sequence, so it never counts as waiting.
    private static void AppendSuperseded(QueuedDelivery delivery)
        => IntegrationLaneEvents.Append(delivery.Request.JobFolderPath,
            delivery.Request.Project, delivery.Request.JobId, "ejected",
            sequence: delivery.Sequence, outcome: "Superseded");

    private long NextSequence()
    {
        while (true)
        {
            var current = Volatile.Read(ref _sequence);
            var next = Math.Max(DateTime.UtcNow.Ticks, current + 1);
            if (Interlocked.CompareExchange(ref _sequence, next, current) == current)
                return next;
        }
    }

    private sealed class ProjectQueue
    {
        public List<QueuedDelivery> Pending { get; } = [];
        public bool Running { get; set; }
    }

    private sealed record QueuedDelivery(
        RemoteDeliveryIntegrationRequest Request,
        string DeliveryKey,
        long Sequence,
        TaskCompletionSource<MergeIntoIntegrationResult> Completion);

    private sealed record DeliveryReplay(
        long Sequence,
        TaskCompletionSource<MergeIntoIntegrationResult> Completion,
        DateTimeOffset? CompletedAtUtc,
        RemoteDeliveryIntegrationRequest Request);

    private sealed class QueuedDeliveryComparer : IComparer<QueuedDelivery>
    {
        public static QueuedDeliveryComparer Instance { get; } = new();

        public int Compare(QueuedDelivery? left, QueuedDelivery? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var delivered = left.Request.DeliveredAtUtc.CompareTo(right.Request.DeliveredAtUtc);
            return delivered != 0 ? delivered : left.Sequence.CompareTo(right.Sequence);
        }
    }

    /// <summary>Value of <c>details.stage</c> on the integration rows of integrate-on-delivery.</summary>
    public const string PreHumanReviewStage = "pre-human-review";

    private static async Task<MergeIntoIntegrationResult> IntegrateAndRecordAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskProvenanceService provenance,
        TimelineLog timeline,
        Func<Action, bool>? publicationFence)
    {
        // The start row pairs with the outcome row below, so the integration
        // span is read from the ledger instead of from the merge step whose
        // end happens to lie near the outcome. Written to the folder the
        // request names: the card does not move while the merge runs.
        timeline.Append(
            request.JobFolderPath,
            TimelineEventKinds.IntegrationStarted,
            TimelineActors.System,
            $"Remote delivery integration into {request.IntegrationBranch} started before Human Review.",
            details: new Dictionary<string, string>
            {
                ["integrationBranch"] = request.IntegrationBranch,
                ["integrationStrategy"] = request.IntegrationStrategy,
                ["pipelineType"] = request.PipelineType,
                ["stage"] = PreHumanReviewStage,
            });

        var result = await runner.RunAsync(
            request.Project,
            request.JobId,
            request.JobFolderPath,
            request.WatchPath,
            request.IntegrationBranch,
            CancellationToken.None,
            request.IntegrationStrategy,
            request.PipelineType,
            publicationFence,
            request.ReviewedResultSha).ConfigureAwait(false);

        var job = scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null) return result;
        if (result.FailureCode == AcceptedIntegrationFailureCodes.StaleAttempt)
            return result;
        if (result.Outcome.IsFreshMerge()
            && !string.IsNullOrWhiteSpace(result.MergedSha))
        {
            provenance.RecordMerge(job, result.MergedSha);
            job = scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        var success = result.Outcome.IsSuccessfulIntegration();
        if (result.Outcome is MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict)
            return result;

        timeline.Append(
            job.FolderPath,
            success ? TimelineEventKinds.IntegrationSucceeded : TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            success
                ? $"Remote delivery integrated into {request.IntegrationBranch} before Human Review."
                : $"Immediate Remote delivery integration failed ({result.Outcome}); the task remains reviewable with a visible integration failure.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = result.Error ?? string.Empty,
                ["stage"] = PreHumanReviewStage,
            });
        return result;
    }

    private static void RecordPreReviewFailure(
        RemoteDeliveryIntegrationRequest request,
        string failureCode,
        string summary,
        string detail,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline)
    {
        var now = DateTime.UtcNow;
        pipelineLog.RecordStep(request.JobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = failureCode,
            VerdictSummary = summary,
            Reason = detail,
            FailureCode = failureCode,
        });
        timeline.Append(
            request.JobFolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"{summary} The task remains available for review and delivery recovery.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = failureCode,
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = detail,
                ["stage"] = PreHumanReviewStage,
            });
    }
}
