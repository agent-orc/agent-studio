using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateShardHangPolicyTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(300_000, 10)]
    [InlineData(420_000, 14)]
    [InlineData(1_800_000, 60)]
    public void Silence_bound_uses_twice_the_shard_estimate_with_a_ten_minute_floor(
        double estimatedMs, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), GateShardHangPolicy.IdleBound(estimatedMs));

    [Fact]
    public void Completed_report_grace_is_two_minutes()
        => Assert.Equal(TimeSpan.FromMinutes(2), GateShardHangPolicy.CompletedGrace);

    [Fact]
    public void Blame_sequence_names_the_last_started_tests()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-blame-sequence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Sequence_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, "<TestSequence><Test Name=\"Product.Tests.First\" />"
                + "<Test Name=\"Product.Tests.Stalled\" /></TestSequence>");
            Assert.Equal(["Product.Tests.First", "Product.Tests.Stalled"],
                GateShardHangWatch.BlameSequenceNames(file, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
