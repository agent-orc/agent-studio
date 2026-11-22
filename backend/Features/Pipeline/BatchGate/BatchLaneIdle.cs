namespace AgentStudio.Pipeline;

/// <summary>AGT-3857: why a formed batch waits while its project runs no batch gate.</summary>
public static class BatchLaneIdleCauses
{
    /// <summary>A per-task gate, a direct integration or its pending push holds the lane.</summary>
    public const string PerTaskHolder = "per-task-holder";

    /// <summary>The server restarted, or a previous process's flight claim or batch is still being recovered.</summary>
    public const string RestartRecovery = "restart-recovery";

    /// <summary>Nothing holds the lane; the planner has not started the batch yet.</summary>
    public const string Planner = "planner";

    public static readonly IReadOnlyList<string> All = [PerTaskHolder, RestartRecovery, Planner];
}

/// <summary>
/// What one pilot observation knows about a project's batch lane.
/// <paramref name="BatchGateRunning"/> is a live flight claim past its
/// reservation step, or a running stacked successor.
/// <paramref name="QueuedForGateSlot"/> is that flight's gate waiting for a
/// machine slot. <paramref name="PreviousProcessClaim"/> is a flight claim of
/// another process that is no longer live, or whose expiry is not settled yet.
/// </summary>
public sealed record BatchLaneIdleFacts(
    bool FormedBatchWaiting,
    bool BatchGateRunning,
    bool QueuedForGateSlot,
    bool ReservationWait,
    bool PreviousProcessClaim,
    bool RecoveryPending,
    bool PushPending,
    int PerTaskGatesRunning,
    string? PlannerHold = null);

public sealed record BatchLaneIdleCause(string Cause, string Detail);

/// <summary>Classifies one observation of a project's batch lane.</summary>
public static class BatchLaneIdlePolicy
{
    /// <summary>Null while no formed batch waits or a batch gate runs.</summary>
    public static BatchLaneIdleCause? Classify(BatchLaneIdleFacts facts)
    {
        if (!facts.FormedBatchWaiting) return null;
        if (facts.BatchGateRunning && !facts.QueuedForGateSlot) return null;
        if (facts.PreviousProcessClaim)
            return new(BatchLaneIdleCauses.RestartRecovery,
                "the flight claim of a previous process awaits its expiry");
        if (facts.RecoveryPending)
            return new(BatchLaneIdleCauses.RestartRecovery,
                "restart recovery has not finished for an interrupted batch");
        if (facts.QueuedForGateSlot)
            return facts.PerTaskGatesRunning > 0
                ? new(BatchLaneIdleCauses.PerTaskHolder,
                    $"the batch gate waits for a gate slot while {Gates(facts.PerTaskGatesRunning)} run")
                : null;
        if (facts.ReservationWait)
            return new(BatchLaneIdleCauses.PerTaskHolder, facts.PushPending
                ? "the batch waits for an approved per-task push to reach origin"
                : "a direct per-task integration holds the project reservation");
        if (facts.PerTaskGatesRunning > 0)
            return new(BatchLaneIdleCauses.PerTaskHolder, $"{Gates(facts.PerTaskGatesRunning)} run");
        return new(BatchLaneIdleCauses.Planner,
            facts.PlannerHold ?? "the planner has not started the formed batch yet");
    }

    private static string Gates(int count) => count == 1 ? "1 per-task gate" : $"{count} per-task gates";
}

/// <summary>
/// One interval in which a formed batch waited with no batch gate running.
/// <see cref="Observer"/> is the flight claim owner of the process that saw it;
/// <see cref="EndedAtUtc"/> is null while the interval is open.
/// </summary>
public sealed record BatchLaneIdleInterval(
    string Project, string Cause, string Detail, string? BatchId,
    DateTimeOffset StartedAtUtc, DateTimeOffset LastObservedAtUtc, DateTimeOffset? EndedAtUtc,
    string Observer)
{
    public double Minutes(DateTimeOffset nowUtc)
        => Math.Max(0, ((EndedAtUtc ?? nowUtc) - StartedAtUtc).TotalMinutes);
}

public sealed record BatchLaneIdleCurrent(string Cause, string Detail, string? BatchId,
    DateTimeOffset SinceUtc, double Minutes);

public sealed record BatchLaneIdleTotals(double PerTaskHolderMinutes, double RestartRecoveryMinutes,
    double PlannerMinutes)
{
    public double TotalMinutes => PerTaskHolderMinutes + RestartRecoveryMinutes + PlannerMinutes;
}

public sealed record BatchLaneIdleEntry(string Cause, string Detail, string? BatchId,
    DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, double Minutes);

/// <summary>The lane idle measure of the batch gate state response.</summary>
public sealed record BatchLaneIdleSummary(BatchLaneIdleCurrent? Current,
    BatchLaneIdleTotals Last24Hours, BatchLaneIdleTotals Last7Days,
    IReadOnlyList<BatchLaneIdleEntry> Intervals)
{
    public const int IntervalLimit = 20;

    /// <summary>
    /// Summarises the closed intervals and the open one. An open interval a
    /// previous process wrote means the server restarted while a formed batch
    /// waited: it reads as restart recovery since its last observation.
    /// </summary>
    public static BatchLaneIdleSummary From(IReadOnlyList<BatchLaneIdleInterval> closed,
        BatchLaneIdleInterval? open, string observer, DateTimeOffset nowUtc)
    {
        var all = closed.ToList();
        BatchLaneIdleCurrent? current = null;
        if (open is not null)
        {
            if (open.Observer == observer)
            {
                current = new(open.Cause, open.Detail, open.BatchId, open.StartedAtUtc, open.Minutes(nowUtc));
                all.Add(open with { EndedAtUtc = null });
            }
            else
            {
                all.Add(open with { EndedAtUtc = open.LastObservedAtUtc });
                var restart = new BatchLaneIdleInterval(open.Project, BatchLaneIdleCauses.RestartRecovery,
                    BatchGateLaneIdleRestart.Detail, open.BatchId, open.LastObservedAtUtc,
                    nowUtc, null, observer);
                current = new(restart.Cause, restart.Detail, restart.BatchId, restart.StartedAtUtc,
                    restart.Minutes(nowUtc));
                all.Add(restart);
            }
        }
        return new(current, Totals(all, nowUtc - TimeSpan.FromHours(24), nowUtc),
            Totals(all, nowUtc - TimeSpan.FromDays(7), nowUtc),
            all.OrderByDescending(item => item.StartedAtUtc).Take(IntervalLimit)
                .Select(item => new BatchLaneIdleEntry(item.Cause, item.Detail, item.BatchId,
                    item.StartedAtUtc, item.EndedAtUtc, item.Minutes(nowUtc))).ToArray());
    }

    private static BatchLaneIdleTotals Totals(IEnumerable<BatchLaneIdleInterval> intervals,
        DateTimeOffset fromUtc, DateTimeOffset nowUtc)
    {
        double Sum(string cause) => intervals.Where(item => item.Cause == cause).Sum(item =>
        {
            var start = item.StartedAtUtc < fromUtc ? fromUtc : item.StartedAtUtc;
            var end = item.EndedAtUtc ?? nowUtc;
            return end > start ? (end - start).TotalMinutes : 0;
        });
        return new(Sum(BatchLaneIdleCauses.PerTaskHolder), Sum(BatchLaneIdleCauses.RestartRecovery),
            Sum(BatchLaneIdleCauses.Planner));
    }
}

internal static class BatchGateLaneIdleRestart
{
    public const string Detail = "the server restarted while a formed batch waited";
}
