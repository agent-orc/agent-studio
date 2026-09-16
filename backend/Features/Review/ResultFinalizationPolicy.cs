namespace AgentStudio.Review;

/// <summary>
/// Whether the Result summary may be attempted while a remote runner is still
/// waiting for the acknowledgement of its delivered result.
/// </summary>
public enum ResultSummaryAdmission
{
    /// <summary>The upload did not ask for Result finalization at all.</summary>
    NotRequested,
    /// <summary>Attempt the summary inside the bounded acknowledgement budget.</summary>
    Attempt,
    /// <summary>Hand the summary straight to the retry queue and acknowledge now.</summary>
    Defer,
}

/// <summary>How the Result summary stands at the moment the result is acknowledged.</summary>
public enum ResultSummaryDelivery
{
    /// <summary>No finalization was requested; the upload carried artifacts only.</summary>
    NotRequested,
    /// <summary>A real <c>status.md</c> exists and travels with the acknowledgement.</summary>
    Generated,
    /// <summary>No summary yet, but one is queued for a later retry.</summary>
    Pending,
    /// <summary>The bounded summary budget is spent; a later retry is still queued.</summary>
    Degraded,
}

/// <summary>Closed set of reasons a Result summary is pending rather than generated.</summary>
public static class ResultSummaryPendingReasons
{
    /// <summary>Sustained host CPU saturation refuses the summary one-shot.</summary>
    public const string LoadThrottle = "load-throttle";
    /// <summary>The summary is still running past the acknowledgement budget.</summary>
    public const string GenerationInFlight = "generation-in-flight";
}

/// <summary>
/// What the acknowledgement carries for one delivered remote result:
/// whether <c>status.md</c> joins the workspace evidence commit, the wire
/// status the runner logs, the card-timeline line, and whether a later summary
/// retry is owed.
/// </summary>
public sealed record ResultAcknowledgementPlan(
    ResultSummaryDelivery Delivery,
    bool CommitStatusDocument,
    string? ResultDocumentStatus,
    string? TimelineSummary,
    bool ScheduleRetry)
{
    public bool Generated => Delivery == ResultSummaryDelivery.Generated;

    /// <summary>Artifacts-only upload: nothing to finalize, nothing to retry.</summary>
    public static readonly ResultAcknowledgementPlan NotRequested = new(
        ResultSummaryDelivery.NotRequested,
        CommitStatusDocument: false,
        ResultDocumentStatus: null,
        TimelineSummary: null,
        ScheduleRetry: false);
}

/// <summary>
/// AGT-2850. Pure decision behind remote Result finalization: the summary is a
/// convenience on top of a delivered result, never a precondition for
/// acknowledging it. A saturated host (or any other summary-side failure) must
/// leave the card with a pending summary and a queued retry, not with a lost
/// run the lease authority then requeues.
/// </summary>
public static class ResultFinalizationPolicy
{
    /// <summary>Longest reason text carried onto the wire and the card timeline.</summary>
    public const int MaxReasonChars = 300;

    private const string ExhaustedBudget = "summary retry budget exhausted";

    /// <summary>
    /// Decide whether the acknowledgement path may spend time on the summary.
    /// A throttled host is already known to queue the summary one-shot behind
    /// sustained CPU saturation, so waiting for it only risks the runner's
    /// delivery timeout.
    /// </summary>
    public static ResultSummaryAdmission Admit(bool finalizeRequested, bool hostThrottled)
    {
        if (!finalizeRequested) return ResultSummaryAdmission.NotRequested;
        return hostThrottled ? ResultSummaryAdmission.Defer : ResultSummaryAdmission.Attempt;
    }

    /// <summary>
    /// Map an observed summary state onto the acknowledgement the runner
    /// receives. Every branch acknowledges; only the summary side varies.
    /// </summary>
    public static ResultAcknowledgementPlan Plan(ResultSummaryDelivery delivery, string? reason)
        => delivery switch
        {
            ResultSummaryDelivery.NotRequested => ResultAcknowledgementPlan.NotRequested,
            ResultSummaryDelivery.Generated => new(
                delivery,
                CommitStatusDocument: true,
                ResultDocumentStatus: "generated",
                TimelineSummary: null,
                ScheduleRetry: false),
            ResultSummaryDelivery.Pending => Pending(
                Limit(reason) ?? ResultSummaryPendingReasons.GenerationInFlight),
            _ => Degraded(Limit(reason) ?? ExhaustedBudget),
        };

    /// <summary>Trim and bound a free-form failure reason for wire + timeline use.</summary>
    public static string? Limit(string? reason)
    {
        var value = reason?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= MaxReasonChars ? value : value[..MaxReasonChars];
    }

    private static ResultAcknowledgementPlan Pending(string reason) => new(
        ResultSummaryDelivery.Pending,
        CommitStatusDocument: false,
        ResultDocumentStatus: "pending:" + reason,
        TimelineSummary: $"Summary pending ({Humanize(reason)})",
        ScheduleRetry: true);

    private static ResultAcknowledgementPlan Degraded(string error) => new(
        ResultSummaryDelivery.Degraded,
        CommitStatusDocument: false,
        ResultDocumentStatus: "degraded:" + error,
        TimelineSummary: $"Summary pending (degraded: {error})",
        ScheduleRetry: true);

    private static string Humanize(string reason) => reason.Replace('-', ' ');
}
