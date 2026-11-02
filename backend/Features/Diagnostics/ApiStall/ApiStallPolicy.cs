namespace AgentStudio.Diagnostics;

/// <summary>
/// One late tick of the <see cref="ApiStallDetector"/>: the timer was due
/// <see cref="DelayMs"/> milliseconds before its continuation ran. The runtime
/// figures are taken when the late tick runs and tell a GC pause (large
/// <see cref="GcPauseMs"/>) apart from thread-pool starvation (many
/// <see cref="PendingWorkItems"/>, a growing <see cref="ThreadPoolThreads"/>).
/// </summary>
public sealed record ApiStall(
    DateTimeOffset DetectedAtUtc,
    long DelayMs,
    long GcPauseMs,
    int ThreadPoolThreads,
    long PendingWorkItems,
    int ProcessThreads);

/// <summary>Wire shape of <c>GET /api/diagnostics/api-stalls</c>.</summary>
public sealed record ApiStallSnapshot(
    string Status,
    DateTimeOffset CheckedAtUtc,
    int IntervalMs,
    int WarningThresholdMs,
    int AlarmThresholdMs,
    int AlarmWindowMinutes,
    long MaxDelayMs,
    IReadOnlyList<ApiStall> Recent);

public enum ApiStallLevel
{
    None,
    Warning,
    Alarm,
}

/// <summary>
/// Code-owned thresholds of the stall detector (AGT-3369). The alarm threshold
/// matches the acceptance bar of the 1 November 2026 finding: no stall above
/// five seconds.
/// </summary>
public sealed record ApiStallThresholds(
    TimeSpan Interval,
    TimeSpan Warning,
    TimeSpan Alarm,
    TimeSpan AlarmWindow)
{
    public static ApiStallThresholds Default { get; } = new(
        Interval: TimeSpan.FromMilliseconds(500),
        Warning: TimeSpan.FromSeconds(1),
        Alarm: TimeSpan.FromSeconds(5),
        AlarmWindow: TimeSpan.FromHours(1));
}

/// <summary>Pure decisions of the stall detector.</summary>
public static class ApiStallPolicy
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Alarm = "alarm";

    /// <summary>How much later than scheduled the tick ran; never negative.</summary>
    public static TimeSpan Lateness(TimeSpan scheduled, TimeSpan elapsed)
        => elapsed > scheduled ? elapsed - scheduled : TimeSpan.Zero;

    public static ApiStallLevel Classify(TimeSpan lateness, ApiStallThresholds thresholds)
        => lateness >= thresholds.Alarm ? ApiStallLevel.Alarm
            : lateness >= thresholds.Warning ? ApiStallLevel.Warning
            : ApiStallLevel.None;

    /// <summary>
    /// Status Operations shows: the worst level among the stalls detected
    /// inside the alarm window. Older stalls stay listed but no longer alarm.
    /// </summary>
    public static string Status(IEnumerable<ApiStall> recent, DateTimeOffset now, ApiStallThresholds thresholds)
    {
        var worst = ApiStallLevel.None;
        foreach (var stall in recent)
        {
            if (now - stall.DetectedAtUtc > thresholds.AlarmWindow) continue;
            var level = Classify(TimeSpan.FromMilliseconds(stall.DelayMs), thresholds);
            if (level > worst) worst = level;
        }
        return worst switch
        {
            ApiStallLevel.Alarm => Alarm,
            ApiStallLevel.Warning => Warning,
            _ => Ok,
        };
    }
}
