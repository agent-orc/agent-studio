namespace AgentStudio.Runner;

/// <summary>
/// Point-in-time snapshot of the auto-review post-processing queue.
/// Exposed by <see cref="AutoReviewQueueEndpoints"/> for the admin/hosts
/// view and consumed by the status-bar review-plane indicator.
/// </summary>
public sealed record AutoReviewQueueSnapshot
{
    /// <summary>
    /// Combined review backlog: <see cref="LegacyQueueDepth"/> plus
    /// <see cref="PendingReviewAttempts"/>. AGT-2848: on a remote-review fleet
    /// the legacy queue is permanently empty, so this used to read the same as
    /// <see cref="LegacyQueueDepth"/> alone and stayed at zero while attempt
    /// authority held a real backlog. This is the number the stagnation and
    /// parallelism policies react to.
    /// </summary>
    public int QueueDepth { get; init; }

    /// <summary>Cards sitting in the local <c>AutoReviewPostProcessingQueue</c>, not yet picked up by a processing slot.</summary>
    public int LegacyQueueDepth { get; init; }

    /// <summary>Current canonical ReviewAttempts in attempt-authority state <c>Pending</c> (queued, unclaimed).</summary>
    public int PendingReviewAttempts { get; init; }

    /// <summary>
    /// Post-processing jobs actively running (inferred from the decision
    /// orchestrator's activity view). Zero when the orchestrator is idle or
    /// the service is disabled.
    /// </summary>
    public int ActiveJobs { get; init; }

    /// <summary>
    /// True when cards have been waiting for longer than
    /// <see cref="StagnantThresholdMinutes"/> without any drain progress.
    /// </summary>
    public bool IsStagnant { get; init; }

    /// <summary>
    /// UTC time when queue depth first became positive in the current
    /// unbroken run that eventually triggered stagnation. Null when not
    /// stagnant.
    /// </summary>
    public DateTime? StagnantSince { get; init; }

    public int StagnantThresholdMinutes { get; init; }

    /// <summary>
    /// AGT-2987: true when pending ReviewAttempts exist and no attempt has been
    /// claimed for <see cref="StagnantThresholdMinutes"/>. Only a delivered
    /// claim restarts this clock; coding runs and legacy dequeues do not.
    /// </summary>
    public bool ReviewClaimStagnant { get; init; }

    /// <summary>Latest delivered review claim (persisted lease acquisitions included). Null when none is known.</summary>
    public DateTime? LastReviewClaimAt { get; init; }

    public string? OldestPendingAttemptId { get; init; }

    public string? OldestPendingTaskKey { get; init; }

    public DateTime? OldestPendingAttemptCreatedAt { get; init; }

    /// <summary>
    /// Typed reason the oldest pending attempt could not be claimed, as last
    /// observed by the claim endpoint (for example <c>unclaimable-plan-requirements</c>).
    /// Null when no executor has reported it unclaimable.
    /// </summary>
    public string? UnclaimableReason { get; init; }

    public IReadOnlyList<string>? UnclaimableMissingCapabilities { get; init; }

    /// <summary>Completed review passes per minute over the trailing <see cref="ThroughputWindowMinutes"/> window.</summary>
    public double DrainRatePerMinute { get; init; }

    /// <summary>Median review-pass duration in milliseconds over the trailing window. Null when no pass completed in-window.</summary>
    public double? MedianReviewDurationMs { get; init; }

    public double ThroughputWindowMinutes { get; init; }

    public DateTime ObservedAt { get; init; }
}

/// <summary>
/// Detects auto-review post-processing cards that stop draining while the
/// queue remains non-empty. Acute transitions are visible at the admin REST
/// endpoint and as warning-level structured log events.
///
/// Stagnation rule, one clock per side of the backlog (AGT-2987):
/// <list type="bullet">
/// <item>Canonical: pending ReviewAttempts exist and none has been claimed for
/// the threshold (<see cref="ReviewClaimStagnationPolicy"/>). Before AGT-2987
/// one shared clock was reset by any legacy dequeue as well, which kept the
/// flag false for 24 hours while every pending attempt was unclaimable.</item>
/// <item>Legacy: the local post-processing queue is non-empty and no legacy
/// card has started since it last became non-empty, for the threshold.</item>
/// </list>
/// </summary>
public sealed class AutoReviewQueueStagnationWatchdog : BackgroundService
{
    public const int DefaultStagnantThresholdMinutes = 20;
    public const int DefaultIntervalSeconds = 30;
    public const int DefaultThroughputWindowMinutes = 30;

    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly AttemptAuthorityService _authority;
    private readonly ReviewClaimUnclaimableLog _unclaimable;
    private readonly AutoReviewStatusSnapshot _status;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoReviewQueueStagnationWatchdog> _logger;
    private readonly object _gate = new();

    private AutoReviewQueueSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
    private DateTime? _nonEmptyQueueSince;
    private DateTime? _lastStartedAtWhenNonEmpty;
    private bool _warningActive;
    private DateTime? _lastWarningAt;

    public AutoReviewQueueStagnationWatchdog(
        AutoReviewPostProcessingQueue queue,
        AttemptAuthorityService authority,
        ReviewClaimUnclaimableLog unclaimable,
        AutoReviewStatusSnapshot status,
        IConfiguration configuration,
        ILogger<AutoReviewQueueStagnationWatchdog> logger)
    {
        _queue = queue;
        _authority = authority;
        _unclaimable = unclaimable;
        _status = status;
        _configuration = configuration;
        _logger = logger;
    }

    public AutoReviewQueueSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public AutoReviewQueueSnapshot Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var thresholdMinutes = Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewQueueStagnation:ThresholdMinutes")
            ?? DefaultStagnantThresholdMinutes,
            1, 24 * 60);

        // AGT-2848: a remote-review fleet drains ReviewAttempts through attempt
        // authority (fenced claim/settle), not the legacy post-processing
        // worker, so the legacy queue alone reads zero while a real backlog
        // sits in attempt authority. Both sources feed the one backlog number
        // the stagnation and parallelism policies react to.
        var legacyQueueDepth = _queue.PendingCount;
        var claimActivity = _authority.ReadReviewClaimActivity();
        var pendingReviewAttempts = claimActivity.PendingAttempts;
        var pendingCount = legacyQueueDepth + pendingReviewAttempts;
        var legacyStartedAt = _queue.LastStartedAt;
        var activeJobs = _status.Read().ActiveJobs.Count;
        var threshold = TimeSpan.FromMinutes(thresholdMinutes);
        var claimStagnation = ReviewClaimStagnationPolicy.Evaluate(claimActivity, now, threshold);
        var unclaimable = _unclaimable.Latest(claimActivity.OldestPendingAttemptId);

        var throughputWindowMinutes = Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewQueueStagnation:ThroughputWindowMinutes")
            ?? DefaultThroughputWindowMinutes,
            1, 24 * 60);
        var throughput = _queue.Telemetry.Summarize(now, TimeSpan.FromMinutes(throughputWindowMinutes));

        lock (_gate)
        {
            if (legacyQueueDepth == 0)
            {
                _nonEmptyQueueSince = null;
                _lastStartedAtWhenNonEmpty = null;
            }
            else
            {
                if (_nonEmptyQueueSince == null)
                {
                    _nonEmptyQueueSince = now;
                    _lastStartedAtWhenNonEmpty = legacyStartedAt;
                }
                else if (legacyStartedAt != _lastStartedAtWhenNonEmpty)
                {
                    // A legacy card was picked up since we noticed the legacy
                    // queue was non-empty: reset only the legacy clock. The
                    // canonical side has its own claim clock (AGT-2987).
                    _nonEmptyQueueSince = now;
                    _lastStartedAtWhenNonEmpty = legacyStartedAt;
                }
            }

            var legacyStagnant = legacyQueueDepth > 0
                && _nonEmptyQueueSince is { } since
                && now - since >= threshold;
            var isStagnant = legacyStagnant || claimStagnation.IsStagnant;
            var stagnantSince = Earlier(
                legacyStagnant ? _nonEmptyQueueSince : null,
                claimStagnation.IsStagnant ? claimStagnation.WaitingSince : null);

            var next = new AutoReviewQueueSnapshot
            {
                QueueDepth = pendingCount,
                LegacyQueueDepth = legacyQueueDepth,
                PendingReviewAttempts = pendingReviewAttempts,
                ActiveJobs = activeJobs,
                IsStagnant = isStagnant,
                StagnantSince = stagnantSince,
                StagnantThresholdMinutes = thresholdMinutes,
                ReviewClaimStagnant = claimStagnation.IsStagnant,
                LastReviewClaimAt = claimActivity.LastClaimAt,
                OldestPendingAttemptId = claimActivity.OldestPendingAttemptId,
                OldestPendingTaskKey = claimActivity.OldestPendingTaskKey,
                OldestPendingAttemptCreatedAt = claimActivity.OldestPendingCreatedAt,
                UnclaimableReason = unclaimable is null
                    ? null
                    : AgentStudio.TaskServer.Contracts.ReviewClaimEmptyReasons.UnclaimablePlanRequirements,
                UnclaimableMissingCapabilities = unclaimable?.MissingCapabilities,
                DrainRatePerMinute = throughput.DrainRatePerMinute,
                MedianReviewDurationMs = throughput.MedianDurationMs,
                ThroughputWindowMinutes = throughput.WindowMinutes,
                ObservedAt = now,
            };

            PublishLogTransition(_current, next, now);
            _current = next;
            return _current;
        }
    }

    /// <summary>Earlier of two optional timestamps; null only when both are null.</summary>
    private static DateTime? Earlier(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a < b ? a : b;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewQueueStagnation:IntervalSeconds")
            ?? DefaultIntervalSeconds,
            5, 15 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("auto-review-queue-stagnation-watchdog-stopped");
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
            _logger.LogWarning(ex, "auto-review-queue-stagnation-watchdog-failed");
        }
    }

    private void PublishLogTransition(
        AutoReviewQueueSnapshot previous,
        AutoReviewQueueSnapshot next,
        DateTime now)
    {
        if (!next.IsStagnant)
        {
            if (_warningActive)
            {
                _logger.LogInformation(
                    "auto-review-queue-stagnation-recovered queueDepth={QueueDepth}",
                    next.QueueDepth);
                _warningActive = false;
            }
            _lastWarningAt = null;
            return;
        }

        var repeatDue = _lastWarningAt is null || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (_warningActive && !repeatDue)
            return;

        _logger.LogWarning(
            "auto-review-queue-stagnant queueDepth={QueueDepth} activeJobs={ActiveJobs} stagnantSince={StagnantSince} "
            + "thresholdMinutes={ThresholdMinutes} reviewClaimStagnant={ReviewClaimStagnant} "
            + "oldestPendingAttempt={OldestPendingAttemptId} oldestPendingTask={OldestPendingTaskKey} "
            + "lastReviewClaimAt={LastReviewClaimAt} unclaimableReason={UnclaimableReason} missing={MissingCapabilities}",
            next.QueueDepth,
            next.ActiveJobs,
            next.StagnantSince,
            next.StagnantThresholdMinutes,
            next.ReviewClaimStagnant,
            next.OldestPendingAttemptId ?? "none",
            next.OldestPendingTaskKey ?? "none",
            next.LastReviewClaimAt,
            next.UnclaimableReason ?? "none",
            string.Join(",", next.UnclaimableMissingCapabilities ?? []));
        _warningActive = true;
        _lastWarningAt = now;
    }
}

public static class AutoReviewQueueEndpoints
{
    public static void MapAutoReviewQueueEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/auto-review-queue", (AutoReviewQueueStagnationWatchdog watchdog) =>
            Results.Ok(watchdog.Refresh()));
    }
}
