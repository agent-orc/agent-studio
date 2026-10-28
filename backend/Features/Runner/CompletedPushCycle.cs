using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>
/// One completed-push sweep (AGT-3337). Remembers which repositories already
/// fetched their integration branch, so a backstop cycle over hundreds of
/// completed cards fetches each repository at most once instead of once per
/// card. A single worker request uses its own cycle.
/// </summary>
public sealed class CompletedPushCycle
{
    private readonly ConcurrentDictionary<string, byte> _fetched = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>True for the first caller per repository root; false afterwards.</summary>
    public bool TryClaimFetch(string repoRoot)
        => _fetched.TryAdd(Path.GetFullPath(repoRoot).TrimEnd('/', '\\'), 0);

    public int FetchedRepositories => _fetched.Count;
}
