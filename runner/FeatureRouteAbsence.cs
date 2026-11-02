using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Pauses a host poll loop whose feature route the server does not map. A
/// monolith answers the standalone Task Server's feature routes with
/// <see cref="RouteAbsenceContract.ErrorCode"/>. The loop logs that once and
/// then polls only every <see cref="RecheckInterval"/>, so a server that maps
/// the route later is picked up without a host restart.
/// </summary>
public sealed class FeatureRouteAbsence(string feature, Action<string> log)
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    public bool IsAbsent { get; private set; }

    /// <summary>True when the server said it does not map the called route.</summary>
    public static bool IsRouteAbsent(Exception exception)
        => exception is TaskServerException { StatusCode: 404, ErrorCode: RouteAbsenceContract.ErrorCode };

    /// <summary>
    /// Records a failed poll. Returns true when the failure is the route's
    /// absence, which the caller must not log again.
    /// </summary>
    public bool TryRecordAbsent(Exception exception)
    {
        if (!IsRouteAbsent(exception)) return false;
        if (!IsAbsent)
            log($"{feature} paused: this server does not map its route; re-checking every {RecheckInterval.TotalMinutes:0} minutes");
        IsAbsent = true;
        return true;
    }

    /// <summary>Records a poll the server answered on the feature route.</summary>
    public void RecordAnswered()
    {
        if (IsAbsent) log($"{feature} resumed: this server maps its route");
        IsAbsent = false;
    }

    /// <summary>The loop's own interval, or <see cref="RecheckInterval"/> while the route is absent.</summary>
    public TimeSpan NextDelay(TimeSpan pollInterval) => IsAbsent ? RecheckInterval : pollInterval;
}
