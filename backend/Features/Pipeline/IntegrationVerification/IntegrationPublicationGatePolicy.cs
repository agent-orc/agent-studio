namespace AgentStudio.Pipeline;

/// <summary>Checks that each publication after a verified tree has its own passed gate.</summary>
public static class IntegrationPublicationGatePolicy
{
    public const int MaxFirstParentSteps = 4096;

    public sealed record FirstParentCommit(string Sha, string ParentSha);
    public sealed record Publication(string TreeSha, string BaseSha, string Gate);

    public sealed record Decision(bool Verified, string? MissingTree = null, bool MissingAncestry = false);

    /// <summary>
    /// The range is newest first. Its parent links must reach the recorded
    /// verification tree before any publication can cover a later commit.
    /// A batch gate can cover several consecutive first-parent commits.
    /// </summary>
    public static Decision Check(string verifiedTree, string currentTree,
        IReadOnlyList<FirstParentCommit> laterCommits,
        IReadOnlyCollection<Publication> publications)
    {
        if (string.Equals(verifiedTree, currentTree, StringComparison.OrdinalIgnoreCase))
            return new Decision(true);
        if (laterCommits.Count == 0 || laterCommits.Count > MaxFirstParentSteps
            || !string.Equals(laterCommits[0].Sha, currentTree, StringComparison.OrdinalIgnoreCase))
            return new Decision(false, currentTree, MissingAncestry: true);

        for (var i = 0; i < laterCommits.Count; i++)
        {
            var expectedParent = i + 1 < laterCommits.Count
                ? laterCommits[i + 1].Sha : verifiedTree;
            if (!string.Equals(laterCommits[i].ParentSha, expectedParent, StringComparison.OrdinalIgnoreCase))
                return new Decision(false, laterCommits[i].Sha, MissingAncestry: true);
        }

        var positions = laterCommits.Select((commit, index) => (commit.Sha, index))
            .ToDictionary(item => item.Sha, item => item.index, StringComparer.OrdinalIgnoreCase);
        var byTree = publications
            .GroupBy(item => item.TreeSha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < laterCommits.Count;)
        {
            var sha = laterCommits[index].Sha;
            if (!byTree.TryGetValue(sha, out var publication))
                return new Decision(false, sha);
            if (string.Equals(publication.BaseSha, verifiedTree, StringComparison.OrdinalIgnoreCase))
                return new Decision(true);
            if (!positions.TryGetValue(publication.BaseSha, out var baseIndex) || baseIndex <= index)
                return new Decision(false, sha);
            index = baseIndex;
        }
        return new Decision(true);
    }
}
