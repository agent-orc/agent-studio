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
    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly ProjectSettingsService _settings;
    private readonly RemoteDeliveryIntegrationCoordinator _integration;
    private readonly TaskTransitionService _transitions;
    private readonly HumanReviewEscalation _escalation;
    private readonly ILogger<AutoReviewDeliveryResumeService> _logger;

    public AutoReviewDeliveryResumeService(
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        TaskIntegrationStatusService integrationStatus,
        ProjectSettingsService settings,
        RemoteDeliveryIntegrationCoordinator integration,
        TaskTransitionService transitions,
        HumanReviewEscalation escalation,
        ILogger<AutoReviewDeliveryResumeService> logger)
    {
        _scanner = scanner;
        _authority = authority;
        _integrationStatus = integrationStatus;
        _settings = settings;
        _integration = integration;
        _transitions = transitions;
        _escalation = escalation;
        _logger = logger;
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

        var taskKey = CanonicalKey(task);
        var review = string.IsNullOrWhiteSpace(taskKey)
            ? null
            : _authority.GetTaskProjection(taskKey!).CurrentReviewAttempt;
        var settlement = RemoteDeliverySettlementStore.Read(task.FolderPath);
        if (!RemoteDeliverySettlementStore.MatchesAttempt(settlement, review?.AttemptId))
            settlement = null;

        var decision = AutoReviewResumePolicy.Decide(
            task.State,
            task.Fixture,
            review?.State,
            review?.Outcome,
            IntegrationStatuses.IsMerged(ReadIntegrationStatus(task)),
            settlement?.Stage,
            settlement?.ShouldIntegrate ?? false,
            IntegrationGateJournal.Read(task.FolderPath) is not null);

        if (decision.Action == AutoReviewResumeAction.None)
            return new AutoReviewResumeOutcome(decision.Action, decision.Reason, Resumed: false);

        _logger.LogInformation(
            "auto-review-delivery-resume-started project={Project} job={JobId} attempt={AttemptId} action={Action} reason={Reason} source={Source}",
            task.ProjectName, task.Id, review!.AttemptId, decision.Action, decision.Reason, source);

        var integrationOutcome = decision.Reason == AutoReviewResumePolicy.Reasons.DeliveryGateFailed
            ? AcceptedIntegrationFailureCodes.DeliveryGateFailed
            : settlement?.IntegrationOutcome;
        var integrationDetail = settlement?.IntegrationDetail;

        if (decision.Action == AutoReviewResumeAction.StartIntegration)
        {
            // The coordinator coalesces a replay of the same delivery key and
            // the merge runner answers AlreadyMerged for a delivery the branch
            // already contains, so re-entering here cannot double-merge.
            var request = BuildIntegrationRequest(task, settlement!);
            var result = await _integration.EnqueueAsync(request).ConfigureAwait(false);
            integrationOutcome = result.Outcome.ToString();
            integrationDetail = result.AutomaticRecoveryDetail;
            RemoteDeliverySettlementStore.Advance(
                task.FolderPath,
                RemoteDeliverySettlementStage.IntegrationSettled,
                integrationOutcome,
                integrationDetail);
        }
        else if (decision.Reason == AutoReviewResumePolicy.Reasons.DeliveryGateFailed)
        {
            _integration.RecordGateFailure(
                BuildIntegrationRequest(task, settlement!),
                settlement!.GateReason);
            RemoteDeliverySettlementStore.Advance(
                task.FolderPath,
                RemoteDeliverySettlementStage.IntegrationSettled,
                integrationOutcome);
        }

        return await CompleteTransitionAsync(
                task,
                review,
                decision,
                integrationOutcome,
                integrationDetail,
                source,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The normal <c>4-auto-review -&gt; 5-human-review</c> transition the
    /// interrupted request never reached. It is the same move the report
    /// endpoint makes, with the same park verdict, so the acceptance rail sees
    /// an ordinary reviewed-and-integrated card afterwards and carries it into
    /// the completed lane on its own schedule.
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
        var moved = await _transitions.MoveAsync(
            task.Id,
            TaskStates.HumanReview,
            task.WatchPath,
            ct,
            cause: $"remote-review-resume:{review.AttemptId}",
            reason: integrationDetail,
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
        RemoteDeliverySettlementRecord settlement)
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
            settlement.DeliveredAtUtc);

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

    private static string? CanonicalKey(TaskInfo task)
        => new[] { task.Key, task.TaskKey, task.Id }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
