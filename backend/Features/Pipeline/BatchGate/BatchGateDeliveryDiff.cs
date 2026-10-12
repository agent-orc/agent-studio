using AgentStudio.Git;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// A delivery's own changes: from its merge base with the fetched integration
/// line to the immutable result SHA. The Studio checkout holds every result
/// object (the completion fetch stores it under <c>refs/remotes/origin</c>) but
/// neither the local <c>refs/heads</c> result ref nor a current local
/// integration branch, so only the SHA and the remote-tracking ref are used.
/// </summary>
internal sealed record BatchGateDeliveryDiff(IReadOnlyList<string> Paths, int Lines)
{
    /// <summary>The ref the planning tick's integration fetch refreshes.</summary>
    public static string IntegrationRef(string branch)
        => $"refs/remotes/origin/{TaskIntegrationBranch.Name(branch)}";

    /// <summary>Changed paths, or null when the diff cannot be computed.</summary>
    public static IReadOnlyList<string>? ChangedPaths(GitService git, string repo,
        string branch, string? resultSha)
    {
        var mergeBase = MergeBase(git, repo, branch, resultSha);
        return mergeBase is null ? null
            : git.ChangedPathsAgainstMergeBase(repo, mergeBase, resultSha!);
    }

    /// <summary>Changed paths and lines, or null when the diff cannot be computed.</summary>
    public static BatchGateDeliveryDiff? Read(GitService git, string repo, string branch,
        string? resultSha)
    {
        var mergeBase = MergeBase(git, repo, branch, resultSha);
        if (mergeBase is null) return null;
        var paths = git.ChangedPathsAgainstMergeBase(repo, mergeBase, resultSha!);
        if (paths is null) return null;
        // Both endpoints are commit SHAs, so the range cache cannot go stale.
        var lines = git.GetFilesChangedInRangeAtRoot(repo, mergeBase, resultSha!)
            .Sum(change => change.Added + change.Removed);
        return new BatchGateDeliveryDiff(paths, lines);
    }

    private static string? MergeBase(GitService git, string repo, string branch,
        string? resultSha)
        => string.IsNullOrWhiteSpace(resultSha) ? null
            : git.GetMergeBase(repo, IntegrationRef(branch), resultSha);
}
