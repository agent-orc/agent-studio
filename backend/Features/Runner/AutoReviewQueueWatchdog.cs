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
/// Stagnation rule: the combined backlog (<see cref="AutoReviewPostProcessingQueue.PendingCount"/>
/// plus current attempt-authority ReviewAttempts in state Pending) is greater
/// than zero, AND no card has left either side of that backlog (the later of
/// <see cref="AutoReviewPostProcessingQueue.LastStartedAt"/> and
/// <see cref="AttemptAuthorityService.LastReviewClaimAtUtc"/>) since the
/// combined backlog last became non-empty, for longer than the configured
/// threshold.
/// </summary>
public sealed class AutoReviewQueueStagnationWatchdog : BackgroundService
{
    public const int DefaultStagnantThresholdMinutes = 20;
    public const int DefaultIntervalSeconds = 30;
    public const int DefaultThroughputWindowMinutes = 30;

    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly AttemptAuthorityService _authority;
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
        AutoReviewStatusSnapshot status,
        IConfiguration configuration,
        ILogger<AutoReviewQueueStagnationWatchdog> logger)
    {
        _queue = queue;
        _authority = authority;
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
        var pendingReviewAttempts = _authority.ListPendingReviewAttempts().Count;
        var pendingCount = legacyQueueDepth + pendingReviewAttempts;
        var lastStartedAt = Later(_queue.LastStartedAt, _authority.LastReviewClaimAtUtc);
        var activeJobs = _status.Read().ActiveJobs.Count;
        var threshold = TimeSpan.FromMinutes(thresholdMinutes);

        var throughputWindowMinutes = Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewQueueStagnation:ThroughputWindowMinutes")
            ?? DefaultThroughputWindowMinutes,
            1, 24 * 60);
        var throughput = _queue.Telemetry.Summarize(now, TimeSpan.FromMinutes(throughputWindowMinutes));

        lock (_gate)
        {
            if (pendingCount == 0)
            {
                _nonEmptyQueueSince = null;
                _lastStartedAtWhenNonEmpty = null;
            }
            else
            {
                if (_nonEmptyQueueSince == null)
                {
                    _nonEmptyQueueSince = now;
                    _lastStartedAtWhenNonEmpty = lastStartedAt;
                }
                else if (lastStartedAt != _lastStartedAtWhenNonEmpty)
                {
                    // A card was picked up since we noticed the queue was non-empty:
                    // reset the stagnation clock so the threshold applies to a
                    // new period of undraining depth, not from the first card ever.
                    _nonEmptyQueueSince = now;
                    _lastStartedAtWhenNonEmpty = lastStartedAt;
                }
            }

            var isStagnant = pendingCount > 0
                && _nonEmptyQueueSince is { } since
                && now - since >= threshold;
            var stagnantSince = isStagnant ? _nonEmptyQueueSince : null;

            var next = new AutoReviewQueueSnapshot
            {
                QueueDepth = pendingCount,
                LegacyQueueDepth = legacyQueueDepth,
                PendingReviewAttempts = pendingReviewAttempts,
                ActiveJobs = activeJobs,
                IsStagnant = isStagnant,
                StagnantSince = stagnantSince,
                StagnantThresholdMinutes = thresholdMinutes,
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

    /// <summary>Later of two optional timestamps; null only when both are null.</summary>
    private static DateTime? Later(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

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
            "auto-review-queue-stagnant queueDepth={QueueDepth} activeJobs={ActiveJobs} stagnantSince={StagnantSince} thresholdMinutes={ThresholdMinutes}",
            next.QueueDepth,
            next.ActiveJobs,
            next.StagnantSince,
            next.StagnantThresholdMinutes);
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
