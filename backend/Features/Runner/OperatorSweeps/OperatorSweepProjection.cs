namespace AgentStudio.Runner;

/// <summary>Why one sweep did or did not act on one card in the last tick.</summary>
public sealed record OperatorSweepCardDecision(
    string Sweep,
    string Action,
    string Reason,
    string? Detail,
    string? SubjectKey);

/// <summary>One card in a decision lane, its shared round budget, and every sweep's verdict on it.</summary>
public sealed record OperatorSweepCardState(
    string TaskKey,
    string JobId,
    string Title,
    string Lane,
    int RoundsUsed,
    int RoundsAllowed,
    IReadOnlyList<OperatorSweepCardDecision> Decisions)
{
    public int RoundsRemaining => Math.Max(0, RoundsAllowed - RoundsUsed);
}

public sealed record OperatorSweepRecentAction(
    DateTime AtUtc,
    string TaskKey,
    string Reason,
    string Detail);

/// <summary>One sweep's run state and pause state for one project.</summary>
public sealed record OperatorSweepStatus(
    string Sweep,
    bool Paused,
    DateTime? PausedAtUtc,
    string? PausedBy,
    string? PauseReason,
    DateTime? LastRunStartedAtUtc,
    DateTime? LastRunFinishedAtUtc,
    string? LastRunError,
    bool IsOverdue,
    int LastActed,
    int LastHeld,
    int LastWaitingForPerson,
    IReadOnlyList<OperatorSweepRecentAction> RecentActions);

public sealed record OperatorSweepWaitingCard(
    string TaskKey,
    string Title,
    string Lane,
    string Sweep,
    string Reason,
    string Detail);

/// <summary>
/// Response of <c>GET /api/projects/{project}/operator-sweeps</c>, shown next to
/// the pipeline health alarm.
/// </summary>
/// <param name="Status"><c>healthy</c>, <c>paused</c>, <c>disabled</c>, or <c>alarm</c> (a sweep failed or is overdue).</param>
public sealed record OperatorSweepProjection(
    string Project,
    DateTime CapturedAtUtc,
    string Status,
    bool Enabled,
    int TickIntervalSeconds,
    int MaxRoundsPerCard,
    DateTime? LastTickAtUtc,
    IReadOnlyList<OperatorSweepStatus> Sweeps,
    IReadOnlyList<OperatorSweepCardState> Cards,
    IReadOnlyList<OperatorSweepWaitingCard> WaitingForPerson);

/// <summary>Pure health rules of the projection.</summary>
public static class OperatorSweepHealthPolicy
{
    public const string Healthy = "healthy";
    public const string Paused = "paused";
    public const string Disabled = "disabled";
    public const string Alarm = "alarm";

    /// <summary>
    /// A sweep is overdue when it has not finished a tick for two intervals
    /// (plus the initial delay after a start). This is the signal the 41-hour
    /// standstill of 2026-09-29/30 lacked.
    /// </summary>
    public static bool IsOverdue(
        DateTime? lastFinishedAtUtc,
        DateTime serviceStartedAtUtc,
        OperatorSweepOptions options,
        DateTime nowUtc)
    {
        var grace = options.TickInterval * 2;
        var since = lastFinishedAtUtc ?? serviceStartedAtUtc + options.InitialDelay;
        return nowUtc - since > grace;
    }

    public static string Status(bool enabled, IReadOnlyList<OperatorSweepStatus> sweeps)
    {
        if (!enabled) return Disabled;
        if (sweeps.Any(sweep => sweep.LastRunError is not null || sweep.IsOverdue)) return Alarm;
        return sweeps.Count > 0 && sweeps.All(sweep => sweep.Paused) ? Paused : Healthy;
    }
}
