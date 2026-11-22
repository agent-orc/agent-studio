namespace AgentStudio.Git;

public partial class GitService
{
    /// <summary>
    /// Changed paths of a delivery against its merge base with the current
    /// integration line (AGT-3964). The line is every ref that can carry it:
    /// the Studio lane, the freshly fetched published branch, and any
    /// <paramref name="extraBases"/> the caller proves are on that line. Git
    /// takes the merge base of the delivery with a hypothetical merge of all
    /// of them, so integration-branch history the delivery merged in (a
    /// runner's round-start merge of develop) never counts as the delivery's
    /// own change, even while the lane still lags the published branch.
    /// Returns null when no base resolves or git fails; callers then take
    /// their conservative path.
    /// </summary>
    public IReadOnlyList<string>? ChangedPathsAgainstIntegrationLine(
        string repoRoot,
        string branch,
        string sourceSha,
        IReadOnlyList<string>? extraBases = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsLikelyBranchName(branch) || !IsLikelyBranchName(sourceSha)) return null;

        // Best effort: a failed fetch leaves the last known published tip,
        // which is never newer than origin, so it can only over-report.
        var fetched = FetchIntegrationBranch(repoRoot, branch, cancellationToken);
        if (!fetched.Success)
            _logger.LogWarning(
                "integration-line diff uses the last known origin/{Branch} at {Path}: {Error}",
                branch, repoRoot, fetched.Error);

        var lineTips = new List<string>();
        foreach (var candidate in new[]
                 {
                     GetBranchTip(repoRoot, IntegrationLaneRef(branch)),
                     GetBranchTip(repoRoot, PublishedIntegrationRef(repoRoot, branch)),
                 })
        {
            if (candidate is not null && !lineTips.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                lineTips.Add(candidate);
        }
        if (lineTips.Count == 0) return null;

        // An extra base counts only when the integration line already carries
        // it. A continuation round's base can be the card's own unintegrated
        // earlier result, which must never hide that result's changes.
        var bases = new List<string>(lineTips);
        foreach (var extra in extraBases ?? [])
        {
            if (!AgentStudio.Pipeline.ReviewSubjectStore.IsValidResultSha(extra)
                || bases.Contains(extra, StringComparer.OrdinalIgnoreCase)) continue;
            if (lineTips.Any(tip => IsAncestor(repoRoot, extra, tip))) bases.Add(extra);
        }

        var mergeBaseArgs = new List<string> { "merge-base", sourceSha };
        mergeBaseArgs.AddRange(bases);
        var (mergeBase, _, mergeBaseCode) = RunGitArgs(repoRoot, [.. mergeBaseArgs]);
        var baseSha = mergeBase.Trim();
        if (mergeBaseCode != 0 || !AgentStudio.Pipeline.ReviewSubjectStore.IsValidResultSha(baseSha)) return null;

        var (output, _, code) = RunGitArgs(
            repoRoot, "diff", "--name-only", baseSha, sourceSha, RevisionsOnly);
        if (code != 0) return null;
        return output.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
