namespace AgentStudio.Runner;

public sealed record RemoteQueueStarvationItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime EnteredLaneAt { get; init; }
    public RemoteDispatchRejection? LastRejection { get; init; }
    public string? BlockReasonCode { get; init; }
    public string? BlockReason { get; init; }
}

public sealed record RemoteQueueStarvationSnapshot
{
    public bool Active { get; init; }
    public int WaitingTaskCount { get; init; }
    public int AvailableSlots { get; init; }
    public int ThresholdMinutes { get; init; }
    public bool ClaimProgressStalled { get; init; }
    public DateTime? LastSuccessfulClaimAt { get; init; }
    public bool HasRejections { get; init; }
    public int BuildProfileGateBlockedTaskCount { get; init; }
    public DateTime? OldestEnteredLaneAt { get; init; }
    public DateTime ObservedAt { get; init; }
    public IReadOnlyList<RemoteQueueStarvationItem> Items { get; init; } = [];
    /// <summary>Acute signal: <c>limited</c>, <c>paused</c>, <c>stalled</c>, or null.</summary>
    public string? Signal { get; init; }
    public IReadOnlyList<ProviderLimitStatus> ProviderLimits { get; init; } = [];
    public IReadOnlyList<RunnerPickupPause> PickupPauses { get; init; } = [];
}

public sealed record RunnerPickupPause(
    string ProjectName,
    string Reason,
    DateTime? PausedAt,
    DateTime? AutoResumeAt);

/// <summary>Pure policy for the ready-queue starvation watchdog.</summary>
public static class RemoteQueueStarvationPolicy
{
    public static RemoteQueueStarvationSnapshot Evaluate(
        DateTime now,
        TimeSpan threshold,
        IEnumerable<TaskInfo> tasks,
        Func<string, ProjectSettings> projectSettings,
        TaskReferenceIndex references,
        IEnumerable<ClientIdentity> runners,
        bool allowsLocalCoding = false)
    {
        var liveRunners = runners
            .Where(runner => string.Equals(
                runner.RunnerDaemonState,
                "running",
                StringComparison.OrdinalIgnoreCase))
            .Where(runner => runner.Kind != ClientIdentityKind.Retired)
            .Where(runner => runner.LastSeenAt is { } lastSeen
                             && now - lastSeen.ToUniversalTime() <= TimeSpan.FromMinutes(2))
            .ToList();
        var availableSlots = liveRunners
            .Where(runner => runner.DrainRequestedAt is null)
            .Sum(runner => Math.Max(0, runner.RunnerAvailableSlots ?? 0));
        var lastSuccessfulClaimAt = liveRunners
            .Where(runner => runner.RunnerLastClaimAt is not null)
            .Select(runner => runner.RunnerLastClaimAt!.Value.ToUniversalTime())
            .OrderByDescending(claimedAt => claimedAt)
            .Cast<DateTime?>()
            .FirstOrDefault();

        var routedReadyTasks = tasks
            .Where(task => task.State == TaskStates.Ready && !task.Fixture)
            .Where(task =>
            {
                var settings = projectSettings(task.ProjectName);
                var location = ProjectExecutionPolicy.ResolveExecutionLocation(settings);
                return (location != ExecutionLocations.Local
                        || (!allowsLocalCoding && WorktreeRunPolicy.RequiresWorktree(task.Mode)))
                       && ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
                       && AgentTypes.IsAutoPickupEligible(task.Agent)
                       && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
                       && (!settings.IntakeEnabled.GetValueOrDefault()
                           || task.Phase == LifecyclePhases.IntakePassed)
                       && !references.EvaluateWaitsOn(task).Blocked;
            })
            .ToList();
        var oldestEligibleAt = routedReadyTasks
            .Select(task => task.EnteredLaneAt.ToUniversalTime())
            .OrderBy(enteredAt => enteredAt)
            .Cast<DateTime?>()
            .FirstOrDefault();
        var progressReferenceAt = lastSuccessfulClaimAt ?? oldestEligibleAt;
        var claimProgressStalled = progressReferenceAt is { } referenceAt
                                   && now - referenceAt >= threshold;

        var items = routedReadyTasks
            .Select(task => (Task: task, Gate: BuildProfileGate.Evaluate(
                projectSettings(task.ProjectName).BuildProfile),
                LocalPlacementBlocked: ProjectExecutionPolicy.ResolveExecutionLocation(projectSettings(task.ProjectName))
                    == ExecutionLocations.Local && !allowsLocalCoding
                    && WorktreeRunPolicy.RequiresWorktree(task.Mode)))
            .Where(candidate => !candidate.Gate.AllowsPickup
                           || candidate.LocalPlacementBlocked
                           || candidate.Task.RemoteDispatchRejection is not null
                           || (claimProgressStalled
                               && now - candidate.Task.EnteredLaneAt.ToUniversalTime() >= threshold))
            .OrderBy(candidate => candidate.Task.EnteredLaneAt)
            .Select(candidate => new RemoteQueueStarvationItem
            {
                TaskKey = candidate.Task.Key ?? candidate.Task.TaskKey ?? candidate.Task.Id,
                TaskId = candidate.Task.Id,
                ProjectName = candidate.Task.ProjectName,
                Title = candidate.Task.Title,
                EnteredLaneAt = candidate.Task.EnteredLaneAt,
                LastRejection = candidate.Task.RemoteDispatchRejection,
                BlockReasonCode = candidate.LocalPlacementBlocked ? RemoteExecutionRequirement.Code
                    : candidate.Gate.AllowsPickup ? null : "build-profile-gate",
                BlockReason = candidate.LocalPlacementBlocked
                    ? $"Project {candidate.Task.ProjectName} is placed at local execution, but local coding is disabled on this orchestrator."
                    : candidate.Gate.AllowsPickup ? null : candidate.Gate.Reason,
            })
            .ToList();
        var hasRejections = items.Any(item => item.LastRejection is not null);

        return new RemoteQueueStarvationSnapshot
        {
            Active = items.Count > 0
                     && (availableSlots > 0 || items.Any(item =>
                         item.BlockReasonCode is "build-profile-gate" or RemoteExecutionRequirement.Code)),
            WaitingTaskCount = items.Count,
            AvailableSlots = availableSlots,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            ClaimProgressStalled = claimProgressStalled,
            LastSuccessfulClaimAt = lastSuccessfulClaimAt,
            HasRejections = hasRejections,
            BuildProfileGateBlockedTaskCount = items.Count(item =>
                string.Equals(item.BlockReasonCode, "build-profile-gate", StringComparison.Ordinal)),
            OldestEnteredLaneAt = items.FirstOrDefault()?.EnteredLaneAt,
            ObservedAt = now,
            Items = items,
            Signal = items.Count > 0 ? "stalled" : null,
        };
    }
}

/// <summary>
/// Pure server projection that overlays provider and pickup admission state on
/// the queue watchdog. Provider limits and breaker pauses are acute even when
/// there is no Ready-card starvation row to display.
/// </summary>
public static class RemoteQueueStarvationVisibility
{
    public static RemoteQueueStarvationSnapshot Project(
        RemoteQueueStarvationSnapshot snapshot,
        IReadOnlyList<RemoteQueueStarvationItem> visibleItems,
        IReadOnlyList<ProjectRunnerStatus> visibleStatuses)
    {
        var providerLimits = visibleStatuses
            .SelectMany(project => project.ProviderLimits)
            .GroupBy(limit => limit.CliType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(limit => limit.LimitedUntil).First())
            .OrderBy(limit => limit.CliType, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pickupPauses = visibleStatuses
            .Where(project => string.Equals(project.ModeSource, "circuit-breaker", StringComparison.OrdinalIgnoreCase))
            .Where(project => project.Mode is "manual" or "paused")
            .Select(project => new RunnerPickupPause(
                project.ProjectName,
                project.BreakerReason ?? project.ModeReason ?? "infra circuit breaker",
                project.ModeChangedAt,
                project.BreakerCooldownUntil))
            .ToList();
        var signal = providerLimits.Count > 0
            ? "limited"
            : pickupPauses.Count > 0
                ? "paused"
                : visibleItems.Count > 0 ? "stalled" : null;

        return snapshot with
        {
            Active = providerLimits.Count > 0
                     || pickupPauses.Count > 0
                     || (visibleItems.Count > 0
                         && (snapshot.AvailableSlots > 0 || visibleItems.Any(item =>
                             string.Equals(item.BlockReasonCode, "build-profile-gate", StringComparison.Ordinal)))),
            WaitingTaskCount = visibleItems.Count,
            HasRejections = visibleItems.Any(item => item.LastRejection is not null),
            BuildProfileGateBlockedTaskCount = visibleItems.Count(item =>
                string.Equals(item.BlockReasonCode, "build-profile-gate", StringComparison.Ordinal)),
            OldestEnteredLaneAt = visibleItems.FirstOrDefault()?.EnteredLaneAt,
            Items = visibleItems,
            Signal = signal,
            ProviderLimits = providerLimits,
            PickupPauses = pickupPauses,
        };
    }
}

/// <summary>
/// Detects remote Ready cards that stop making progress while a live Runner
/// continues to report free capacity. Acute transitions are visible both to
/// the board endpoint and as warning-level structured events.
/// </summary>
public sealed class RemoteQueueStarvationWatchdog : BackgroundService
{
    public const int DefaultThresholdMinutes = 30;
    public const int DefaultIntervalSeconds = 30;

    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly ClientIdentityStore _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteQueueStarvationWatchdog> _logger;
    private readonly TaskRunnerService? _runner;
    private readonly object _gate = new();
    private RemoteQueueStarvationSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
    private string? _warningSignature;
    private DateTime? _lastWarningAt;

    public RemoteQueueStarvationWatchdog(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        ClientIdentityStore clients,
        IConfiguration configuration,
        ILogger<RemoteQueueStarvationWatchdog> logger,
        TaskRunnerService? runner = null)
    {
        _scanner = scanner;
        _settings = settings;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
        _runner = runner;
    }

    public RemoteQueueStarvationSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public RemoteQueueStarvationSnapshot Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var thresholdMinutes = Math.Clamp(
            _configuration.GetValue<int?>("RemoteQueueStarvation:ThresholdMinutes")
            ?? DefaultThresholdMinutes,
            1,
            24 * 60);
        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();
        var next = RemoteQueueStarvationPolicy.Evaluate(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            snapshot.Live,
            _settings.Get,
            snapshot.References,
            _clients.ListAll(),
            _runner?.AllowsLocalCoding == true);

        lock (_gate)
        {
            PublishLogTransition(_current, next, now);
            _current = next;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("RemoteQueueStarvation:IntervalSeconds")
            ?? DefaultIntervalSeconds,
            5,
            15 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("remote-ready-starvation-watchdog-stopped");
        }
    }

    private void RefreshSafely()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "remote-ready-starvation-watchdog-failed");
        }
    }

    internal void PublishLogTransition(
        RemoteQueueStarvationSnapshot previous,
        RemoteQueueStarvationSnapshot next,
        DateTime now)
    {
        if (!next.Active)
        {
            if (previous.Active)
                _logger.LogInformation("remote-ready-starvation-recovered");
            _warningSignature = null;
            _lastWarningAt = null;
            return;
        }

        var signature = string.Join(',', next.Items.Select(item => $"{item.TaskKey}:{item.BlockReasonCode}:{item.BlockReason}"));
        var repeatDue = _lastWarningAt is null || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (string.Equals(signature, _warningSignature, StringComparison.Ordinal) && !repeatDue)
            return;

        _logger.LogWarning(
            "remote-ready-starvation waitingTasks={WaitingTaskCount} availableSlots={AvailableSlots} oldestEnteredLaneAt={OldestEnteredLaneAt} lastSuccessfulClaimAt={LastSuccessfulClaimAt} rejectedTasks={RejectedTaskCount} causes={Causes}",
            next.WaitingTaskCount,
            next.AvailableSlots,
            next.OldestEnteredLaneAt,
            next.LastSuccessfulClaimAt,
            next.Items.Count(item => item.LastRejection is not null),
            string.Join("; ", next.Items.Where(item => item.BlockReason is not null)
                .Select(item => $"{item.TaskKey}: {item.BlockReason}")));
        _warningSignature = signature;
        _lastWarningAt = now;
    }
}

public static class RemoteQueueStarvationEndpoints
{
    public static void MapRemoteQueueStarvationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/queue-starvation", (
            HttpContext context,
            RemoteQueueStarvationWatchdog watchdog,
            ProjectRegistry projects,
            TaskRunnerService runner) =>
        {
            var snapshot = watchdog.Refresh();
            var status = runner.GetStatus();
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            bool CanSee(string project) => human is null
                || ProjectAccessAuthorization.Allows(human.User, project, projects);

            var visibleItems = snapshot.Items
                .Where(item => CanSee(item.ProjectName))
                .ToList();
            var visibleStatuses = status.Projects.Values
                .Where(project => CanSee(project.ProjectName))
                .ToList();
            return Results.Ok(RemoteQueueStarvationVisibility.Project(snapshot, visibleItems, visibleStatuses));
        });
    }
}
