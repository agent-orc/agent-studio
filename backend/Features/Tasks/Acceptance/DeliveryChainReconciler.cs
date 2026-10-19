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

    public DeliveryChainReconciler(TaskScannerService scanner,
        TaskIntegrationStatusService statuses, TaskTransitionService transitions,
        IConfiguration configuration, ILogger<DeliveryChainReconciler> logger,
        GateFailureRouter? gateFailures = null, AiOperatorDecisionGateway? decisions = null)
    {
        _scanner = scanner;
        _statuses = statuses;
        _transitions = transitions;
        _configuration = configuration;
        _logger = logger;
        _gateFailures = gateFailures;
        _decisions = decisions ?? AiOperatorDecisionGateway.ForTestHost(configuration);
    }

    public string Decider => AiOperatorDeciders.DeliveryChain;

    private enum Applied { None, Moved, Failed }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_configuration.GetValue("DeliveryChain:Guarded", true)) return 0;
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => card.State is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Completed)
            .ToArray();
        var statuses = _statuses.BuildLookup(cards);
        var candidates = cards
            .Where(AcceptanceIntegrationPolicy.IsIntegrationRequired)
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
            completedReopenLimitReached);

    private static string SubjectKey(TaskInfo card, DeliveryChainOperatorFacts facts)
        => AiOperatorDecisionGateway.FactsSubjectKey("delivery", card.Id, facts);

    private async Task<Applied> ApplyDecisionAsync(TaskInfo card, TaskIntegrationStatus? status,
        AiOperatorDecision decision, CancellationToken ct)
    {
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
        var category = fatal
            ? gateParkCategory ?? status?.Failure?.Code ?? (status?.Status == IntegrationStatuses.NoBranch
                ? "missing-delivery" : "integration-failed")
            : "delivery-verdict-stale";
        var action = fatal
            ? "Recover the delivery, resolve the integration failure and rerun the gate."
            : "Integrate the current delivery and repeat review.";
        var outcome = await _transitions.MoveAsync(card.Id, target, card.WatchPath, ct,
            cause: TimelineActors.System, reason: gateParkReason ?? $"{category}: {action}",
            expectedSourceState: card.State, suppressProductExecution: true,
            transitionCause: LaneChangeCauses.AcceptanceIntegrationFailed,
            transitionDetail: category);
        if (outcome.Status == MoveJobStatus.Success)
        {
            if (target == TaskStates.AutoReview
                && _scanner.FindJob(card.Id, card.WatchPath) is { } returned)
            {
                TaskJsonFile.UpdateField(returned.FolderPath, "phase",
                    LifecyclePhases.Integrating, _logger);
                _scanner.InvalidateCache();
            }
            return Applied.Moved;
        }
        if (outcome.Status == MoveJobStatus.SourceStateMismatch) return Applied.None;
        _logger.LogWarning("delivery-chain-reconcile-failed task={TaskKey} target={Target} status={Status} message={Message}",
            card.TaskKey, target, outcome.Status, outcome.Message);
        return Applied.Failed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
