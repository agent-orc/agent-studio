namespace AgentStudio.Shared;

/// <summary>What a remote Progress-lane run is, from the server's own evidence.</summary>
public enum RemoteRunLiveness
{
    /// <summary>A fenced lease is held and its heartbeat is recent.</summary>
    Running,

    /// <summary>
    /// The lease is still valid but its heartbeat has gone quiet. The link
    /// blipped; the holder is expected back within the lease.
    /// </summary>
    Disconnected,

    /// <summary>
    /// No authority covers this run any more: the heartbeat is older than the
    /// lease it was supposed to renew, or no lease is held at all and no
    /// activity has arrived. Nobody is demonstrably driving the card.
    /// </summary>
    Stale,
}

/// <summary>
/// AGT-2869: the pure read-side verdict that separates a live remote run from a
/// phantom one.
///
/// <para>
/// A runner whose result transfer could not reach a restarting Task Server
/// keeps its worktree, its durable result, and its persisted slot - but it
/// stops heartbeating. The board used to present exactly that card as
/// <c>remote-running</c>, so an operator could not tell it apart from a run
/// that was still working. The distinction the server can always make is
/// whether any authority still covers "now": a heartbeat inside its lease, a
/// lease inside its TTL, or fresh job-folder activity.
/// </para>
/// </summary>
public static class RemoteRunStalenessPolicy
{
    /// <summary>
    /// How long a held lease tolerates heartbeat silence before the connection
    /// is called disconnected. Shorter than any lease TTL, so a blip is visible
    /// long before authority actually lapses.
    /// </summary>
    public static readonly TimeSpan HeartbeatGrace = TimeSpan.FromSeconds(75);

    /// <summary>
    /// How long an ownerless remote run may replay from the job folder before
    /// its silence counts as stale rather than as reconnecting.
    /// </summary>
    public static readonly TimeSpan ActivityGrace = TimeSpan.FromMinutes(3);

    /// <summary>Verdict for a remote run that still holds a server-side lease record.</summary>
    /// <param name="leaseState">
    /// <c>active</c> while the lease is inside its TTL; <c>expired</c> or
    /// <c>released</c> once it is not.
    /// </param>
    public static RemoteRunLiveness ForLeasedRun(
        string leaseState,
        DateTime lastHeartbeatUtc,
        DateTime nowUtc)
    {
        // An expired lease means the heartbeat stopped before the lease it was
        // renewing ran out. That is the phantom: the holder is gone, or is
        // holding a result it cannot deliver.
        if (!string.Equals(leaseState, "active", StringComparison.Ordinal))
            return RemoteRunLiveness.Stale;
        return nowUtc - lastHeartbeatUtc > HeartbeatGrace
            ? RemoteRunLiveness.Disconnected
            : RemoteRunLiveness.Running;
    }

    /// <summary>
    /// Verdict for a remote-routed run with no lease record at all - the state a
    /// Task Server restart leaves behind. Fresh job-folder or client activity
    /// still proves a runner is pushing; silence does not.
    /// </summary>
    public static RemoteRunLiveness ForOwnerlessRun(DateTime? lastActivityUtc, DateTime nowUtc)
        => lastActivityUtc.HasValue && nowUtc - lastActivityUtc.Value <= ActivityGrace
            ? RemoteRunLiveness.Running
            : RemoteRunLiveness.Stale;

    /// <summary>
    /// The last runner event the server can honestly name, worded so an
    /// operator reading the card knows what is being waited on. Returns null
    /// when the run is live and needs no explanation.
    /// </summary>
    public static string? DescribeLastRunnerEvent(
        RemoteRunLiveness liveness,
        DateTime? lastHeartbeatUtc,
        DateTime? lastActivityUtc) => liveness switch
        {
            RemoteRunLiveness.Running => null,
            RemoteRunLiveness.Disconnected => lastHeartbeatUtc is { } beat
                ? $"Last runner heartbeat {beat:u}; the fenced lease is still valid."
                : "The fenced lease is still valid, but no runner heartbeat has been recorded.",
            _ => Latest(lastHeartbeatUtc, lastActivityUtc) is { } seen
                ? $"Last runner event {seen:u}; no fenced authority is driving this run."
                : "No runner event has been recorded; no fenced authority is driving this run.",
        };

    private static DateTime? Latest(DateTime? first, DateTime? second)
    {
        if (!first.HasValue) return second;
        if (!second.HasValue) return first;
        return first.Value >= second.Value ? first : second;
    }
}
