using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// Work a read found missing and asks the producer to do with priority
/// (Git facts S3). <see cref="Key"/> deduplicates queued work.
/// </summary>
public abstract record GitFactWork
{
    public abstract string Key { get; }
}

/// <summary>Read the facts of one commit.</summary>
public sealed record GitCommitWork(string Sha) : GitFactWork
{
    public override string Key => "commit:" + Sha.ToLowerInvariant();
}

/// <summary>List the non-merge commits of <c>base..head</c>.</summary>
public sealed record GitRangeWork(string BaseSha, string HeadSha) : GitFactWork
{
    public override string Key => GitCommitRangeKeys.Sha(BaseSha, HeadSha);
}

/// <summary>
/// List the commits of a wall-clock run window (runs without captured SHAs).
/// A null <see cref="ToUtc"/> is a run still in flight: its window is read up
/// to the pass and kept until HEAD moves.
/// </summary>
public sealed record GitWindowWork(DateTime FromUtc, DateTime? ToUtc) : GitFactWork
{
    public override string Key => GitCommitRangeKeys.Window(FromUtc, ToUtc);
}

/// <summary>Write the diff object of one commit.</summary>
public sealed record GitCommitDiffWork(string Sha) : GitFactWork
{
    public override string Key => "diff-commit:" + Sha.ToLowerInvariant();
}

/// <summary>Write the aggregate diff object of an ordered commit set.</summary>
public sealed record GitAggregateDiffWork(IReadOnlyList<string> Shas) : GitFactWork
{
    public override string Key => "diff-aggregate:" + string.Join(',', Shas).ToLowerInvariant();
}

/// <summary>Write the tree diff object of <c>base..head</c>.</summary>
public sealed record GitRangeDiffWork(string BaseSha, string HeadSha) : GitFactWork
{
    public override string Key => "diff-range:" + GitCommitRangeKeys.Sha(BaseSha, HeadSha);
}

/// <summary>Index the curated merges of a ref name a read used.</summary>
public sealed record GitMergesWork(string RefName) : GitFactWork
{
    public override string Key => "merges:" + RefName;
}

/// <summary>A bounded S4 file-history or object request for one card path.</summary>
public sealed record GitFileHistoryWork(string JobId, string WatchPath, string Source,
    string Path, string? At = null) : GitFactWork
{
    public override string Key => $"file:{JobId}:{Source}:{Path}:{At}";
}

/// <summary>Keys of <c>git_commit_ranges</c>.</summary>
public static class GitCommitRangeKeys
{
    public static string Sha(string baseSha, string headSha)
        => $"range:{baseSha.ToLowerInvariant()}..{headSha.ToLowerInvariant()}";

    public static string Window(DateTime fromUtc, DateTime? toUtc)
        => $"window:{Seconds(fromUtc)}..{(toUtc is { } to ? Seconds(to).ToString(System.Globalization.CultureInfo.InvariantCulture) : "open")}";

    private static long Seconds(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified
            ? DateTimeKind.Utc : value.Kind)).ToUnixTimeSeconds();
}

/// <summary>
/// The facts of one repository that only grow: commit facts by SHA, commit
/// ranges, diff object manifests, and SHAs the repository does not have. Shared
/// by every published generation of the repository; entries are immutable.
/// </summary>
public sealed class GitRepositoryFactCatalog
{
    private readonly ConcurrentDictionary<string, GitCommitFact> _commits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _abbreviations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _ranges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitDiffObjectManifest> _manifests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitDiffObject> _memoryObjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string? Head, IReadOnlyList<string> Shas)> _openWindows =
        new(StringComparer.Ordinal);

    /// <summary>Diff objects kept in memory when the store is unavailable.</summary>
    internal const int MaxMemoryObjects = 512;

    internal int CommitCount => _commits.Count;

    /// <summary>The facts of a full or abbreviated SHA, or null.</summary>
    public GitCommitFact? Commit(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        sha = sha.Trim();
        if (_commits.TryGetValue(sha, out var fact)) return fact;
        return _abbreviations.TryGetValue(sha, out var full) && _commits.TryGetValue(full, out fact) ? fact : null;
    }

    public IReadOnlyList<string>? Range(string key) => _ranges.TryGetValue(key, out var shas) ? shas : null;

    /// <summary>True when the producer looked for this SHA and the repository does not have it.</summary>
    public bool IsMissing(string sha) => _missing.ContainsKey(sha.Trim());

    internal void AddCommit(GitCommitFact fact)
    {
        _commits[fact.Sha] = fact;
        _missing.TryRemove(fact.Sha, out _);
    }

    internal void AddAbbreviation(string abbreviated, string full)
    {
        if (!string.Equals(abbreviated, full, StringComparison.OrdinalIgnoreCase)) _abbreviations[abbreviated] = full;
        _missing.TryRemove(abbreviated, out _);
    }

    internal void AddRange(string key, IReadOnlyList<string> shas) => _ranges[key] = shas;

    /// <summary>The commits of an open run window and the HEAD they were read at, or null.</summary>
    public (string? Head, IReadOnlyList<string> Shas)? OpenWindow(string key)
        => _openWindows.TryGetValue(key, out var window) ? window : null;

    internal void AddOpenWindow(string key, string? head, IReadOnlyList<string> shas) => _openWindows[key] = (head, shas);

    internal void MarkMissing(string sha) => _missing[sha.Trim()] = 0;

    /// <summary>A moved ref can bring a once-missing object; the next read looks again.</summary>
    internal void ForgetMissing() => _missing.Clear();

    internal bool HasRange(string key) => _ranges.ContainsKey(key);

    internal GitDiffObjectManifest? Manifest(string objectId)
        => _manifests.TryGetValue(objectId, out var manifest) ? manifest
            : _memoryObjects.TryGetValue(objectId, out var memory) ? memory.Manifest : null;

    internal void AddManifest(GitDiffObjectManifest manifest) => _manifests[manifest.ObjectId] = manifest;

    internal void AddMemoryObject(GitDiffObject diff)
    {
        if (_memoryObjects.Count >= MaxMemoryObjects)
        {
            foreach (var key in _memoryObjects.Keys.Take(MaxMemoryObjects / 4)) _memoryObjects.TryRemove(key, out _);
        }
        _memoryObjects[diff.Manifest.ObjectId] = diff;
    }

    internal GitDiffObject? MemoryObject(string objectId) => _memoryObjects.TryGetValue(objectId, out var diff) ? diff : null;
}

/// <summary>
/// One repository's published Git facts (Git facts S3): the generation and its
/// ref object IDs, the reachability sets of the integration and release refs
/// at that generation, the run trailer index, the curated merge index, the
/// project integration view, and the shared <see cref="Catalog"/>. Readers join
/// these with the current task; none of them starts Git.
/// </summary>
public sealed record GitPublishedFacts(
    string RepoId,
    string WatchPath,
    string RepositoryRoot,
    long Generation,
    DateTimeOffset PublishedAt,
    string State,
    IReadOnlyDictionary<string, string> Refs,
    string IntegrationBranch,
    string ReleaseBranch,
    IReadOnlyDictionary<string, GitReachableSet> Reachability,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RunCommits,
    IReadOnlyDictionary<string, IReadOnlyList<GitIntegrationMergeCommit>> Merges,
    GitStoredProjectView? ProjectView,
    GitRepositoryFactCatalog Catalog)
{
    public const string ReadyState = "ready";
    public const string RestoredState = "restored";
    public const string UnavailableState = "unavailable";

    /// <summary>The tip of a short or full ref name at this generation, or null.</summary>
    public string? Tip(string name)
        => GitRefObjectIds.Resolve(Refs, name) is { } full && GitReachabilityIndex.IsSha(Refs[full]) ? Refs[full] : null;

    /// <summary>
    /// True when <paramref name="sha"/> is reachable from the local branch or
    /// its <c>origin/</c> mirror at this generation, the rule of
    /// <c>merge-base --is-ancestor</c> against both.
    /// </summary>
    public bool IsContainedIn(string sha, string branch)
        => Reach("refs/heads/" + branch)?.Contains(sha) == true
            || Reach("refs/remotes/origin/" + branch)?.Contains(sha) == true;

    public GitReachableSet? Reach(string fullRef) => Reachability.GetValueOrDefault(fullRef);

    /// <summary>Facts for a project that has no Git repository: every join answers from the task alone.</summary>
    public static GitPublishedFacts Unavailable(string watchPath, string integrationBranch) => new(
        "", watchPath, "", 0, DateTimeOffset.MinValue, UnavailableState, new Dictionary<string, string>(),
        integrationBranch, TaskProvenanceService.ReleaseBranch, new Dictionary<string, GitReachableSet>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<GitIntegrationMergeCommit>>(),
        null, new GitRepositoryFactCatalog());
}

/// <summary>What a read found for a task's repository.</summary>
public readonly record struct GitFactsLookup(bool Known, GitPublishedFacts? Facts);

/// <summary>
/// The request side of the Git fact store (Git facts S3): the published facts
/// per repository and the priority queue to the producer. Request handlers
/// read here and never start Git. A miss asks the producer for the missing
/// work and answers <c>warming</c>; the producer's next run, a short debounce
/// later, fills it.
/// </summary>
public sealed class GitCardFactReader
{
    private readonly ConcurrentDictionary<string, Slot> _repos = new(FileSystemPathComparer.Instance);
    private readonly GitFactStore? _store;

    public GitCardFactReader(GitFactStore? store = null)
    {
        _store = store;
    }

    /// <summary>Raised after a repository's facts were published; the argument is the watch path.</summary>
    public event Action<string>? Published;

    /// <summary>The repository facts of a task's watch path.</summary>
    public GitFactsLookup Read(string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return default;
        return _repos.TryGetValue(Normalize(watchPath), out var slot) ? new GitFactsLookup(true, slot.Facts) : default;
    }

    /// <summary>The repository facts of a project by its indexed name or watch path.</summary>
    public GitFactsLookup ReadProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return default;
        if (_repos.TryGetValue(Normalize(project), out var byPath)) return new GitFactsLookup(true, byPath.Facts);
        var slot = _repos.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.RepoId, project, StringComparison.OrdinalIgnoreCase));
        return slot is null ? default : new GitFactsLookup(true, slot.Facts);
    }

    /// <summary>
    /// Queues <paramref name="work"/> for the repository's producer with
    /// priority. The producer runs outside this request: the read scope and
    /// the execution context do not flow into it.
    /// </summary>
    public void Request(string? watchPath, IReadOnlyCollection<GitFactWork> work)
    {
        if (work.Count == 0 || string.IsNullOrWhiteSpace(watchPath)) return;
        if (!_repos.TryGetValue(Normalize(watchPath), out var slot) || slot.RequestWork is null) return;
        using var detached = GitReadRequestGuard.Detach();
        using var suppressed = ExecutionContext.SuppressFlow();
        slot.RequestWork(work);
    }

    /// <summary>The manifest of a diff object, from memory or the store. Null when it does not exist yet.</summary>
    public GitDiffObjectManifest? DiffObject(GitPublishedFacts facts, string objectId)
    {
        if (facts.Catalog.Manifest(objectId) is { } known) return known;
        if (_store is null) return null;
        var stored = _store.ReadDiffObjects([objectId]).GetValueOrDefault(objectId);
        if (stored is not null) facts.Catalog.AddManifest(stored);
        return stored;
    }

    /// <summary>The patches of a diff object, all files or one path. No Git.</summary>
    internal IReadOnlyList<GitPatchFile> DiffPatches(GitPublishedFacts facts, string objectId, string? path)
    {
        if (facts.Catalog.MemoryObject(objectId) is { } memory)
            return path is null ? memory.Patches
                : memory.Patches.Where(file => string.Equals(file.Path, path, StringComparison.Ordinal)).ToList();
        return _store?.ReadDiffObjectPatches(objectId, path) ?? [];
    }

    internal void Register(string watchPath, string repoId, Action<IReadOnlyCollection<GitFactWork>> requestWork)
        => _repos[Normalize(watchPath)] = new Slot(repoId, requestWork);

    internal void Unregister(string watchPath) => _repos.TryRemove(Normalize(watchPath), out _);

    internal void Publish(string watchPath, GitPublishedFacts facts)
    {
        var key = Normalize(watchPath);
        var slot = _repos.GetOrAdd(key, _ => new Slot(facts.RepoId, null));
        Volatile.Write(ref slot.Facts, facts);
        try { Published?.Invoke(watchPath); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SilentCatch.Note(ex, "GitCardFactReader: published handler failed");
        }
    }

    /// <summary>Stores a diff object the producer built: in the store, else in memory.</summary>
    internal void Store(GitRepositoryFactCatalog catalog, GitDiffObject diff)
    {
        if (_store is { IsAvailable: true } && _store.WriteDiffObject(diff)) catalog.AddManifest(diff.Manifest);
        else catalog.AddMemoryObject(diff);
    }

    private static string Normalize(string path) => AgentStudio.Tasks.TaskListGitProjectionCache.NormalizePath(path);

    private sealed class Slot(string repoId, Action<IReadOnlyCollection<GitFactWork>>? requestWork)
    {
        public string RepoId { get; } = repoId;
        public Action<IReadOnlyCollection<GitFactWork>>? RequestWork { get; } = requestWork;
        public GitPublishedFacts? Facts;
    }
}
