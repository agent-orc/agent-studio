namespace AgentStudio.Cli;

/// <summary>What a quota window measures, inferred from its label.</summary>
public enum QuotaWindowKind
{
    Session,
    Weekly,
    Other,
}

/// <summary>
/// Outcome of <see cref="QuotaForecastPolicy.Forecast"/>. Serialized as the
/// kebab-case strings in <see cref="QuotaForecastPolicy.StatusName"/>.
/// </summary>
public enum QuotaForecastStatus
{
    /// <summary>No reading recorded for this window.</summary>
    NoData,
    /// <summary>Fewer than two readings, or less than <see cref="QuotaForecastPolicy.MinRateSpan"/> of history, in the current cycle.</summary>
    InsufficientData,
    /// <summary>The latest reading is already at or above 100 %.</summary>
    Reached,
    /// <summary>Usage did not grow over the rate window; no forecast at this rate.</summary>
    Idle,
    /// <summary>At the current rate the window reaches 100 % before it resets (or the reset is unknown).</summary>
    FullBeforeReset,
    /// <summary>At the current rate the window resets before it reaches 100 %.</summary>
    ResetsFirst,
}

/// <summary>Rate and forecast for one quota window at one point in time.</summary>
public sealed record QuotaForecast(
    QuotaForecastStatus Status,
    double? CurrentPct,
    DateTime? CurrentAt,
    double? RatePctPerHour,
    DateTime? RateFrom,
    DateTime? ForecastFullAt,
    DateTime? ResetAt,
    bool ReachesFullBeforeReset);

/// <summary>
/// Pure burn-rate and time-to-100 % forecast over the recorded quota series
/// (AGT-3001). This replaces the operator's manual sampling ("67 % at 20:27Z,
/// 83 % at 04:27Z, so 2 % per hour") with the same arithmetic over real history:
///
/// <list type="number">
///   <item>Only the current reset cycle counts. A reading whose own reset time
///         had passed by the next reading, or a drop of more than
///         <see cref="ResetDropPct"/> points, starts a new cycle.</item>
///   <item>The rate is the change from the earliest reading inside the last
///         <see cref="RateLookback"/> of the cycle to the latest reading,
///         divided by the hours between them. A falling or flat series is idle.</item>
///   <item>The forecast extrapolates linearly from the latest reading to 100 %
///         and compares that moment with the latest known reset.</item>
/// </list>
///
/// Unlike <see cref="QuotaWindowProjection"/>, which infers an average pace from
/// the window start for admission, this uses the recent observed pace, which is
/// what the operator asks about during a busy night.
/// </summary>
public static class QuotaForecastPolicy
{
    public static readonly TimeSpan RateLookback = TimeSpan.FromHours(3);
    public static readonly TimeSpan MinRateSpan = TimeSpan.FromMinutes(20);
    public const double ResetDropPct = 1.0;
    private const double FullPct = 100.0;
    private static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(365);

    public static QuotaWindowKind Classify(string? label) =>
        QuotaWindowProjection.InferWindowLength(label) switch
        {
            { } length when length == TimeSpan.FromHours(5) => QuotaWindowKind.Session,
            { } length when length == TimeSpan.FromDays(7) => QuotaWindowKind.Weekly,
            _ => QuotaWindowKind.Other,
        };

    public static string KindName(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.Session => "session",
        QuotaWindowKind.Weekly => "weekly",
        _ => "other",
    };

    public static string StatusName(QuotaForecastStatus status) => status switch
    {
        QuotaForecastStatus.NoData => "no-data",
        QuotaForecastStatus.InsufficientData => "insufficient-data",
        QuotaForecastStatus.Reached => "reached",
        QuotaForecastStatus.Idle => "idle",
        QuotaForecastStatus.FullBeforeReset => "full-before-reset",
        _ => "resets-first",
    };

    /// <summary>
    /// The readings of the latest reset cycle, oldest first.
    /// <paramref name="points"/> must be the readings of a single window.
    /// </summary>
    public static IReadOnlyList<QuotaHistoryPoint> CurrentCycle(IReadOnlyList<QuotaHistoryPoint> points)
    {
        var ordered = points.OrderBy(point => point.At).ToList();
        var start = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var earlier = ordered[i - 1];
            var later = ordered[i];
            var resetPassed = earlier.ResetAt is { } reset && reset <= later.At;
            var dropped = earlier.UsedPct - later.UsedPct > ResetDropPct;
            if (resetPassed || dropped) start = i;
        }
        return ordered.GetRange(start, ordered.Count - start);
    }

    /// <summary>Rate and forecast for the readings of one window.</summary>
    public static QuotaForecast Forecast(IReadOnlyList<QuotaHistoryPoint> points, DateTime nowUtc)
    {
        if (points.Count == 0)
            return new(QuotaForecastStatus.NoData, null, null, null, null, null, null, false);

        var cycle = CurrentCycle(points);
        var latest = cycle[^1];
        var reset = latest.ResetAt;
        var resetStillAhead = reset is null || reset > nowUtc;

        if (latest.UsedPct >= FullPct)
            return new(QuotaForecastStatus.Reached, latest.UsedPct, latest.At, null, null, latest.At, reset, resetStillAhead);

        var anchor = cycle.First(point => point.At >= latest.At - RateLookback);
        var span = latest.At - anchor.At;
        if (span < MinRateSpan)
            return new(QuotaForecastStatus.InsufficientData, latest.UsedPct, latest.At, null, null, null, reset, false);

        var rate = (latest.UsedPct - anchor.UsedPct) / span.TotalHours;
        if (rate <= 0)
            return new(QuotaForecastStatus.Idle, latest.UsedPct, latest.At, 0, anchor.At, null, reset, false);

        var hoursToFull = (FullPct - latest.UsedPct) / rate;
        if (hoursToFull > MaxHorizon.TotalHours)
            return new(QuotaForecastStatus.Idle, latest.UsedPct, latest.At, rate, anchor.At, null, reset, false);

        var fullAt = latest.At + TimeSpan.FromHours(hoursToFull);
        var fullFirst = reset is null || fullAt < reset;
        return new(
            fullFirst ? QuotaForecastStatus.FullBeforeReset : QuotaForecastStatus.ResetsFirst,
            latest.UsedPct,
            latest.At,
            rate,
            anchor.At,
            fullAt,
            reset,
            fullFirst);
    }
}
