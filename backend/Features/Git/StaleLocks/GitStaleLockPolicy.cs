namespace AgentStudio.Git;

/// <summary>
/// Whether a running git process holds a lock file. <see cref="Unknown"/> means
/// the process inventory could not be read, which is never proof of absence.
/// </summary>
public enum GitLockOwnership
{
    None,
    Owned,
    Unknown,
}

/// <summary>What the Task Server does with one observed git lock file.</summary>
public enum GitLockVerdict
{
    /// <summary>Old enough and no git process holds it: remove it and continue.</summary>
    Clear,
    /// <summary>Younger than the threshold: a live writer is plausible, so wait.</summary>
    KeepYoung,
    /// <summary>A running git process works in this repository: wait.</summary>
    KeepOwned,
    /// <summary>The process inventory failed: wait, never guess.</summary>
    KeepOwnerUnknown,
}

/// <summary>
/// Pure decision for one git lock file (<c>index.lock</c>, <c>HEAD.lock</c>,
/// <c>packed-refs.lock</c>, <c>refs/**/*.lock</c>) the Task Server finds before
/// a git write on a repository it owns (AGT-3000).
///
/// <para>The 2026-09-27 incident: a git process died and left a 0-byte
/// <c>index.lock</c> in the workspace repository. Every evidence flush then
/// failed on "Unable to create '.../index.lock': File exists" for 36 hours,
/// because the only recovery was a sub-second retry that waits for a live
/// writer to finish. A lock nobody holds never goes away on its own.</para>
/// </summary>
public static class GitStaleLockPolicy
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Slack on both sides of a killed command's lifetime and on the
    /// "started after the lock was written" exclusion. File times come from a
    /// coarse kernel clock that can lag the wall clock by a few milliseconds, so
    /// an exact comparison could miss the killed command's own lock or rule out
    /// a live git that wrote its lock right after it started.
    /// </summary>
    public static readonly TimeSpan KilledCommandClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Age is checked first so a young lock never needs a process inventory. A
    /// negative age (lock stamped in the future, clock skew) counts as young.
    /// A lock that a git command killed by this server left behind
    /// (<paramref name="leftByKilledCommand"/>, AGT-3409) is never young: its
    /// writer is known to be gone, so only the ownership probe decides.
    /// </summary>
    public static GitLockVerdict Decide(
        TimeSpan age, TimeSpan threshold, GitLockOwnership ownership, bool leftByKilledCommand = false)
    {
        if (age < threshold && !leftByKilledCommand) return GitLockVerdict.KeepYoung;
        return ownership switch
        {
            GitLockOwnership.None => GitLockVerdict.Clear,
            GitLockOwnership.Owned => GitLockVerdict.KeepOwned,
            _ => GitLockVerdict.KeepOwnerUnknown,
        };
    }

    /// <summary>Only an aged lock, or one a killed command left, is worth a process inventory.</summary>
    public static bool NeedsOwnershipProbe(TimeSpan age, TimeSpan threshold, bool leftByKilledCommand = false)
        => leftByKilledCommand || age >= threshold;

    /// <summary>
    /// Whether a lock may belong to a git command that ran from
    /// <paramref name="commandStartedUtc"/> until it was killed at
    /// <paramref name="commandEndedUtc"/>: it was last written inside that
    /// window (widened by <see cref="KilledCommandClockSlack"/>). A lock written
    /// before the command started belongs to someone else and stays with the
    /// ordinary age rule; one written after the kill is a new writer's.
    /// </summary>
    public static bool WrittenDuringCommand(DateTime lockLastWriteTimeUtc, DateTime commandStartedUtc, DateTime commandEndedUtc)
        => lockLastWriteTimeUtc >= commandStartedUtc - KilledCommandClockSlack
           && lockLastWriteTimeUtc <= commandEndedUtc + KilledCommandClockSlack;

    /// <summary>Stable log token for a verdict.</summary>
    public static string Reason(GitLockVerdict verdict) => verdict switch
    {
        GitLockVerdict.Clear => "stale",
        GitLockVerdict.KeepYoung => "young",
        GitLockVerdict.KeepOwned => "owned",
        _ => "owner-unknown",
    };

    /// <summary>Compact, space-free age for structured logs: <c>36h43m</c>, <c>12m</c>, <c>40s</c>.</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return $"{(int)age.TotalSeconds}s";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalHours}h{age.Minutes}m";
    }
}
