using System.Collections.Concurrent;
using AgentStudio.Pipeline;

namespace AgentStudio.Runner;

/// <param name="Action">What the policy decided for this card.</param>
/// <param name="Reason">Why, as a stable token.</param>
/// <param name="Resumed">True when this pass actually moved the card forward.</param>
/// <param name="Detail">Operator-facing detail, e.g. the integration outcome or the lane-write failure.</param>
public sealed record AutoReviewResumeOutcome(
    AutoReviewResumeAction Action,
    string Reason,
    bool Resumed,
    string? Detail = null);

/// <param name="Scanned">Cards in <c>4-auto-review</c> the sweep looked at.</param>
/// <param name="Integrated">Cards whose <c>remote-delivery-integration</c> this sweep started.</param>
/// <param name="Completed">Cards whose pending lane transition this sweep finished.</param>
/// <param name="Failed">Cards whose resume was attempted and did not land.</param>
public sealed record AutoReviewResumeReport(int Scanned, int Integrated, int Completed, int Failed);

/// <summary>
/// Restart-safe resume of the post-review delivery sequence (AGT-2860).
///
/// <para>
/// Boundary validation (is this an Auto Review card with a current attempt),
/// application coordination (read the authority projection, the integration
/// verdict and the settlement sidecar), pure decision
/// (<see cref="AutoReviewResumePolicy"/>), then bounded side effects: at most
/// one integration enqueue and one lane transition per card, both idempotent.
/// </para>
/// <para>
/// It never creates a ReviewAttempt and never re-reviews a subject. A card that
/// already passed has earned its verdict; the only thing a restart can lose is
/// the work that was supposed to follow it, and that is exactly what this
/// replays. The 45-minute re-review an operator had to trigger by hand for
/// AGT-2855 is the cost this exists to remove.
/// </para>
/// </summary>
public sealed class AutoReviewDeliveryResumeService
{
    public static class Reasons
    {
        /// <summary>
        /// The reviewed RunAttempt is no longer the task's current settled
        /// delivery: a reissue leased a new run, without a new review generation
        /// (AGT-3091).
        /// </summary>
        public const string ReviewedDeliverySuperseded = "reviewed-delivery-superseded";

        /// <summary>
        /// Another caller (the report request, or an earlier pass) is already
        /// driving this card's integration; this pass does not wait for it.
        /// </summary>
        public const string IntegrationInFlight = "integration-in-flight";
    }

    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly ProjectSettingsService _settings;
    private readonly RemoteDeliveryIntegrationCoordinator _integration;
    private readonly TaskTransitionService _transitions;
    private readonly HumanReviewEscalation _escalation;
    private readonly ILogger<AutoReviewDeliveryResumeService> _logger;
    private readonly CauseBreakerService? _causeBreakers;
    private readonly AutoReviewPostProcessingQueue? _queue;
    private readonly TimeProvider _time;
    // A superseded delivery is logged once per task and review generation, not
    // on every sweep.
    private readonly ConcurrentDictionary<string, string> _reportedSupersessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resumeGates =
        new(StringComparer.OrdinalIgnoreCase);

    public AutoReviewDeliveryResumeService(
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        TaskIntegrationStatusService integrationStatus,
        ProjectSettingsService settings,
        RemoteDeliveryIntegrationCoordinator integration,
        TaskTransitionService transitions,
        HumanReviewEscalation escalation,
        ILogger<AutoReviewDeliveryResumeService> logger,
        CauseBreakerService? causeBreakers = null,
        AutoReviewPostProcessingQueue? queue = null,
        TimeProvider? time = null)
    {
        _scanner = scanner;
        _authority = authority;
        _integrationStatus = integrationStatus;
        _settings = settings;
        _integration = integration;
        _transitions = transitions;
        _escalation = escalation;
        _logger = logger;
        _causeBreakers = causeBreakers;
        _queue = queue;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// One pass over every card in <c>4-auto-review</c>. Runs at boot and from
    /// the post-processing backstop; a card that needs nothing costs one
    /// authority lookup and one cached integration read. Never throws - one
    /// unresumable card must not stop the rest.
    /// </summary>
    public async Task<AutoReviewResumeReport> RunOnceAsync(string source, CancellationToken ct = default)
    {
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(task => string.Equals(task.State, TaskStates.AutoReview, StringComparison.Ordinal)
                           && !task.Fixture)
            .ToList();

        var integrated = 0;
        var completed = 0;
        var failed = 0;
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            AutoReviewResumeOutcome outcome;
            try
            {
                outcome = await ResumeAsync(card, source, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(
                    ex,
                    "auto-review-delivery-resume-failed project={Project} job={JobId} source={Source}",
                    card.ProjectName, card.Id, source);
                continue;
            }

            if (!outcome.Resumed)
            {
                if (outcome.Action != AutoReviewResumeAction.None) failed++;
                continue;
            }
            if (outcome.Action == AutoReviewResumeAction.StartIntegration) integrated++;
            else completed++;
        }

        var report = new AutoReviewResumeReport(cards.Count, integrated, completed, failed);
        if (integrated > 0 || completed > 0 || failed > 0)
        {
            _logger.LogInformation(
                "auto-review-delivery-resume source={Source} scanned={Scanned} integrated={Integrated} completed={Completed} failed={Failed}",
                source, report.Scanned, report.Integrated, report.Completed, report.Failed);
        }
        return report;
    }

    /// <summary>
    /// Resumes one card. Safe to call on every deferral pass: a card the policy
    /// has nothing to say about returns <see cref="AutoReviewResumeAction.None"/>
    /// without a single write.
    /// </summary>
    public async Task<AutoReviewResumeOutcome> ResumeAsync(
        TaskInfo task,
        string source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var gate = _resumeGates.GetOrAdd(task.ProjectName + "/" + task.Id,
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ResumeCoreAsync(task, source, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<AutoReviewResumeOutcome> ResumeCoreAsync(
        TaskInfo task, string source, CancellationToken ct)
    {
        var settledProbe = CanonicalKey(task) is { } key
            ? _authority.GetTaskProjection(key).CurrentReviewAttempt
            : null;
        if (settledProbe is { Outcome: ReviewTerminalOutcome.Pass, TerminalAt: not null })
            _causeBreakers?.ObserveGreen(settledProbe.TaskKey, settledProbe.AttemptId);

        // AGT-3091: the sweep's snapshot can be a whole gate old. AGT-2951 was
        // reissued to 2-ready at 18:09 and integrated from a 4-auto-review
        // snapshot at 18:12. Every decision and write below uses the lane as
        // it is now.
        if (CurrentAutoReviewCard(task) is not { } current)
            return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                AutoReviewResumePolicy.Reasons.OutsideAutoReview, Resumed: false);
        task = current;

        var taskKey = CanonicalKey(task);
        var review = string.IsNullOrWhiteSpace(taskKey)
            ? null
            : _authority.GetTaskProjection(taskKey!).CurrentReviewAttempt;
        if (review is not null && review.Reports.Any(report => report.AuthorityStatus == AttemptWriteStatus.Accepted))
        {
            var journal = RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId);
            var recorded = RemoteDeliverySettlementStore.Read(task.FolderPath);
            if (journal.Status == RemoteReviewSettlementReadStatus.Repair
                || journal.Status == RemoteReviewSettlementReadStatus.Missing
                   && recorded?.JournalRequired == true)
                return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                    "review-settlement-repair-required", Resumed: false, journal.Reason);
            if (journal.Entry is { } journalEntry
                && !RemoteReviewSettlementPolicy.MatchesAcceptedReview(journalEntry, review))
                return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                    "review-settlement-repair-required", Resumed: false, "review-journal-authority-mismatch");
            if (journal.Entry is { } restorable)
                RemoteReviewSettlementPolicy.RestoreDeliverySidecar(task, restorable);
        }
        var settlement = RemoteDeliverySettlementStore.Read(task.FolderPath);
        if (!RemoteDeliverySettlementStore.MatchesAttempt(settlement, review?.AttemptId))
            settlement = null;

        var deliveryMerged = ReadIntegrationStatus(task) == IntegrationStatuses.Integrated;
        var decision = AutoReviewResumePolicy.Decide(
            task.State,
            task.Fixture,
            review?.State,
            review?.Outcome,
            deliveryMerged,
            settlement?.Stage,
            settlement?.ShouldIntegrate ?? false,
            IntegrationGateJournal.Read(task.FolderPath) is not null);

        if (decision.Action == AutoReviewResumeAction.None)
            return new AutoReviewResumeOutcome(decision.Action, decision.Reason, Resumed: false);

        if (!IsCurrent(review!))
            return new AutoReviewResumeOutcome(AutoReviewResumeAction.None, "superseded-review-generation", Resumed: false);
        if (Superseded(task, review!) is { } superseded)
            return superseded;

        // The retry record belongs to this exact review generation. It retains
        // the budget across restarts and across both resume callers.
        var now = _time.GetUtcNow();
        var retry = AutoReviewResumeRetryStore.Read(task.FolderPath, review!.AttemptId);
        var resumeAttempts = retry?.Attempts ?? 0;
        var nextResumeAt = retry?.NextAtUtc;
        var resumeReason = retry?.Reason;
        if (resumeAttempts >= AutoReviewResumeRetryPolicy.MaxAttempts)
        {
            ShowResumeWait(task, resumeAttempts, nextResumeAt, resumeReason);
            return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                "delivery-resume-exhausted", false, resumeReason);
        }
        if (!AutoReviewResumeRetryPolicy.CanStart(resumeAttempts, nextResumeAt, now))
            return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                "delivery-resume-backoff", false, resumeReason);

        AutoReviewResumeRetryStore.Write(task.FolderPath, new AutoReviewResumeRetryRecord(
            review.AttemptId, resumeAttempts + 1,
            now + AutoReviewResumeRetryPolicy.Delay(resumeAttempts + 1),
            "resume interrupted before progress"));

        Task<MergeIntoIntegrationResult>? pendingIntegration = null;
        if (decision.Action == AutoReviewResumeAction.StartIntegration)
        {
            // The coordinator coalesces a replay of the same delivery key and
            // the merge runner answers AlreadyMerged for a delivery the branch
            // already contains, so re-entering here cannot double-merge. After
            // an operator re-entry the last completed result is exactly the
            // stale verdict being replaced, so it is not handed back. An
            // integration another caller is still driving is not waited for
            // (AGT-3091): it would hold this pass, and the lane snapshot of
            // every card behind it, for that caller's whole gate.
            if (!_integration.TryEnqueueUnlessInFlight(
                    BuildIntegrationRequest(task, settlement!, review!),
                    settlement!.DiscardCompletedReplay
                    || decision.Reason == AutoReviewResumePolicy.Reasons.OperatorReentry,
                    out var started))
            {
                if (retry is null) AutoReviewResumeRetryStore.Clear(task.FolderPath, review.AttemptId);
                else AutoReviewResumeRetryStore.Write(task.FolderPath, retry);
                return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                    Reasons.IntegrationInFlight, Resumed: false);
            }
            pendingIntegration = started;
        }

        _logger.LogInformation(
            "auto-review-delivery-resume-started project={Project} job={JobId} attempt={AttemptId} action={Action} reason={Reason} source={Source}",
            task.ProjectName, task.Id, review!.AttemptId, decision.Action, decision.Reason, source);

        var integrationOutcome = decision.Reason == AutoReviewResumePolicy.Reasons.DeliveryGateFailed
            ? AcceptedIntegrationFailureCodes.DeliveryGateFailed
            : settlement?.IntegrationOutcome;
        // A merged delivery owes no failure detail, whatever an earlier
        // integration attempt recorded as its park reason.
        var integrationDetail = deliveryMerged ? null : settlement?.IntegrationDetail;

        try
        {
            if (pendingIntegration is not null)
            {
                var result = await pendingIntegration.ConfigureAwait(false);
                if (!IsCurrent(review!))
                    return new AutoReviewResumeOutcome(AutoReviewResumeAction.None, "superseded-review-generation", Resumed: false);
                // A reissue during the gate: the coordinator or the publication
                // fence refused the delivery, and the card is not owed this park.
                if (Superseded(task, review!) is { } supersededDuringGate)
                    return supersededDuringGate;
                if (CurrentAutoReviewCard(task) is null)
                    return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
                        AutoReviewResumePolicy.Reasons.OutsideAutoReview, Resumed: false);
                integrationOutcome = result.Outcome.ToString();
                integrationDetail = RemoteDeliveryParkReason.For(result);
                RemoteDeliverySettlementStore.RecordIntegration(
                    task.FolderPath,
                    integrationOutcome,
                    integrationDetail);
            }
            else if (decision.Reason == AutoReviewResumePolicy.Reasons.DeliveryGateFailed)
            {
                _integration.RecordGateFailure(
                    BuildIntegrationRequest(task, settlement!, review!),
                    settlement!.GateReason);
                RemoteDeliverySettlementStore.Advance(
                    task.FolderPath,
                    RemoteDeliverySettlementStage.IntegrationSettled,
                    integrationOutcome);
            }

            var completed = await CompleteTransitionAsync(
                    task,
                    review,
                    decision,
                    integrationOutcome,
                    integrationDetail,
                    source,
                    ct)
                .ConfigureAwait(false);
            if (completed.Resumed || CurrentAutoReviewCard(task) is null)
                ClearResumeFailure(task, review.AttemptId);
            else if (completed.Action != AutoReviewResumeAction.None)
                RecordResumeFailure(task, review.AttemptId, completed.Detail ?? completed.Reason);
            return completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            RecordResumeFailure(task, review.AttemptId, ex.Message);
            _logger.LogWarning(ex,
                "auto-review-delivery-resume-failed project={Project} job={JobId} attempt={AttemptId}",
                task.ProjectName, task.Id, review.AttemptId);
            return new AutoReviewResumeOutcome(decision.Action,
                "delivery-resume-failed", false, ex.Message);
        }
    }

    private void RecordResumeFailure(TaskInfo task, string attemptId, string reason)
    {
        var retry = AutoReviewResumeRetryStore.Read(task.FolderPath, attemptId);
        if (retry is null) return;
        AutoReviewResumeRetryStore.Write(task.FolderPath, retry with { Reason = reason });
        ShowResumeWait(task, retry.Attempts, retry.NextAtUtc, reason);
        _logger.LogWarning(
            "auto-review-delivery-resume-deferred project={Project} job={JobId} attempt={AttemptId} tries={Tries} next={Next} reason={Reason}",
            task.ProjectName, task.Id, attemptId, retry.Attempts, retry.NextAtUtc, reason);
    }

    private void ShowResumeWait(TaskInfo task, int attempts, DateTimeOffset? nextAt, string? reason)
    {
        var exhausted = attempts >= AutoReviewResumeRetryPolicy.MaxAttempts;
        _queue?.SetWaitState(task.ProjectName, task.Id, new AutoReviewQueueWaitState(
            exhausted ? "delivery-resume-exhausted" : "delivery-resume-backoff",
            reason, attempts,
            (nextAt ?? _time.GetUtcNow()).UtcDateTime));
    }

    private void ClearResumeFailure(TaskInfo task, string attemptId)
    {
        var current = _scanner.FindJob(task.Id, task.WatchPath) ?? task;
        AutoReviewResumeRetryStore.Clear(current.FolderPath, attemptId);
        _queue?.ClearWaitState(task.ProjectName, task.Id);
    }

    /// <summary>The card as the scanner holds it now, or null when it is not a live Auto Review card.</summary>
    private TaskInfo? CurrentAutoReviewCard(TaskInfo task)
        => _scanner.FindJob(task.Id, task.WatchPath) is { Fixture: false } current
           && string.Equals(current.State, TaskStates.AutoReview, StringComparison.Ordinal)
            ? current
            : null;

    private bool IsCurrent(ReviewAttemptDto review)
        => string.Equals(_authority.GetTaskProjection(review.TaskKey).CurrentReviewAttempt?.AttemptId,
            review.AttemptId, StringComparison.Ordinal);

    /// <summary>
    /// Only the reviewed RunAttempt may integrate or leave Auto Review on this
    /// review's verdict (AGT-3091). A concern or finding reissue leases a new
    /// RunAttempt without a new review generation, so <see cref="IsCurrent"/>
    /// stays true for the old verdict until the next delivery is reviewed.
    /// Null while the reviewed delivery is still current.
    /// </summary>
    private AutoReviewResumeOutcome? Superseded(TaskInfo task, ReviewAttemptDto review)
    {
        if (_authority.IsReviewedDeliveryCurrent(review.AttemptId))
        {
            _reportedSupersessions.TryRemove(review.TaskKey, out _);
            return null;
        }
        if (!_reportedSupersessions.TryGetValue(review.TaskKey, out var reported)
            || !string.Equals(reported, review.AttemptId, StringComparison.Ordinal))
        {
            _reportedSupersessions[review.TaskKey] = review.AttemptId;
            _logger.LogInformation(
                "auto-review-delivery-resume-skipped project={Project} job={JobId} attempt={AttemptId} reason={Reason}",
                task.ProjectName, task.Id, review.AttemptId, Reasons.ReviewedDeliverySuperseded);
        }
        return new AutoReviewResumeOutcome(AutoReviewResumeAction.None,
            Reasons.ReviewedDeliverySuperseded, Resumed: false);
    }

    /// <summary>
    /// The normal <c>4-auto-review -&gt; 5-human-review</c> transition the
    /// interrupted request never reached. It is the same move the report
    /// endpoint makes, with the same park verdict. Human acceptance happens
    /// only after the published delivery reaches this lane.
    /// </summary>
    private async Task<AutoReviewResumeOutcome> CompleteTransitionAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        AutoReviewResumeDecision decision,
        string? integrationOutcome,
        string? integrationDetail,
        string source,
        CancellationToken ct)
    {
        var outcomeLabel = review.Outcome?.ToString() ?? "Pass";
        if (!IsCurrent(review))
            return new AutoReviewResumeOutcome(AutoReviewResumeAction.None, "superseded-review-generation", Resumed: false);
        var moved = await _transitions.MoveAsync(
            task.Id,
            TaskStates.HumanReview,
            task.WatchPath,
            ct,
            cause: $"remote-review-resume:{review.AttemptId}",
            reason: integrationDetail,
            authorityWrite: new AttemptWriteReference(
                review.AttemptId, review.LastFence, review.AuthorityEpoch,
                $"lane:resume:{review.AttemptId}"),
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: integrationOutcome ?? outcomeLabel).ConfigureAwait(false);

        if (moved.Status == MoveJobStatus.SourceStateMismatch)
        {
            // Another pass won the race and already moved the card. That is the
            // intended end state, so it is not a failure - only nothing left to
            // do here.
            return new AutoReviewResumeOutcome(
                decision.Action, decision.Reason, Resumed: false, moved.Message);
        }
        if (moved.Status == MoveJobStatus.IntegrationFailed)
            return new AutoReviewResumeOutcome(decision.Action, decision.Reason,
                Resumed: false, moved.Message);
        if (moved.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "auto-review-delivery-resume-lane-write-failed project={Project} job={JobId} status={Status} message={Message}",
                task.ProjectName, task.Id, moved.Status, moved.Message);
            return new AutoReviewResumeOutcome(
                decision.Action, decision.Reason, Resumed: false, moved.Message);
        }

        var folder = moved.NewFolderPath ?? task.FolderPath;
        _escalation.RecordRemoteReviewParkVerdict(
            task.ProjectName,
            task.Id,
            folder,
            outcomeLabel,
            review.TerminalReason ?? $"Remote Review settled {outcomeLabel} before the backend restarted.",
            ReviewAttemptChainSummary.Build(_authority
                .GetTaskProjection(review.TaskKey, includeArchived: true)
                .ReviewAttempts
                .Select(ReviewAttemptChainEntry.From)));
        RemoteDeliverySettlementStore.Advance(
            folder,
            RemoteDeliverySettlementStage.LaneSettled,
            integrationOutcome,
            integrationDetail);

        _logger.LogInformation(
            "auto-review-delivery-resumed project={Project} job={JobId} attempt={AttemptId} action={Action} reason={Reason} integration={Integration} source={Source}",
            task.ProjectName, task.Id, review.AttemptId, decision.Action, decision.Reason,
            integrationOutcome ?? "none", source);
        return new AutoReviewResumeOutcome(
            decision.Action, decision.Reason, Resumed: true, integrationOutcome);
    }

    private RemoteDeliveryIntegrationRequest BuildIntegrationRequest(
        TaskInfo task,
        RemoteDeliverySettlementRecord settlement,
        ReviewAttemptDto review)
        => new(
            task.ProjectName,
            task.Id,
            task.FolderPath,
            task.WatchPath,
            string.IsNullOrWhiteSpace(settlement.IntegrationBranch)
                ? TaskIntegrationBranch.Resolve(task, _settings.Get(task.ProjectName).IntegrationBranch)
                : settlement.IntegrationBranch,
            string.IsNullOrWhiteSpace(settlement.IntegrationStrategy)
                ? _settings.Get(task.ProjectName).IntegrationStrategy
                : settlement.IntegrationStrategy,
            string.IsNullOrWhiteSpace(settlement.PipelineType)
                ? PipelineTypes.Resolve(task)
                : settlement.PipelineType,
            settlement.DeliveredAtUtc,
            settlement.ReviewAttemptId,
            review.Subject.ExpectedResultSha);

    /// <summary>
    /// Git-derived integration verdict for one card. Never throws: an
    /// unavailable repository reads as "not merged", which at worst re-enters
    /// the idempotent integration path instead of skipping a real transition.
    /// </summary>
    private string? ReadIntegrationStatus(TaskInfo task)
    {
        try
        {
            return _integrationStatus.BuildLookup([task]).Values.FirstOrDefault()?.Status;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "AutoReviewDeliveryResumeService: integration verdict is best-effort");
            return null;
        }
    }

    private string? CanonicalKey(TaskInfo task)
    {
        var keys = new[] { task.Key, task.TaskKey, task.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return keys.FirstOrDefault(key =>
                   _authority.GetTaskProjection(key).CurrentReviewAttempt is not null)
               ?? keys.FirstOrDefault();
    }
}
