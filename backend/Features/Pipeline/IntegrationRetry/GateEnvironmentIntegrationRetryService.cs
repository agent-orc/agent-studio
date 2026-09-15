namespace AgentStudio.Pipeline;

/// <param name="Decision">What the policy decided for the card on this pass.</param>
/// <param name="Outcome">Integration outcome when an attempt actually ran; null otherwise.</param>
/// <param name="Attempt">1-based attempt number that was spent; 0 when none was.</param>
public sealed record GateEnvironmentRetryResult(
    GateEnvironmentRetryDecision Decision,
    MergeIntoIntegrationOutcome? Outcome = null,
    int Attempt = 0,
    string? Error = null)
{
    public bool Retried => Outcome is not null;
    public bool Integrated => Outcome?.IsSuccessfulIntegration() == true;
}

/// <summary>
/// Drives the bounded automatic retry of an integration that failed with a gate
/// environment failure after the card's review already passed (AGT-2824).
///
/// The rail deliberately replays only the integration. The passed review for the
/// same delivery SHA is reused as-is: a toolchain crash before test discovery
/// tells us nothing about the delivery, so spending a 30+ minute review slot to
/// re-grade it is pure waste. The same entry point serves the operator action,
/// so the manual and the automatic path cannot drift.
/// </summary>
public sealed class GateEnvironmentIntegrationRetryService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly TimelineLog _timeline;
    private readonly ILogger<GateEnvironmentIntegrationRetryService> _logger;
    private readonly Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> _integrate;
    private readonly Func<string, ReviewGrade> _latestReview;

    public GateEnvironmentIntegrationRetryService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        RemoteDeliveryIntegrationCoordinator coordinator,
        AttemptAuthorityService authority,
        ILogger<GateEnvironmentIntegrationRetryService> logger)
        : this(
            scanner,
            settings,
            pipelineLog,
            timeline,
            coordinator.EnqueueAsync,
            taskKey => LatestTerminalReview(authority, taskKey),
            logger)
    {
    }

    internal GateEnvironmentIntegrationRetryService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> integrate,
        Func<string, ReviewGrade> latestReview,
        ILogger<GateEnvironmentIntegrationRetryService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _pipelineLog = pipelineLog;
        _timeline = timeline;
        _integrate = integrate;
        _latestReview = latestReview;
        _logger = logger;
    }

    /// <summary>Newest terminal review verdict for a task and the SHA it graded.</summary>
    public sealed record ReviewGrade(bool Passed, string? ReviewedSha);

    /// <summary>
    /// Lifts everything the pure policy reads off disk and the authority store.
    /// </summary>
    public GateEnvironmentRetryState ReadState(TaskInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var mergeStep = ReadLatestMergeStep(job);
        var failure = mergeStep is null
            ? null
            : AcceptedIntegrationFailurePolicy.Classify(
                mergeStep.Status,
                mergeStep.Verdict,
                mergeStep.Reason,
                mergeStep.VerdictSummary,
                mergeStep.FailureCode);
        var review = SafeReview(job);
        var failedAt = mergeStep?.CompletedAt ?? mergeStep?.StartedAt;
        return new GateEnvironmentRetryState(
            job.State,
            failure?.Code,
            review.Passed,
            ReviewSubjectStore.Read(job.FolderPath)?.ResultSha,
            review.ReviewedSha,
            failedAt is { } stamp
                ? new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc))
                : null,
            IntegrationRetryLedger.Read(job.FolderPath));
    }

    /// <summary>
    /// One pass over a single card: retry, park, or leave it alone. Used by the
    /// sweep; the operator action calls <see cref="RetryOnOperatorRequestAsync"/>.
    /// </summary>
    public async Task<GateEnvironmentRetryResult> AdvanceAsync(
        TaskInfo job,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var state = ReadState(job);
        var decision = GateEnvironmentRetryPolicy.Decide(state, nowUtc);
        switch (decision.Action)
        {
            case GateEnvironmentRetryAction.Retry:
                return await ExecuteAsync(
                    job,
                    state,
                    decision,
                    IntegrationRetryLedger.BackstopTrigger,
                    nowUtc,
                    ct).ConfigureAwait(false);
            case GateEnvironmentRetryAction.Park:
                Park(job, state, nowUtc);
                return new GateEnvironmentRetryResult(decision, Attempt: decision.AttemptNumber);
            default:
                return new GateEnvironmentRetryResult(decision);
        }
    }

    /// <summary>
    /// The operator's "Retry integration". Same admission rules and the same
    /// replay as the sweep, but it does not wait for the backoff window and it
    /// grants a fresh bounded budget: a human who repaired the gate host has
    /// exactly the information the timer does not have.
    /// </summary>
    public async Task<GateEnvironmentRetryResult> RetryOnOperatorRequestAsync(
        TaskInfo job,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var state = ReadState(job);
        var admission = GateEnvironmentRetryPolicy.DecideOperatorRetry(state);
        if (admission.Action != GateEnvironmentRetryAction.Retry)
            return new GateEnvironmentRetryResult(admission);

        IntegrationRetryLedger.Clear(job.FolderPath);
        return await ExecuteAsync(
            job,
            state,
            admission,
            IntegrationRetryLedger.OperatorTrigger,
            nowUtc,
            ct).ConfigureAwait(false);
    }

    private async Task<GateEnvironmentRetryResult> ExecuteAsync(
        TaskInfo job,
        GateEnvironmentRetryState state,
        GateEnvironmentRetryDecision decision,
        string trigger,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The attempt is counted before the merge runs. A crash mid-gate must
        // not hand the card an unbounded budget - that is the exact loop this
        // card replaces.
        var ledger = IntegrationRetryLedger.RecordAttempt(
            job.FolderPath,
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            state.DeliverySha!,
            trigger,
            nowUtc);

        var settings = _settings.Get(job.ProjectName);
        var request = new RemoteDeliveryIntegrationRequest(
            job.ProjectName,
            job.Id,
            job.FolderPath,
            job.WatchPath,
            TaskIntegrationBranch.Resolve(job, settings.IntegrationBranch),
            settings.IntegrationStrategy,
            PipelineTypes.Resolve(job),
            // Deliberately "now" and not the original delivery timestamp: the
            // coordinator coalesces replays of an identical delivery key, so
            // reusing the delivered-at stamp would return the failed result of
            // the attempt this retry exists to redo.
            nowUtc,
            $"{trigger} {ledger.Attempts}/{GateEnvironmentRetryPolicy.MaxAutomaticAttempts}");

        _logger.LogInformation(
            "gate-environment-retry started project={Project} job={JobId} attempt={Attempt}/{Max} trigger={Trigger} sha={Sha}",
            job.ProjectName,
            job.Id,
            ledger.Attempts,
            GateEnvironmentRetryPolicy.MaxAutomaticAttempts,
            trigger,
            Short(state.DeliverySha));

        MergeIntoIntegrationResult result;
        try
        {
            result = await _integrate(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "gate-environment-retry failed project={Project} job={JobId} attempt={Attempt}",
                job.ProjectName,
                job.Id,
                ledger.Attempts);
            return new GateEnvironmentRetryResult(
                decision,
                MergeIntoIntegrationOutcome.Error,
                ledger.Attempts,
                ex.Message);
        }

        var current = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        if (result.Outcome.IsSuccessfulIntegration())
        {
            // The environment budget only governs environment failures. Once the
            // delivery is in, the ledger is history.
            IntegrationRetryLedger.Clear(current.FolderPath);
        }
        else if (result.Outcome != MergeIntoIntegrationOutcome.GateEnvironmentFailure)
        {
            // A conflict, a red gate, or an error is a decided outcome the normal
            // integration rails own. Dropping the ledger hands the card over
            // instead of spending the remaining environment budget on it.
            IntegrationRetryLedger.Clear(current.FolderPath);
        }

        _logger.LogInformation(
            "gate-environment-retry finished project={Project} job={JobId} attempt={Attempt} outcome={Outcome}",
            job.ProjectName,
            job.Id,
            ledger.Attempts,
            result.Outcome);
        return new GateEnvironmentRetryResult(
            decision,
            result.Outcome,
            ledger.Attempts,
            result.Error);
    }

    private void Park(TaskInfo job, GateEnvironmentRetryState state, DateTimeOffset nowUtc)
    {
        var detail = ReadLatestMergeStep(job)?.Reason;
        var reason = GateEnvironmentRetryPolicy.ParkedReason(detail);
        IntegrationRetryLedger.RecordPark(
            job.FolderPath,
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            state.DeliverySha!,
            reason,
            nowUtc);
        _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Integration parked after {GateEnvironmentRetryPolicy.MaxAutomaticAttempts} automatic retries; "
            + "the gate environment is still broken and the passed review still stands.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
                ["stage"] = RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage,
                ["detail"] = reason,
                ["retryAttempt"] = $"parked {GateEnvironmentRetryPolicy.MaxAutomaticAttempts}/{GateEnvironmentRetryPolicy.MaxAutomaticAttempts}",
            });
        _logger.LogWarning(
            "gate-environment-retry parked project={Project} job={JobId} after {Max} attempts",
            job.ProjectName,
            job.Id,
            GateEnvironmentRetryPolicy.MaxAutomaticAttempts);
    }

    private ReviewGrade SafeReview(TaskInfo job)
    {
        if (string.IsNullOrWhiteSpace(job.TaskKey)) return new ReviewGrade(false, null);
        try
        {
            return _latestReview(job.TaskKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "gate-environment-retry review lookup failed project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return new ReviewGrade(false, null);
        }
    }

    private PipelineStepExecution? ReadLatestMergeStep(TaskInfo job)
    {
        try
        {
            return _pipelineLog.Read(job.FolderPath)?.Steps.LastOrDefault(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.MergeIntoDevelopStepId,
                    StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "gate-environment-retry pipeline read failed project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    /// <summary>
    /// Newest review attempt that actually produced a grade. Superseded and
    /// cancelled attempts carry no verdict and must not hide the pass behind
    /// them.
    /// </summary>
    private static ReviewGrade LatestTerminalReview(AttemptAuthorityService authority, string taskKey)
    {
        var graded = authority.GetTaskProjection(taskKey).ReviewAttempts
            .Where(review => review.Outcome is not null
                             and not ReviewTerminalOutcome.Superseded
                             and not ReviewTerminalOutcome.Cancellation)
            .OrderBy(review => review.TerminalAt ?? review.CreatedAt)
            .ThenBy(review => review.CreatedAt)
            .LastOrDefault();
        return graded is null
            ? new ReviewGrade(false, null)
            : new ReviewGrade(
                graded.Outcome == ReviewTerminalOutcome.Pass,
                graded.TestedResultSha is { Length: > 0 } tested
                    ? tested
                    : graded.Subject.ExpectedResultSha);
    }

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) || sha.Length < 7 ? sha ?? "" : sha[..7];
}
