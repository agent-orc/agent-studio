namespace AgentStudio.Pipeline;

/// <summary>
/// Durable compatibility safety net for an acceptance integration transaction
/// written by an older backend process. New acceptance requests never create
/// the integrating phase. A restart before a legacy volatile queue drains may
/// resume that transaction and move the task to Completed only after successful
/// integration. Legacy Completed cards and remote <c>no-branch</c> outcomes
/// remain recoverable.
/// </summary>
public sealed class AcceptedIntegrationBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptedIntegrationBackstopHostedService> _logger;
    private readonly TaskTransitionService? _transitions;
    private readonly TimelineLog? _timeline;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly HistoricalIntegrationVerificationSweep? _historicalSweep;
    private readonly AcceptedIntegrationInventorySweep? _historicalInventory;
    private readonly object _alertGate = new();
    private readonly AcceptedIntegrationAlertLogState _alertLog = new();
    private AcceptedIntegrationAlertSnapshot _currentAlert = new()
    {
        ObservedAt = DateTime.UtcNow,
        ThresholdMinutes = 30,
    };

    public AcceptedIntegrationBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        MergeIntoDevelopRunner runner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AcceptedIntegrationBackstopHostedService> logger,
        TaskTransitionService? transitions = null,
        TimelineLog? timeline = null,
        PipelineExecutionLog? pipelineLog = null,
        HistoricalIntegrationVerificationSweep? historicalSweep = null,
        AcceptedIntegrationInventorySweep? historicalInventory = null)
    {
        _scanner = scanner;
        _settings = settings;
        _runner = runner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
        _transitions = transitions;
        _timeline = timeline;
        _pipelineLog = pipelineLog;
        _historicalSweep = historicalSweep;
        _historicalInventory = historicalInventory;
    }

    public AcceptedIntegrationAlertSnapshot CurrentAlert
    {
        get { lock (_alertGate) return _currentAlert; }
    }

    public int RunOnce()
    {
        // AGT-2480: the automation scanner excludes fixture cards. Keep the
        // acceptedJobs name from AGT-2428 because it describes the recovery set.
        var acceptedJobs = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(AcceptedIntegrationBackstopPolicy.IsRecoveryCandidate)
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(DeliveryOrder)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statusByKey = _integrationStatus.BuildLookup(acceptedJobs);
        var attemptOutcomes = new List<MergeIntoIntegrationOutcome>();
        foreach (var job in acceptedJobs)
        {
            var attemptRecorded = false;
            try
            {
                statusByKey.TryGetValue(job.TaskKey, out var status);
                var decision = _integrationStatus.ResolveAcceptedIntegrationRecovery(job, status);
                if (decision.Action == AcceptedIntegrationRecoveryAction.Ignore)
                    continue;
                if (decision.Action == AcceptedIntegrationRecoveryAction.Finalize)
                {
                    FinalizeTransactionalAccept(job);
                    ClearPendingTag(job);
                    continue;
                }
                if (decision.Action == AcceptedIntegrationRecoveryAction.ReturnToReview)
                {
                    ReturnTransactionalAcceptToReview(
                        job,
                        NormalizeOutcome(decision.LastMergeAttempt?.Verdict) ?? "IntegrationFailed",
                        decision.LastMergeAttempt?.Reason ?? decision.LastMergeAttempt?.VerdictSummary);
                    continue;
                }

                var settings = _settings.Get(job.ProjectName);
                var result = _runner.Run(
                    job.ProjectName,
                    job.Id,
                    job.FolderPath,
                    job.WatchPath,
                    settings.IntegrationBranch,
                    settings.IntegrationStrategy,
                    PipelineTypes.Resolve(job));
                attemptOutcomes.Add(result.Outcome);
                attemptRecorded = true;
                if (result.Outcome.IsSuccessfulIntegration())
                {
                    FinalizeTransactionalAccept(job);
                    ClearPendingTag(job);
                }
                else
                {
                    ReturnTransactionalAcceptToReview(
                        job,
                        result.Outcome.ToString(),
                        result.Error);
                }
            }
            catch (Exception ex)
            {
                if (!attemptRecorded)
                    attemptOutcomes.Add(MergeIntoIntegrationOutcome.Error);
                RecordUnexpectedFailure(job, ex.Message);
                ReturnTransactionalAcceptToReview(
                    job,
                    MergeIntoIntegrationOutcome.Error.ToString(),
                    ex.Message);
                _logger.LogError(
                    ex,
                    "accepted-integration-backstop item failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        var summary = AcceptedIntegrationBackstopPolicy.Summarize(attemptOutcomes);
        AcceptedIntegrationBackstopTelemetry.LogSweep(_logger, summary);
        try
        {
            RefreshAlert(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "accepted-integration-alert-refresh-failed");
        }
        return summary.Integrated;
    }

    internal AcceptedIntegrationAlertSnapshot RefreshAlert(DateTime nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var thresholdMinutes = Math.Clamp(
            _configuration.GetValue<int?>("Integration:AcceptedAlertThresholdMinutes") ?? 30,
            1,
            24 * 60);
        // The alert is a filesystem invariant, so each interval reads current
        // task.json records rather than relying on the board snapshot cache.
        // This bounds convergence after an external recovery append to one
        // backstop interval even when the watcher misses the write.
        var candidates = _scanner.ScanAllJobsRaw()
            .Where(job => !job.Fixture)
            .Where(IsPotentialAlertLane)
            .OrderBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statusByKey = _integrationStatus.BuildLookup(candidates);
        var policyCandidates = new List<AcceptedIntegrationAlertCandidate>(candidates.Count);

        foreach (var job in candidates)
        {
            statusByKey.TryGetValue(job.TaskKey, out var status);
            var recovery = _integrationStatus.ResolveAcceptedIntegrationRecovery(job, status);
            if (recovery.Action == AcceptedIntegrationRecoveryAction.Ignore)
                continue;
            var acceptanceRecord = ResolveAcceptanceRecord(job, recovery.LastMergeAttempt);
            var candidate = new AcceptedIntegrationAlertCandidate
            {
                Task = job,
                AcceptedAt = acceptanceRecord.RecordedAt,
                HasIntegrationRecord = acceptanceRecord.Exists,
                HasHistoricalVerification = TaskIntegrationRecordDetector.LatestVerification(job) is not null,
                IntegrationStatus = status?.Status,
                LastOutcome = NormalizeOutcome(recovery.LastMergeAttempt?.Verdict),
                Detail = recovery.LastMergeAttempt?.Reason
                         ?? recovery.LastMergeAttempt?.VerdictSummary
                         ?? status?.Detail,
            };
            if (AcceptedIntegrationBackstopPolicy.IsAlertCandidate(candidate))
                policyCandidates.Add(candidate);
        }

        var next = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            policyCandidates);
        lock (_alertGate)
        {
            _alertLog.Publish(_logger, _currentAlert, next, now);
            _currentAlert = next;
            return _currentAlert;
        }
    }

    private static bool IsPotentialAlertLane(TaskInfo job)
        => job.State is TaskStates.Completed or TaskStates.Archive;

    private (bool Exists, DateTime RecordedAt) ResolveAcceptanceRecord(
        TaskInfo job,
        PipelineStepExecution? lastMergeAttempt)
    {
        // Only an acceptance-stage start counts as the acceptance record; the
        // integrate-on-delivery start (stage pre-human-review) precedes Human
        // Review and would make a later acceptance look older than it is.
        var integrationStarted = _timeline is null
            ? null
            : TaskIntegrationRecordDetector.LatestAcceptanceStarted(
                _timeline.ReadAll(job.FolderPath));
        if (integrationStarted is not null)
            return (true, integrationStarted.Ts.ToUniversalTime());

        var mergeRecordedAt = lastMergeAttempt?.StartedAt ?? lastMergeAttempt?.CompletedAt;
        if (mergeRecordedAt is { } recordedAt)
            return (true, recordedAt.ToUniversalTime());

        var verification = TaskIntegrationRecordDetector.LatestOperatorVisibleVerification(job);
        return verification is null
            ? (false, default)
            : (true, verification.AcceptedAtUtc?.ToUniversalTime() ?? job.EnteredLaneAt.ToUniversalTime());
    }

    private static string? NormalizeOutcome(string? verdict)
        => verdict?.Trim().ToLowerInvariant() switch
        {
            "merged" => nameof(MergeIntoIntegrationOutcome.Merged),
            "merged-after-rebase" => nameof(MergeIntoIntegrationOutcome.MergedAfterRebase),
            "already-merged" or "already-integrated" => nameof(MergeIntoIntegrationOutcome.AlreadyMerged),
            "no-branch" => nameof(MergeIntoIntegrationOutcome.NoTaskBranch),
            "conflict" => nameof(MergeIntoIntegrationOutcome.Conflict),
            "gate-failed" => nameof(MergeIntoIntegrationOutcome.GateFailed),
            "gate-environment-failure" => nameof(MergeIntoIntegrationOutcome.GateEnvironmentFailure),
            "pushed-for-review" => nameof(MergeIntoIntegrationOutcome.PushedForReview),
            "error" => nameof(MergeIntoIntegrationOutcome.Error),
            null or "" => null,
            _ => verdict,
        };

    private void RecordUnexpectedFailure(TaskInfo job, string detail)
    {
        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "error",
            VerdictSummary = "Accepted integration recovery failed.",
            Reason = detail,
        });
    }

    private static DateTimeOffset DeliveryOrder(TaskInfo job)
        => ReviewSubjectStore.Read(job.FolderPath)?.CompletedAtUtc
           ?? (job.EnteredLaneAt == default
               ? DateTimeOffset.MaxValue
               : new DateTimeOffset(DateTime.SpecifyKind(job.EnteredLaneAt, DateTimeKind.Utc)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the one-time historical bookkeeping migration before the
        // mutating recovery loop. The initial yield keeps disk and Git work
        // off the host startup path, while the ordering prevents legacy cards
        // from being mistaken for live acceptance transactions.
        await Task.Yield();
        if (_historicalSweep is not null)
        {
            try
            {
                var report = await _historicalSweep.RunOnceAsync(stoppingToken);
                if (!report.Completed)
                {
                    _logger.LogError(
                        "accepted-integration-backstop paused because historical verification had {Failures} write failure(s)",
                        report.WriteFailures);
                    return;
                }
                _historicalInventory?.Run();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "accepted-integration-backstop paused because historical verification failed");
                return;
            }
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("Integration:BackstopIntervalMinutes") ?? 15,
            1,
            24 * 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Accepted integration backstop sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ClearPendingTag(TaskInfo job)
    {
        var tags = (job.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count == (job.Tags?.Count ?? 0)) return;
        _mutations.SetJobTags(job.Id, tags, job.WatchPath);
    }

    private void FinalizeTransactionalAccept(TaskInfo job)
    {
        if (job.State != TaskStates.HumanReview || _transitions == null) return;
        var moved = _transitions.MoveAsync(
                job.Id,
                TaskStates.Completed,
                job.WatchPath,
                CancellationToken.None,
                cause: TimelineActors.System,
                reason: "Recovered transactional acceptance after integration succeeded.",
                expectedSourceState: TaskStates.HumanReview,
                suppressIntegrationTrigger: true,
                transitionCause: LaneChangeCauses.Accepted,
                transitionDetail: "acceptance-integration-recovered")
            .GetAwaiter()
            .GetResult();
        if (moved.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "Backstop integrated {JobId}, but finalizing Completed failed with {Status}: {Message}",
                job.Id,
                moved.Status,
                moved.Message);
            return;
        }

        var completed = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        _mutations.SetJobPhase(completed.FolderPath, null);
        completed = _scanner.FindJob(job.Id, job.WatchPath) ?? completed;
        TryClearAcceptanceIntegrationStatus(completed);
        _timeline?.Append(
            completed.FolderPath,
            TimelineEventKinds.IntegrationSucceeded,
            TimelineActors.System,
            "Recovered acceptance integration succeeded; task moved to Completed.");
    }

    private void ReturnTransactionalAcceptToReview(
        TaskInfo job,
        string outcome,
        string? detail)
    {
        if (job.State == TaskStates.Completed && _transitions != null)
        {
            var moveReason = $"Acceptance integration ended with {outcome}: "
                             + (detail ?? "The delivery was not integrated.");
            var moved = _transitions.MoveAsync(
                    job.Id,
                    TaskStates.HumanReview,
                    job.WatchPath,
                    CancellationToken.None,
                    cause: TimelineActors.System,
                    reason: moveReason,
                    expectedSourceState: TaskStates.Completed,
                    suppressIntegrationTrigger: true,
                    transitionCause: LaneChangeCauses.AcceptanceIntegrationFailed,
                    transitionDetail: outcome)
                .GetAwaiter()
                .GetResult();
            if (moved.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "Backstop integration failed for {JobId}, but returning to Human Review failed with {Status}: {Message}",
                    job.Id,
                    moved.Status,
                    moved.Message);
                return;
            }
            job = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        }

        if (job.State != TaskStates.HumanReview) return;

        _mutations.SetJobPhase(job.FolderPath, null);
        var reviewed = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        TryWriteAcceptanceIntegrationFailure(reviewed, outcome, detail);
        _timeline?.Append(
            reviewed.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Recovered acceptance integration failed ({outcome}); the task remains in Human Review.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = outcome,
                ["detail"] = detail ?? string.Empty,
            });
    }

    private void TryWriteAcceptanceIntegrationFailure(
        TaskInfo job,
        string outcome,
        string? detail)
    {
        try
        {
            AcceptanceIntegrationStatusDocument.WriteFailure(
                job.FolderPath,
                outcome,
                detail,
                _settings.Get(job.ProjectName).IntegrationBranch);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "acceptance-integration-status-write-failed project={Project} job={JobId} outcome={Outcome}",
                job.ProjectName,
                job.Id,
                outcome);
        }
    }

    private void TryClearAcceptanceIntegrationStatus(TaskInfo job)
    {
        try
        {
            AcceptanceIntegrationStatusDocument.Clear(job.FolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "acceptance-integration-status-clear-failed project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
        }
    }
}

public static class AcceptedIntegrationBackstopEndpoints
{
    public static void MapAcceptedIntegrationBackstopEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipeline/accepted-integration-alert", (
            HttpContext context,
            AcceptedIntegrationBackstopHostedService backstop,
            ProjectRegistry projects) =>
        {
            var snapshot = backstop.CurrentAlert;
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(snapshot);

            var visibleItems = snapshot.Items
                .Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects))
                .ToList();
            return Results.Ok(snapshot with
            {
                Active = visibleItems.Count > 0,
                StalledTaskCount = visibleItems.Count,
                OldestAcceptedAt = visibleItems.FirstOrDefault()?.AcceptedAt,
                Items = visibleItems,
            });
        });
    }
}
