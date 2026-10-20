namespace AgentRunner.RunTargets;

/// <summary>
/// Pure restart decisions for an app whose container is not running. Docker's
/// own <c>on-failure:3</c> covers short blips; after that the host restarts
/// with exponential backoff, reports a crash loop from the fifth attempt, and
/// forgets the attempts once the app has stayed up for <see cref="StableAfter"/>.
/// A revision that restarts <see cref="RollbackRestarts"/> times (Docker's and
/// the host's restarts together) within <see cref="RollbackWindow"/> is rolled
/// back to the kept previous revision.
/// </summary>
public static class RunTargetCrashPolicy
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);
    public const int CrashLoopThreshold = 5;
    public const int RollbackRestarts = 5;
    public static readonly TimeSpan RollbackWindow = TimeSpan.FromMinutes(10);

    /// <summary>Delay before the next restart after <paramref name="attempts"/> restarts.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        if (attempts <= 0) return TimeSpan.Zero;
        var exponent = Math.Min(attempts - 1, 16);
        var delay = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << exponent));
        return delay > MaxDelay ? MaxDelay : delay;
    }

    public static bool RestartDue(DateTime now, DateTime? nextRestartAt) => nextRestartAt is null || now >= nextRestartAt;

    public static bool IsCrashLoop(int attempts) => attempts >= CrashLoopThreshold;

    public static bool IsStable(DateTime now, DateTime? lastRestartAt)
        => lastRestartAt is null || now - lastRestartAt >= StableAfter;

    /// <summary>Restarts that still count toward a rollback.</summary>
    public static IEnumerable<DateTime> InWindow(IEnumerable<DateTime> restarts, DateTime now)
        => restarts.Where(at => now - at < RollbackWindow);

    public static bool ShouldRollBack(IEnumerable<DateTime> restarts, DateTime now)
        => InWindow(restarts, now).Count() >= RollbackRestarts;
}
