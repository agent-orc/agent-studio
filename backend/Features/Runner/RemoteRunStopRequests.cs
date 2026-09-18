using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>What a stop request did for one card.</summary>
public enum RunStopDispatch
{
    /// <summary>No such card.</summary>
    NotFound,

    /// <summary>A local CLI process was signalled, which is the pre-AGT-2870 behaviour.</summary>
    StoppedLocally,

    /// <summary>
    /// The card runs on a remote host. The request is recorded for the attempt
    /// and delivered on the owning runner's next lease renewal.
    /// </summary>
    RemoteStopRequested,

    /// <summary>Nothing is running here and no remote attempt holds the card.</summary>
    NoRunningAttempt,
}

/// <summary>
/// Plain facts the stop decision needs, read once by the endpoint: whether a
/// local process was actually signalled, and whether a remote runner currently
/// holds this card's fenced lease.
/// </summary>
public sealed record RunStopFacts(bool LocalProcessStopped, bool RemoteLeaseHeld);

/// <summary>
/// Pure dispatch decision for <c>POST /api/tasks/{key}/stop</c> (AGT-2870).
///
/// <para>
/// Before this, the endpoint asked the local CLI backend only, so a card
/// executing on a remote host answered 404 and an operator had no way to stop
/// the run other than killing the process on the host by hand. A held remote
/// lease is the positive evidence that a run exists and that its owner can be
/// told to end it on the next heartbeat.
/// </para>
/// </summary>
public static class RemoteRunStopPolicy
{
    public static RunStopDispatch Decide(RunStopFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.LocalProcessStopped) return RunStopDispatch.StoppedLocally;
        return facts.RemoteLeaseHeld
            ? RunStopDispatch.RemoteStopRequested
            : RunStopDispatch.NoRunningAttempt;
    }
}

/// <summary>Wire values of <see cref="RemoteRunStopRequest.Reason"/>.</summary>
public static class RemoteRunStopReasons
{
    /// <summary>The operator pressed Pause.</summary>
    public const string User = "user";

    /// <summary>
    /// Pause and Send: a follow-up is queued and must start as the next round as
    /// soon as the stopped attempt hands back.
    /// </summary>
    public const string Followup = "followup";

    /// <summary>A supervisor watchdog asked for the run to end.</summary>
    public const string Watchdog = "watchdog";

    public static string From(RunStopReason reason) => reason switch
    {
        RunStopReason.FollowupPause => Followup,
        RunStopReason.Watchdog => Watchdog,
        _ => User,
    };

    public static bool IsFollowup(string? reason)
        => string.Equals((reason ?? string.Empty).Trim(), Followup, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One recorded, not yet consumed operator stop for a remote attempt.</summary>
public sealed record RemoteRunStopRequest(
    string TaskKey,
    string Reason,
    DateTime RequestedAtUtc,
    string? AttemptId = null,
    string? RequestedBy = null);

/// <summary>
/// The stop requests waiting for their runner to pick them up. In-memory on
/// purpose: a request is delivered on the next heartbeat (seconds), and a
/// backend restart in that window loses nothing an operator cannot repeat,
/// whereas a durable request could outlive the attempt it was meant for and stop
/// an unrelated later round.
/// </summary>
public sealed class RemoteRunStopRequestStore
{
    private readonly ConcurrentDictionary<string, RemoteRunStopRequest> _requests =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTime> _utcNow;

    public RemoteRunStopRequestStore()
        : this(() => DateTime.UtcNow)
    {
    }

    internal RemoteRunStopRequestStore(Func<DateTime> utcNow) => _utcNow = utcNow;

    public RemoteRunStopRequest Record(
        string taskKey,
        string reason,
        string? attemptId = null,
        string? requestedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        var request = new RemoteRunStopRequest(
            taskKey.Trim(),
            string.IsNullOrWhiteSpace(reason) ? RemoteRunStopReasons.User : reason.Trim(),
            _utcNow(),
            string.IsNullOrWhiteSpace(attemptId) ? null : attemptId.Trim(),
            string.IsNullOrWhiteSpace(requestedBy) ? null : requestedBy.Trim());
        _requests[request.TaskKey] = request;
        return request;
    }

    /// <summary>
    /// The pending request for this card, if any. Peeking does not consume it:
    /// the runner may miss a heartbeat, and the request stays valid until the
    /// attempt it belongs to actually hands back.
    /// </summary>
    public RemoteRunStopRequest? Peek(string? taskKey)
        => string.IsNullOrWhiteSpace(taskKey)
            ? null
            : _requests.TryGetValue(taskKey.Trim(), out var request) ? request : null;

    /// <summary>Drop the request once its attempt released or completed.</summary>
    public RemoteRunStopRequest? Clear(string? taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        return _requests.TryRemove(taskKey.Trim(), out var request) ? request : null;
    }
}
