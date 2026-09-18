using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The decision behind the scanner's folder memos: may a (length, last-write)
/// fingerprint taken now be trusted to change on the next write?
///
/// <para>It may not while the write that produced it is still inside the
/// filesystem's timestamp granule, because a second write landing in that same
/// granule without changing the length leaves the fingerprint identical. The
/// matrix below is the whole rule; <see cref="TaskScannerLiveFolderMemoTests"/>
/// then shows a scanner honouring it.</para>
/// </summary>
public class TaskFolderMemoPolicyTests
{
    private static readonly DateTime Observed = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Granularity = TimeSpan.FromSeconds(1);

    [Theory]
    // Settled writes: far enough from the observation instant that any later
    // write lands on a different timestamp.
    [InlineData(-60_000, true)]
    [InlineData(-1_001, true)]
    [InlineData(-1_000, true)]
    // Racy writes: a second write in this window can reproduce the same stamp.
    [InlineData(-999, false)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    // Symmetric, so clock skew between the host and a mounted volume is judged
    // the same way rather than trusted by accident.
    [InlineData(1, false)]
    [InlineData(999, false)]
    // A timestamp genuinely ahead of the clock is settled: a later write moves
    // it backwards, which the fingerprint still detects.
    [InlineData(1_000, true)]
    [InlineData(60_000, true)]
    public void IsMemoizable_TrustsOnlyWritesOutsideTheTimestampGranule(
        int lastWriteOffsetMs, bool expected)
    {
        var lastWriteUtc = Observed.AddMilliseconds(lastWriteOffsetMs);

        Assert.Equal(
            expected,
            TaskFolderMemoPolicy.IsMemoizable(lastWriteUtc, Observed, Granularity));
    }

    [Fact]
    public void DefaultTimestampGranularity_ClearsTheCoarsestSupportedFilesystemStamp()
    {
        // ext3 and HFS+ stamp whole seconds; the Linux coarse clock that ext4 and
        // tmpfs use moves once per timer tick. One second covers both, so the
        // default never has to be tuned per host.
        Assert.True(TaskFolderMemoPolicy.DefaultTimestampGranularity >= TimeSpan.FromSeconds(1));
    }
}
