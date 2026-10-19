namespace AgentRunner.RunTargets;

/// <summary>
/// Pure restart decisions for an app whose container is not running. Docker's
/// own <c>on-failure:3</c> covers short blips; after that the host restarts
/// with exponential backoff, reports a crash loop from the fifth attempt, and
/// forgets the attempts once the app has stayed up for <see cref="StableAfter"/>.
/// </summary>
public static class RunTargetCrashPolicy
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);
    public const int CrashLoopThreshold = 5;

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
}
