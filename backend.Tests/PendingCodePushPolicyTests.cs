using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PendingCodePushPolicyTests
{
    [Fact]
    public void Diverged_operator_push_releases_unpublishable_merge_marker()
        => Assert.True(PendingCodePushPolicy.IsResolved(false, false, true,
            true, true, "approved", "approved"));

    [Theory]
    [InlineData(true, "new-approved")]
    [InlineData(false, "approved")]
    public void Superseded_or_rolled_back_merge_releases_old_marker(
        bool mergePassed, string currentSha)
        => Assert.True(PendingCodePushPolicy.IsResolved(false, true, true,
            true, mergePassed, currentSha, "approved"));

    [Fact]
    public void Recorded_successful_push_releases_marker_after_restart()
        => Assert.True(PendingCodePushPolicy.IsResolved(true, false, true,
            true, true, "approved", "approved"));

    [Fact]
    public void Current_unpublished_merge_keeps_marker()
        => Assert.False(PendingCodePushPolicy.IsResolved(false, true, true,
            true, true, "approved", "approved"));

    [Fact]
    public void Card_leaving_integration_releases_marker()
        => Assert.True(PendingCodePushPolicy.IsResolved(false, true, true,
            false, true, "approved", "approved"));
}
