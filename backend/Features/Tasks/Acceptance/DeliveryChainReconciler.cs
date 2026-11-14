using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Rechecks integration when the target ref or delivery changes. AGT-3060: the
/// lane rule is the AI Operator decider <c>delivery-chain</c>
/// (<see cref="DeliveryChainPolicy"/>); the <c>OperatorDecision</c> switch
/// selects whether the monolith decides it in process or the engine does. This
/// service collects the facts and applies the decision or the engine receipt.
/// </summary>
public sealed class DeliveryChainReconciler : BackgroundService, IAiOperatorEffect
{
    internal const int MaxCompletedReopensPerPass = 20;
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _statuses;
    private readonly TaskTransitionService _transitions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeliveryChainReconciler> _logger;
    private readonly GateFailureRouter? _gateFailures;
    private readonly AiOperatorDecisionGateway _decisions;
    private readonly AgentStudio.Pipeline.TaskGateCancellationRegistry? _gateCancellation;
    private readonly TaskIntegrationRecoveryService? _recovery;
    private readonly AgentStudio.Runner.AutoReviewStrandingWatchdog? _stranding;
    private readonly AgentStudio.Runner.HumanReviewEscalation? _escalation;
    private readonly TimelineLog? _timeline;
    private readonly object _staleRecoveryGate = new();
    private readonly HashSet<string> _staleRecoveries = new(StringComparer.Ordinal);

    public DeliveryChainReconciler(TaskScannerService scanner,
        TaskIntegrationStatusService statuses, TaskTransitionService transitions,
        IConfiguration configuration, ILogger<DeliveryChainReconciler> logger,
        GateFailureRouter? gateFailures = null, AiOperatorDecisionGateway? decisions = null,
        AgentStudio.Pipeline.TaskGateCancellationRegistry? gateCancellation = null,
        TaskIntegrationRecoveryService? recovery = null,
        AgentStudio.Runner.AutoReviewStrandingWatchdog? stranding = null,
        TimelineLog? timeline = null,
        AgentStudio.Runner.HumanReviewEscalation? escalation = null)
    {
        _scanner = scanner;
        _statuses = statuses;
        _transitions = transitions;
        _configuration = configuration;
        _logger = logger;
        _gateFailures = gateFailures;
        _decisions = decisions ?? AiOperatorDecisionGateway.ForTestHost(configuration);
        _gateCancellation = gateCancellation;
        _recovery = recovery;
        _stranding = stranding;
        _timeline = timeline;
        _escalation = escalation;
    }

    public string Decider => AiOperatorDeciders.DeliveryChain;

    private enum Applied { None, Moved, Failed }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_configuration.GetValue("DeliveryChain:Guarded", true)) return 0;
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => card.State is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Completed)
            .ToArray();
        var recovering = cards.Where(NeedsStaleVerdictRecovery).ToArray();
        foreach (var card in recovering)
            QueueStaleVerdictRecovery(card);
        var recoveryKeys = recovering.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
        var statuses = _statuses.BuildLookup(cards);
        var candidates = cards
            .Where(card => !recoveryKeys.Contains(card.TaskKey)
                && AcceptanceIntegrationPolicy.IsIntegrationRequired(card))
            .Select(card =>
            {
                statuses.TryGetValue(card.TaskKey, out var status);
                return (Card: card, Status: status, Facts: Facts(card, status, completedReopenLimitReached: false));
            })
            .ToArray();
        // The per-pass bound counts every stale Delivered acceptance. Crossing
        // it stops the entire Delivered reopen batch; taking only the first N
        // would drain the lane over successive passes.
        var completedReopens = candidates.Count(item => item.Facts.AcceptedDeliveryStale);
        var completedAlarm = completedReopens > MaxCompletedReopensPerPass;
        var planned = 0;
        var waiting = 0;
        var moved = 0;
        var failed = 0;
        foreach (var (card, status, unbounded) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (status?.Status == IntegrationStatuses.Integrated)
                _gateCancellation?.Cancel(card.ProjectName, card.Id);
            var facts = unbounded with { CompletedReopenLimitReached = completedAlarm };
            var submission = _decisions.Submit(new AiOperatorDecisionRequest(card.ProjectName, card.Id, card.State,
                new AiOperatorDecisionPayload(Decider, SubjectKey(card, facts), DeliveryChain: facts),
                Period: null));
            if (submission.WaitsForEngine) { waiting++; continue; }
            var decision = submission.MonolithDecision!;
            if (decision.Disposition == AiOperatorDisposition.Hold) continue;
            planned++;
            switch (await ApplyDecisionAsync(card, status, decision, ct))
            {
                case Applied.Moved: moved++; break;
                case Applied.Failed: failed++; break;
            }
        }
        if (planned > 0 || waiting > 0 || completedAlarm)
        {
            if (completedAlarm)
                _logger.LogWarning(
                    "delivery-chain-reconcile-summary planned={Planned} waitingForEngine={Waiting} completedReopens={CompletedReopens} completedReopenLimit={CompletedReopenLimit} completedBlocked={CompletedBlocked} moved={Moved} failed={Failed} alarm=completed-reopen-limit",
                    planned, waiting, completedReopens, MaxCompletedReopensPerPass,
                    completedReopens, moved, failed);
            else
                _logger.LogInformation(
                    "delivery-chain-reconcile-summary planned={Planned} waitingForEngine={Waiting} completedReopens={CompletedReopens} completedReopenLimit={CompletedReopenLimit} completedBlocked={CompletedBlocked} moved={Moved} failed={Failed} alarm=none",
                    planned, waiting, completedReopens, MaxCompletedReopensPerPass,
                    0, moved, failed);
        }
        return moved;
    }

    /// <summary>
    /// Applies the engine's <c>delivery-chain</c> receipt. A card that left its
    /// lane or whose facts changed after submission is settled without an
    /// effect; the next pass asks again with the new facts.
    /// </summary>
    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var frozen = application.Payload.DeliveryChain
            ?? throw new InvalidDataException("The delivery-chain occurrence has no facts.");
        var entry = _scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, application.ProjectId, StringComparison.Ordinal));
        var card = entry is null ? null : _scanner.FindJob(application.TaskId, entry.Path);
        if (card is null || card.State != application.ExpectedSourceState) return;
        if (NeedsStaleVerdictRecovery(card))
        {
            QueueStaleVerdictRecovery(card);
            return;
        }
        _statuses.BuildLookup([card]).TryGetValue(card.TaskKey, out var status);
        if (SubjectKey(card, Facts(card, status, frozen.CompletedReopenLimitReached))
            != application.Payload.SubjectKey) return;
        if (await ApplyDecisionAsync(card, status, application.Decision, ct) == Applied.Failed)
            throw new InvalidOperationException($"delivery-chain-reconcile-failed task={card.TaskKey}");
    }

    internal static DeliveryChainOperatorFacts Facts(TaskInfo card, TaskIntegrationStatus? status,
        bool completedReopenLimitReached)
        => new(card.State,
            card.Phase == LifecyclePhases.Integrating,
            status?.Status == IntegrationStatuses.Integrated,
            DeliveryLanePolicy.RequiresEscalation(status),
            card.State == TaskStates.Completed && DeliveryLanePolicy.IsAcceptedDeliveryStale(card, status),
            completedReopenLimitReached,
            status?.Failure?.Code == AcceptedIntegrationFailureCodes.MergeConflict
                || status?.Failure?.ConflictReport is not null);

    private const string StaleVerdictCategory = "delivery-verdict-stale";

    /// <summary>Lane reason of an integrated delivery that enters Human Review.</summary>
    internal const string PromotionReason = "The delivery is integrated and waits for the human verdict.";

    /// <summary>
    /// The fact behind an acceptance park, without a slug prefix or a next step.
    /// The recorded failure reason is kept whenever there is one.
    /// </summary>
    internal static string IntegrationParkReason(string category, TaskIntegrationStatus? status)
    {
        // A stale verdict is no integration failure; an older failure on the
        // status would describe a delivery that is no longer current.
        if (category == StaleVerdictCategory)
            return "The review verdict no longer matches the current delivery, so it cannot be accepted.";
        var fact = category == "missing-delivery"
            ? "No delivery branch was found for this card, so its work cannot be integrated"
            : "The delivery could not be integrated";
        var failure = status?.Failure?.Reason?.Trim();
        if (string.IsNullOrEmpty(failure)) return fact + ".";
        return failure[^1] is '.' or '!' or '?' ? $"{fact}: {failure}" : $"{fact}: {failure}.";
    }

    private static string SubjectKey(TaskInfo card, DeliveryChainOperatorFacts facts)
        => AiOperatorDecisionGateway.FactsSubjectKey("delivery", card.Id, facts);

    private bool NeedsStaleVerdictRecovery(TaskInfo card)
    {
        if (card.State != TaskStates.AutoReview || _timeline is null || _stranding is null)
            return false;
        var events = _timeline.ReadAll(card.FolderPath).ToArray();
        var lastMove = Array.FindLastIndex(events, entry => entry.Kind == TimelineEventKinds.LaneChanged);
        return lastMove >= 0
            && events[lastMove].Details?.GetValueOrDefault(LaneChangeCauses.DetailQualifierKey)
                == StaleVerdictCategory
            && !events.Skip(lastMove + 1).Any(entry => entry.Kind == TimelineEventKinds.DeliveryReviewRequested);
    }

    private void QueueStaleVerdictRecovery(TaskInfo card)
    {
        if (_stranding is null || card.PendingIntent is not null) return;
        var key = $"{card.TaskKey}:{card.EnteredLaneAt.Ticks}";
        lock (_staleRecoveryGate)
        {
            if (!_staleRecoveries.Add(key)) return;
        }
        _ = Task.Run(async () =>
        {
            try { await CompleteStaleVerdictRecoveryAsync(card); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "delivery-chain-stale-recovery-failed task={TaskKey}", card.TaskKey);
            }
            finally
            {
                lock (_staleRecoveryGate) _staleRecoveries.Remove(key);
            }
        });
    }

    private async Task CompleteStaleVerdictRecoveryAsync(TaskInfo card)
    {
        var integration = await _stranding!.IntegrateCurrentDeliveryAsync(card);
        var current = _scanner.FindJob(card.Id, card.WatchPath);
        if (current is null || current.State != TaskStates.AutoReview
            || current.EnteredLaneAt != card.EnteredLaneAt
            || current.PendingIntent is not null
            || !NeedsStaleVerdictRecovery(current)) return;
        if (integration?.Outcome is MergeIntoIntegrationOutcome.Conflict
            or MergeIntoIntegrationOutcome.AgentRoundRequired)
        {
            // The coordinator normally starts the round. Persist the fallback
            // when its handoff left this delivery in Auto Review.
            if (current.PendingIntent is null)
            {
                var queued = _recovery is not null
                    && _statuses.BuildLookup([current]).TryGetValue(current.TaskKey, out var freshStatus)
                    && freshStatus is not null
                    && _recovery.Queue(current, freshStatus, DeliveryChainReasons.MergeRoundQueued,
                        TaskIntegrationRecoveryService.AcceptanceRailSource).Queued;
                if (!queued)
                    await EscalateRecoveryFailureAsync(current,
                        "The current delivery conflicts with develop, but a merge round could not be queued.",
                        DeliveryChainReasons.MergeRoundQueued, CancellationToken.None);
            }
            return;
        }
        if (integration?.Outcome.IsSuccessfulIntegration() != true
            || !_stranding.RequestFreshReview(current))
        {
            await EscalateRecoveryFailureAsync(current,
                "The delivery verdict became stale, but the current delivery could not be integrated and sent for review: "
                    + (integration?.Error ?? integration?.Outcome.ToString() ?? "integration unavailable"),
                DeliveryChainReasons.VerdictStale, CancellationToken.None);
            return;
        }
        _timeline?.Append(current.FolderPath,
            TimelineEventKinds.DeliveryReviewRequested, TimelineActors.System,
            "The current delivery was integrated after the stale verdict; a fresh review was requested.",
            details: new Dictionary<string, string> { ["reason"] = StaleVerdictCategory });
    }

    private async Task<Applied> ApplyDecisionAsync(TaskInfo card, TaskIntegrationStatus? status,
        AiOperatorDecision decision, CancellationToken ct)
    {
        if (decision.Effect == AiOperatorEffects.QueueMergeRound)
        {
            var round = _recovery is not null && status is not null
                ? _recovery.Queue(card, status, DeliveryChainReasons.MergeRoundQueued,
                    TaskIntegrationRecoveryService.AcceptanceRailSource)
                : new TaskIntegrationRecoveryResult(false,
                    "The integration recovery service or current delivery status is unavailable.");
            if (round.Queued) return Applied.Moved;
            _logger.LogWarning("delivery-chain-merge-round-failed task={TaskKey} reason={Reason}",
                card.TaskKey, round.Error);
            var parked = await EscalateRecoveryFailureAsync(card,
                "The delivery conflicts with develop, but a merge round could not be queued: " + round.Error,
                DeliveryChainReasons.MergeRoundQueued, ct);
            return parked.Status == MoveJobStatus.Success ? Applied.Moved : Applied.Failed;
        }
        string? target = decision.Effect switch
        {
            AiOperatorEffects.PromoteToHumanReview => TaskStates.HumanReview,
            AiOperatorEffects.ReturnToAutoReview => TaskStates.AutoReview,
            AiOperatorEffects.Escalate => TaskStates.Escalated,
            _ => null,
        };
        if (target is null) return Applied.None;
        var fatal = DeliveryLanePolicy.RequiresEscalation(status);
        string? gateParkReason = null;
        string? gateParkCategory = null;
        // AGT-3009: a red merge gate is classified and routed before it may
        // park. Only a route that parks reaches a parked lane, and then with
        // a typed [category] reason instead of an operator-decision park.
        if (fatal && _gateFailures is not null && GateFailureRouter.Handles(status))
        {
            var routing = await _gateFailures.RouteAsync(card, status!, ct);
            if (routing.Parks)
            {
                gateParkReason = routing.ParkReason;
                gateParkCategory = routing.Route.Category;
            }
            else if (card.State == TaskStates.HumanReview
                     && routing.Route.Action == GateFailureRouteAction.ReplayGate)
            {
                // The replay ladder leaves accepted-integration cards to their
                // backstop, so a stale Human Review card goes back to Auto
                // Review, where the ladder replays it.
                fatal = false;
                target = TaskStates.AutoReview;
            }
            else return Applied.None;
        }
        // AGT-3428: no gate ran, so there is nothing to classify. The park
        // reason is the message naming the missing definition and its example.
        if (fatal && gateParkReason is null
            && status?.Failure?.Code == AcceptedIntegrationFailureCodes.VerifyDefinitionMissing)
            gateParkReason = $"[{AcceptedIntegrationFailureCodes.VerifyDefinitionMissing}] {status.Failure.Reason} "
                             + "Then use Retry integration.";
        var category = fatal
            ? gateParkCategory ?? status?.Failure?.Code ?? (status?.Status == IntegrationStatuses.NoBranch
                ? "missing-delivery" : "integration-failed")
            : StaleVerdictCategory;
        // AGT-3586: a park reason states what happened, once, as a sentence.
        // The category travels as the transition detail, and the park copy
        // offers the next step from the buttons the lane really has.
        // AGT-3621: a promotion is no failure. It used to be recorded as a stale
        // verdict, which parked an integrated card behind a false banner.
        var promotion = decision.Effect == AiOperatorEffects.PromoteToHumanReview;
        var reason = promotion
            ? PromotionReason
            : gateParkReason ?? IntegrationParkReason(category, status);
        var outcome = await _transitions.MoveAsync(card.Id, target, card.WatchPath, ct,
            cause: TimelineActors.System, reason: reason,
            expectedSourceState: card.State, suppressProductExecution: true,
            transitionCause: promotion ? LaneChangeCauses.Promoted : LaneChangeCauses.AcceptanceIntegrationFailed,
            transitionDetail: promotion ? DeliveryChainReasons.Integrated : category);
        if (outcome.Status == MoveJobStatus.Success)
        {
            if (target == TaskStates.AutoReview
                && _scanner.FindJob(card.Id, card.WatchPath) is { } returned)
            {
                if (decision.ReasonCode is DeliveryChainReasons.VerdictStale
                    or DeliveryChainReasons.AcceptedDeliveryStale)
                {
                    if (_stranding is null)
                    {
                        var parked = await EscalateRecoveryFailureAsync(returned,
                            "The delivery verdict became stale, but the current delivery could not be integrated and sent for review: integration unavailable",
                            DeliveryChainReasons.VerdictStale, ct);
                        return parked.Status == MoveJobStatus.Success ? Applied.Moved : Applied.Failed;
                    }
                    TaskJsonFile.UpdateField(returned.FolderPath, "phase",
                        LifecyclePhases.Integrating, _logger);
                    _scanner.InvalidateCache();
                    _scanner.PublishCoreFromKnownFolder(returned.FolderPath);
                    _timeline?.Append(returned.FolderPath,
                        TimelineEventKinds.DeliveryIntegrationRequested, TimelineActors.System,
                        "The stale verdict returned to Auto Review; integration of the current delivery was queued.",
                        details: new Dictionary<string, string> { ["reason"] = decision.ReasonCode });
                    QueueStaleVerdictRecovery(returned);
                }
                else
                {
                    TaskJsonFile.UpdateField(returned.FolderPath, "phase",
                        LifecyclePhases.Integrating, _logger);
                    _scanner.InvalidateCache();
                    _scanner.PublishCoreFromKnownFolder(returned.FolderPath);
                }
            }
            return Applied.Moved;
        }
        if (outcome.Status == MoveJobStatus.SourceStateMismatch) return Applied.None;
        _logger.LogWarning("delivery-chain-reconcile-failed task={TaskKey} target={Target} status={Status} message={Message}",
            card.TaskKey, target, outcome.Status, outcome.Message);
        return Applied.Failed;
    }

    private Task<MoveJobOutcome> EscalateRecoveryFailureAsync(TaskInfo card, string reason,
        string category, CancellationToken ct)
        => _escalation is null
            ? Task.FromResult(new MoveJobOutcome(MoveJobStatus.Failure, "Escalation service unavailable."))
            : _escalation.EscalateAsync(card.Id, card.WatchPath, card.ProjectName,
                category, reason, ct, expectedSourceState: card.State);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "delivery-chain-reconcile-failed"); }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
