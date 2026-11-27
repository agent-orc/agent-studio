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
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private readonly GitService? _git;
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
        AgentStudio.Runner.HumanReviewEscalation? escalation = null, GitService? git = null)
    {
        _git = git;
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
        var recalled = await RecallObsoleteStaleParksAsync(ct);
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
                return (Card: card, Status: status, Facts: CurrentFacts(card, status, completedReopenLimitReached: false));
            })
            .ToArray();
        // The per-pass bound counts every stale Delivered acceptance. Crossing
        // it stops the entire Delivered reopen batch; taking only the first N
        // would drain the lane over successive passes.
        var completedReopens = candidates.Count(item => item.Facts.AcceptedDeliveryStale);
        var completedAlarm = completedReopens > MaxCompletedReopensPerPass;
        var planned = 0;
        var waiting = 0;
        var moved = recalled;
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
        if (frozen.Recovery is { } recoveryFacts)
        {
            if (_stranding is null || card.PendingIntent is not null) return;
            if (recoveryFacts.Recalling)
                await RecallStaleParkAsync(card, ct, application);
            else if (StaleVerdictRecovery.Read(card) is { Phase: "awaiting-decision" } receipt
                && receipt.Delivery == RecoveryDeliveryIdentity(card)
                && (receipt.LaneEnteredTicks == 0 || receipt.LaneEnteredTicks == card.EnteredLaneAt.Ticks))
            {
                _statuses.BuildLookup([card]).TryGetValue(card.TaskKey, out var currentStatus);
                var facts = RecoveryFacts(card, currentStatus, receipt);
                if (SubjectKey(card, facts) == application.Payload.SubjectKey)
                    await ApplyRecoveryDecisionAsync(card, receipt, application.Decision, ct);
            }
            return;
        }
        if (NeedsStaleVerdictRecovery(card))
        {
            QueueStaleVerdictRecovery(card);
            return;
        }
        _statuses.BuildLookup([card]).TryGetValue(card.TaskKey, out var status);
        if (SubjectKey(card, CurrentFacts(card, status, frozen.CompletedReopenLimitReached))
            != application.Payload.SubjectKey) return;
        if (await ApplyDecisionAsync(card, status, application.Decision, ct) == Applied.Failed)
            throw new InvalidOperationException($"delivery-chain-reconcile-failed task={card.TaskKey}");
    }

    private DeliveryChainOperatorFacts CurrentFacts(TaskInfo card, TaskIntegrationStatus? status,
        bool completedReopenLimitReached)
        => Facts(card, status, completedReopenLimitReached) with
        { ReviewVerdictStale = _stranding?.HasSupersededVerdict(card) == true };

    private static bool Verified(TaskIntegrationStatus? status)
        => status?.Status == IntegrationStatuses.Integrated
            && status.Verification is { State: IntegrationVerificationStates.Verified, GateFailed: false };

    private async Task<int> RecallObsoleteStaleParksAsync(CancellationToken ct)
    {
        if (_stranding is null || _timeline is null) return 0;
        var count = 0;
        foreach (var card in _scanner.ScanAllAutomationJobs().Where(c => c.State == TaskStates.Escalated
                     && c.PendingIntent is null))
            count += await RecallStaleParkAsync(card, ct);
        return count;
    }

    private async Task<int> RecallStaleParkAsync(TaskInfo card, CancellationToken ct,
        AiOperatorApplication? application = null)
    {
        if (_stranding is null || _timeline is null || card.State != TaskStates.Escalated
            || card.PendingIntent is not null) return 0;
        var taskWriteTicks = File.GetLastWriteTimeUtc(Path.Combine(card.FolderPath, "task.json")).Ticks;
        var park = ParkedBlockerMarker.TryRead(card.FolderPath, _logger);
        if (park?.CauseDetail == "operator-decision") return 0;
        var lastMove = _timeline.ReadAll(card.FolderPath)
            .LastOrDefault(e => e.Kind == TimelineEventKinds.LaneChanged);
        // Match both the current marker and its machine-owned lane receipt.
        // A later human hold, even with copied prose, must stay in place.
        var machineOwned = park?.Cause == LaneChangeCauses.Escalated
            && lastMove?.Actor == TimelineActors.System
            && lastMove.Details?.GetValueOrDefault(LaneChangeCauses.DetailQualifierKey) == park.CauseDetail;
        var recovery = StaleVerdictRecovery.Read(card);
        var recoveryOwned = recovery is { Phase: not "reviewed" };
        var stalePark = recoveryOwned
            || park?.CauseDetail is StaleVerdictCategory or AcceptedIntegrationFailureCodes.GateCapacity
                && park.Reason.StartsWith("The delivery verdict became stale,", StringComparison.Ordinal);
        if (!machineOwned || !stalePark) return 0;
        if (!_stranding.PrepareCurrentDelivery(card)) return 0;
        _statuses.BuildLookup([card]).TryGetValue(card.TaskKey, out var status);
        var retryOwned = park!.CauseDetail == AcceptedIntegrationFailureCodes.GateCapacity
            || recoveryOwned && recovery!.Delivery != RecoveryDeliveryIdentity(card);
        if (!Verified(status) && GateFailureRouter.Handles(status) && _gateFailures is not null)
            retryOwned = !(await _gateFailures.RouteAsync(card, status!, ct)).Parks;
        var facts = CurrentFacts(card, status, false) with
        { Recovery = new StaleVerdictRecoveryFacts(Verified(status), Recalling: true,
            SystemOwned: machineOwned, StalePark: stalePark, RetryOwnerAvailable: retryOwned,
                DeliveryIdentity: RecoveryDeliveryIdentity(card), VerdictIdentity: _stranding.CurrentVerdictIdentity(card),
                LaneEnteredTicks: card.EnteredLaneAt.Ticks) };
        var decision = application?.Decision;
        if (application is not null && SubjectKey(card, facts) != application.Payload.SubjectKey) return 0;
        if (decision is null)
        {
            var submission = SubmitRecovery(card, facts);
            if (submission.WaitsForEngine) return 0;
            decision = submission.MonolithDecision!;
        }
        if (decision.Effect != AiOperatorEffects.ReturnToAutoReview) return 0;
        var moved = await _transitions.MoveAsync(card.Id, TaskStates.AutoReview, card.WatchPath, ct,
            cause: TimelineActors.System,
            reason: Verified(status)
                ? "The machine stale-verdict park is obsolete: the current delivery is integrated and verified. Fresh review was queued."
                : "The machine stale-verdict park is obsolete: automatic integration recovery owns the retry. Fresh review will follow integration.",
            expectedSourceState: card.State, expectedSourceEnteredLaneAt: card.EnteredLaneAt,
            expectedSourceTaskJsonWriteTicks: taskWriteTicks,
            suppressProductExecution: true,
            transitionCause: LaneChangeCauses.AcceptanceIntegrationFailed,
            transitionDetail: StaleVerdictCategory);
        if (moved.Status != MoveJobStatus.Success) return 0;

        if (_scanner.FindJob(card.Id, card.WatchPath) is { } current)
        {
            TaskJsonFile.UpdateField(current.FolderPath, "phase", LifecyclePhases.Integrating, _logger);
            _scanner.InvalidateCache();
            _scanner.PublishCoreFromKnownFolder(current.FolderPath);
            BeginRecovery(current);
            QueueStaleVerdictRecovery(current);
        }
        return 1;
    }

    private string RecoveryDeliveryIdentity(TaskInfo card)
        => _stranding?.CurrentDeliveryIdentity(card)
            ?? ReviewSubjectStore.Read(card.FolderPath)?.ResultSha ?? "unavailable";

    internal StaleVerdictRecovery BeginRecovery(TaskInfo card)
    {
        // Legacy cards use changing activity time as their lane-entry fallback.
        // Freeze that entry before a new log file can change the scanner value.
        lock (TaskJsonFile.SyncRoot(card.FolderPath))
        {
            using var json = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(card.FolderPath, "task.json")));
            if (!json.RootElement.TryGetProperty("enteredLaneAt", out var entered)
                || entered.ValueKind != System.Text.Json.JsonValueKind.String
                || !entered.TryGetDateTime(out _))
            {
                TaskJsonFile.UpdateFieldOrThrow(card.FolderPath, "enteredLaneAt", card.EnteredLaneAt);
                _scanner.InvalidateCache();
            }
        }
        var delivery = RecoveryDeliveryIdentity(card);
        var existing = StaleVerdictRecovery.Read(card);
        if (existing is not null && existing.Delivery == delivery
            && (existing.LaneEnteredTicks == 0 || existing.LaneEnteredTicks == card.EnteredLaneAt.Ticks)
            && existing.Phase is not ("reviewed" or "escalated")) return existing;
        var recovery = new StaleVerdictRecovery(delivery,
            _stranding?.CurrentVerdictIdentity(card) ?? "unavailable", "integrating",
            LaneEnteredTicks: card.EnteredLaneAt.Ticks);
        recovery.Write(card);
        return recovery;
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
        if (card.State != TaskStates.AutoReview) return false;
        if (StaleVerdictRecovery.Read(card) is { Phase: not ("reviewed" or "escalated") }) return true;
        if (_timeline is null || _stranding is null) return false;
        if (_stranding.HasSupersededVerdict(card)) return true;
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
        BeginRecovery(card);
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
        var live = _scanner.FindJob(card.Id, card.WatchPath);
        if (live is null || live.State != TaskStates.AutoReview
            || live.EnteredLaneAt != card.EnteredLaneAt || live.PendingIntent is not null) return;
        card = live;
        var receipt = BeginRecovery(card);
        _stranding!.PrepareCurrentDelivery(card);
        _statuses.BuildLookup([card]).TryGetValue(card.TaskKey, out var before);
        if (!Verified(before) && GateFailureRouter.Handles(before) && _gateFailures is not null)
        {
            var route = await _gateFailures.RouteAsync(card, before!);
            var routed = _scanner.FindJob(card.Id, card.WatchPath);
            if (routed is null || routed.State != TaskStates.AutoReview
                || routed.EnteredLaneAt != card.EnteredLaneAt || routed.PendingIntent is not null
                || receipt.Delivery != RecoveryDeliveryIdentity(routed)) return;
            _statuses.BuildLookup([routed]).TryGetValue(routed.TaskKey, out var afterRoute);
            if (Verified(afterRoute))
            {
                receipt = receipt with { Phase = "awaiting-decision",
                    DecisionFacts = new StaleVerdictRecoveryFacts(true) };
                receipt.Write(routed);
                var recovered = SubmitRecovery(routed, RecoveryFacts(routed, afterRoute, receipt));
                if (!recovered.WaitsForEngine)
                    await ApplyRecoveryDecisionAsync(routed, receipt, recovered.MonolithDecision!, CancellationToken.None);
                return;
            }
            card = routed;
            if (route.Parks)
            {
                (receipt with { Phase = "escalated" }).Write(card);
                await EscalateRecoveryFailureAsync(card, receipt.Failure(route.ParkReason ?? route.Route.Reason),
                    route.Route.Category ?? StaleVerdictCategory, CancellationToken.None);
            }
            else (receipt with { Phase = "waiting-retry" }).Write(card);
            return;
        }
        // The environment ladder owns replay. Never spend its budget by
        // running another gate from the stale-verdict sweep.
        if (!Verified(before) && receipt.Phase == "waiting-retry"
            && (GateFailureRouter.Handles(before) || before?.ReachUnavailable == true
                || IntegrationGateJournal.Read(card.FolderPath) is not null)) return;
        var integration = Verified(before) || receipt.Phase is "awaiting-decision" or "requesting-review"
            ? null : await _stranding!.IntegrateCurrentDeliveryAsync(card);
        var current = _scanner.FindJob(card.Id, card.WatchPath);
        if (current is null || current.State != TaskStates.AutoReview
            || current.EnteredLaneAt != card.EnteredLaneAt
            || current.PendingIntent is not null
            || !NeedsStaleVerdictRecovery(current)
            || receipt.Delivery != RecoveryDeliveryIdentity(current)) return;
        _statuses.BuildLookup([current]).TryGetValue(current.TaskKey, out var fresh);
        var awaitingDecision = receipt.Phase is "awaiting-decision" or "requesting-review";
        var decisionFacts = (awaitingDecision ? receipt.DecisionFacts : null) ?? new StaleVerdictRecoveryFacts(false,
            Successful: integration?.Outcome.IsSuccessfulIntegration() == true,
            Conflict: integration?.Outcome is MergeIntoIntegrationOutcome.Conflict or MergeIntoIntegrationOutcome.AgentRoundRequired,
            GateOwned: GateFailureRouter.Handles(fresh),
            Capacity: integration?.FailureCode == AcceptedIntegrationFailureCodes.GateCapacity);
        receipt = receipt with { Phase = "awaiting-decision", DecisionFacts = decisionFacts,
            Error = integration?.Error ?? integration?.Outcome.ToString()
                ?? (awaitingDecision ? receipt.Error : null) ?? fresh?.Failure?.Reason
                ?? "integration service or settled delivery unavailable" };
        receipt.Write(current);
        var submission = SubmitRecovery(current, RecoveryFacts(current, fresh, receipt));
        if (submission.WaitsForEngine) return;
        await ApplyRecoveryDecisionAsync(current, receipt, submission.MonolithDecision!, CancellationToken.None);
    }

    private DeliveryChainOperatorFacts RecoveryFacts(TaskInfo card, TaskIntegrationStatus? status,
        StaleVerdictRecovery receipt)
        => CurrentFacts(card, status, false) with
        { Recovery = receipt.DecisionFacts! with { IntegratedVerified = Verified(status),
            DeliveryIdentity = receipt.Delivery, VerdictIdentity = receipt.Verdict,
            LaneEnteredTicks = card.EnteredLaneAt.Ticks } };

    private AiOperatorSubmission SubmitRecovery(TaskInfo card, DeliveryChainOperatorFacts facts)
    {
        var payload = new AiOperatorDecisionPayload(Decider, SubjectKey(card, facts), DeliveryChain: facts);
        var preview = _decisions.Preview(payload);
        if (preview.Disposition == AiOperatorDisposition.Hold) return new(null, preview);
        // A completed receipt with unchanged facts owns no future wake. Recovery
        // retries need a new bounded occurrence after the next sweep interval.
        return _decisions.Submit(new AiOperatorDecisionRequest(card.ProjectName, card.Id, card.State,
            payload, Period: ReconcileInterval));
    }

    private async Task ApplyRecoveryDecisionAsync(TaskInfo current, StaleVerdictRecovery receipt,
        AiOperatorDecision decision, CancellationToken ct)
    {
        if (decision.Effect == AiOperatorEffects.RequestFreshDeliveryReview)
        {
            await RequestRecoveredReviewAsync(current, receipt);
            return;
        }
        if (decision.Effect == AiOperatorEffects.QueueMergeRound)
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
                {
                    (receipt with { Phase = "escalated" }).Write(current);
                    await EscalateRecoveryFailureAsync(current,
                        receipt.Failure("the current delivery conflicts with the target branch, but a merge round could not be queued"),
                        DeliveryChainReasons.MergeRoundQueued, CancellationToken.None);
                }
            }
            return;
        }
        if (decision.Effect == AiOperatorEffects.WaitDeliveryCapacity)
        {
            (receipt with { Phase = "waiting-capacity" }).Write(current);
            return;
        }
        if (decision.Effect == AiOperatorEffects.RouteDeliveryGateFailure)
        {
            // The next pass routes the recorded typed failure through its owner.
            (receipt with { Phase = "waiting-retry" }).Write(current);
            return;
        }
        if (decision.Effect != AiOperatorEffects.Escalate) return;
        (receipt with { Phase = "escalated" }).Write(current);
        await EscalateRecoveryFailureAsync(current,
            receipt.Failure(receipt.Error ?? decision.Detail),
            StaleVerdictCategory, CancellationToken.None);
    }

    private Task RequestRecoveredReviewAsync(TaskInfo card, StaleVerdictRecovery receipt)
    {
        (receipt with { Phase = "requesting-review" }).Write(card);
        if (!_stranding!.RequestFreshReview(card, receipt.Delivery)) return Task.CompletedTask;
        if (_timeline is not null && !_timeline.ReadAll(card.FolderPath).Any(entry =>
                entry.Kind == TimelineEventKinds.DeliveryReviewRequested
                && entry.Details?.GetValueOrDefault("delivery") == receipt.Delivery
                && entry.Details?.GetValueOrDefault("verdict") == receipt.Verdict))
            _timeline.Append(card.FolderPath,
                TimelineEventKinds.DeliveryReviewRequested, TimelineActors.System,
                $"Delivery {receipt.Delivery} is integrated; fresh review replaces verdict {receipt.Verdict}.",
                details: new Dictionary<string, string> { ["reason"] = StaleVerdictCategory,
                    ["delivery"] = receipt.Delivery, ["verdict"] = receipt.Verdict });
        (receipt with { Phase = "reviewed" }).Write(card);
        return Task.CompletedTask;
    }

    private async Task<Applied> ApplyDecisionAsync(TaskInfo card, TaskIntegrationStatus? status,
        AiOperatorDecision decision, CancellationToken ct)
    {
        if (decision.Effect == AiOperatorEffects.QueueMergeRound)
        {
            if (ReviewSubjectStore.Read(card.FolderPath) is { } subject)
                _git?.TryRecordDispatchRecovery(card, subject,
                    status?.Failure?.ConflictReport, "merge-round", subject.ResultSha + ":" + subject.RunAttemptId + ":" + status?.Failure?.ConflictReport?.IntegrationTipSha);
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
                        var receipt = BeginRecovery(returned);
                        _statuses.BuildLookup([returned]).TryGetValue(returned.TaskKey, out var integrated);
                        if (Verified(integrated))
                        {
                            (receipt with { Phase = "requesting-review" }).Write(returned);
                            return Applied.Moved;
                        }
                        (receipt with { Phase = "escalated" }).Write(returned);
                        var parked = await EscalateRecoveryFailureAsync(returned,
                            receipt.Failure("integration service unavailable"),
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
        using var timer = new PeriodicTimer(ReconcileInterval);
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
