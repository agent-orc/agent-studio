namespace AgentStudio.Tasks;

/// <summary>Rechecks integration when the target ref or delivery changes.</summary>
public sealed class DeliveryChainReconciler : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _statuses;
    private readonly TaskTransitionService _transitions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeliveryChainReconciler> _logger;

    public DeliveryChainReconciler(TaskScannerService scanner,
        TaskIntegrationStatusService statuses, TaskTransitionService transitions,
        IConfiguration configuration, ILogger<DeliveryChainReconciler> logger)
    {
        _scanner = scanner;
        _statuses = statuses;
        _transitions = transitions;
        _configuration = configuration;
        _logger = logger;
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
            var integrated = status?.Status == IntegrationStatuses.Integrated;
            var fatal = DeliveryLanePolicy.RequiresEscalation(status);
            string? target = null;
            if (card.State == TaskStates.AutoReview && card.Phase == LifecyclePhases.Integrating)
                target = integrated ? TaskStates.HumanReview : fatal ? TaskStates.Escalated : null;
            else if (card.State == TaskStates.HumanReview && !integrated)
                target = fatal ? TaskStates.Escalated : TaskStates.AutoReview;
            else if (card.State == TaskStates.Completed)
            {
                var claim = card.CompletionClaim;
                var subject = TaskIntegrationStatusService.CurrentReviewSubject(card);
                var stale = !integrated || claim?.Basis != CompletionClaimBases.IntegratedDelivery
                    || !string.Equals(claim.IntegrationBranch,
                        status?.IntegrationBranch, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(claim.TargetRefFingerprint)
                        || !string.Equals(claim.TargetRefFingerprint,
                            status?.TargetRefFingerprint, StringComparison.Ordinal))
                    || (subject is not null
                        && (!string.Equals(claim.ResultSha, subject.ResultSha,
                                StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(claim.DeliveryEpoch, subject.RunAttemptId,
                                StringComparison.Ordinal)));
                if (stale) target = TaskStates.AutoReview;
            }
            if (target is null) continue;
            var category = fatal
                ? status?.Failure?.Code ?? (status?.Status == IntegrationStatuses.NoBranch
                    ? "missing-delivery" : "integration-failed")
                : "delivery-verdict-stale";
            var action = fatal
                ? "Recover the delivery, resolve the integration failure and rerun the gate."
                : "Integrate the current delivery and repeat review.";
            var outcome = await _transitions.MoveAsync(card.Id, target, card.WatchPath, ct,
                cause: TimelineActors.System, reason: $"{category}: {action}",
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
