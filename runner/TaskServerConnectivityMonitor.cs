namespace AgentRunner;

/// <summary>
/// Process-local view of the route from an Agent Host to its Task Server.
/// The route cannot report through itself while it is down, so the Task Server
/// also treats the connectivity capability's freshness deadline as the remote
/// alarm. This monitor supplies the host-side transition facts, bounded log
/// volume, and the telemetry snapshot delivered when the route is available.
/// </summary>
public sealed class TaskServerConnectivityMonitor
{
    public static readonly TimeSpan DefaultEscalationAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromHours(1);

    private readonly Action<string> _log;
    private readonly TimeSpan _escalationAfter;
    private readonly TimeSpan _summaryInterval;
    private DateTime? _failureStartedAt;
    private DateTime? _escalatedAt;
    private DateTime? _lastSummaryAt;
    private DateTime? _lastRecoveredAt;
    private int _consecutiveFailures;
    private string? _lastError;
    private int? _httpStatus;
    private string? _request;
    private DateTime? _observedAt;
    private bool? _reachable;

    public TaskServerConnectivityMonitor(
        Action<string> log,
        TimeSpan? escalationAfter = null,
        TimeSpan? summaryInterval = null)
    {
        _log = log;
        _escalationAfter = escalationAfter ?? DefaultEscalationAfter;
        _summaryInterval = summaryInterval ?? DefaultSummaryInterval;
    }

    public TaskServerConnectivitySnapshot Snapshot => new(
        _reachable switch
        {
            true => TaskServerConnectivityStates.Reachable,
            false => TaskServerConnectivityStates.Unreachable,
            _ => TaskServerConnectivityStates.Unknown,
        },
        _observedAt,
        _failureStartedAt,
        _consecutiveFailures,
        _escalatedAt,
        _lastError,
        _lastRecoveredAt,
        _httpStatus,
        _request);

    /// <summary>Records a successful Task Server operation and logs recovery once.</summary>
    public bool RecordSuccess(DateTime observedAt, string operation)
    {
        var recovered = _reachable == false;
        if (recovered)
        {
            var duration = _failureStartedAt is null
                ? TimeSpan.Zero
                : observedAt - _failureStartedAt.Value;
            _log(
                "task-server-connectivity status=recovered " +
                $"operation={Token(operation)} outageSeconds={Math.Max(0, duration.TotalSeconds):0} " +
                $"failedAttempts={_consecutiveFailures}");
            _lastRecoveredAt = observedAt;
        }

        _reachable = true;
        _observedAt = observedAt;
        _failureStartedAt = null;
        _escalatedAt = null;
        _lastSummaryAt = null;
        _consecutiveFailures = 0;
        _lastError = null;
        _httpStatus = null;
        _request = null;
        return recovered;
    }

    /// <summary>
    /// Records one failed operation. Only the initial transition, the
    /// five-minute escalation, and hourly summaries are logged.
    /// </summary>
    public void RecordFailure(
        DateTime observedAt,
        string operation,
        Exception exception,
        TimeSpan retryAfter,
        int activeSlots)
    {
        var firstFailure = _reachable != false;
        _reachable = false;
        _observedAt = observedAt;
        _failureStartedAt ??= observedAt;
        _consecutiveFailures++;
        _lastError = Bounded(exception.Message, 500);
        _httpStatus = exception switch
        {
            HttpRequestException { StatusCode: { } status } => (int)status,
            TaskServerException server => server.StatusCode,
            _ => null,
        };
        _request = operation switch
        {
            "claim poll" => "POST /api/runner/claim",
            "review claim poll" => "POST /api/v1/runners/{runnerId}/review-claims",
            "capability advertisement" or "review capability advertisement"
                or "capability re-registration" => "PUT /api/v1/runners/{runnerId}/capabilities",
            _ => operation,
        };

        if (firstFailure)
        {
            _lastSummaryAt = observedAt;
            _log(
                "task-server-connectivity status=unreachable " +
                $"operation={Token(operation)} activeSlots={activeSlots} " +
                $"retrySeconds={Math.Max(0, retryAfter.TotalSeconds):0} error={Token(_lastError)}");
            return;
        }

        var outage = observedAt - _failureStartedAt.Value;
        if (_escalatedAt is null && outage >= _escalationAfter)
        {
            _escalatedAt = observedAt;
            _lastSummaryAt = observedAt;
            _log(
                "task-server-connectivity status=escalated " +
                $"outageSeconds={Math.Max(0, outage.TotalSeconds):0} failedAttempts={_consecutiveFailures} " +
                $"activeSlots={activeSlots} boardSignal=stale-capability pollingLogs=suppressed");
            return;
        }

        if (_escalatedAt is not null
            && observedAt - (_lastSummaryAt ?? _escalatedAt.Value) >= _summaryInterval)
        {
            _lastSummaryAt = observedAt;
            _log(
                "task-server-connectivity status=still-unreachable " +
                $"outageSeconds={Math.Max(0, outage.TotalSeconds):0} failedAttempts={_consecutiveFailures} " +
                $"activeSlots={activeSlots} pollingLogs=suppressed");
        }
    }

    public static TimeSpan RetryDelay(int pollSeconds, int consecutiveFailures)
    {
        var seconds = Math.Min(Math.Max(1, pollSeconds) * Math.Min(Math.Max(1, consecutiveFailures), 6), 60);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static string Token(string value)
        => string.Concat(value.Select(character => char.IsWhiteSpace(character) ? '_' : character));

    private static string Bounded(string value, int length)
        => value.Length <= length ? value : value[..length];
}

public static class TaskServerConnectivityStates
{
    public const string Unknown = "unknown";
    public const string Reachable = "reachable";
    public const string Unreachable = "unreachable";
}

public sealed record TaskServerConnectivitySnapshot(
    string Status,
    DateTime? ObservedAt,
    DateTime? FailureStartedAt,
    int ConsecutiveFailures,
    DateTime? EscalatedAt,
    string? LastError,
    DateTime? LastRecoveredAt,
    int? HttpStatus = null,
    string? Request = null);
