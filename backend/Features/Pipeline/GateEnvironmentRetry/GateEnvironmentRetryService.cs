using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>Why a requested retry could not start.</summary>
public enum GateEnvironmentRetryStatus
{
    /// <summary>The integration replay ran; <see cref="GateEnvironmentRetryResult.Outcome"/> carries its verdict.</summary>
    Replayed,

    /// <summary>The card is not in a gate-environment retry state.</summary>
    NotApplicable,

    /// <summary>The card's current delivery SHA has no settled passed review to reuse.</summary>
    NoPassedReview,

    /// <summary>Another replay for the same card is already running.</summary>
    AlreadyRunning,
}

/// <summary>One automatic agent round for a parked gate fault on this card.</summary>
public static class GateEnvironmentContinuationPolicy
{
    public static bool ShouldStart(bool automaticEnabled, int priorRoundsForDelivery)
        => automaticEnabled && priorRoundsForDelivery < 1;
}

/// <param name="DeliverySha">Delivery SHA whose passed review was reused.</param>
/// <param name="Rung">
/// 1-based ladder rung this replay spent, or 0 for an operator replay, which
/// restarts the ladder instead of occupying a rung in it.
/// </param>
/// <param name="Code">
/// The <see cref="GateEnvironmentRetryReasons"/> slug the policy decided on, so
/// a refusal names the rule that refused it instead of a generic category.
/// </param>
/// <param name="ComparedDeliverySha">Full SHA used to find a matching review attempt.</param>
/// <param name="LatestAttempt">Latest review for that SHA, or null when none was found or consulted.</param>
public sealed record GateEnvironmentRetryResult(
    GateEnvironmentRetryStatus Status,
    string Reason,
    string? DeliverySha = null,
    string? IntegrationBranch = null,
    int Rung = 0,
    MergeIntoIntegrationOutcome? Outcome = null,
    string? Code = null,
    string? ComparedDeliverySha = null,
    GateEnvironmentReviewAttempt? LatestAttempt = null);

public sealed record GateEnvironmentReviewAttempt(
    string Id,
    ReviewTerminalOutcome? Outcome,
    DateTime? TerminalAt);

internal sealed record GateEnvironmentReviewLookup(
    bool Passed,
    GateEnvironmentReviewAttempt? LatestAttempt,
    string? FailureReason = null);

/// <summary>Counters of one sweep, published for logging and tests.</summary>
public sealed record GateEnvironmentRetrySweep(
    int Candidates,
    int Retried,
    int Waiting,
    int Parked);

/// <summary>
/// Bounded automatic recovery from a gate environment failure (AGT-2824).
/// <para>
/// A gate environment failure rolls the merge back and leaves the card in Human
/// Review saying it "will be retried", but until this service existed nothing
/// did: the accepted-integration backstop only re-drives accepted cards, and the
/// acceptance rail only reacts to <c>conflict-skipped</c>, which CAC-18
/// deliberately keeps this failure out of. The only operator path left was a
/// full new remote review for a delivery whose review had already passed.
/// </para>
/// <para>
/// This service replays the integration alone. It never creates a review
/// attempt: it verifies that the card's current delivery SHA still carries a
/// settled <c>Pass</c> in attempt authority and then re-runs
/// <see cref="MergeIntoDevelopRunner"/> against that same SHA. Scheduling,
/// bounds, and the parked reason are decided by
/// <see cref="GateEnvironmentRetryPolicy"/>; this class owns only the reads,
/// the serialization, and the writes.
/// </para>
/// </summary>
public sealed class GateEnvironmentRetryService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TimelineLog _timeline;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskProvenanceService _provenance;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GateEnvironmentRetryService> _logger;
    private readonly TimeProvider _time;
    private readonly TaskRunnerService? _continuations;

    // One replay per card at a time. A sweep rung and the operator action run
    // the same merge against the same branch, so letting both in would spend two
    // rungs on one host fault and race two merges into one integration branch.
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public GateEnvironmentRetryService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskIntegrationStatusService integrationStatus,
        PipelineExecutionLog pipelineLog,
        MergeIntoDevelopRunner runner,
        TimelineLog timeline,
        AttemptAuthorityService authority,
        TaskProvenanceService provenance,
        IConfiguration configuration,
        ILogger<GateEnvironmentRetryService> logger,
        TimeProvider? time = null,
        TaskRunnerService? continuations = null)
    {
        _scanner = scanner;
        _settings = settings;
        _integrationStatus = integrationStatus;
        _pipelineLog = pipelineLog;
        _runner = runner;
        _timeline = timeline;
        _authority = authority;
        _provenance = provenance;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _continuations = continuations;
    }

    public GateEnvironmentRetryOptions Options
        => GateEnvironmentRetryOptions.FromConfiguration(_configuration);

    /// <summary>
    /// One bounded ladder sweep over the delivered lanes. Never throws: a single
    /// unhealthy card must not stop the others from being retried.
    /// </summary>
    public async Task<GateEnvironmentRetrySweep> RunOnceAsync(CancellationToken ct = default)
    {
        var options = Options;
        if (!options.Enabled) return new GateEnvironmentRetrySweep(0, 0, 0, 0);

        var jobs = _scanner.ScanAllAutomationJobs()
            .Where(job => GateEnvironmentRetryPolicy.Lanes.Contains(job.State))
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (jobs.Count == 0) return new GateEnvironmentRetrySweep(0, 0, 0, 0);

        var statusByKey = _integrationStatus.BuildLookup(jobs);
        var candidates = 0;
        var retried = 0;
        var waiting = 0;
        var parked = 0;

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!_settings.Get(job.ProjectName).AutomaticFailureContinuationsEnabled) continue;
            try
            {
                statusByKey.TryGetValue(job.TaskKey, out var status);
                var evaluation = Evaluate(job, status, options);
                if (evaluation.Decision.Action == GateEnvironmentRetryAction.Ignore) continue;

                candidates++;
                switch (evaluation.Decision.Action)
                {
                    case GateEnvironmentRetryAction.Wait:
                        waiting++;
                        break;
                    case GateEnvironmentRetryAction.Park:
                        if (await ParkAsync(job, evaluation, ct).ConfigureAwait(false)) parked++;
                        break;
                    case GateEnvironmentRetryAction.Retry:
                        if (!_inFlight.TryAdd(job.TaskKey, 0)) break;
                        try
                        {
                            var result = await ReplayAsync(
                                job,
                                evaluation,
                                GateEnvironmentRetrySources.Sweep,
                                ct).ConfigureAwait(false);
                            if (result.Status == GateEnvironmentRetryStatus.Replayed) retried++;
                        }
                        finally
                        {
                            _inFlight.TryRemove(job.TaskKey, out _);
                        }
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

        if (retried > 0 || parked > 0)
        {
            _logger.LogInformation(
                "gate-environment-retry sweep candidates={Candidates} retried={Retried} waiting={Waiting} parked={Parked}",
                candidates,
                retried,
                waiting,
                parked);
        }
        return new GateEnvironmentRetrySweep(candidates, retried, waiting, parked);
    }

    /// <summary>
    /// The explicit "Retry integration" operator action. Same semantics as a
    /// ladder rung - the passed review for the unchanged delivery SHA is reused
    /// and no review attempt is created - but it ignores the remaining backoff,
    /// because the operator asking for it is the signal that the gate host was
    /// repaired. It also restarts the ladder, so a parked card recovers its
    /// automatic budget instead of parking again on the next fault.
    /// <para>
    /// The button overrides the ladder's <em>timing</em>, never its
    /// <em>eligibility</em>: the very same <see cref="GateEnvironmentRetryPolicy"/>
    /// evaluation the sweep uses decides whether there is anything to replay,
    /// and every <see cref="GateEnvironmentRetryAction.Ignore"/> is honoured.
    /// Replaying past <c>acceptance-integration-in-flight</c>, for instance,
    /// would race a second merge against the acceptance transaction that is
    /// already driving this card into the integration branch.
    /// </para>
    /// </summary>
    public async Task<GateEnvironmentRetryResult> RetryNowAsync(
        TaskInfo job,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        // The guard is taken before anything is read. A replay already in flight
        // has merged into the integration branch and not yet run its gate, so the
        // card's integration verdict is mid-transaction; deciding from it would
        // answer "nothing to retry" for a card that is being retried right now.
        if (!_inFlight.TryAdd(job.TaskKey, 0))
        {
            return new GateEnvironmentRetryResult(
                GateEnvironmentRetryStatus.AlreadyRunning,
                "A gate-environment integration retry for this task is already running.",
                Code: GateEnvironmentRetryReasons.AlreadyRunning);
        }

        try
        {
            var status = _integrationStatus.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
            var evaluation = Evaluate(job, status, Options);
            var decision = evaluation.Decision;
            // Wait and Park are timing verdicts the operator is entitled to
            // override; Ignore is the policy saying this card has nothing to
            // replay, and the sweep and the button must agree about that.
            if (decision.Action == GateEnvironmentRetryAction.Ignore)
            {
                var lookupFailure = evaluation.ReviewLookup.FailureReason;
                return new GateEnvironmentRetryResult(
                    decision.Reason == GateEnvironmentRetryReasons.NoPassedReview
                        ? GateEnvironmentRetryStatus.NoPassedReview
                        : GateEnvironmentRetryStatus.NotApplicable,
                    lookupFailure ?? GateEnvironmentRetryReasons.Explain(decision.Reason),
                    evaluation.DeliverySha,
                    evaluation.IntegrationBranch,
                    Code: decision.Reason,
                    ComparedDeliverySha: evaluation.DeliverySha,
                    LatestAttempt: evaluation.ReviewLookup.LatestAttempt);
            }

            return await ReplayAsync(
                job,
                evaluation,
                GateEnvironmentRetrySources.Operator,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(job.TaskKey, out _);
        }
    }

    /// <summary>
    /// Everything the policy needs plus the facts the writes need, read once so
    /// the sweep and the operator action cannot disagree about the same card.
    /// </summary>
    internal GateEnvironmentRetryEvaluation Evaluate(
        TaskInfo job,
        TaskIntegrationStatus? status,
        GateEnvironmentRetryOptions options)
    {
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        var deliverySha = ReviewSubjectStore.IsValidResultSha(subject?.ResultSha)
            ? subject!.ResultSha
            : null;
        var projectSettings = _settings.Get(job.ProjectName);
        var integrationBranch = status?.IntegrationBranch
                                ?? TaskIntegrationBranch.Resolve(job, projectSettings.IntegrationBranch);
        var mergeStep = _integrationStatus.ReadLatestMergeStep(job);
        var ledger = GateEnvironmentRetryReceipts.Read(_timeline, job.FolderPath, deliverySha);
        // TaskKey is a path-qualified scanner identity. Attempt authority uses
        // the card's stable public key, as GET /api/attempts/tasks/{key} does.
        var now = _time.GetUtcNow();
        var withoutReview = GateEnvironmentRetryPolicy.Decide(
            job,
            status,
            false,
            ledger.AttemptsSpent,
            ledger.LastAttemptAt,
            AsOffset(mergeStep?.CompletedAt ?? mergeStep?.StartedAt),
            options,
            now);
        // Let the policy decide whether review evidence matters before loading
        // archives. Most cards in these lanes have no gate failure to retry.
        var reviewLookup = withoutReview.Reason == GateEnvironmentRetryReasons.NoPassedReview
            ? HasPassedReview(job.Key ?? job.TaskKey, deliverySha)
            : new GateEnvironmentReviewLookup(false, null);
        var decision = reviewLookup.Passed
            ? GateEnvironmentRetryPolicy.Decide(
                job,
                status,
                true,
                ledger.AttemptsSpent,
                ledger.LastAttemptAt,
                AsOffset(mergeStep?.CompletedAt ?? mergeStep?.StartedAt),
                options,
                now)
            : withoutReview;

        return new GateEnvironmentRetryEvaluation(
            decision,
            ledger,
            deliverySha,
            integrationBranch,
            projectSettings.IntegrationStrategy,
            mergeStep,
            reviewLookup);
    }

    /// <summary>
    /// True when the <em>latest</em> review for exactly the delivery SHA
    /// the card would integrate ended in <c>Pass</c>.
    /// <para>
    /// Two SHAs of scoping matter here. A review that passed for an older SHA is
    /// not reusable - it did not look at this delivery. And any passed review
    /// for this SHA is not enough either: a delivery can be re-reviewed without
    /// changing, and an older <c>Pass</c> that a later <c>ProductFailure</c>
    /// overturned is not a green light. Only the last word on this delivery
    /// counts, exactly as the integration rail treats it. An unfinished newer
    /// review also refuses replay.
    /// </para>
    /// </summary>
    private GateEnvironmentReviewLookup HasPassedReview(string taskKey, string? deliverySha)
    {
        if (string.IsNullOrWhiteSpace(deliverySha)) return new(false, null);
        try
        {
            // The attempts API includes compacted history. A settled review can
            // leave the live authority file while its delivery remains retryable.
            var projection = _authority.GetTaskProjection(taskKey, includeArchived: true);
            return MatchReview(projection, deliverySha);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "gate-environment-retry passed-review lookup failed task={TaskKey} deliverySha={DeliverySha}",
                taskKey, deliverySha);
            return new(false, null, $"Review lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static GateEnvironmentReviewLookup MatchReview(
        AttemptAuthorityProjection projection,
        string deliverySha)
    {
        // OrderBy is stable and the projection preserves creation order, so
        // two reviews settled in the same tick still resolve to the later
        // one rather than to an arbitrary winner.
        var latest = projection.ReviewAttempts
            .Where(attempt => string.Equals(
                attempt.Subject.ExpectedResultSha,
                deliverySha,
                StringComparison.Ordinal))
            .OrderBy(attempt => Utc(attempt.TerminalAt ?? attempt.CreatedAt))
            .LastOrDefault();
        return new(
            latest?.Outcome == ReviewTerminalOutcome.Pass,
            latest is null ? null : new(latest.AttemptId, latest.Outcome, latest.TerminalAt));
    }

    /// <summary>
    /// Runs one integration replay. The caller owns the in-flight guard for this
    /// task key, so the merge, its gate, and its possible rollback are the only
    /// ones touching the integration branch for this card.
    /// </summary>
    private async Task<GateEnvironmentRetryResult> ReplayAsync(
        TaskInfo job,
        GateEnvironmentRetryEvaluation evaluation,
        string source,
        CancellationToken ct)
    {
        var deliverySha = evaluation.DeliverySha!;
        // The receipt is written before the merge starts. A crash between the
        // receipt and the outcome then costs one rung instead of leaving a
        // budget that can never be exhausted. An operator replay is not a rung:
        // it restarts the ladder, so it carries rung 0.
        var isOperator = source == GateEnvironmentRetrySources.Operator;
        var rung = isOperator ? 0 : evaluation.Ledger.AttemptsSpent + 1;
        var maximum = evaluation.Decision.MaxAttempts;
        GateEnvironmentRetryReceipts.RecordRetry(
            _timeline,
            job.FolderPath,
            deliverySha,
            source,
            rung,
            isOperator
                ? $"Operator replayed the integration of {Short(deliverySha)} into {evaluation.IntegrationBranch} "
                  + "after a gate environment failure, reusing the passed review and restarting the bounded ladder."
                : $"Gate-environment retry {rung}/{maximum} replayed the integration of "
                  + $"{Short(deliverySha)} into {evaluation.IntegrationBranch}, reusing the passed review.");

        var result = await _runner.RunAsync(
            job.ProjectName,
            job.Id,
            job.FolderPath,
            job.WatchPath,
            evaluation.IntegrationBranch,
            ct,
            evaluation.IntegrationStrategy,
            PipelineTypes.Resolve(job)).ConfigureAwait(false);

        RecordOutcome(job, evaluation, result, rung, maximum, source);
        return new GateEnvironmentRetryResult(
            GateEnvironmentRetryStatus.Replayed,
            result.Outcome.IsSuccessfulIntegration()
                ? "The delivery integrated without a new review round."
                : $"The replayed integration ended with {result.Outcome}.",
            deliverySha,
            evaluation.IntegrationBranch,
            rung,
            result.Outcome);
    }

    private void RecordOutcome(
        TaskInfo job,
        GateEnvironmentRetryEvaluation evaluation,
        MergeIntoIntegrationResult result,
        int rung,
        int maximum,
        string source)
    {
        var success = result.Outcome.IsSuccessfulIntegration();
        var current = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        if (success && result.Outcome.IsFreshMerge() && !string.IsNullOrWhiteSpace(result.MergedSha))
        {
            _provenance.RecordMerge(current, result.MergedSha);
            current = _scanner.FindJob(job.Id, job.WatchPath) ?? current;
        }

        _timeline.Append(
            current.FolderPath,
            success ? TimelineEventKinds.IntegrationSucceeded : TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            Outcome(success, rung, maximum, evaluation.IntegrationBranch, result.Outcome),
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = evaluation.IntegrationBranch,
                ["detail"] = result.Error ?? string.Empty,
                [GateEnvironmentRetryReceipts.DeliveryShaKey] = evaluation.DeliverySha!,
                [GateEnvironmentRetryReceipts.SourceKey] = source,
                [GateEnvironmentRetryReceipts.RungKey] = rung.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Writes the parked reason onto the durable merge step so the card's
    /// integration chip, its tooltip, and the Evidence tab all name the
    /// environment failure the ladder gave up on. Written once per ladder; the
    /// receipt makes the repeat sweeps idempotent.
    /// </summary>
    private async Task<bool> ParkAsync(
        TaskInfo job, GateEnvironmentRetryEvaluation evaluation, CancellationToken ct)
    {
        if (evaluation.Ledger.Parked) return false;
        var step = evaluation.MergeStep;
        if (step is null || evaluation.DeliverySha is null) return false;

        var reason = GateEnvironmentRetryPolicy.ParkedReason(
            evaluation.Decision.AttemptsSpent,
            evaluation.IntegrationBranch,
            step.Reason) + " Gate evidence: post-steps/pre-develop-build-gate-*.log (resource measurements and slow tests).";
        _pipelineLog.RecordStep(job.FolderPath, step with { Reason = reason });
        GateEnvironmentRetryReceipts.RecordParked(
            _timeline,
            job.FolderPath,
            evaluation.DeliverySha,
            evaluation.Decision.AttemptsSpent,
            reason);
        _logger.LogWarning(
            "gate-environment-retry parked project={Project} job={JobId} attempts={Attempts}",
            job.ProjectName,
            job.Id,
            evaluation.Decision.AttemptsSpent);
        await ContinueParkedFailureOnceAsync(job, evaluation, reason, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ContinueParkedFailureOnceAsync(
        TaskInfo job,
        GateEnvironmentRetryEvaluation evaluation,
        string reason,
        CancellationToken ct)
    {
        if (_continuations is null || evaluation.DeliverySha is null) return;
        var priorRounds = _timeline.ReadAll(job.FolderPath).Count(entry =>
                entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
                && entry.Details?.GetValueOrDefault("source") == "gate-environment-continuation");
        if (!GateEnvironmentContinuationPolicy.ShouldStart(
                _settings.Get(job.ProjectName).AutomaticFailureContinuationsEnabled,
                priorRounds))
            return;

        var subject = ReviewSubjectStore.Read(job.FolderPath);
        var prompt = IntegrationContinuationPrompt.Build(
            job.Key ?? job.Id,
            subject?.ResultRef,
            evaluation.DeliverySha,
            evaluation.IntegrationBranch,
            evaluation.MergeStep?.StepId ?? "pre-develop-build-gate",
            reason,
            evidence: evaluation.MergeStep?.VerdictSummary,
            evidenceRef: evaluation.MergeStep?.EvidenceRef);
        try
        {
            await _continuations.ContinueJobAsync(
                job.Id, prompt, job.WatchPath, mode: ContinueModes.Extend, ct: ct)
                .ConfigureAwait(false);
            var current = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
            _timeline.Append(current.FolderPath,
                TimelineEventKinds.IntegrationRecoveryQueued,
                TimelineActors.System,
                "Automatically queued one continuation after the gate environment retry budget was spent.",
                payloadRef: evaluation.MergeStep?.EvidenceRef ?? "pipeline-execution.json",
                details: new Dictionary<string, string>
                {
                    ["automatic"] = "true",
                    ["source"] = "gate-environment-continuation",
                    ["deliverySha"] = evaluation.DeliverySha,
                    ["failureStage"] = evaluation.MergeStep?.StepId ?? "pre-develop-build-gate",
                    ["failureEvidenceRef"] = evaluation.MergeStep?.EvidenceRef ?? string.Empty,
                });
        }
        catch (TaskOperationException ex)
        {
            _logger.LogWarning(ex,
                "gate-environment-retry could not queue continuation project={Project} job={JobId}",
                job.ProjectName, job.Id);
        }
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static DateTimeOffset? AsOffset(DateTime? value)
        => value is not { } instant
            ? null
            : new DateTimeOffset(instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime());

    private static string Outcome(
        bool success,
        int rung,
        int maximum,
        string integrationBranch,
        MergeIntoIntegrationOutcome outcome)
    {
        var attempt = rung == 0
            ? "The operator integration replay"
            : $"Gate-environment retry {rung}/{maximum}";
        return success
            ? $"{attempt} integrated the reviewed delivery into {integrationBranch} without a new review round."
            : $"{attempt} failed again ({outcome}); the reviewed delivery is still not integrated.";
    }

    private static string Short(string sha)
        => sha.Length <= 10 ? sha : sha[..10];
}

/// <summary>One card's gate-environment retry facts, read once per evaluation.</summary>
internal sealed record GateEnvironmentRetryEvaluation(
    GateEnvironmentRetryDecision Decision,
    GateEnvironmentRetryLedger Ledger,
    string? DeliverySha,
    string IntegrationBranch,
    string IntegrationStrategy,
    PipelineStepExecution? MergeStep,
    GateEnvironmentReviewLookup ReviewLookup);
