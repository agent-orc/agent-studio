using System.Diagnostics;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Stall detector of the API process (AGT-3369). On 1 November 2026 the API
/// logged nothing for up to 61 seconds at a time while users saw the backend
/// go offline; nothing in the process said so. This service waits on a short
/// timer in a loop. The timer's continuation runs on the thread pool, so a
/// stop-the-world GC pause or a starved pool makes it run late. A tick that
/// runs at least <see cref="ApiStallThresholds.Warning"/> late logs an
/// <c>api-stall</c> warning with the delay and the runtime figures that tell
/// the two causes apart, and is kept in a bounded list that
/// <c>GET /api/diagnostics/api-stalls</c> serves to Operations.
/// </summary>
public sealed class ApiStallDetector : BackgroundService
{
    internal const int RecentCapacity = 50;

    private readonly ILogger<ApiStallDetector> _logger;
    private readonly TimeProvider _time;
    private readonly ApiStallThresholds _thresholds;
    private readonly Lock _gate = new();
    private readonly Queue<ApiStall> _recent = new();
    private long _maxDelayMs;
    private TimeSpan _gcPauseAtLastTick;
    private long _waits;

    public ApiStallDetector(
        ILogger<ApiStallDetector> logger,
        TimeProvider? time = null,
        ApiStallThresholds? thresholds = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _thresholds = thresholds ?? ApiStallThresholds.Default;
        _gcPauseAtLastTick = GC.GetTotalPauseDuration();
    }

    /// <summary>Waits the loop has started; lets a test advance a fake clock only once the timer exists.</summary>
    internal long Waits => Interlocked.Read(ref _waits);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var scheduledAt = _time.GetTimestamp();
            try
            {
                var wait = Task.Delay(_thresholds.Interval, _time, stoppingToken);
                Interlocked.Increment(ref _waits);
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            Observe(_time.GetElapsedTime(scheduledAt));
        }
    }

    /// <summary>
    /// Records one tick that took <paramref name="elapsed"/> for a wait of
    /// <see cref="ApiStallThresholds.Interval"/>. Returns the stall when the
    /// tick was late enough to count as one.
    /// </summary>
    internal ApiStall? Observe(TimeSpan elapsed)
    {
        var gcPause = GC.GetTotalPauseDuration();
        var gcPauseSinceLastTick = gcPause - _gcPauseAtLastTick;
        _gcPauseAtLastTick = gcPause;

        var lateness = ApiStallPolicy.Lateness(_thresholds.Interval, elapsed);
        var level = ApiStallPolicy.Classify(lateness, _thresholds);
        if (level == ApiStallLevel.None) return null;

        int processThreads;
        using (var process = Process.GetCurrentProcess()) processThreads = process.Threads.Count;
        var stall = new ApiStall(
            _time.GetUtcNow(),
            (long)lateness.TotalMilliseconds,
            (long)gcPauseSinceLastTick.TotalMilliseconds,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            processThreads);
        lock (_gate)
        {
            _recent.Enqueue(stall);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
            _maxDelayMs = Math.Max(_maxDelayMs, stall.DelayMs);
        }
        _logger.LogWarning(
            "api-stall delayMs={DelayMs} level={Level} gcPauseMs={GcPauseMs} threadPoolThreads={ThreadPoolThreads} pendingWorkItems={PendingWorkItems} processThreads={ProcessThreads}",
            stall.DelayMs, level, stall.GcPauseMs, stall.ThreadPoolThreads, stall.PendingWorkItems, stall.ProcessThreads);
        return stall;
    }

    public ApiStallSnapshot Snapshot()
    {
        ApiStall[] recent;
        long max;
        lock (_gate)
        {
            recent = [.. _recent.Reverse()];
            max = _maxDelayMs;
        }
        var now = _time.GetUtcNow();
        return new ApiStallSnapshot(
            ApiStallPolicy.Status(recent, now, _thresholds),
            now,
            (int)_thresholds.Interval.TotalMilliseconds,
            (int)_thresholds.Warning.TotalMilliseconds,
            (int)_thresholds.Alarm.TotalMilliseconds,
            (int)_thresholds.AlarmWindow.TotalMinutes,
            max,
            recent);
    }
}
