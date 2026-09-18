using System.Net;
using System.Net.Http;

namespace AgentRunner;

/// <summary>What the daemon has to do with a persisted slot nobody is driving.</summary>
internal enum FinalizationRetryAction
{
    /// <summary>Leave the slot alone: another authority owns its outcome.</summary>
    Skip,

    /// <summary>Re-drive the finalization from the durable facts of the same attempt.</summary>
    Redrive,

    /// <summary>The process generation is provably gone; retry the honest release.</summary>
    ReleaseDead,
}

/// <summary>
/// AGT-2869 - the pure decision behind in-place finalization retry.
///
/// <para>
/// A worker's result lives on disk. When result transfer, completion recording
/// or hand-back fails because the Task Server is restarting, the slot stays
/// persisted in <c>finalizing</c> and the daemon must re-drive it on its poll
/// loop instead of waiting for the next daemon restart: startup reconciliation
/// proves the durable state is complete and sufficient, and this policy decides
/// when the running daemon may use the very same steps.
/// </para>
///
/// <para>
/// Phases that name a dead or transferred authority are never re-driven. A
/// handed-off slot belongs to the replacement daemon, and a lease-loss terminal
/// belongs to whoever took the lease over - re-driving either would publish a
/// second delivery candidate for one card.
/// </para>
/// </summary>
internal static class FinalizationRetryPolicy
{
    /// <summary>Slot phase persisted once the worker's terminal result exists.</summary>
    public const string FinalizingPhase = "finalizing";

    /// <summary>Phases whose authority belongs to another generation or holder.</summary>
    private static readonly string[] ForeignAuthorityPhases =
    [
        "handed-off",
        "authority-deadline-exhausted",
        "lease-authority-rejected",
    ];

    /// <summary>
    /// The bounded retry ladder: 15 s, 30 s, then every 60 s. The caller stops
    /// re-driving when the run timeout has elapsed, so the ladder itself never
    /// needs a terminal step.
    /// </summary>
    public static TimeSpan Delay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Decide what a persisted slot that no live execution owns needs. The
    /// observation arguments are exactly the two facts startup reconciliation
    /// uses, so an in-loop retry and a daemon restart reach the same verdict.
    /// </summary>
    public static FinalizationRetryAction Decide(
        string phase,
        bool hasDurableResult,
        bool isLive)
    {
        if (ForeignAuthorityPhases.Contains(phase, StringComparer.Ordinal))
            return FinalizationRetryAction.Skip;
        if (hasDurableResult || isLive) return FinalizationRetryAction.Redrive;
        return FinalizationRetryAction.ReleaseDead;
    }

    /// <summary>
    /// "The Task Server is restarting or momentarily unwell", not "this runner
    /// is wrong": transport faults (connection refused, reset, a response that
    /// ended prematurely), request timeouts, and the gateway/unavailable replies
    /// a restarting server emits. A definitive 4xx - a real protocol or
    /// authority answer - is never a retryable transfer fault.
    /// </summary>
    public static bool IsRetryableTransportFault(Exception exception) => exception switch
    {
        HttpRequestException => true,
        HttpIOException => true,
        IOException io when io.InnerException is not null => IsRetryableTransportFault(io.InnerException),
        TimeoutException => true,
        TaskCanceledException => true,
        TaskServerException server => IsRetryableStatus(server.StatusCode),
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsRetryableTransportFault),
        _ => exception.InnerException is not null
             && IsRetryableTransportFault(exception.InnerException),
    };

    /// <summary>
    /// A restarting server answers 502/503/504 through its proxy and may reject
    /// a lease re-claim with 409 while its authority is still loading. Both are
    /// "come back in a moment", so the persisted slot is retained and retried.
    /// </summary>
    private static bool IsRetryableStatus(int statusCode) => statusCode switch
    {
        (int)HttpStatusCode.BadGateway => true,
        (int)HttpStatusCode.ServiceUnavailable => true,
        (int)HttpStatusCode.GatewayTimeout => true,
        (int)HttpStatusCode.RequestTimeout => true,
        (int)HttpStatusCode.Conflict => true,
        >= 500 => true,
        _ => false,
    };
}
