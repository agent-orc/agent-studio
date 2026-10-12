using Contract = AgentStudio.TaskServer.Contracts;

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

    /// <summary>Whether stagnation may contribute to a raise once the queue depth threshold is met.</summary>
    public bool RaiseOnStagnation { get; init; } = true;

    /// <summary>Minimum time between two raises (and between a raise and a subsequent lower), so a single burst cannot ratchet parallelism up in lockstep with every poll tick.</summary>
    public TimeSpan RaiseCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Minimum time between two lowers. Longer than <see cref="RaiseCooldown"/>: scaling down is the flap-prone direction, since a drained queue can refill on the next completion wave.</summary>
    public TimeSpan LowerCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the queue must have been continuously empty before a lower is considered at all.</summary>
    public TimeSpan DrainedIdleBeforeLower { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Relative duration growth after a raise that identifies a regression.</summary>
    public double SlowdownThreshold { get; init; } = 0.25;

    /// <summary>Role-cgroup throttled wall-time share required to attribute slowdown to CPU supply.</summary>
    public double HighThrottledShareThreshold { get; init; } = 0.10;

    public static readonly AdaptiveReviewParallelismOptions Default = new();
}

/// <summary>
/// Pure raise/hold/lower decision for the review plane's parallelism ceiling.
/// Distinct raise and lower cooldowns prevent rapid changes. A drained queue
/// must stay empty before lowering; host saturation can lower a busy plane.
/// </summary>
public static class AdaptiveReviewParallelismPolicy
{
    public const int SanctionedMin = 1;
    public const int SanctionedMax = 6;
    public const double MinimumCoresPerReview = 2.0;

    public static AdaptiveReviewParallelismDecision Evaluate(
        int currentRecommendation,
        int queueDepth,
        bool isStagnant,
        DateTime nowUtc,
        DateTime? lastChangeAtUtc,
        DateTime? queueEmptySinceUtc,
        AdaptiveReviewParallelismOptions? options = null,
        Contract.ReviewPlaneBudgetDto? planeBudget = null,
        double? durationBeforeLastRaiseSeconds = null)
    {
        var opts = options ?? AdaptiveReviewParallelismOptions.Default;
        var current = Math.Clamp(
            currentRecommendation,
            planeBudget is null ? SanctionedMin : 0,
            SanctionedMax);
        var sinceLastChange = lastChangeAtUtc is { } last ? nowUtc - last : TimeSpan.Zero;
        var planeCeiling = planeBudget is null
            ? SanctionedMax
            : Math.Clamp(
                (int)Math.Floor(planeBudget.PlaneCpuCores / MinimumCoresPerReview),
                0,
                SanctionedMax);

        if (current > planeCeiling)
        {
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Lower,
                planeCeiling,
                PlaneReason("ceiling reduced", planeBudget!, planeCeiling));
        }

        var throttled = planeBudget?.ThrottledShare >= opts.HighThrottledShareThreshold;
        var slowed = durationBeforeLastRaiseSeconds is > 0
            && planeBudget?.RollingReviewDurationSeconds is > 0
            && planeBudget.RollingReviewDurationSeconds
                > durationBeforeLastRaiseSeconds.Value * (1 + opts.SlowdownThreshold);
        var saturated = throttled || slowed;
        var pressureReason = throttled && slowed
            ? $"role throttled share {planeBudget!.ThrottledShare:P0} and rolling review duration grew "
              + $"{(planeBudget.RollingReviewDurationSeconds!.Value / durationBeforeLastRaiseSeconds!.Value - 1) * 100:F0}%"
            : throttled
                ? $"role throttled share {planeBudget!.ThrottledShare:P0}"
                : "rolling review duration regressed after the last raise";

        if (saturated && current > opts.BaselineParallelism)
        {
            if (sinceLastChange < opts.LowerCooldown)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current,
                    $"host saturated ({pressureReason}); lower cooldown active");
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Lower,
                Math.Max(opts.BaselineParallelism, current - 1),
                $"host saturated ({pressureReason}); returning toward the baseline ({opts.BaselineParallelism})");
        }

        // AGT-2848: an operator-raised baseline (AutoReviewQueueAdaptiveParallelism:
        // BaselineParallelism) used to only reach a running recommendation seeded
        // below it once a raise/lower crossed it in passing - a backend restart was
        // the only reliable way to pick up the new floor. A non-empty queue is
        // evidence the raised floor is needed. It is adopted with fresh host
        // headroom, ahead of and unblocked by the adaptive raise cooldown.
        if (queueDepth > 0 && opts.BaselineParallelism > current)
        {
            if (saturated)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current,
                    $"configured baseline raised, but host saturated ({pressureReason})");
            if (planeBudget is null)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current,
                    "configured baseline raised, but no fresh review plane budget");
            var adopted = Math.Min(planeCeiling, Math.Min(SanctionedMax, opts.BaselineParallelism));
            if (adopted <= current)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold,
                    current,
                    PlaneReason("raise refused", planeBudget!, planeCeiling));
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Raise,
                adopted,
                $"configured baseline raised to {opts.BaselineParallelism}; adopted at runtime without a backend restart");
        }

        var depthWarrantsRaise = queueDepth >= opts.RaiseQueueDepthThreshold;
        if (depthWarrantsRaise && current < SanctionedMax)
        {
            if (saturated)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current,
                    $"{(isStagnant ? "stagnant but " : "")}host saturated ({pressureReason})");
            if (isStagnant && !opts.RaiseOnStagnation)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current, "stagnation raises disabled by configuration");
            if (planeBudget is null)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current, "raise refused: no fresh review plane budget");
            if (current >= planeCeiling)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current,
                    PlaneReason("raise refused", planeBudget, planeCeiling));
            if (sinceLastChange < opts.RaiseCooldown)
                return new AdaptiveReviewParallelismDecision(
                    ReviewParallelismAction.Hold, current, "raise cooldown active");
            var target = Math.Min(SanctionedMax, current + 1);
            var reason = isStagnant
                ? $"queue stagnant at depth {queueDepth}"
                : $"queue depth {queueDepth} at or above the raise threshold ({opts.RaiseQueueDepthThreshold})";
            return new AdaptiveReviewParallelismDecision(ReviewParallelismAction.Raise, target, reason);
        }

        if (isStagnant && !depthWarrantsRaise)
        {
            return new AdaptiveReviewParallelismDecision(
                ReviewParallelismAction.Hold,
                current,
                $"stagnant queue depth {queueDepth} below raise threshold ({opts.RaiseQueueDepthThreshold})");
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

    private static string PlaneReason(
        string prefix,
        Contract.ReviewPlaneBudgetDto budget,
        int supportedWorkers)
        => $"{prefix}: plane {budget.PlaneCpuCores * 100:0}% supports {supportedWorkers} workers "
           + $"at {MinimumCoresPerReview:0} cores each (cpu.max={budget.CpuMax}, host={budget.HostCores} cores)";
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
    private readonly V1ReviewExecutorRegistry _registry;
    private readonly ILogger<AdaptiveReviewParallelismAdvisor> _logger;
    private readonly object _gate = new();

    private DateTime? _queueEmptySince;
    private DateTime? _lastChangeAtUtc;
    private AdaptiveReviewParallelismDecision _current;
    private double? _durationBeforeLastRaiseSeconds;

    public AdaptiveReviewParallelismAdvisor(
        AutoReviewQueueStagnationWatchdog stagnation,
        IConfiguration configuration,
        ILogger<AdaptiveReviewParallelismAdvisor> logger,
        V1ReviewExecutorRegistry registry)
    {
        _stagnation = stagnation;
        _configuration = configuration;
        _logger = logger;
        _registry = registry;
        var baseline = ReadOptions(configuration).BaselineParallelism;
        _current = new AdaptiveReviewParallelismDecision(ReviewParallelismAction.Hold, baseline, "startup baseline");
        _lastChangeAtUtc = DateTime.UtcNow;
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
                options,
                _registry.LatestReviewPlaneBudget(now),
                _durationBeforeLastRaiseSeconds);

            if (decision.Action != ReviewParallelismAction.Hold
                && decision.RecommendedParallelism != _current.RecommendedParallelism)
            {
                if (decision.Action == ReviewParallelismAction.Raise)
                {
                    _durationBeforeLastRaiseSeconds =
                        _registry.LatestReviewPlaneBudget(now)?.RollingReviewDurationSeconds;
                }
                _lastChangeAtUtc = now;
                _logger.LogInformation(
                    "auto-review-parallelism-recommendation-changed action={Action} from={From} to={To} reason={Reason}",
                    decision.Action, _current.RecommendedParallelism, decision.RecommendedParallelism, decision.Reason);
            }
            else if (!string.Equals(decision.Reason, "within the current band", StringComparison.Ordinal))
            {
                var budget = _registry.LatestReviewPlaneBudget(now);
                _logger.LogInformation(
                    "auto-review-parallelism-decision action={Action} current={Current} queueDepth={QueueDepth} stagnant={Stagnant} planeCpuCores={PlaneCpuCores} throttledShare={ThrottledShare} rollingDurationSeconds={RollingDurationSeconds} reason={Reason}",
                    decision.Action,
                    _current.RecommendedParallelism,
                    queueDepth,
                    isStagnant,
                    budget?.PlaneCpuCores,
                    budget?.ThrottledShare,
                    budget?.RollingReviewDurationSeconds,
                    decision.Reason);
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
            RaiseOnStagnation = section.GetValue<bool?>("RaiseOnStagnation") ?? defaults.RaiseOnStagnation,
            RaiseCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("RaiseCooldownMinutes") ?? defaults.RaiseCooldown.TotalMinutes)),
            LowerCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("LowerCooldownMinutes") ?? defaults.LowerCooldown.TotalMinutes)),
            DrainedIdleBeforeLower = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("DrainedIdleBeforeLowerMinutes") ?? defaults.DrainedIdleBeforeLower.TotalMinutes)),
            SlowdownThreshold = Math.Clamp(
                section.GetValue<double?>("SlowdownThreshold") ?? defaults.SlowdownThreshold,
                0.05, 2),
            HighThrottledShareThreshold = Math.Clamp(
                section.GetValue<double?>("HighThrottledShareThreshold") ?? defaults.HighThrottledShareThreshold,
                0.01, 1),
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
