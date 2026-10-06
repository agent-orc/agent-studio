namespace AgentStudio.Tasks;

/// <summary>Rechecks integration when the target ref or delivery changes.</summary>
public sealed class DeliveryChainReconciler : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _statuses;
    private readonly TaskTransitionService _transitions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeliveryChainReconciler> _logger;
    private readonly GateFailureRouter? _gateFailures;

    public DeliveryChainReconciler(TaskScannerService scanner,
        TaskIntegrationStatusService statuses, TaskTransitionService transitions,
        IConfiguration configuration, ILogger<DeliveryChainReconciler> logger,
        GateFailureRouter? gateFailures = null)
    {
        _scanner = scanner;
        _statuses = statuses;
        _transitions = transitions;
        _configuration = configuration;
        _logger = logger;
        _gateFailures = gateFailures;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_configuration.GetValue("DeliveryChain:Guarded", true)) return 0;
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => card.State is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Completed)
            .ToArray();
        var statuses = _statuses.BuildLookup(cards);
        var moved = 0;
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(card)) continue;
            statuses.TryGetValue(card.TaskKey, out var status);
            var fatal = DeliveryLanePolicy.RequiresEscalation(status);
            var target = DeliveryLanePolicy.ReconciliationTarget(card, status);
            if (target is null) continue;
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
                else continue;
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
                moved++;
                if (target == TaskStates.AutoReview
                    && _scanner.FindJob(card.Id, card.WatchPath) is { } returned)
                {
                    TaskJsonFile.UpdateField(returned.FolderPath, "phase",
                        LifecyclePhases.Integrating, _logger);
                    _scanner.InvalidateCache();
                }
            }
            else if (outcome.Status != MoveJobStatus.SourceStateMismatch)
                _logger.LogWarning("delivery-chain-reconcile-failed task={TaskKey} target={Target} status={Status} message={Message}",
                    card.TaskKey, target, outcome.Status, outcome.Message);
        }
        return moved;
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
