using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ControlPlaneAuditRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-recovery-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(2147483649L)]
    [InlineData(long.MaxValue)]
    public void Hash_lookup_rejects_sequences_outside_the_snapshot_before_converting_to_int(long sequence)
    {
        var chain = Snapshot(0, ["first"]);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => chain.HashAt(sequence));

        Assert.Equal("sequence", exception.ParamName);
    }

    [Theory]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue - 2)]
    public void Hash_lookup_uses_the_segment_offset_even_when_the_anchor_exceeds_int_range(long anchor)
    {
        var chain = Snapshot(anchor, ["first", "second"]);

        Assert.Equal("first", chain.HashAt(anchor + 1));
        Assert.Equal("second", chain.HashAt(anchor + 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.HashAt(anchor));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.HashAt(long.MinValue));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue)]
    public void Recovery_rejects_an_invalid_snapshot_length_without_changing_the_file(long length)
    {
        var log = new ControlPlaneAuditLog(_root);
        log.Append(new("operator", "system", "original", "200"));
        var bytes = File.ReadAllBytes(log.FilePath);
        var check = log.VerifyChain() with { SnapshotLength = length };

        Assert.False(log.TryAnchorBreak(check, "Invalid snapshot.", "test-occurrence", DateTime.UnixEpoch));

        Assert.Equal(bytes, File.ReadAllBytes(log.FilePath));
        Assert.Null(log.LatestRecoveryAnchor);
        Assert.True(log.VerifyChain().Ok);
    }

    private static ChainCheck Snapshot(long anchor, IReadOnlyList<string> hashes)
        => new(true, hashes, null, null, null, anchor, anchor + hashes.Count,
            new(0, ControlPlaneAuditLog.GenesisHash, null), 0, "", anchor + hashes.Count);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
