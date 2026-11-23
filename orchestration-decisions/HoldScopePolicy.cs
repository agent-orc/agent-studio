namespace AgentStudio.OrchestrationDecisions;

/// <summary>Covering folders match the immediate directory. Suite areas include descendants.</summary>
public sealed record RedTestHoldScope(IReadOnlyList<string> CoveringFolders, IReadOnlyList<string> Files,
    IReadOnlyList<string> SuiteAreas);
public sealed record RedTestHold(RedTestHoldScope Scope, string RepairCardId,
    string? AttributedRevertCardId = null, IReadOnlyList<string>? OwnerExemptCardIds = null);

public static class HoldScopePolicy
{
    public static RedTestHoldScope Create(string testFile, IReadOnlyCollection<string> coveringFolders,
        IReadOnlyCollection<string> confirmedSuspectPaths, IReadOnlyCollection<string> suiteAreas)
    {
        var folders = coveringFolders.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var fallback = folders.Length == 0 || folders.Length > RedTestTriageBounds.MaximumCoveringFolders;
        return new(fallback ? [] : folders,
            confirmedSuspectPaths.Append(testFile).Select(Normalize).Distinct(StringComparer.Ordinal).ToArray(),
            fallback ? suiteAreas.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray() : []);
    }

    public static bool MayIntegrate(IReadOnlyCollection<string> changedPaths,
        IReadOnlyCollection<RedTestHold> holds, string cardId)
        => holds.All(hold => cardId == hold.RepairCardId || cardId == hold.AttributedRevertCardId
            || (hold.OwnerExemptCardIds?.Contains(cardId, StringComparer.Ordinal) ?? false)
            || !changedPaths.Any(path => Overlaps(path, hold.Scope)));

    public static bool LaterMergeIsCoupled(IReadOnlyCollection<string> suspectPaths,
        IReadOnlyCollection<string> laterFirstParentMergePaths)
        => suspectPaths.Select(Normalize).Intersect(laterFirstParentMergePaths.Select(Normalize), StringComparer.Ordinal).Any();

    // Compatibility for the original exact-file hold vocabulary.
    public static bool MayMergeFiles(IReadOnlyCollection<string> changedPaths, IReadOnlyCollection<string> heldFiles)
        => !changedPaths.Any(path => heldFiles.Contains(path, StringComparer.OrdinalIgnoreCase));

    private static bool Overlaps(string path, RedTestHoldScope scope)
    {
        path = Normalize(path);
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? "" : path[..slash];
        return scope.Files.Contains(path, StringComparer.Ordinal)
            || scope.CoveringFolders.Contains(directory, StringComparer.Ordinal)
            || scope.SuiteAreas.Any(area => path == area || path.StartsWith(area + "/", StringComparison.Ordinal));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
