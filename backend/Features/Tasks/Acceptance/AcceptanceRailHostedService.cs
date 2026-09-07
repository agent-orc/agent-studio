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
                var decision = AcceptanceRailPolicy.Decide(job, status, used, options);
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
                        if (decision.Reason == "retryable-review-failure")
                        {
                            if (await RequeueReviewFailureAsync(job, ct)) requeued++;
                            else failed++;
                        }
                        else if (status is not null && Requeue(job, status, used + 1)) requeued++;
                        else failed++;
                        break;
                    case AcceptanceRailAction.Escalate:
                        if (job.State == TaskStates.Escalated && HasExhaustionReceipt(job))
                            break;
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

    private async Task<bool> RequeueReviewFailureAsync(TaskInfo job, CancellationToken ct)
    {
        var failure = job.ReviewFailure;
        if (failure is null) return false;

        var outcome = await _transitions.MoveAsync(
            job.Id,
            TaskStates.AutoReview,
            job.WatchPath,
            ct,
            cause: TimelineActors.System,
            reason: $"The acceptance rail requeued a {failure.FailureClass} review failure ({failure.RetryNumber}/{failure.MaximumRetries}).",
            expectedSourceState: TaskStates.HumanReview,
            transitionCause: LaneChangeCauses.ReviewInfrastructure,
            transitionDetail: "review-failure-retry");
        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "acceptance-rail-review-requeue-refused project={Project} job={JobId} status={Status} message={Message}",
                job.ProjectName,
                job.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        var moved = _scanner.FindJob(job.Id, job.WatchPath);
        if (moved is not null)
        {
            AppendAction(
                moved,
                "requeued",
                $"Requeued {failure.FailureClass} review failure {failure.RetryNumber}/{failure.MaximumRetries} after its backoff.",
                failure.RetryNumber);
        }
        return true;
    }

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
