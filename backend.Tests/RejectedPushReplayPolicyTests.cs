using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RejectedPushReplayPolicyTests
{
    [Theory]
    [InlineData(0, true, true, false, RejectedPushReplayAction.ReuseGreenOnNewBase)]
    [InlineData(0, true, true, true, RejectedPushReplayAction.ReturnToBatchQueue)]
    [InlineData(0, true, false, false, RejectedPushReplayAction.ReturnToBatchQueue)]
    [InlineData(0, false, true, false, RejectedPushReplayAction.ReturnToBatchQueue)]
    [InlineData(1, true, true, false, RejectedPushReplayAction.ReturnToBatchQueue)]
    public void Moved_base_decision_follows_gate_and_path_overlap(
        int replayCount, bool oldBaseIsAncestor, bool gatePassed, bool overlap,
        RejectedPushReplayAction expected)
    {
        var action = RejectedPushReplayPolicy.Decide(replayCount, oldBaseIsAncestor,
            gatePassed, ["delivery.txt"], overlap ? ["DELIVERY.txt"] : ["batch.txt"]);
        Assert.Equal(expected, action);
    }

    [Fact]
    public void Unknown_paths_cannot_reuse_a_green_verdict()
        => Assert.Equal(RejectedPushReplayAction.ReturnToBatchQueue,
            RejectedPushReplayPolicy.Decide(0, true, true, null, ["batch.txt"]));
}
