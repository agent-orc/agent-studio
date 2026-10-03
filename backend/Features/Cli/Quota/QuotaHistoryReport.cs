namespace AgentStudio.Cli;

/// <summary>Response of <c>GET /api/cli/quota/history</c> (AGT-3001).</summary>
public sealed record QuotaHistoryResponse(
    string CliType,
    int Hours,
    DateTime From,
    DateTime To,
    int RetentionDays,
    double RateLookbackHours,
    IReadOnlyList<QuotaHistoryWindowResponse> Windows);

public sealed record QuotaHistoryWindowResponse(
    string Label,
    string Kind,
    IReadOnlyList<QuotaHistoryPointResponse> Points,
    QuotaForecastResponse Forecast);

public sealed record QuotaHistoryPointResponse(DateTime At, double UsedPct, DateTime? ResetAt);

public sealed record QuotaForecastResponse(
    string Status,
    double? CurrentPct,
    DateTime? CurrentAt,
    double? RatePctPerHour,
    DateTime? RateFrom,
    DateTime? ForecastFullAt,
    DateTime? ResetAt,
    bool ReachesFullBeforeReset);

/// <summary>
/// Builds the history response from the recorded series. The forecast always
/// reads the whole retained series, so a short <c>hours</c> range does not
/// starve the three-hour rate window or hide the start of the current cycle.
/// </summary>
public static class QuotaHistoryReport
{
    public const int DefaultHours = 48;
    public static readonly int MaxHours = (int)QuotaHistoryStore.Retention.TotalHours;

    public static QuotaHistoryResponse Build(string cliType, int hours, IReadOnlyList<QuotaHistoryPoint> retained, DateTime nowUtc)
    {
        var from = nowUtc - TimeSpan.FromHours(hours);
        var windows = retained
            .GroupBy(point => point.Window, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var points = group.OrderBy(point => point.At).ToList();
                var kind = QuotaForecastPolicy.Classify(group.Key);
                var forecast = QuotaForecastPolicy.Forecast(points, nowUtc);
                return (Kind: kind, Window: new QuotaHistoryWindowResponse(
                    points[^1].Window,
                    QuotaForecastPolicy.KindName(kind),
                    points.Where(point => point.At >= from)
                        .Select(point => new QuotaHistoryPointResponse(point.At, point.UsedPct, point.ResetAt))
                        .ToList(),
                    new QuotaForecastResponse(
                        QuotaForecastPolicy.StatusName(forecast.Status),
                        forecast.CurrentPct,
                        forecast.CurrentAt,
                        forecast.RatePctPerHour is { } rate ? Math.Round(rate, 3) : null,
                        forecast.RateFrom,
                        forecast.ForecastFullAt,
                        forecast.ResetAt,
                        forecast.ReachesFullBeforeReset)));
            })
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Window.Label, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Window)
            .ToList();

        return new QuotaHistoryResponse(
            cliType,
            hours,
            from,
            nowUtc,
            (int)QuotaHistoryStore.Retention.TotalDays,
            QuotaForecastPolicy.RateLookback.TotalHours,
            windows);
    }
}
