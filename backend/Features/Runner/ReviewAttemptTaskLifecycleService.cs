namespace AgentStudio.Runner;

/// <summary>
/// Joins task-lane authority to ReviewAttempt authority. Terminal lane moves,
/// boot recovery, and Remote Review claims all pass through the same lock so a
/// claim cannot escape after the owning task has landed outside Auto Review.
/// </summary>
public sealed class ReviewAttemptTaskLifecycleService
{
    private readonly object _gate = new();
    private readonly AttemptAuthorityService _authority;
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly ILogger<ReviewAttemptTaskLifecycleService> _logger;

    public ReviewAttemptTaskLifecycleService(
        AttemptAuthorityService authority,
        TaskScannerService scanner,
        TimelineLog timeline,
        ILogger<ReviewAttemptTaskLifecycleService> logger)
    {
        _authority = authority;
        _scanner = scanner;
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// Serializes a terminal lane mutation with ReviewAttempt revocation. The
    /// lane move lands first; only a successful move can revoke authority.
    /// </summary>
    public MoveJobOutcome ExecuteTerminalTransition(
        TaskInfo task,
        string targetState,
        Func<MoveJobOutcome> transition)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(transition);

        lock (_gate)
        {
            var outcome = transition();
            if (outcome.Status != MoveJobStatus.Success
                || string.Equals(task.State, targetState, StringComparison.Ordinal)
                || !IsTerminalTaskState(targetState))
            {
                return outcome;
            }

            var reason =
                $"Task entered terminal lane '{targetState}'; open ReviewAttempt authority was superseded by the lane transition.";
            var taskIndex = BuildTaskIndex([task]);
            var changed = _authority.SupersedeOpenReviewAttempts(taskKey =>
                taskIndex.ContainsKey(taskKey)
                    ? reason
                    : null);
            AppendJournalEntries(
                changed,
                taskIndex,
                "lane-transition",
                targetState,
                outcome.NewFolderPath);
            return outcome;
        }
    }

    /// <summary>
    /// Boot-time repair for ReviewAttempts that survived after their cards left
    /// Auto Review. The operation is idempotent because terminal attempts are
    /// excluded by the authority service.
    /// </summary>
    public int SweepUnclaimableAttempts(string source = "boot-sweep")
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            return SupersedeUnclaimableLocked(tasks, source).Count;
        }
    }

    /// <summary>
    /// Performs the card-state guard and the fenced selection under one
    /// lifecycle lock. Every non-Auto-Review candidate is superseded before the
    /// authority selects the next attempt.
    /// </summary>
    public AttemptWriteResult ClaimNextReview(
        string executorId,
        string hostId,
        string instanceId,
        int? requestedTtlSeconds,
        Func<ReviewAttemptDto, ReviewClaimPreparation>? prepare = null)
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            SupersedeUnclaimableLocked(tasks, "claim-guard");
            var claimed = _authority.ClaimNextReview(
                executorId,
                hostId,
                instanceId,
                requestedTtlSeconds,
                prepare);
            AppendClaimEntry(claimed, tasks);
            return claimed;
        }
    }

    /// <summary>
    /// Mints a ReviewAttempt only after its owning card has durably entered Auto
    /// Review. The lane check and mint share the claim lifecycle lock, so a
    /// claim poll can observe either no attempt or an eligible attempt, never a
    /// fresh attempt while the card still resides in Progress.
    /// </summary>
    public AttemptWriteResult CreateReviewAttemptInAutoReview(
        TaskInfo task,
        CreateReviewAttemptRequest request)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            var current = _scanner.FindJob(task.Id, task.WatchPath);
            if (current is null
                || !string.Equals(current.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.InvalidState,
                    string.Empty,
                    "ReviewAttempt can be minted only after the task has entered 4-auto-review.");
            }

            return _authority.CreateReviewAttempt(request);
        }
    }

    /// <summary>
    /// Restores a canonical review subject when an older claim guard terminalized
    /// it before the completion lane move landed. Only the exact incident shape
    /// is eligible: Auto Review card, current superseded ReviewAttempt, and a
    /// completed current RunAttempt with its immutable result envelope.
    /// </summary>
    public int SweepSupersededAutoReviewAttempts(string source = "auto-review-recovery")
    {
        lock (_gate)
        {
            var repaired = 0;
            foreach (var task in _scanner.ScanAllAutomationJobs()
                         .Where(task => string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase)))
            {
                var taskKey = string.IsNullOrWhiteSpace(task.Key)
                    ? (!string.IsNullOrWhiteSpace(task.TaskKey) ? task.TaskKey : task.Id)
                    : task.Key;
                var projection = _authority.GetTaskProjection(taskKey);
                var review = projection.CurrentReviewAttempt;
                var run = projection.CurrentRunAttempt;
                if (review is not { State: AttemptLifecycleState.Superseded, Outcome: ReviewTerminalOutcome.Superseded }
                    || run is not { State: AttemptLifecycleState.Completed, ResultEnvelope: not null, ResultSha: not null }
                    || !string.Equals(review.SourceRunAttemptId, run.AttemptId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var subject = review.Subject;
                var created = _authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                    taskKey,
                    subject.RepositoryId,
                    subject.ExpectedResultSha,
                    run.AttemptId,
                    subject.TaskRequirementsHash,
                    subject.ReviewPolicyHash,
                    subject.EvidenceDigestInputs,
                    $"review-recovery:{review.AttemptId}",
                    RepositoryUrl: subject.RepositoryUrl ?? run.ResultEnvelope.RepositoryUrl,
                    ResultRef: subject.ResultRef ?? run.ResultEnvelope.ImmutableRemoteRef,
                    Plan: subject.Plan));
                if (!created.Accepted)
                {
                    _logger.LogWarning(
                        "review-attempt-auto-review-recovery-failed task={TaskKey} sourceAttempt={AttemptId} status={Status} message={Message}",
                        taskKey, review.AttemptId, created.Status, created.Message);
                    continue;
                }

                repaired++;
                _logger.LogInformation(
                    "review-attempt-auto-review-recovered task={TaskKey} sourceAttempt={AttemptId} replacementAttempt={ReplacementAttemptId} source={Source}",
                    taskKey, review.AttemptId, created.AttemptId, source);
            }
            return repaired;
        }
    }

    /// <summary>
    /// Applies the same card-state guard to the attempt-addressed compatibility
    /// claim route.
    /// </summary>
    public AttemptWriteResult ClaimReview(
        string attemptId,
        string executorId,
        string hostId,
        int? requestedTtlSeconds,
        string idempotencyKey,
        string? instanceId = null)
    {
        lock (_gate)
        {
            var tasks = BuildTaskIndex(_scanner.ScanAllJobsWithArchive());
            SupersedeUnclaimableLocked(tasks, "claim-guard");
            var claimed = _authority.ClaimReview(
                attemptId,
                executorId,
                hostId,
                requestedTtlSeconds,
                idempotencyKey,
                instanceId);
            AppendClaimEntry(claimed, tasks);
            return claimed;
        }
    }

    /// <summary>
    /// Ledger row for a granted review claim: the lease acquisition is the exact
    /// end of the review queue wait, so the cycle-time analysis no longer has to
    /// approximate it by the first projected step row of the attempt. A replayed
    /// (duplicate) claim writes nothing; the original claim already did.
    /// </summary>
    private void AppendClaimEntry(AttemptWriteResult claimed, IReadOnlyDictionary<string, TaskInfo> tasks)
    {
        if (claimed.Status != AttemptWriteStatus.Accepted
            || claimed.ReviewAttempt is not { Lease: { } lease } review)
            return;
        if (!tasks.TryGetValue(review.TaskKey, out var task))
        {
            _logger.LogWarning(
                "review-attempt-claimed-journal-missing-task attempt={AttemptId} task={TaskKey} executor={ExecutorId}",
                review.AttemptId,
                review.TaskKey,
                lease.ExecutorId);
            return;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        _timeline.Append(task.FolderPath, new TimelineEvent
        {
            Ts = lease.AcquiredAt,
            Kind = TimelineEventKinds.ReviewAttemptClaimed,
            Actor = $"remote-review:{lease.ExecutorId}",
            Summary = $"ReviewAttempt {review.AttemptId} claimed by {lease.ExecutorId} on {lease.HostId}.",
            RunId = review.AttemptId,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attemptId"] = review.AttemptId,
                ["leaseId"] = lease.LeaseId,
                ["executorId"] = lease.ExecutorId,
                ["hostId"] = lease.HostId,
                ["executionLocation"] = "remote",
                ["fence"] = lease.Fence.ToString(invariant),
                ["authorityEpoch"] = lease.AuthorityEpoch.ToString(invariant),
                ["subjectId"] = review.Subject.SubjectId,
                ["sourceRunAttemptId"] = review.SourceRunAttemptId,
                ["expectedResultSha"] = review.Subject.ExpectedResultSha,
                ["acquiredAt"] = lease.AcquiredAt.ToString("O", invariant),
                ["expiresAt"] = lease.ExpiresAt.ToString("O", invariant),
            },
        });
    }

    private IReadOnlyList<ReviewAttemptDto> SupersedeUnclaimableLocked(
        IReadOnlyDictionary<string, TaskInfo> tasks,
        string source)
    {
        var changed = _authority.SupersedeOpenReviewAttempts(taskKey =>
        {
            if (!tasks.TryGetValue(taskKey, out var task))
            {
                return
                    $"Task '{taskKey}' is not present in the task store; open ReviewAttempt authority was superseded by the {source}.";
            }

            return string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Task is in lane '{task.State}', not '{TaskStates.AutoReview}'; open ReviewAttempt authority was superseded by the {source}.";
        });
        AppendJournalEntries(changed, tasks, source);
        return changed;
    }

    private void AppendJournalEntries(
        IReadOnlyList<ReviewAttemptDto> changed,
        IReadOnlyDictionary<string, TaskInfo> tasks,
        string source,
        string? laneOverride = null,
        string? folderOverride = null)
    {
        foreach (var review in changed)
        {
            if (!tasks.TryGetValue(review.TaskKey, out var task)
                && string.IsNullOrWhiteSpace(folderOverride))
            {
                _logger.LogWarning(
                    "review-attempt-superseded-journal-missing-task attempt={AttemptId} task={TaskKey} source={Source}",
                    review.AttemptId,
                    review.TaskKey,
                    source);
                continue;
            }

            var lane = laneOverride ?? task!.State;
            var folder = folderOverride ?? task!.FolderPath;
            _timeline.Append(
                folder,
                TimelineEventKinds.ReviewAttemptSuperseded,
                TimelineActors.System,
                $"ReviewAttempt {review.AttemptId} was superseded because the task is in {lane}.",
                runId: review.AttemptId,
                details: new Dictionary<string, string>
                {
                    ["attemptId"] = review.AttemptId,
                    ["authority"] = AttemptWriteStatus.Superseded.ToString(),
                    ["lane"] = lane,
                    ["source"] = source,
                });
        }
    }

    private static Dictionary<string, TaskInfo> BuildTaskIndex(IEnumerable<TaskInfo> tasks)
    {
        var result = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            Add(task.TaskKey);
            Add(task.Key);
            Add(task.Id);

            void Add(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    result.TryAdd(key, task);
            }
        }
        return result;
    }

    private static bool IsTerminalTaskState(string state)
        => state is TaskStates.Completed or TaskStates.Archive;
}
