namespace AgentStudio.Pipeline;

/// <summary>Checks that each publication after a verified tree has its own passed gate.</summary>
public static class IntegrationPublicationGatePolicy
{
    private const int MaxFirstParentSteps = 4096;

    public sealed record Publication(string TreeSha, string BaseSha, string Gate);

    public sealed record Decision(bool Verified, string? MissingTree = null);

    /// <summary>
    /// A batch gate covers its candidate back to its pre-publication tip. A
    /// per-task gate covers one first-parent commit. The walk also proves the
    /// original verification tree lies on the published first-parent line.
    /// </summary>
    public static Decision Check(string verifiedTree, string currentTree,
        Func<string, string?> firstParent, IReadOnlyCollection<Publication> publications)
    {
        if (string.Equals(verifiedTree, currentTree, StringComparison.OrdinalIgnoreCase))
            return new Decision(true);
        var byTree = publications
            .GroupBy(item => item.TreeSha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var cursor = currentTree;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = 0;
        while (!string.Equals(cursor, verifiedTree, StringComparison.OrdinalIgnoreCase))
        {
            if (++steps > MaxFirstParentSteps
                || !visited.Add(cursor) || !byTree.TryGetValue(cursor, out var publication))
                return new Decision(false, cursor);
            var parent = firstParent(cursor);
            if (parent is null) return new Decision(false, cursor);
            if (!string.Equals(publication.BaseSha, parent, StringComparison.OrdinalIgnoreCase))
            {
                // A batch gate spans all replay commits from its base to its
                // candidate. Its base must really be on this first-parent line.
                var inside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (parent is not null
                    && !string.Equals(parent, publication.BaseSha, StringComparison.OrdinalIgnoreCase)
                    && inside.Add(parent) && ++steps <= MaxFirstParentSteps)
                    parent = firstParent(parent);
                if (parent is null || steps > MaxFirstParentSteps || !inside.Add(parent))
                    return new Decision(false, cursor);
            }
            cursor = parent;
        }
        return new Decision(true);
    }
}
