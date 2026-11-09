using AgentStudio.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3369: on 1 November 2026 the API logged nothing for up to 61 seconds
/// at a time and nothing in the process said it had stalled. The stall
/// detector's timer notices when it fired late, logs an <c>api-stall</c>
/// warning with the delay, and keeps the stall for Operations.
/// </summary>
public sealed class ApiStallDetectorTests
{
    // clock-independent: a fixed start for the fake clock, not a comparison with the wall clock.
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ApiStallThresholds Thresholds = ApiStallThresholds.Default;

    // ---- Policy matrix -------------------------------------------------------

    [Theory]
    [InlineData(500, 400, 0)]
    [InlineData(500, 500, 0)]
    [InlineData(500, 1700, 1200)]
    [InlineData(500, 61_500, 61_000)]
    public void Lateness_is_the_time_past_the_scheduled_wait(int scheduledMs, int elapsedMs, int expectedMs)
        => Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            ApiStallPolicy.Lateness(TimeSpan.FromMilliseconds(scheduledMs), TimeSpan.FromMilliseconds(elapsedMs)));

    [Theory]
    [InlineData(0, ApiStallLevel.None)]
    [InlineData(999, ApiStallLevel.None)]
    [InlineData(1000, ApiStallLevel.Warning)]
    [InlineData(4999, ApiStallLevel.Warning)]
    [InlineData(5000, ApiStallLevel.Alarm)]
    [InlineData(61_000, ApiStallLevel.Alarm)]
    public void Classify_uses_the_warning_and_alarm_thresholds(int latenessMs, ApiStallLevel expected)
        => Assert.Equal(expected, ApiStallPolicy.Classify(TimeSpan.FromMilliseconds(latenessMs), Thresholds));

    [Fact]
    public void Status_is_the_worst_stall_inside_the_alarm_window()
    {
        var now = T0.AddHours(3);
        ApiStall Stall(TimeSpan ago, long delayMs) => new(now - ago, delayMs, 0, 8, 0, 40);

        Assert.Equal(ApiStallPolicy.Ok, ApiStallPolicy.Status([], now, Thresholds));
        Assert.Equal(ApiStallPolicy.Warning, ApiStallPolicy.Status([Stall(TimeSpan.FromMinutes(5), 1500)], now, Thresholds));
        Assert.Equal(
            ApiStallPolicy.Alarm,
            ApiStallPolicy.Status([Stall(TimeSpan.FromMinutes(5), 1500), Stall(TimeSpan.FromMinutes(59), 61_000)], now, Thresholds));
        // A 61-second stall two hours ago no longer alarms; it stays listed.
        Assert.Equal(ApiStallPolicy.Ok, ApiStallPolicy.Status([Stall(TimeSpan.FromHours(2), 61_000)], now, Thresholds));
    }

    // ---- Detector ------------------------------------------------------------

    [Fact]
    public void Tick_on_time_records_and_logs_nothing()
    {
        var logger = new CapturingLogger();
        var detector = new ApiStallDetector(logger, new FakeTimeProvider(T0), Thresholds);

        Assert.Null(detector.Observe(TimeSpan.FromMilliseconds(520)));
        Assert.Null(detector.Observe(Thresholds.Interval + TimeSpan.FromMilliseconds(999)));

        var snapshot = detector.Snapshot();
        Assert.Equal(ApiStallPolicy.Ok, snapshot.Status);
        Assert.Empty(snapshot.Recent);
        Assert.Equal(0, snapshot.MaxDelayMs);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Late_tick_logs_a_warning_with_the_delay_and_alarms_in_the_snapshot()
    {
        var logger = new CapturingLogger();
        var time = new FakeTimeProvider(T0);
        var detector = new ApiStallDetector(logger, time, Thresholds);

        var stall = detector.Observe(Thresholds.Interval + TimeSpan.FromSeconds(61));

        Assert.NotNull(stall);
        Assert.Equal(61_000, stall.DelayMs);
        Assert.Equal(T0, stall.DetectedAtUtc);
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("api-stall delayMs=61000", message);
        Assert.Contains("level=Alarm", message);

        var snapshot = detector.Snapshot();
        Assert.Equal(ApiStallPolicy.Alarm, snapshot.Status);
        Assert.Equal(61_000, snapshot.MaxDelayMs);
        Assert.Equal(5000, snapshot.AlarmThresholdMs);
        Assert.Equal(stall, Assert.Single(snapshot.Recent));

        time.Advance(Thresholds.AlarmWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(ApiStallPolicy.Ok, detector.Snapshot().Status);
    }

    [Fact]
    public void Recent_stalls_are_bounded_newest_first_and_keep_the_maximum()
    {
        var detector = new ApiStallDetector(new CapturingLogger(), new FakeTimeProvider(T0), Thresholds);
        detector.Observe(Thresholds.Interval + TimeSpan.FromSeconds(30));
        for (var i = 0; i < ApiStallDetector.RecentCapacity + 10; i++)
            detector.Observe(Thresholds.Interval + TimeSpan.FromMilliseconds(1000 + i));

        var snapshot = detector.Snapshot();
        Assert.Equal(ApiStallDetector.RecentCapacity, snapshot.Recent.Count);
        Assert.Equal(1000 + ApiStallDetector.RecentCapacity + 9, snapshot.Recent[0].DelayMs);
        Assert.Equal(30_000, snapshot.MaxDelayMs);
    }

    [Fact]
    public async Task Timer_that_fires_late_is_detected_by_the_running_service()
    {
        var logger = new CapturingLogger();
        var time = new FakeTimeProvider(T0);
        using var detector = new ApiStallDetector(logger, time, Thresholds);
        await detector.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => detector.Waits > 0, TimeSpan.FromSeconds(10)), "the detector never started waiting");

        // The pending tick was due after 500 ms; its continuation only runs
        // once the clock has moved 6.5 seconds, as after a 6-second pause.
        time.Advance(Thresholds.Interval + TimeSpan.FromSeconds(6));

        Assert.True(
            SpinWait.SpinUntil(() => detector.Snapshot().Recent.Count > 0, TimeSpan.FromSeconds(10)),
            "the late tick was not recorded");
        await detector.StopAsync(CancellationToken.None);

        var stall = Assert.Single(detector.Snapshot().Recent);
        Assert.Equal(6000, stall.DelayMs);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("api-stall delayMs=6000"));
    }

    private sealed class CapturingLogger : ILogger<ApiStallDetector>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
