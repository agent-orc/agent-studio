namespace AgentStudio.Runner;

public enum ReviewParallelismAction { Hold, Raise, Lower }

/// <summary>
/// One evaluation of the adaptive review-parallelism policy: the value this
/// card recommends running the review plane at, and why. This is a
/// recommendation only. Applying it means an operator (or an explicitly
/// opted-in executor) runs the AGT-2628 sanctioned command,
/// <c>sudo agent-runner-deploy config review RUNNER_MAX_PARALLELISM &lt;value&gt;</c>,
/// which this card does not invoke automatically - see
/// docs/system/domains/runner.md and the AGT-2645 results dossier for why
/// the actual execution step is a deliberate follow-up, not a default.
/// </summary>
public sealed record AdaptiveReviewParallelismDecision(
    ReviewParallelismAction Action,
    int RecommendedParallelism,
    string Reason);

public sealed record AdaptiveReviewParallelismOptions
{
    /// <summary>Steady-state target once the queue has drained. Matches the pre-AGT-2645 operating point (parallelism 2).</summary>
    public int BaselineParallelism { get; init; } = 2;

    /// <summary>Queue depth that triggers a raise, even without stagnation.</summary>
    public int RaiseQueueDepthThreshold { get; init; } = 5;

    /// <summary>Minimum time between two raises (and between a raise and a subsequent lower), so a single burst cannot ratchet parallelism up in lockstep with every poll tick.</summary>
    public TimeSpan RaiseCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Minimum time between two lowers. Longer than <see cref="RaiseCooldown"/>: scaling down is the flap-prone direction, since a drained queue can refill on the next completion wave.</summary>
    public TimeSpan LowerCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the queue must have been continuously empty before a lower is considered at all.</summary>
    public TimeSpan DrainedIdleBeforeLower { get; init; } = TimeSpan.FromMinutes(10);

    public static readonly AdaptiveReviewParallelismOptions Default = new();
}

/// <summary>
/// Pure raise/hold/lower decision for the review plane's parallelism ceiling.
/// Two-sided hysteresis (distinct raise vs. lower cooldowns, plus a sustained-empty
/// requirement before lowering) keeps a queue oscillating around the raise threshold
/// from ratcheting parallelism up and down every poll tick.
/// </summary>
public static class AdaptiveReviewParallelismPolicy
{
    public const int SanctionedMin = 1;
    public const int SanctionedMax = 6;

    public static AdaptiveReviewParallelismDecision Evaluate(
        int currentRecommendation,
        int queueDepth,
        bool isStagnant,
        DateTime nowUtc,
        DateTime? lastChangeAtUtc,
        DateTime? queueEmptySinceUtc,
        AdaptiveReviewParallelismOptions? options = null)
    {
        var opts = options ?? AdaptiveReviewParallelismOptions.Default;
        var current = Math.Clamp(currentRecommendation, SanctionedMin, SanctionedMax);
        var sinceLastChange = lastChangeAtUtc is { } last ? nowUtc - last : TimeSpan.MaxValue;

        // AGT-2848: an operator-raised baseline (AutoReviewQueueAdaptiveParallelism:
        // BaselineParallelism) used to only reach a running recommendation seeded
        // below it once a raise/lower crossed it in passing - a backend restart was
        // the only reliable way to pick up the new floor. A non-empty queue is
        // evidence the raised floor is actually needed right now, so it is adopted
        // on this refresh, ahead of and unblocked by the raise cooldown below.
        if (queueDepth > 0 && opts.BaselineParallelism > current)
        {
            var adopted = Math.Min(SanctionedMax, opts.BaselineParallelism);
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Raise,
                adopted,
                $"configured baseline raised to {opts.BaselineParallelism}; adopted at runtime without a backend restart");
        }

        if ((queueDepth >= opts.RaiseQueueDepthThreshold || isStagnant)
            && current < SanctionedMax
            && sinceLastChange >= opts.RaiseCooldown)
        {
            var target = Math.Min(SanctionedMax, current + 1);
            var reason = isStagnant
                ? $"queue stagnant at depth {queueDepth}"
                : $"queue depth {queueDepth} at or above the raise threshold ({opts.RaiseQueueDepthThreshold})";
            return new AdaptiveReviewParallelismDecision(ReviewParallelismAction.Raise, target, reason);
        }

        if (queueDepth == 0
            && current > opts.BaselineParallelism
            && sinceLastChange >= opts.LowerCooldown
            && queueEmptySinceUtc is { } emptySince
            && nowUtc - emptySince >= opts.DrainedIdleBeforeLower)
        {
            var target = Math.Max(opts.BaselineParallelism, current - 1);
            var idleMinutes = (nowUtc - emptySince).TotalMinutes;
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Lower,
                target,
                $"queue empty for {idleMinutes:F0}m, returning toward the baseline ({opts.BaselineParallelism})");
        }

        return new AdaptiveReviewParallelismDecision(ReviewParallelismAction.Hold, current, "within the current band");
    }
}

/// <summary>
/// Tracks the review plane's queue-empty duration and applies
/// <see cref="AdaptiveReviewParallelismPolicy"/> on the same cadence as
/// <see cref="AutoReviewQueueStagnationWatchdog"/>. Owns the running
/// recommendation as its own state (seeded at the configured baseline)
/// rather than reading a live RUNNER_MAX_PARALLELISM value from the fleet,
/// because the backend has no reliable per-host signal for the value
/// currently in effect on every review runner - see the AGT-2645 dossier.
/// Reads its queue depth and stagnation flag from
/// <see cref="AutoReviewQueueStagnationWatchdog"/> rather than the legacy
/// queue directly (AGT-2848), so both signals are the same combined
/// legacy-queue-plus-attempt-authority backlog the watchdog already computes.
/// </summary>
public sealed class AdaptiveReviewParallelismAdvisor : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;

    private readonly AutoReviewQueueStagnationWatchdog _stagnation;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdaptiveReviewParallelismAdvisor> _logger;
    private readonly object _gate = new();

    private DateTime? _queueEmptySince;
    private DateTime? _lastChangeAtUtc;
    private AdaptiveReviewParallelismDecision _current;

    public AdaptiveReviewParallelismAdvisor(
        AutoReviewQueueStagnationWatchdog stagnation,
        IConfiguration configuration,
        ILogger<AdaptiveReviewParallelismAdvisor> logger)
    {
        _stagnation = stagnation;
        _configuration = configuration;
        _logger = logger;
        var baseline = ReadOptions(configuration).BaselineParallelism;
        _current = new AdaptiveReviewParallelismDecision(ReviewParallelismAction.Hold, baseline, "startup baseline");
    }

    public AdaptiveReviewParallelismDecision Current
    {
        get { lock (_gate) return _current; }
    }

    public AdaptiveReviewParallelismDecision Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var options = ReadOptions(_configuration);
        var reviewQueue = _stagnation.Current;
        var queueDepth = reviewQueue.QueueDepth;
        var isStagnant = reviewQueue.IsStagnant;

        lock (_gate)
        {
            _queueEmptySince = queueDepth == 0 ? (_queueEmptySince ?? now) : null;

            var decision = AdaptiveReviewParallelismPolicy.Evaluate(
                _current.RecommendedParallelism,
                queueDepth,
                isStagnant,
                now,
                _lastChangeAtUtc,
                _queueEmptySince,
                options);

            if (decision.Action != ReviewParallelismAction.Hold
                && decision.RecommendedParallelism != _current.RecommendedParallelism)
            {
                _lastChangeAtUtc = now;
                _logger.LogInformation(
                    "auto-review-parallelism-recommendation-changed action={Action} from={From} to={To} reason={Reason}",
                    decision.Action, _current.RecommendedParallelism, decision.RecommendedParallelism, decision.Reason);
            }

            _current = decision;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("AutoReviewQueueAdaptiveParallelism:IntervalSeconds")
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
            _logger.LogDebug("auto-review-parallelism-advisor-stopped");
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
            _logger.LogWarning(ex, "auto-review-parallelism-advisor-failed");
        }
    }

    private static AdaptiveReviewParallelismOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("AutoReviewQueueAdaptiveParallelism");
        var defaults = AdaptiveReviewParallelismOptions.Default;
        return new AdaptiveReviewParallelismOptions
        {
            BaselineParallelism = Math.Clamp(
                section.GetValue<int?>("BaselineParallelism") ?? defaults.BaselineParallelism,
                AdaptiveReviewParallelismPolicy.SanctionedMin, AdaptiveReviewParallelismPolicy.SanctionedMax),
            RaiseQueueDepthThreshold = Math.Max(1,
                section.GetValue<int?>("RaiseQueueDepthThreshold") ?? defaults.RaiseQueueDepthThreshold),
            RaiseCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("RaiseCooldownMinutes") ?? defaults.RaiseCooldown.TotalMinutes)),
            LowerCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("LowerCooldownMinutes") ?? defaults.LowerCooldown.TotalMinutes)),
            DrainedIdleBeforeLower = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("DrainedIdleBeforeLowerMinutes") ?? defaults.DrainedIdleBeforeLower.TotalMinutes)),
        };
    }
}

public static class AdaptiveReviewParallelismEndpoints
{
    public static void MapAdaptiveReviewParallelismEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/auto-review-parallelism-recommendation",
            (AdaptiveReviewParallelismAdvisor advisor) => Results.Ok(advisor.Refresh()));
    }
}
