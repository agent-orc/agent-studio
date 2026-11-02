namespace AgentStudio.Pipeline;

/// <summary>
/// Time limit for a <c>git add</c> (and the commit after it) in the workspace
/// repository, from the bytes git has to hash (AGT-3409).
///
/// <para>A fixed 30 seconds killed an evidence flush on 2026-11-01, and the
/// killed add left <c>index.lock</c> behind. Measured on a copy of the
/// workspace repository (snapshot 2026-10-15, Git 2.43): 76,326 files and
/// 4.7 GiB under <c>projects/</c>, single files up to 41.6 MB
/// (<c>prompts.jsonl</c>) and many 12-20 MB review settlement logs. A no-op
/// add over 13,042 files took 0.04 s; adding 145 MB took 6.0 s and 323 MB
/// took 12.9 s on a host with load 30-50, about 25 MB/s. The cost follows the
/// bytes to hash, not the file count.</para>
///
/// <para>The limit is the base plus the bytes at a floor rate (default 2 MiB/s,
/// a twelfth of the measured rate), capped. A 41.6 MB file gets 50 seconds, a
/// 1 GiB catch-up about 9 minutes.</para>
/// </summary>
public static class WorkspaceAddTimeoutPolicy
{
    public static readonly TimeSpan DefaultBase = TimeSpan.FromSeconds(30);
    public const long DefaultBytesPerSecond = 2L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximum = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The limit for adding <paramref name="bytesToAdd"/> bytes. Unknown size
    /// (null) gets the maximum: a listing that could not be read says nothing
    /// about how long the add takes.
    /// </summary>
    public static TimeSpan For(long? bytesToAdd, TimeSpan baseTimeout, long bytesPerSecond, TimeSpan maximum)
    {
        if (maximum < baseTimeout) maximum = baseTimeout;
        if (bytesToAdd is null) return maximum;
        var rate = Math.Max(1, bytesPerSecond);
        var seconds = baseTimeout.TotalSeconds + Math.Max(0, bytesToAdd.Value) / (double)rate;
        return seconds >= maximum.TotalSeconds ? maximum : TimeSpan.FromSeconds(seconds);
    }
}
