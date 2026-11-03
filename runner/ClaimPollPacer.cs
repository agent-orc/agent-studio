using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Paces a host loop that polls a claim route the operator may need at any
/// moment (device sign-in, Git setup). An answered poll keeps the
/// <see cref="BaseInterval"/>, so a person who starts a session gets a fast
/// claim. A failing poll backs off up to <see cref="MaxInterval"/> and is logged
/// once per distinct failure with its status; the count of repeats is logged
/// when the failure changes or the poll answers again (AGT-3432: each host
/// logged a bare exception type every two seconds). A server that says it does
/// not map the route (<see cref="RouteAbsenceContract"/>) is logged once and
/// the loop stops for this process.
/// </summary>
public sealed class ClaimPollPacer(string feature, Action<string> log)
{
    public static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    private string? _failure;
    private int _repeats;

    /// <summary>Consecutive failed polls since the last answer.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>True once the server said it does not map the route; the loop must end.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Records a poll the server answered and returns the delay before the next one.</summary>
    public TimeSpan RecordAnswered()
    {
        if (_failure is not null)
            log($"{feature} poll recovered after {ConsecutiveFailures} failed polls (last: {_failure} x{_repeats})");
        _failure = null;
        _repeats = 0;
        ConsecutiveFailures = 0;
        return BaseInterval;
    }

    /// <summary>Records a failed poll and returns the delay before the next one.</summary>
    public TimeSpan RecordFailed(Exception exception)
    {
        if (FeatureRouteAbsence.IsRouteAbsent(exception))
        {
            if (!Stopped)
                log($"{feature} stopped: this server does not map its route; restart the host after the server maps it");
            Stopped = true;
            return Timeout.InfiniteTimeSpan;
        }
        ConsecutiveFailures++;
        var failure = Describe(exception);
        if (failure != _failure)
        {
            if (_failure is not null)
                log($"{feature} poll failed {_repeats} times with {_failure}; now {failure}");
            else
                log($"{feature} poll failed: {failure}; backing off up to {MaxInterval.TotalSeconds:0}s, repeats are counted");
            _failure = failure;
            _repeats = 0;
        }
        _repeats++;
        return Backoff(ConsecutiveFailures);
    }

    /// <summary>The delay after <paramref name="consecutiveFailures"/> failed polls: doubling, capped.</summary>
    public static TimeSpan Backoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return BaseInterval;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures, 16));
        return TimeSpan.FromTicks((long)Math.Min(BaseInterval.Ticks * factor, MaxInterval.Ticks));
    }

    /// <summary>The status or transport fault of a failed poll, without its message.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        TaskServerException { ErrorCode: { Length: > 0 } code } server => $"status={server.StatusCode} code={code}",
        TaskServerException server => $"status={server.StatusCode}",
        HttpRequestException { StatusCode: { } status } => $"status={(int)status}",
        HttpRequestException transport => $"transport={transport.HttpRequestError}",
        TaskCanceledException or TimeoutException => "timeout",
        _ => $"type={exception.GetType().Name}",
    };
}
