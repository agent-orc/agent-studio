using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Covering folders match the immediate directory. Suite areas include descendants.</summary>
public sealed record RedTestHoldScope(IReadOnlyList<string> CoveringFolders, IReadOnlyList<string> Files,
    IReadOnlyList<string> SuiteAreas);
public sealed record RedTestHold(RedTestHoldScope Scope, string RepairCardId,
    string? AttributedRevertCardId = null, IReadOnlyList<string>? OwnerExemptCardIds = null);

public static class HoldScopePolicy
{
    public static IReadOnlyList<TestQuarantineHoldFact> ActiveHolds(
        IReadOnlyList<ProjectTestQuarantine> quarantines, DateTimeOffset now)
        => quarantines.Where(entry => entry.Kind == "held" && TestQuarantineLifecyclePolicy.Active(entry, now))
            .Select(entry =>
            {
                var scope = Create(entry.TestFile ?? "", entry.CoveringFolders,
                    entry.ConfirmedSuspectPaths, entry.SuiteAreas);
                return new TestQuarantineHoldFact(entry.Id, entry.Test, entry.Card, scope.CoveringFolders,
                    scope.Files.Where(path => path.Length > 0).ToArray(), scope.SuiteAreas, entry.ExpiresAtUtc,
                    entry.AttributedRevertCardId, entry.OwnerExemptCardIds);
            }).ToArray();

    public static string? IntegrationBlock(IReadOnlyCollection<string> changedPaths,
        IReadOnlyList<TestQuarantineHoldFact> holds, string cardId, IReadOnlyList<string>? aliases = null)
    {
        foreach (var fact in holds)
        {
            var hold = new RedTestHold(new(fact.CoveringFolders, fact.Files, fact.SuiteAreas),
                fact.RepairCardId, fact.AttributedRevertCardId, fact.OwnerExemptCardIds);
            if ((aliases ?? []).Append(cardId).Any(id => MayIntegrate(changedPaths, [hold], id))) continue;
            var path = changedPaths.First(path => Overlaps(path, hold.Scope));
            var folder = fact.SuiteAreas.FirstOrDefault(area => area.Length == 0 || path == area || path.StartsWith(area + "/", StringComparison.Ordinal))
                ?? fact.CoveringFolders.FirstOrDefault(folder => path.StartsWith(folder + "/", StringComparison.Ordinal))
                ?? path;
            return $"Quarantine {fact.QuarantineId}: {fact.Test} holds {(folder.Length == 0 ? "repository (suite area unavailable)" : folder)}; repair {fact.RepairCardId}.";
        }
        return null;
    }

    public static RedTestHoldScope Create(string testFile, IReadOnlyCollection<string> coveringFolders,
        IReadOnlyCollection<string> confirmedSuspectPaths, IReadOnlyCollection<string> suiteAreas)
    {
        var folders = coveringFolders.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var fallback = UsesSuiteAreaFallback(folders);
        return new(fallback ? [] : folders,
            confirmedSuspectPaths.Append(testFile).Select(Normalize).Distinct(StringComparer.Ordinal).ToArray(),
            fallback ? suiteAreas.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray() : []);
    }

    /// <summary>
    /// RT-3: a test file that names no repository type, or types in more than
    /// <see cref="RedTestTriageBounds.MaximumCoveringFolders"/> folders, is held on
    /// its test project's area instead.
    /// </summary>
    public static bool UsesSuiteAreaFallback(IReadOnlyCollection<string> coveringFolders)
    {
        var distinct = coveringFolders.Select(Normalize).Distinct(StringComparer.Ordinal).Count();
        return distinct == 0 || distinct > RedTestTriageBounds.MaximumCoveringFolders;
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
            || scope.SuiteAreas.Any(area => area.Length == 0 || path == area || path.StartsWith(area + "/", StringComparison.Ordinal));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
