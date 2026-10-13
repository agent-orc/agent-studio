namespace AgentStudio.Git;

/// <summary>How one batch member reaches the candidate.</summary>
public enum BatchReplayRoute
{
    /// <summary>The candidate already contains the result; the member adds nothing of its own.</summary>
    AlreadyContained,

    /// <summary>A linear delivery on the candidate tip: fast-forward with an identity mapping.</summary>
    Identity,

    /// <summary>A linear delivery behind the tip: conflict-free mechanical rebase, one replacement per commit.</summary>
    Rebase,

    /// <summary>
    /// A delivery with merge commits in its range (a fix round that merged the
    /// integration branch): merge the untouched result commit into the candidate.
    /// </summary>
    Merge,
}

/// <summary>Why a batch member did not reach the candidate.</summary>
public enum BatchReplayFailure
{
    None,

    /// <summary>A textual conflict with the candidate; the candidate ref stays put.</summary>
    Conflict,

    /// <summary>The member itself cannot be mapped to one exact candidate commit; eject it alone.</summary>
    Unattributable,

    /// <summary>Git or the disposable worktree failed; the member says nothing about itself.</summary>
    Infrastructure,
}

/// <summary>
/// AGT-3104: which replay keeps per-member SHA attribution exact. A linear
/// delivery keeps the one-to-one identity or rebase mapping. A delivery with a
/// merge commit in its range cannot be rebased one-to-one, so its result commit
/// is merged into the candidate instead: every delivery SHA stays unchanged on
/// the candidate and the result maps to that one merge commit. A linear rebase
/// that still loses its one-to-one mapping (a commit became empty on the tip)
/// takes the merge route as well.
/// </summary>
public static class BatchReplayPolicy
{
    public static BatchReplayRoute Choose(
        bool resultContainedInTip, bool tipIsAncestorOfResult, int mergeCommitsInRange)
    {
        if (mergeCommitsInRange < 0)
            throw new ArgumentOutOfRangeException(nameof(mergeCommitsInRange));
        if (resultContainedInTip) return BatchReplayRoute.AlreadyContained;
        if (mergeCommitsInRange > 0) return BatchReplayRoute.Merge;
        return tipIsAncestorOfResult ? BatchReplayRoute.Identity : BatchReplayRoute.Rebase;
    }
}
