using AgentStudio.Git;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2996: direct matrix tests for the integration lane. The lane follows
/// only published history, and the developer checkout's branch follows the
/// lane only by fast-forward and never over commits someone made there.
/// </summary>
public sealed class IntegrationLanePolicyTests
{
    [Theory]
    [InlineData(null, null, false, false, IntegrationLaneSyncAction.NothingToFollow)]
    [InlineData("lane", null, false, false, IntegrationLaneSyncAction.KeepPending)]
    [InlineData(null, "pub", false, false, IntegrationLaneSyncAction.Seed)]
    [InlineData("same", "same", true, true, IntegrationLaneSyncAction.UpToDate)]
    [InlineData("lane", "pub", true, false, IntegrationLaneSyncAction.KeepPending)]
    [InlineData("lane", "pub", false, true, IntegrationLaneSyncAction.FastForward)]
    [InlineData("lane", "pub", false, false, IntegrationLaneSyncAction.Diverged)]
    public void DecideSync_FollowsOnlyPublishedHistory(
        string? laneTip,
        string? publishedTip,
        bool laneContainsPublished,
        bool publishedContainsLane,
        IntegrationLaneSyncAction expected)
    {
        var action = IntegrationLanePolicy.DecideSync(new IntegrationLaneSyncState(
            laneTip, publishedTip, laneContainsPublished, publishedContainsLane));

        Assert.Equal(expected, action);
    }

    [Theory]
    // No local branch: nothing to follow.
    [InlineData(null, "origin", false, false, false, DeveloperCheckoutReleaseAction.NoLocalBranch)]
    // The checkout already has the published result (equal or further along).
    [InlineData("local", "origin", true, false, true, DeveloperCheckoutReleaseAction.AlreadyContains)]
    [InlineData("local", "origin", true, true, true, DeveloperCheckoutReleaseAction.AlreadyContains)]
    // Behind the published result: fast-forward.
    [InlineData("local", "origin", false, true, true, DeveloperCheckoutReleaseAction.FastForward)]
    [InlineData("local", null, false, true, false, DeveloperCheckoutReleaseAction.FastForward)]
    // Someone committed on the checkout's branch: never touched.
    [InlineData("local", "origin", false, false, false, DeveloperCheckoutReleaseAction.LocalAhead)]
    [InlineData("local", null, false, false, false, DeveloperCheckoutReleaseAction.LocalAhead)]
    // Committed on top of the published SHA before release ran: still ahead of origin, warned.
    [InlineData("local", "origin", true, false, false, DeveloperCheckoutReleaseAction.LocalAhead)]
    // No origin: the local branch is the publication, so containing it is enough.
    [InlineData("local", null, true, false, false, DeveloperCheckoutReleaseAction.AlreadyContains)]
    // On origin already, just on another line than the published result.
    [InlineData("local", "origin", false, false, true, DeveloperCheckoutReleaseAction.NotFastForward)]
    public void DecideRelease_OnlyFastForwardsAndNeverOverCheckoutCommits(
        string? localTip,
        string? originTip,
        bool localContainsPublished,
        bool publishedContainsLocal,
        bool originContainsLocal,
        DeveloperCheckoutReleaseAction expected)
    {
        var action = IntegrationLanePolicy.DecideRelease(new DeveloperCheckoutReleaseState(
            localTip, originTip, localContainsPublished, publishedContainsLocal, originContainsLocal));

        Assert.Equal(expected, action);
    }
}
