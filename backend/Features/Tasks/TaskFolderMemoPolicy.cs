namespace AgentStudio.Tasks;

/// <summary>
/// Decides whether a just-parsed task folder may be memoized by
/// <see cref="TaskScannerService"/>.
///
/// <para>Both folder memos identify a version of <c>task.json</c> by its length
/// plus its last-write time. A kernel stamps that time from a coarse clock:
/// measured on the Linux review host, 94% of back-to-back rewrites of one file
/// land on a single <c>st_mtime_ns</c> value. Two writes inside one such granule
/// that keep the length identical are therefore indistinguishable, and a
/// single-field rewrite usually does keep it: <c>"key": "DEM-5"</c> to
/// <c>"DEM-6"</c>, one lane name swapped for an equally long one, a boolean
/// flipped between two four-letter spellings. When the second write is the one a
/// caller just made, the memo keeps serving the content from before it until
/// something else moves the length or the clock - the stale key the duplicate-key
/// sweep then re-resolved on every pass (AGT-2867).</para>
///
/// <para>So a folder whose <c>task.json</c> was written too close to the moment
/// the fingerprint was taken is parsed but not memoized. The next scan re-parses
/// it and memoizes once the write has aged out, which costs one extra parse for
/// the handful of folders a mutation just touched. This is the rule git applies
/// to "racily clean" index entries whose mtime matches the index's own.</para>
/// </summary>
internal static class TaskFolderMemoPolicy
{
    /// <summary>
    /// How far a write must be from the fingerprint's observation instant before
    /// the (length, last-write) pair is trusted. One second clears the coarse
    /// clock on every filesystem the workspace is supported on, including the
    /// one-second stamps of ext3 and HFS+.
    /// </summary>
    internal static readonly TimeSpan DefaultTimestampGranularity = TimeSpan.FromSeconds(1);

    /// <summary>
    /// True when a (length, last-write) fingerprint taken at
    /// <paramref name="observedAtUtc"/> can still be trusted to change on the next
    /// write to the file.
    ///
    /// <para>The window is symmetric, so a last-write time a hair ahead of the
    /// clock (skew between the host and a mounted volume) is judged the same way.
    /// A timestamp far in the future is settled rather than racy: any later write
    /// moves it backwards, which the fingerprint still detects.</para>
    /// </summary>
    internal static bool IsMemoizable(
        DateTime lastWriteUtc,
        DateTime observedAtUtc,
        TimeSpan timestampGranularity)
        => Math.Abs((observedAtUtc - lastWriteUtc).Ticks) >= timestampGranularity.Ticks;
}
