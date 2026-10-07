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
    /// Age is checked first so a young lock never needs a process inventory. A
    /// negative age (lock stamped in the future, clock skew) counts as young.
    /// </summary>
    public static GitLockVerdict Decide(TimeSpan age, TimeSpan threshold, GitLockOwnership ownership)
    {
        if (age < threshold) return GitLockVerdict.KeepYoung;
        return ownership switch
        {
            GitLockOwnership.None => GitLockVerdict.Clear,
            GitLockOwnership.Owned => GitLockVerdict.KeepOwned,
            _ => GitLockVerdict.KeepOwnerUnknown,
        };
    }

    /// <summary>Only an aged lock is worth a process inventory.</summary>
    public static bool NeedsOwnershipProbe(TimeSpan age, TimeSpan threshold) => age >= threshold;

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
