namespace AgentRunner;

public enum ReviewReportSubmissionAction
{
    Retry,
    TerminalTaskMissing,
    TerminalSuperseded,
    TerminalRejected,
}

/// <summary>
/// Pure classification for a failed terminal review-report delivery. Transport
/// and ordinary 5xx failures are retried. Missing task authority and fenced 4xx
/// replies are terminal because replay cannot make the same identity current.
/// </summary>
public static class ReviewReportSubmissionPolicy
{
    public static ReviewReportSubmissionAction Decide(
        int? statusCode,
        string? errorCode,
        bool transportFailure)
    {
        if (string.Equals(errorCode, "task-not-found", StringComparison.OrdinalIgnoreCase)
            || statusCode == 404)
        {
            return ReviewReportSubmissionAction.TerminalTaskMissing;
        }

        if (string.Equals(errorCode, "Superseded", StringComparison.OrdinalIgnoreCase))
            return ReviewReportSubmissionAction.TerminalSuperseded;

        if (transportFailure
            || statusCode is null
            || statusCode is 408 or 429
            || statusCode >= 500)
            return ReviewReportSubmissionAction.Retry;

        return ReviewReportSubmissionAction.TerminalRejected;
    }

    public static string TerminalClassification(ReviewReportSubmissionAction action) => action switch
    {
        ReviewReportSubmissionAction.TerminalTaskMissing => "TaskNotFound",
        ReviewReportSubmissionAction.TerminalSuperseded => "Superseded",
        _ => "ReportRejected",
    };

    /// <summary>Base delay for the first transport-failure retry; doubles per consecutive failure.</summary>
    public static readonly TimeSpan TransportRetryBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Cap for the transport-failure doubling.</summary>
    public static readonly TimeSpan TransportRetryMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Delay before the next report submission attempt. Transport failures
    /// (timeout, connection reset - the class of fault a tunnel outage or a
    /// momentarily overloaded host produces) back off exponentially so a
    /// recovering server is not immediately re-hammered; other retryable
    /// failures (5xx, 429, 408) keep the existing poll-interval-scaled delay,
    /// since those already came with an authoritative response and do not
    /// need the same caution.
    /// </summary>
    public static TimeSpan RetryDelay(int consecutiveFailures, bool transportFailure, int pollSeconds)
    {
        if (!transportFailure)
            return TaskServerConnectivityMonitor.RetryDelay(pollSeconds, consecutiveFailures);

        var factor = Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        var seconds = Math.Min(TransportRetryMaxDelay.TotalSeconds, TransportRetryBaseDelay.TotalSeconds * factor);
        return TimeSpan.FromSeconds(seconds);
    }
}
