namespace AgentStudio.Pipeline;

/// <summary>
/// When a workspace artifact commit that failed on a git lock is tried again
/// (AGT-3409). The commit runs at a card's run boundary; a lock in the
/// workspace repository is a transient condition of that repository, never a
/// reason to fail or escalate the card. The schedule spans about 19 minutes,
/// past the 10-minute stale-lock threshold, so even a lock left by a crashed
/// git outside this server is cleared by the guard before the last round.
/// </summary>
public static class ArtifactCommitLockRetryPolicy
{
    public const int DefaultRounds = 7;

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>
    /// The wait before retry <paramref name="round"/> (1-based), or null when
    /// <paramref name="maxRounds"/> are spent. Rounds past the table repeat its
    /// last delay.
    /// </summary>
    public static TimeSpan? DelayBefore(int round, int maxRounds)
    {
        if (round < 1 || round > maxRounds) return null;
        return Delays[Math.Min(round, Delays.Length) - 1];
    }

    /// <summary>
    /// A failure worth retrying later: git refused because a lock file exists.
    /// A timeout or any other git error is not a lock condition.
    /// </summary>
    public static bool IsLockFailure(WorkspaceArtifactCommitResult result)
        => !result.Success
           && result.Error is { } error
           && WorkspaceArtifactCommitService.IsIndexLockContention(1, error);
}
