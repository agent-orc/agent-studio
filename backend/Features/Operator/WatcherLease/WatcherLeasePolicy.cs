namespace AgentStudio.Operator;

/// <summary>
/// The one watcher lease of an installation (OP-2, guardrail "One watcher at a
/// time"). <see cref="Actor"/> is the principal that holds it and
/// <see cref="Session"/> the label the watching client chose.
/// </summary>
public sealed record WatcherLease(
    string LeaseId,
    string Actor,
    string? Session,
    DateTime GrantedAtUtc,
    DateTime LastHeartbeatUtc,
    string? TakenOverFrom = null);

public enum WatcherLeaseOutcome { Granted, TakenOver, Conflict }

public sealed record WatcherLeaseDecision(WatcherLeaseOutcome Outcome, WatcherLease? Current);

public enum WatcherHeartbeatOutcome { Renewed, Lost }

/// <summary>
/// Pure lease decisions. A lease is active until its holder misses
/// heartbeats for the lease timeout. While it is active a request without the
/// explicit take-over flag conflicts; with the flag it replaces the holder.
/// Only the holder renews or releases its own lease, and it may renew an
/// expired lease nobody else has taken.
/// </summary>
public static class WatcherLeasePolicy
{
    public static bool IsActive(WatcherLease? lease, DateTime nowUtc, TimeSpan timeout)
        => lease is not null && nowUtc - lease.LastHeartbeatUtc < timeout;

    public static DateTime ExpiresAt(WatcherLease lease, TimeSpan timeout) => lease.LastHeartbeatUtc + timeout;

    public static WatcherLeaseDecision Request(WatcherLease? current, bool takeOver, DateTime nowUtc, TimeSpan timeout)
    {
        if (!IsActive(current, nowUtc, timeout)) return new(WatcherLeaseOutcome.Granted, current);
        return takeOver ? new(WatcherLeaseOutcome.TakenOver, current) : new(WatcherLeaseOutcome.Conflict, current);
    }

    public static WatcherHeartbeatOutcome Heartbeat(WatcherLease? current, string leaseId, string actor)
        => IsHolder(current, leaseId, actor) ? WatcherHeartbeatOutcome.Renewed : WatcherHeartbeatOutcome.Lost;

    public static bool IsHolder(WatcherLease? current, string leaseId, string actor)
        => current is not null
            && string.Equals(current.LeaseId, leaseId, StringComparison.Ordinal)
            && string.Equals(current.Actor, actor, StringComparison.Ordinal);
}
