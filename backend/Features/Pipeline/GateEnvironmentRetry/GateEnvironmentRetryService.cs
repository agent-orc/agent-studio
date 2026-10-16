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

/// <summary>
/// Reads the automatic continuation rounds already queued for a parked gate
/// fault; the one-round rule is <see cref="GateEnvironmentContinuationPolicy"/>.
/// </summary>
public static class GateEnvironmentContinuationRounds
{
    private const string Source = "gate-environment-continuation";

    public static int CountPriorRounds(IEnumerable<TimelineEvent> entries, string deliverySha)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliverySha);
        return entries.Count(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && entry.Details?.GetValueOrDefault("source") == Source
            && string.Equals(entry.Details.GetValueOrDefault("deliverySha"),
                deliverySha, StringComparison.OrdinalIgnoreCase));
    }
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
/// bounds, and parking are decided by the AI Operator in the Orchestrator
/// Engine (AGT-3060); this class owns only the reads, the serialization, and
/// the writes of each receipt.
/// </para>
/// </summary>
public sealed class GateEnvironmentRetryService : AgentStudio.Runner.IAiOperatorEffect
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
    private readonly AgentStudio.Runner.AiOperatorDecisionGateway _decisions;

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
        TaskRunnerService? continuations = null,
        AgentStudio.Runner.AiOperatorDecisionGateway? decisions = null)
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
        _decisions = decisions ?? AgentStudio.Runner.AiOperatorDecisionGateway.ForTestHost(configuration);
    }

    public string Decider => AiOperatorDeciders.GateEnvironmentRetry;

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
            .Where(job => GateEnvironmentRetryFactsReader.Lanes.Contains(job.State))
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
                // Trigger signal only: a card without a gate-environment failure
                // raises no occurrence. The AI Operator decides the ladder.
                if (!GateEnvironmentRetryFactsReader.IsGateEnvironmentFailure(status)) continue;
                var evaluation = Evaluate(job, status, options);
                if (MonolithTransitionAdapter.GateEnvironmentRetryPolicyOperatorReplayRefusal(evaluation.Facts) is not null) continue;

                candidates++;
                var payload = new AiOperatorDecisionPayload(Decider,
                    $"gate-environment:{evaluation.DeliverySha}:{evaluation.Ledger.AttemptsSpent}",
                    GateEnvironmentRetry: OperatorFacts(job, evaluation));
                var submission = _decisions.Submit(new AgentStudio.Runner.AiOperatorDecisionRequest(
                    job.ProjectName, job.Id, job.State, payload, options.SweepInterval));
                if (submission.MonolithDecision is not { } decision)
                {
                    waiting++;
                    continue;
                }
                switch (await ApplyDecisionAsync(job, evaluation, decision, ct).ConfigureAwait(false))
                {
                    case GateEnvironmentRetryAction.Wait:
                        waiting++;
                        break;
                    case GateEnvironmentRetryAction.Park:
                        parked++;
                        break;
                    case GateEnvironmentRetryAction.Retry:
                        retried++;
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
            // The operator intent overrides the ladder's timing; the authority
            // only validates that there is something to replay, and the sweep
            // and the button must agree about that.
            if (MonolithTransitionAdapter.GateEnvironmentRetryPolicyOperatorReplayRefusal(evaluation.Facts) is { } refusal)
            {
                var lookupFailure = evaluation.ReviewLookup.FailureReason;
                return new GateEnvironmentRetryResult(
                    refusal == GateEnvironmentRetryReasons.NoPassedReview
                        ? GateEnvironmentRetryStatus.NoPassedReview
                        : GateEnvironmentRetryStatus.NotApplicable,
                    lookupFailure ?? GateEnvironmentRetryReasons.Explain(refusal),
                    evaluation.DeliverySha,
                    evaluation.IntegrationBranch,
                    Code: refusal,
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
    /// AGT-3060: applies the engine's gate-environment receipt. The facts are
    /// read again; a card whose failure, delivery or spent rungs changed since
    /// the occurrence was frozen is settled without an effect.
    /// </summary>
    public async Task ApplyAsync(AgentStudio.Runner.AiOperatorApplication application, CancellationToken ct)
    {
        var job = _scanner.ScanAllAutomationJobs().FirstOrDefault(candidate =>
            candidate.ProjectName == application.ProjectId
            && (candidate.Id == application.TaskId || candidate.TaskKey == application.TaskId));
        if (job is null || job.State != application.ExpectedSourceState) return;
        var status = _integrationStatus.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
        var evaluation = Evaluate(job, status, Options);
        if (MonolithTransitionAdapter.GateEnvironmentRetryPolicyOperatorReplayRefusal(evaluation.Facts) is not null
            || application.Payload.SubjectKey
                != $"gate-environment:{evaluation.DeliverySha}:{evaluation.Ledger.AttemptsSpent}")
            return;
        await ApplyDecisionAsync(job, evaluation, application.Decision, ct).ConfigureAwait(false);
    }

    private async Task<GateEnvironmentRetryAction> ApplyDecisionAsync(TaskInfo job,
        GateEnvironmentRetryEvaluation evaluation, AiOperatorDecision decision, CancellationToken ct)
    {
        switch (decision.Effect)
        {
            case AiOperatorEffects.ParkIntegration:
            case AiOperatorEffects.ParkAndContinue:
                return await ParkAsync(job, evaluation,
                    decision.Effect == AiOperatorEffects.ParkAndContinue, ct).ConfigureAwait(false)
                    ? GateEnvironmentRetryAction.Park : GateEnvironmentRetryAction.Ignore;
            case AiOperatorEffects.ReplayIntegration:
                if (!_inFlight.TryAdd(job.TaskKey, 0)) return GateEnvironmentRetryAction.Ignore;
                try
                {
                    var result = await ReplayAsync(job, evaluation, GateEnvironmentRetrySources.Sweep, ct)
                        .ConfigureAwait(false);
                    return result.Status == GateEnvironmentRetryStatus.Replayed
                        ? GateEnvironmentRetryAction.Retry : GateEnvironmentRetryAction.Ignore;
                }
                finally
                {
                    _inFlight.TryRemove(job.TaskKey, out _);
                }
            default:
                return GateEnvironmentRetryAction.Wait;
        }
    }

    private GateEnvironmentRetryOperatorFacts OperatorFacts(TaskInfo job, GateEnvironmentRetryEvaluation evaluation)
        => new(evaluation.Facts, evaluation.Ledger.Parked,
            _settings.Get(job.ProjectName).AutomaticFailureContinuationsEnabled,
            evaluation.DeliverySha is null ? 0 : GateEnvironmentContinuationRounds.CountPriorRounds(
                _timeline.ReadAll(job.FolderPath), evaluation.DeliverySha));

    /// <summary>
    /// The facts the ladder needs plus the facts the writes need, read once so
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
        var inFlight = IntegrationGateJournal.Read(job.FolderPath) is not null;
        var withoutReview = GateEnvironmentRetryFactsReader.Facts(
            job,
            status,
            false,
            ledger.AttemptsSpent,
            ledger.LastAttemptAt,
            AsOffset(mergeStep?.CompletedAt ?? mergeStep?.StartedAt),
            options,
            now,
            inFlight);
        // Load review archives only when every other eligibility fact holds.
        // Most cards in these lanes have no gate failure to retry.
        var reviewLookup = MonolithTransitionAdapter.GateEnvironmentRetryPolicyOperatorReplayRefusal(withoutReview)
            == GateEnvironmentRetryReasons.NoPassedReview
            ? HasPassedReview(job.Key ?? job.TaskKey, deliverySha)
            : new GateEnvironmentReviewLookup(false, null);

        return new GateEnvironmentRetryEvaluation(
            withoutReview with { ReviewPassed = reviewLookup.Passed },
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
        var maximum = evaluation.Facts.Backoff.Count;
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

        var now = _time.GetUtcNow();
        var prior = IntegrationLaneEvents.Read(job.FolderPath);
        var sequence = Math.Max(now.UtcTicks, prior.Select(e => e.Sequence).DefaultIfEmpty(0).Max() + 1);
        using var deliveryScope = IntegrationLaneEvents.BeginDelivery(sequence);
        var deliveredAt = ReviewSubjectStore.Read(job.FolderPath)?.CompletedAtUtc;
        IntegrationLaneEvents.Append(job.FolderPath, job.ProjectName, job.Id, "enqueued",
            deliveredAtUtc: deliveredAt, reason: "gate-environment-retry", atUtc: now);
        IntegrationLaneEvents.Append(job.FolderPath, job.ProjectName, job.Id, "dequeued", atUtc: now);
        MergeIntoIntegrationResult result;
        try
        {
            result = await _runner.RunAsync(
                job.ProjectName,
                job.Id,
                job.FolderPath,
                job.WatchPath,
                evaluation.IntegrationBranch,
                ct,
                evaluation.IntegrationStrategy,
                PipelineTypes.Resolve(job)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IntegrationLaneEvents.Append(job.FolderPath, job.ProjectName, job.Id, "ejected",
                outcome: "error", reason: ex.Message, atUtc: _time.GetUtcNow());
            throw;
        }
        IntegrationLaneEvents.Append(job.FolderPath, job.ProjectName, job.Id,
            result.Outcome.IsSuccessfulIntegration() ? "ready-to-publish" : "ejected",
            outcome: result.Outcome.ToString(), reason: result.Error,
            receipt: result.MergedSha, atUtc: _time.GetUtcNow());

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
        TaskInfo job, GateEnvironmentRetryEvaluation evaluation, bool continueOnce, CancellationToken ct)
    {
        if (evaluation.Ledger.Parked) return false;
        var step = evaluation.MergeStep;
        if (step is null || evaluation.DeliverySha is null) return false;

        var attempts = Math.Clamp(evaluation.Ledger.AttemptsSpent, 0, evaluation.Facts.Backoff.Count);
        var reason = MonolithTransitionAdapter.GateEnvironmentRetryPolicyParkedReason(
            attempts,
            evaluation.IntegrationBranch,
            step.Reason) + " Gate evidence: post-steps/pre-develop-build-gate-*.log (resource measurements and slow tests).";
        _pipelineLog.RecordStep(job.FolderPath, step with { Reason = reason });
        GateEnvironmentRetryReceipts.RecordParked(
            _timeline,
            job.FolderPath,
            evaluation.DeliverySha,
            attempts,
            reason);
        _logger.LogWarning(
            "gate-environment-retry parked project={Project} job={JobId} attempts={Attempts}",
            job.ProjectName,
            job.Id,
            attempts);
        if (continueOnce)
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
    GateEnvironmentRetryFacts Facts,
    GateEnvironmentRetryLedger Ledger,
    string? DeliverySha,
    string IntegrationBranch,
    string IntegrationStrategy,
    PipelineStepExecution? MergeStep,
    GateEnvironmentReviewLookup ReviewLookup);
