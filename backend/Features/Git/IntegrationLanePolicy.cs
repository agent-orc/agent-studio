namespace AgentStudio.Git;

/// <summary>What synchronizing the integration lane with the published branch must do.</summary>
public enum IntegrationLaneSyncAction
{
    /// <summary>No lane yet: create it at the published tip.</summary>
    Seed,

    /// <summary>The lane already sits on the published tip.</summary>
    UpToDate,

    /// <summary>The lane carries gated merges that are not published yet; keep them.</summary>
    KeepPending,

    /// <summary>The published branch moved on; fast-forward the lane to it.</summary>
    FastForward,

    /// <summary>Nothing is published and no lane exists; the merge reports the missing branch.</summary>
    NothingToFollow,

    /// <summary>Lane and published branch both carry commits the other lacks.</summary>
    Diverged,
}

/// <summary>
/// Git facts for one lane synchronization. <paramref name="PublishedTip"/> is
/// <c>origin/&lt;branch&gt;</c> after a fetch, or the local branch for a
/// repository without an origin, where that branch is the only publication.
/// </summary>
public sealed record IntegrationLaneSyncState(
    string? LaneTip,
    string? PublishedTip,
    bool LaneContainsPublished,
    bool PublishedContainsLane);

/// <summary>What releasing a published integration result to the developer checkout must do.</summary>
public enum DeveloperCheckoutReleaseAction
{
    /// <summary>The local branch is an ancestor of the published SHA; fast-forward it.</summary>
    FastForward,

    /// <summary>The local branch already contains the published SHA.</summary>
    AlreadyContains,

    /// <summary>The developer repository has no local branch of that name.</summary>
    NoLocalBranch,

    /// <summary>Someone committed on the local branch; it is ahead of origin and stays untouched.</summary>
    LocalAhead,

    /// <summary>The local branch is on origin but not on the published line; it stays untouched.</summary>
    NotFastForward,
}

/// <summary>
/// Git facts for one release. <paramref name="OriginTip"/> is
/// <c>origin/&lt;branch&gt;</c> after the push, or null for a repository
/// without an origin.
/// </summary>
public sealed record DeveloperCheckoutReleaseState(
    string? LocalTip,
    string? OriginTip,
    bool LocalContainsPublished,
    bool PublishedContainsLocal,
    bool OriginContainsLocal);

/// <summary>
/// Pure lifecycle policy for the integration lane (AGT-2996).
///
/// <para>The developer checkout and the Studio-owned integration worktree share
/// every branch ref. When the lane merged straight onto <c>develop</c>, the
/// developer checkout carried each merge for the whole build gate, and a push
/// from that checkout published a merge no gate had approved. The lane
/// therefore keeps its own ref: merges, gates, and rollbacks move only the lane,
/// and the developer's branch follows the lane only after the integration push
/// worker published the gated result.</para>
/// </summary>
public static class IntegrationLanePolicy
{
    public static IntegrationLaneSyncAction DecideSync(IntegrationLaneSyncState state)
    {
        if (string.IsNullOrWhiteSpace(state.PublishedTip))
            return string.IsNullOrWhiteSpace(state.LaneTip)
                ? IntegrationLaneSyncAction.NothingToFollow
                : IntegrationLaneSyncAction.KeepPending;
        if (string.IsNullOrWhiteSpace(state.LaneTip)) return IntegrationLaneSyncAction.Seed;
        if (string.Equals(state.LaneTip, state.PublishedTip, StringComparison.OrdinalIgnoreCase))
            return IntegrationLaneSyncAction.UpToDate;
        if (state.LaneContainsPublished) return IntegrationLaneSyncAction.KeepPending;
        if (state.PublishedContainsLane) return IntegrationLaneSyncAction.FastForward;
        return IntegrationLaneSyncAction.Diverged;
    }

    public static DeveloperCheckoutReleaseAction DecideRelease(DeveloperCheckoutReleaseState state)
    {
        if (string.IsNullOrWhiteSpace(state.LocalTip)) return DeveloperCheckoutReleaseAction.NoLocalBranch;
        if (state.LocalContainsPublished)
        {
            // Equal to the published SHA, or further along on history origin
            // already carries: nothing to do. Further along with commits origin
            // lacks means someone committed here; that must be reported even
            // though the published SHA is already an ancestor.
            return state.PublishedContainsLocal || state.OriginContainsLocal
                ? DeveloperCheckoutReleaseAction.AlreadyContains
                : DeveloperCheckoutReleaseAction.LocalAhead;
        }
        if (state.PublishedContainsLocal) return DeveloperCheckoutReleaseAction.FastForward;
        // The local branch carries commits the published result does not. Only
        // commits that exist on origin are someone else's publication; anything
        // else was committed in this checkout and is not Studio's to move.
        if (string.IsNullOrWhiteSpace(state.OriginTip) || !state.OriginContainsLocal)
            return DeveloperCheckoutReleaseAction.LocalAhead;
        return DeveloperCheckoutReleaseAction.NotFastForward;
    }
}
