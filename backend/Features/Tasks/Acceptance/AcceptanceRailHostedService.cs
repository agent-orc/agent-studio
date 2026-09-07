using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

public sealed record AcceptanceRailSnapshot
{
    public bool Enabled { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public int HumanReviewDepth { get; init; }
    public int EscalatedDepth { get; init; }
    public int Held { get; init; }
    public int Accepted { get; init; }
    public int Requeued { get; init; }
    public int Escalated { get; init; }
    public int Failed { get; init; }
}

/// <summary>
/// Durable deterministic owner for routine Human Review acceptance and
/// integration-conflict requeue. It runs independently of every orchestrator
/// session and makes no model calls.
/// </summary>
public sealed class AcceptanceRailHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskTransitionService _transitions;
    private readonly TaskIntegrationRecoveryService _recovery;
    private readonly HumanReviewEscalation _humanReviewEscalation;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptanceRailHostedService> _logger;
    private readonly object _snapshotGate = new();
    private AcceptanceRailSnapshot _current = new()
    {
        Enabled = AcceptanceRailDefaults.Enabled,
    };

    public AcceptanceRailHostedService(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskTransitionService transitions,
        TaskIntegrationRecoveryService recovery,
        HumanReviewEscalation humanReviewEscalation,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<AcceptanceRailHostedService> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _transitions = transitions;
        _recovery = recovery;
        _humanReviewEscalation = humanReviewEscalation;
        _timeline = timeline;
        _configuration = configuration;
        _logger = logger;
    }

    public AcceptanceRailSnapshot Current
    {
        get { lock (_snapshotGate) return _current; }
    }

    public async Task<AcceptanceRailSnapshot> RunOnceAsync(CancellationToken ct = default)
    {
        var options = AcceptanceRailOptions.FromConfiguration(_configuration);
        var jobs = _scanner.ScanAllAutomationJobs()
            .Where(job => job.State is TaskStates.HumanReview or TaskStates.Escalated)
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!options.Enabled)
            return Publish(new AcceptanceRailSnapshot { Enabled = false, LastRunAtUtc = DateTime.UtcNow });

        var humanReviewDepth = jobs.Count(job => job.State == TaskStates.HumanReview);
        var escalatedDepth = jobs.Count(job => job.State == TaskStates.Escalated);
        var statusByKey = _integrationStatus.BuildLookup(jobs);
        var held = 0;
        var accepted = 0;
        var requeued = 0;
        var escalated = 0;
        var failed = 0;

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                statusByKey.TryGetValue(job.TaskKey, out var status);
                var used = CountConflictRequeues(job);
                var infrastructureUsed = CountInfrastructureRequeues(job);
                var decision = AcceptanceRailPolicy.Decide(
                    job,
                    status,
                    used,
                    options,
                    DateTimeOffset.UtcNow,
                    infrastructureUsed.Count,
                    infrastructureUsed.LastAt,
                    // The provider reset instant is not carried on the card, so
                    // the rail backs off exponentially instead of waiting for a
                    // known reset. The policy takes the instant for the callers
                    // that do know it.
                    quotaResetAt: null);
                if (decision.Reason == "operator-hold")
                {
                    held++;
                    continue;
                }

                switch (decision.Action)
                {
                    case AcceptanceRailAction.Accept:
                        if (await AcceptAsync(job, ct)) accepted++;
                        else failed++;
                        break;
                    case AcceptanceRailAction.Requeue:
                        if (status is not null && Requeue(job, status, used + 1)) requeued++;
                        else failed++;
                        break;
                    case AcceptanceRailAction.RequeueInfrastructure:
                        if (status is not null
                            && await RequeueInfrastructureAsync(
                                job,
                                status,
                                infrastructureUsed.Count + 1,
                                options.MaxInfrastructureRequeues,
                                ct))
                        {
                            requeued++;
                        }
                        else failed++;
                        break;
                    case AcceptanceRailAction.Escalate:
                        if (job.State == TaskStates.Escalated && HasExhaustionReceipt(job))
                            break;
                        if (decision.Reason == "infrastructure-requeue-budget-exhausted")
                        {
                            if (await EscalateInfrastructureAsync(
                                    job,
                                    status,
                                    infrastructureUsed.Count,
                                    options.MaxInfrastructureRequeues,
                                    ct))
                            {
                                escalated++;
                            }
                            else failed++;
                            break;
                        }
                        if (await EscalateAsync(job, used, options.MaxRequeues, ct)) escalated++;
                        else failed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "acceptance-rail-item-failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        var snapshot = Publish(new AcceptanceRailSnapshot
        {
            Enabled = true,
            LastRunAtUtc = DateTime.UtcNow,
            HumanReviewDepth = humanReviewDepth,
            EscalatedDepth = escalatedDepth,
            Held = held,
            Accepted = accepted,
            Requeued = requeued,
            Escalated = escalated,
            Failed = failed,
        });
        _logger.LogInformation(
            "acceptance-rail-run humanReviewDepth={HumanReviewDepth} escalatedDepth={EscalatedDepth} held={Held} accepted={Accepted} requeued={Requeued} escalated={Escalated} failed={Failed} lastRunAtUtc={LastRunAtUtc}",
            snapshot.HumanReviewDepth,
            snapshot.EscalatedDepth,
            snapshot.Held,
            snapshot.Accepted,
            snapshot.Requeued,
            snapshot.Escalated,
            snapshot.Failed,
            snapshot.LastRunAtUtc);
        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = AcceptanceRailOptions.FromConfiguration(_configuration);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "acceptance-rail-run-failed");
            }

            await Task.Delay(options.Interval, stoppingToken);
        }
    }

    private async Task<bool> AcceptAsync(TaskInfo job, CancellationToken ct)
    {
        var current = _scanner.FindJob(job.Id, job.WatchPath);
        if (current is null)
        {
            _logger.LogWarning(
                "acceptance-rail-accept-stale project={Project} job={JobId} watchPath={WatchPath}",
                job.ProjectName,
                job.Id,
                job.WatchPath);
            return false;
        }

        var outcome = await _transitions.MoveAsync(
            current.Id,
            TaskStates.Completed,
            current.WatchPath,
            ct,
            cause: TimelineActors.System,
            reason: "The acceptance rail accepted the Git-derived integrated delivery.",
            expectedSourceState: TaskStates.HumanReview,
            transitionCause: LaneChangeCauses.Accepted,
            transitionDetail: TaskIntegrationRecoveryService.AcceptanceRailSource);
        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "acceptance-rail-accept-refused project={Project} job={JobId} status={Status} message={Message}",
                job.ProjectName,
                job.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "accepted", "Accepted integrated delivery without a session-bound orchestrator tick.");
        return true;
    }

    private bool Requeue(TaskInfo job, TaskIntegrationStatus status, int retryNumber)
    {
        var result = _recovery.Queue(
            job,
            status,
            status.Failure?.Code ?? AcceptedIntegrationFailureCodes.MergeConflict,
            TaskIntegrationRecoveryService.AcceptanceRailSource,
            retryNumber);
        if (!result.Queued)
        {
            _logger.LogWarning(
                "acceptance-rail-requeue-refused project={Project} job={JobId} error={Error}",
                job.ProjectName,
                job.Id,
                result.Error);
            return false;
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
        {
            AppendAction(
                moved,
                "requeued",
                $"Queued deterministic integration recovery retry {retryNumber}.",
                retryNumber);
        }
        return true;
    }

    /// <summary>
    /// Replays a card whose last failure was attributed to the host or the
    /// provider account (AGT-2749). The delivery is unchanged, so this path must
    /// not write a rebase steer: the card only goes back to
    /// <see cref="TaskStates.AutoReview"/> to be verified again.
    /// </summary>
    private async Task<bool> RequeueInfrastructureAsync(
        TaskInfo job,
        TaskIntegrationStatus status,
        int retryNumber,
        int maximum,
        CancellationToken ct)
    {
        var failureClass = status.Failure?.FailureClass ?? RunFailureClass.Unknown;
        var signature = status.Failure?.FailureSignature ?? RunFailureSignatures.Unclassified;
        var reason = $"The last integration failure is {ClassText(failureClass)} ({signature}), not a verdict on the change. "
                     + $"Requeued to {TaskStates.AutoReview} as retry {retryNumber}/{maximum}.";

        var outcome = await _transitions.MoveAsync(
            job.Id,
            TaskStates.AutoReview,
            job.WatchPath,
            ct,
            cause: TimelineActors.System,
            reason: reason,
            expectedSourceState: job.State,
            transitionCause: LaneChangeCauses.ReviewInfrastructure,
            transitionDetail: TaskIntegrationRecoveryService.AcceptanceRailSource);
        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "acceptance-rail-infrastructure-requeue-refused project={Project} job={JobId} status={Status} message={Message}",
                job.ProjectName,
                job.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, AcceptanceRailReceipts.InfrastructureRequeueAction, reason, retryNumber);
        return true;
    }

    private async Task<bool> EscalateInfrastructureAsync(
        TaskInfo job,
        TaskIntegrationStatus? status,
        int used,
        int maximum,
        CancellationToken ct)
    {
        var failureClass = status?.Failure?.FailureClass ?? RunFailureClass.Unknown;
        var signature = status?.Failure?.FailureSignature ?? RunFailureSignatures.Unclassified;
        var reason = $"Integration recovery stopped after {used}/{maximum} {ClassSlug(failureClass)} requeues ({signature}). "
                     + "Fix the host or the account, then requeue the card.";
        var category = failureClass == RunFailureClass.Quota
            ? HumanReviewEscalationCategories.QuotaExhausted
            : HumanReviewEscalationCategories.Environmental;
        var outcome = await _humanReviewEscalation.EscalateAsync(
            job.Id,
            job.WatchPath,
            job.ProjectName,
            category,
            reason,
            ct);
        if (outcome.Status != MoveJobStatus.Success) return false;

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "escalated", reason, used);
        return true;
    }

    private static string ClassText(RunFailureClass failureClass) => failureClass switch
    {
        RunFailureClass.Quota => "a provider quota fault",
        RunFailureClass.Infrastructure => "an infrastructure fault",
        _ => "an unclassified fault",
    };

    private static string ClassSlug(RunFailureClass failureClass)
        => failureClass.ToString().ToLowerInvariant();

    private async Task<bool> EscalateAsync(
        TaskInfo job,
        int used,
        int maximum,
        CancellationToken ct)
    {
        var reason = $"Integration recovery stopped after {used}/{maximum} conflict requeues. The card requires an operator decision instead of another automatic loop.";
        var outcome = await _humanReviewEscalation.EscalateAsync(
            job.Id,
            job.WatchPath,
            job.ProjectName,
            HumanReviewEscalationCategories.IntegrationRecoveryExhausted,
            reason,
            ct);
        if (outcome.Status != MoveJobStatus.Success) return false;

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
            AppendAction(moved, "escalated", reason, used);
        return true;
    }

    private int CountConflictRequeues(TaskInfo job)
        => _timeline.ReadAll(job.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && string.Equals(
                entry.Details?.GetValueOrDefault("source"),
                TaskIntegrationRecoveryService.AcceptanceRailSource,
                StringComparison.Ordinal));

    /// <summary>
    /// Infrastructure replays already spent on this card, plus the instant of the
    /// last one. An infrastructure requeue writes no recovery intent, so the rail
    /// counts its own receipts instead of the recovery-queued events.
    /// </summary>
    private (int Count, DateTimeOffset? LastAt) CountInfrastructureRequeues(TaskInfo job)
        => AcceptanceRailReceipts.CountInfrastructureRequeues(_timeline, job.FolderPath);

    private bool HasExhaustionReceipt(TaskInfo job)
        => _timeline.ReadAll(job.FolderPath).Any(entry =>
            entry.Kind == TimelineEventKinds.AcceptanceRailActed
            && entry.Details?.GetValueOrDefault("action") == "escalated");

    private void AppendAction(
        TaskInfo job,
        string action,
        string summary,
        int? retryNumber = null)
    {
        var details = new Dictionary<string, string>
        {
            ["action"] = action,
            ["source"] = TaskIntegrationRecoveryService.AcceptanceRailSource,
        };
        if (retryNumber is not null)
            details["retryNumber"] = retryNumber.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        _timeline.Append(
            job.FolderPath,
            TimelineEventKinds.AcceptanceRailActed,
            TimelineActors.System,
            summary,
            details: details);
    }

    private AcceptanceRailSnapshot Publish(AcceptanceRailSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _current = snapshot;
            return _current;
        }
    }
}

public static class AcceptanceRailEndpoints
{
    public static void MapAcceptanceRailEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipeline/acceptance-rail", (
            AcceptanceRailHostedService rail) => Results.Ok(rail.Current));
    }
}
