using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>Counters of one gate-environment retry sweep.</summary>
public sealed record GateEnvironmentRetrySweepSummary(
    int Candidates,
    int Retried,
    int Integrated,
    int Waiting,
    int Parked);

/// <summary>Outcome of one explicit retry request.</summary>
/// <param name="Retried">False when the card was not eligible; <paramref name="Reason"/> says why.</param>
/// <param name="Attempt">1-based number of the retry that ran, or 0 when none did.</param>
public sealed record GateEnvironmentRetryResult(
    bool Retried,
    bool Integrated,
    int Attempt,
    string? Outcome,
    string Reason);

/// <summary>
/// AGT-2824 - bounded side effects behind <see cref="GateEnvironmentRetryPolicy"/>.
///
/// <para>A card whose review passed and whose merge gate then failed with
/// <c>gate-environment-failure</c> had no automatic path forward: the accepted
/// integration backstop only sweeps accepted cards, and the acceptance rail's
/// infrastructure replay moves a card back to <c>4-auto-review</c>, which spends
/// a whole new Remote Review. This rail replays only the merge, for the same
/// delivery SHA, reusing the review that already passed, and never creates a
/// review attempt or moves the card.</para>
///
/// <para>The successful retry leaves the card exactly where the original
/// integration would have: in Human Review with a Git-derived <c>integrated</c>
/// status, which the acceptance rail then accepts on its own tick.</para>
/// </summary>
public sealed class GateEnvironmentRetryService
{
    /// <summary>Value of <c>details.stage</c> on this rail's timeline receipts.</summary>
    public const string RetryStage = "gate-environment-retry";

    private readonly GateEnvironmentRetryHooks _hooks;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly TimelineLog _timeline;
    private readonly ILogger<GateEnvironmentRetryService> _logger;
    // One replay per card at a time. The merge runner already serializes Git,
    // but a sweep racing an impatient operator would otherwise write two
    // receipts and spend two rungs of the ladder on one attempt.
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public GateEnvironmentRetryService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskIntegrationStatusService integrationStatus,
        MergeIntoDevelopRunner runner,
        AttemptAuthorityService authority,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        ILogger<GateEnvironmentRetryService> logger)
        : this(
            new GateEnvironmentRetryHooks(
                () => scanner.ScanAllAutomationJobs()
                    .Where(job => job.State == TaskStates.HumanReview)
                    .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(job => job.EnteredLaneAt)
                    .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                integrationStatus.IsFencedDeliveryIntegrated,
                job => ReadReviewVerdict(authority, job),
                job => IntegrationBranchFor(settings, job),
                (job, ct) => runner.RunAsync(
                    job.ProjectName,
                    job.Id,
                    job.FolderPath,
                    job.WatchPath,
                    IntegrationBranchFor(settings, job),
                    ct,
                    settings.Get(job.ProjectName).IntegrationStrategy,
                    PipelineTypes.Resolve(job))),
            pipelineLog,
            timeline,
            logger)
    {
    }

    internal GateEnvironmentRetryService(
        GateEnvironmentRetryHooks hooks,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        ILogger<GateEnvironmentRetryService> logger)
    {
        _hooks = hooks;
        _pipelineLog = pipelineLog;
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// One sweep over the parked-in-Human-Review cards. Never throws: a single
    /// card's failure is logged and the sweep continues.
    /// </summary>
    public async Task<GateEnvironmentRetrySweepSummary> RunOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var candidates = 0;
        var retried = 0;
        var integrated = 0;
        var waiting = 0;
        var parked = 0;

        foreach (var job in _hooks.Candidates())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = ReadState(job, GateEnvironmentRetryTrigger.Sweep);
                var decision = GateEnvironmentRetryPolicy.Decide(state, nowUtc);
                if (decision.Action == GateEnvironmentRetryAction.Ignore) continue;

                candidates++;
                switch (decision.Action)
                {
                    case GateEnvironmentRetryAction.Wait:
                        waiting++;
                        break;
                    case GateEnvironmentRetryAction.Park:
                        Park(job, state, decision);
                        parked++;
                        break;
                    case GateEnvironmentRetryAction.Retry:
                        var result = await RetryAsync(
                            job,
                            state,
                            decision,
                            GateEnvironmentRetryReceipts.SweepSource,
                            ct).ConfigureAwait(false);
                        if (!result.Retried) break;
                        retried++;
                        if (result.Integrated) integrated++;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "gate-environment-retry item failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        var summary = new GateEnvironmentRetrySweepSummary(
            candidates,
            retried,
            integrated,
            waiting,
            parked);
        if (candidates > 0)
        {
            _logger.LogInformation(
                "gate-environment-retry sweep candidates={Candidates} retried={Retried} integrated={Integrated} waiting={Waiting} parked={Parked}",
                summary.Candidates,
                summary.Retried,
                summary.Integrated,
                summary.Waiting,
                summary.Parked);
        }
        return summary;
    }

    /// <summary>
    /// The explicit "Retry integration" operator action. Same eligibility guards
    /// and the same review reuse as the sweep; it only skips the remaining
    /// backoff and is allowed once the ladder is spent, because a repaired gate
    /// host is exactly the case the park is waiting for.
    /// </summary>
    public async Task<GateEnvironmentRetryResult> RetryNowAsync(
        TaskInfo job,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var state = ReadState(job, GateEnvironmentRetryTrigger.Operator);
        var decision = GateEnvironmentRetryPolicy.Decide(state, DateTimeOffset.UtcNow);
        if (decision.Action != GateEnvironmentRetryAction.Retry)
            return new GateEnvironmentRetryResult(false, false, 0, null, decision.Reason);

        return await RetryAsync(
            job,
            state,
            decision,
            GateEnvironmentRetryReceipts.OperatorSource,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The durable evidence behind one card's decision. Read here so the policy
    /// itself never touches disk.
    /// </summary>
    internal GateEnvironmentRetryState ReadState(
        TaskInfo job,
        GateEnvironmentRetryTrigger trigger)
    {
        var step = LatestMergeStep(job);
        var failure = step is null
            ? null
            : AcceptedIntegrationFailurePolicy.Classify(
                step.Status,
                step.Verdict,
                step.Reason,
                step.VerdictSummary,
                step.FailureCode);
        var failedAt = step?.CompletedAt is { } completedAt
            ? new DateTimeOffset(DateTime.SpecifyKind(completedAt, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var integrationRequired = AcceptanceIntegrationPolicy.IsIntegrationRequired(job);
        var isCandidate = integrationRequired
                          && failure?.Code == AcceptedIntegrationFailureCodes.GateEnvironmentFailure;
        if (!isCandidate)
        {
            // Everything below reads Git, the attempt authority, and the whole
            // timeline. A card the policy will ignore anyway must not pay for
            // that on every sweep.
            return new GateEnvironmentRetryState(
                integrationRequired,
                false,
                failure?.Code,
                failedAt,
                false,
                null,
                null,
                0,
                null,
                false,
                trigger);
        }

        var deliverySha = ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
        var review = _hooks.ReviewVerdict(job);
        var ledger = GateEnvironmentRetryReceipts.Read(_timeline, job.FolderPath, deliverySha);

        return new GateEnvironmentRetryState(
            integrationRequired,
            _hooks.AlreadyIntegrated(job),
            failure!.Code,
            failedAt,
            review.Passed,
            review.ResultSha,
            deliverySha,
            ledger.Count,
            ledger.LastAt,
            ledger.Parked,
            trigger);
    }

    private async Task<GateEnvironmentRetryResult> RetryAsync(
        TaskInfo job,
        GateEnvironmentRetryState state,
        GateEnvironmentRetryDecision decision,
        string source,
        CancellationToken ct)
    {
        if (!_inFlight.TryAdd(job.FolderPath, 0))
        {
            return new GateEnvironmentRetryResult(
                false,
                false,
                0,
                null,
                "An integration retry for this delivery is already running.");
        }

        MergeIntoIntegrationResult result;
        try
        {
            result = await _hooks.Integrate(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: ex.Message);
        }
        finally
        {
            _inFlight.TryRemove(job.FolderPath, out _);
        }

        var integrated = result.Outcome.IsSuccessfulIntegration();
        var attempt = decision.AttemptNumber;
        _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationRetryAttempted,
            TimelineActors.System,
            integrated
                ? $"Integration retry {attempt} reused the passed review and integrated the delivery into {_hooks.IntegrationBranch(job)}."
                : $"Integration retry {attempt} of {GateEnvironmentRetryPolicy.MaxRetries} reused the passed review and ended with {result.Outcome}.",
            details: new Dictionary<string, string>
            {
                ["stage"] = RetryStage,
                ["source"] = source,
                ["attempt"] = Number(attempt),
                ["maxAttempts"] = Number(GateEnvironmentRetryPolicy.MaxRetries),
                [GateEnvironmentRetryReceipts.DeliveryShaDetail] = state.DeliveryResultSha ?? string.Empty,
                ["reviewReused"] = "true",
                ["outcome"] = result.Outcome.ToString(),
                ["detail"] = result.Error ?? string.Empty,
            });

        _logger.LogInformation(
            "gate-environment-retry attempt={Attempt}/{Max} project={Project} job={JobId} source={Source} outcome={Outcome}",
            attempt,
            GateEnvironmentRetryPolicy.MaxRetries,
            job.ProjectName,
            job.Id,
            source,
            result.Outcome);

        return new GateEnvironmentRetryResult(
            true,
            integrated,
            attempt,
            result.Outcome.ToString(),
            decision.Reason);
    }

    /// <summary>
    /// Records the parked reason once the ladder is spent: a receipt on the
    /// timeline, the same sentence on the durable merge step so the card's
    /// integration chip and Evidence tab stop promising a retry, and the
    /// acceptance-integration section of <c>status.md</c>.
    /// </summary>
    private void Park(
        TaskInfo job,
        GateEnvironmentRetryState state,
        GateEnvironmentRetryDecision decision)
    {
        var step = LatestMergeStep(job);
        var reason = GateEnvironmentRetryPolicy.ParkedReason(step?.Reason);

        _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationRetryExhausted,
            TimelineActors.System,
            reason,
            details: new Dictionary<string, string>
            {
                ["stage"] = RetryStage,
                ["attempts"] = Number(state.CompletedRetries),
                ["maxAttempts"] = Number(GateEnvironmentRetryPolicy.MaxRetries),
                [GateEnvironmentRetryReceipts.DeliveryShaDetail] = state.DeliveryResultSha ?? string.Empty,
                ["failureCode"] = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            });

        if (step is not null)
        {
            // The failure code stays gate-environment-failure, so the card keeps
            // its honest Pending status; only the promise of a further automatic
            // retry is replaced by the parked reason.
            _pipelineLog.RecordStep(job.FolderPath, step with { Reason = reason });
        }

        try
        {
            AcceptanceIntegrationStatusDocument.WriteFailure(
                job.FolderPath,
                MergeIntoIntegrationOutcome.GateEnvironmentFailure.ToString(),
                reason,
                _hooks.IntegrationBranch(job));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "gate-environment-retry could not record the parked reason in status.md for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
        }

        _logger.LogWarning(
            "gate-environment-retry parked project={Project} job={JobId} attempts={Attempts} reason={Reason}",
            job.ProjectName,
            job.Id,
            state.CompletedRetries,
            decision.Reason);
    }

    private PipelineStepExecution? LatestMergeStep(TaskInfo job)
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
                "gate-environment-retry merge-step read failed project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    /// <summary>
    /// The card's current review verdict from the attempt authority. The SHA is
    /// the one the review actually materialized, falling back to the subject the
    /// attempt was created for.
    /// </summary>
    private static (bool Passed, string? ResultSha) ReadReviewVerdict(
        AttemptAuthorityService authority,
        TaskInfo job)
    {
        var review = authority.GetTaskProjection(job.TaskKey).CurrentReviewAttempt;
        if (review?.Outcome != ReviewTerminalOutcome.Pass) return (false, null);
        return (
            true,
            string.IsNullOrWhiteSpace(review.TestedResultSha)
                ? review.Subject.ExpectedResultSha
                : review.TestedResultSha);
    }

    private static string IntegrationBranchFor(ProjectSettingsService settings, TaskInfo job)
        => TaskIntegrationBranch.Resolve(job, settings.Get(job.ProjectName).IntegrationBranch);

    private static string Number(int value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The service's seams onto scanning, Git truth, the attempt authority, and the
/// merge runner. Bundled so a test can drive the rail over a real task folder
/// without a repository or an authority store.
/// </summary>
internal sealed record GateEnvironmentRetryHooks(
    Func<IReadOnlyList<TaskInfo>> Candidates,
    Func<TaskInfo, bool> AlreadyIntegrated,
    Func<TaskInfo, (bool Passed, string? ResultSha)> ReviewVerdict,
    Func<TaskInfo, string> IntegrationBranch,
    Func<TaskInfo, CancellationToken, Task<MergeIntoIntegrationResult>> Integrate);
