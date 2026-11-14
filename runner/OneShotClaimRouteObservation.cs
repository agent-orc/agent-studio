using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// What the one-shot poll last saw of its claim route. The daemon reports it
/// with its host telemetry so the Environment page shows the condition once
/// instead of the runner log repeating it.
/// </summary>
internal sealed class OneShotClaimRouteObservation
{
    public const string ClaimRouteMissing = "claim-route-missing";
    public const string StandaloneTaskServerRequired = "standalone-task-server-required";

    private readonly object _gate = new();
    private OneShotClaimRouteConditionDto? _current;

    public OneShotClaimRouteConditionDto? Current { get { lock (_gate) return _current; } }

    /// <summary>The condition this failure names, or null for an ordinary poll failure.</summary>
    public static string? Classify(Exception error) => error switch
    {
        TaskServerException ex when ex.Message.Contains("requires the standalone Task Server",
            StringComparison.OrdinalIgnoreCase) => StandaloneTaskServerRequired,
        TaskServerException { StatusCode: 404 } => ClaimRouteMissing,
        _ => null,
    };

    /// <summary>Records the condition; true when it was not present before.</summary>
    public bool Observe(string condition, int statusCode, string? detail, TimeSpan backoff, DateTime now)
    {
        lock (_gate)
        {
            var first = _current?.Condition == condition ? _current.FirstSeenAt : now;
            var entered = _current is null;
            _current = new OneShotClaimRouteConditionDto(condition, first, now,
                (int)backoff.TotalSeconds, statusCode, detail);
            return entered;
        }
    }

    /// <summary>Clears the condition; true when one was present.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            var had = _current is not null;
            _current = null;
            return had;
        }
    }
}
