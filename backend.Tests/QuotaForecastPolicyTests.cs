using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3001: direct matrix tests for the burn-rate and time-to-100 % policy
/// behind <c>GET /api/cli/quota/history</c>. No clock, store, or host setup.
/// </summary>
public sealed class QuotaForecastPolicyTests
{
    private const string Weekly = "Current week (all models)";
    private static readonly DateTime T0 = new(2026, 9, 28, 20, 27, 0, DateTimeKind.Utc);
    private static readonly DateTime WeeklyReset = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);

    private static QuotaHistoryPoint P(double hoursFromT0, double usedPct, DateTime? resetAt = null) =>
        new(T0.AddHours(hoursFromT0), Weekly, usedPct, resetAt ?? WeeklyReset);

    [Theory]
    [InlineData("Current session", QuotaWindowKind.Session)]
    [InlineData("5-hour", QuotaWindowKind.Session)]
    [InlineData("Current week (all models)", QuotaWindowKind.Weekly)]
    [InlineData("Weekly", QuotaWindowKind.Weekly)]
    [InlineData("7-day", QuotaWindowKind.Weekly)]
    [InlineData("Premium requests", QuotaWindowKind.Other)]
    [InlineData("", QuotaWindowKind.Other)]
    public void Classify_MapsLabelToWindowKind(string label, QuotaWindowKind expected)
    {
        Assert.Equal(expected, QuotaForecastPolicy.Classify(label));
    }

    [Fact]
    public void Forecast_OperatorNight_TwoPercentPerHourReachesFullBeforeReset()
    {
        // The operator's manual samples: 67 % at 20:27Z, 83 % at 04:27Z, with
        // 30-minute readings in between rising at 2 % per hour.
        var points = Enumerable.Range(0, 17).Select(i => P(i * 0.5, 67 + i)).ToList();
        var now = T0.AddHours(8).AddMinutes(1);

        var forecast = QuotaForecastPolicy.Forecast(points, now);

        Assert.Equal(QuotaForecastStatus.FullBeforeReset, forecast.Status);
        Assert.Equal(83, forecast.CurrentPct);
        Assert.Equal(T0.AddHours(8), forecast.CurrentAt);
        Assert.Equal(2.0, forecast.RatePctPerHour!.Value, precision: 6);
        Assert.Equal(T0.AddHours(5), forecast.RateFrom);                 // three hours before the latest reading
        Assert.Equal(T0.AddHours(8 + 8.5), forecast.ForecastFullAt);     // 17 points left at 2 %/h = 8.5 h -> 12:57Z
        Assert.Equal(WeeklyReset, forecast.ResetAt);
        Assert.True(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_RateUsesOnlyTheLastThreeHours()
    {
        // Flat for a day, then a steep 3-hour climb: the recent pace wins.
        var points = new List<QuotaHistoryPoint> { P(0, 40), P(20, 40), P(21, 41), P(24, 50) };

        var forecast = QuotaForecastPolicy.Forecast(points, T0.AddHours(24));

        Assert.Equal(T0.AddHours(21), forecast.RateFrom);
        Assert.Equal(3.0, forecast.RatePctPerHour!.Value, precision: 6);
    }

    [Fact]
    public void Forecast_ResetBeforeFull_ReportsResetsFirst()
    {
        var reset = T0.AddHours(4);
        var points = new List<QuotaHistoryPoint> { P(0, 50, reset), P(2, 52, reset) };

        var forecast = QuotaForecastPolicy.Forecast(points, T0.AddHours(2));

        Assert.Equal(QuotaForecastStatus.ResetsFirst, forecast.Status);
        Assert.Equal(1.0, forecast.RatePctPerHour!.Value, precision: 6);
        Assert.Equal(T0.AddHours(50), forecast.ForecastFullAt);
        Assert.False(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_UnknownReset_TreatsFullAsBeforeReset()
    {
        var points = new List<QuotaHistoryPoint>
        {
            new(T0, Weekly, 50, null),
            new(T0.AddHours(1), Weekly, 60, null),
        };

        var forecast = QuotaForecastPolicy.Forecast(points, T0.AddHours(1));

        Assert.Equal(QuotaForecastStatus.FullBeforeReset, forecast.Status);
        Assert.Null(forecast.ResetAt);
        Assert.True(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_IgnoresReadingsBeforeAPassedReset()
    {
        // 95 % in the old cycle, reset at +1h, then 2 % -> 4 % in the new one.
        var oldReset = T0.AddHours(1);
        var newReset = oldReset.AddDays(7);
        var points = new List<QuotaHistoryPoint>
        {
            P(0, 95, oldReset),
            P(1.5, 2, newReset),
            P(2.5, 4, newReset),
        };

        var forecast = QuotaForecastPolicy.Forecast(points, T0.AddHours(2.5));

        Assert.Equal(T0.AddHours(1.5), forecast.RateFrom);
        Assert.Equal(2.0, forecast.RatePctPerHour!.Value, precision: 6);
        Assert.Equal(T0.AddHours(2.5 + 48), forecast.ForecastFullAt);   // 96 points left at 2 %/h, well before the new reset
        Assert.Equal(newReset, forecast.ResetAt);
        Assert.Equal(QuotaForecastStatus.FullBeforeReset, forecast.Status);
    }

    [Fact]
    public void CurrentCycle_ALargeDropStartsANewCycleEvenWithoutResetTime()
    {
        var points = new List<QuotaHistoryPoint>
        {
            new(T0, Weekly, 80, null),
            new(T0.AddHours(1), Weekly, 3, null),
            new(T0.AddHours(2), Weekly, 5, null),
        };

        var cycle = QuotaForecastPolicy.CurrentCycle(points);

        Assert.Equal([3d, 5d], cycle.Select(point => point.UsedPct));
    }

    [Fact]
    public void CurrentCycle_ParserJitterBelowThresholdStaysInTheCycle()
    {
        var points = new List<QuotaHistoryPoint> { P(0, 60), P(1, 59.5), P(2, 62) };

        Assert.Equal(3, QuotaForecastPolicy.CurrentCycle(points).Count);
    }

    [Fact]
    public void Forecast_NoPoints_IsNoData()
    {
        var forecast = QuotaForecastPolicy.Forecast([], T0);

        Assert.Equal(QuotaForecastStatus.NoData, forecast.Status);
        Assert.Null(forecast.CurrentPct);
        Assert.False(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_SingleReading_IsInsufficientData()
    {
        var forecast = QuotaForecastPolicy.Forecast([P(0, 70)], T0);

        Assert.Equal(QuotaForecastStatus.InsufficientData, forecast.Status);
        Assert.Equal(70, forecast.CurrentPct);
        Assert.Null(forecast.RatePctPerHour);
        Assert.Null(forecast.ForecastFullAt);
    }

    [Fact]
    public void Forecast_SpanShorterThanTwentyMinutes_IsInsufficientData()
    {
        var forecast = QuotaForecastPolicy.Forecast([P(0, 70), P(0.25, 72)], T0.AddHours(0.25));

        Assert.Equal(QuotaForecastStatus.InsufficientData, forecast.Status);
    }

    [Theory]
    [InlineData(70, 70)]
    [InlineData(70, 69.5)]
    public void Forecast_FlatOrFallingWithinJitter_IsIdle(double first, double last)
    {
        var forecast = QuotaForecastPolicy.Forecast([P(0, first), P(2, last)], T0.AddHours(2));

        Assert.Equal(QuotaForecastStatus.Idle, forecast.Status);
        Assert.Equal(0, forecast.RatePctPerHour);
        Assert.Null(forecast.ForecastFullAt);
        Assert.False(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_AtOrAboveFull_IsReachedWhileResetIsAhead()
    {
        var forecast = QuotaForecastPolicy.Forecast([P(0, 96), P(1, 100)], T0.AddHours(1));

        Assert.Equal(QuotaForecastStatus.Reached, forecast.Status);
        Assert.Equal(T0.AddHours(1), forecast.ForecastFullAt);
        Assert.True(forecast.ReachesFullBeforeReset);
    }

    [Fact]
    public void Forecast_TinyRate_BeyondOneYear_IsIdle()
    {
        var forecast = QuotaForecastPolicy.Forecast([P(0, 10), P(3, 10.0001)], T0.AddHours(3));

        Assert.Equal(QuotaForecastStatus.Idle, forecast.Status);
        Assert.Null(forecast.ForecastFullAt);
    }

    [Theory]
    [InlineData(QuotaForecastStatus.NoData, "no-data")]
    [InlineData(QuotaForecastStatus.InsufficientData, "insufficient-data")]
    [InlineData(QuotaForecastStatus.Reached, "reached")]
    [InlineData(QuotaForecastStatus.Idle, "idle")]
    [InlineData(QuotaForecastStatus.FullBeforeReset, "full-before-reset")]
    [InlineData(QuotaForecastStatus.ResetsFirst, "resets-first")]
    public void StatusName_IsTheWireValue(QuotaForecastStatus status, string expected)
    {
        Assert.Equal(expected, QuotaForecastPolicy.StatusName(status));
    }
}
