using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>The commits reachable from one target ref at <see cref="Tip"/>.</summary>
public sealed record GitReachableSet(string FullRef, string Tip, IReadOnlySet<string> Shas)
{
    /// <summary>
    /// Membership with the abbreviated-SHA rule of
    /// <see cref="TaskIntegrationStatusService.AncestorSetContains"/>.
    /// </summary>
    public bool Contains(string sha) => TaskIntegrationStatusService.AncestorSetContains(Shas, sha);
}

/// <summary>One reachability change the producer persists to <c>git_reachability</c>.</summary>
internal sealed record GitReachabilityChange(string FullRef, string Tip, bool Replace, IReadOnlyCollection<string> Shas);

/// <summary>
/// Reachability per target ref (docs/git-facts-architecture, section 03, slice
/// S3): the set of commits reachable from each integration and release ref,
/// kept per repository root. A fast-forward from <c>old</c> to <c>new</c>
/// appends <c>rev-list old..new</c>; any other move recomputes that ref once.
/// Ref tips are read from files, so an unchanged ref costs no Git process.
///
/// <para>
/// <see cref="TaskIntegrationStatusService"/> answers its ancestor sets from
/// here, so card classification, provenance membership, the project
/// integration view and the delivery claim share one reachability computation.
/// The fact producer seeds the index from the store on start and persists its
/// changes, which keeps a restart with unchanged refs free of Git.
/// </para>
/// </summary>
public sealed class GitReachabilityIndex
{
    private readonly ConcurrentDictionary<string, RootEntry> _roots = new(FileSystemPathComparer.Instance);

    /// <summary>Full Git processes this index started; a test and diagnostics hook.</summary>
    internal int FullRecomputes => Volatile.Read(ref _fullRecomputes);
    internal int FastForwards => Volatile.Read(ref _fastForwards);
    private int _fullRecomputes;
    private int _fastForwards;

    /// <summary>
    /// The union of the commits reachable from <paramref name="refNames"/>, the
    /// equivalent of <c>rev-list --ignore-missing</c> over them. Returns false
    /// when a name cannot be answered from the ref files (a reftable
    /// repository, a revision expression, a ref outside the read namespaces)
    /// or Git failed; the caller then asks Git directly.
    /// </summary>
    public bool TryUnion(string root, IReadOnlyCollection<string> refNames, out HashSet<string> union)
    {
        union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        var refs = GitRefObjectIds.Read(root);
        if (refs.ContainsKey(GitRefObjectIds.FormatKey) || refs.ContainsKey(GitRefObjectIds.TruncatedKey)) return false;
        foreach (var name in refNames)
        {
            var full = GitRefObjectIds.Resolve(refs, name);
            if (full is null)
            {
                if (!GitRefObjectIds.Covers(name)) return false;
                continue;
            }
            var set = Ensure(root, full, refs[full]);
            if (set is null) return false;
            union.UnionWith(set.Shas);
        }
        return true;
    }

    /// <summary>
    /// The set of <paramref name="fullRef"/> at <paramref name="tip"/>, brought
    /// up to date incrementally. Null when the tip is not a commit SHA or Git failed.
    /// </summary>
    internal GitReachableSet? Ensure(string root, string fullRef, string tip)
    {
        if (!IsSha(tip)) return null;
        var entry = _roots.GetOrAdd(Key(root), _ => new RootEntry());
        lock (entry.Gate)
        {
            entry.Refs.TryGetValue(fullRef, out var current);
            if (current is not null && string.Equals(current.Set.Tip, tip, StringComparison.OrdinalIgnoreCase))
                return current.Set;
            try
            {
                if (current is not null && GitFactCommands.RevListSince(root, tip, current.Set.Tip) is { FastForward: true } since)
                {
                    var grown = new HashSet<string>(current.Set.Shas, StringComparer.OrdinalIgnoreCase);
                    foreach (var sha in since.Added) grown.Add(entry.Intern(sha));
                    Interlocked.Increment(ref _fastForwards);
                    return Store(entry, fullRef, new GitReachableSet(fullRef, tip, grown), replace: false, since.Added);
                }
                var all = GitFactCommands.RevListAll(root, tip);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sha in all) set.Add(entry.Intern(sha));
                Interlocked.Increment(ref _fullRecomputes);
                return Store(entry, fullRef, new GitReachableSet(fullRef, tip, set), replace: true, all);
            }
            catch (GitFactReadException ex)
            {
                SilentCatch.Note(ex, "GitReachabilityIndex: reachability read failed");
                return null;
            }
        }
    }

    /// <summary>The current set of <paramref name="fullRef"/> without any Git work, or null.</summary>
    internal GitReachableSet? Peek(string root, string fullRef)
    {
        if (!_roots.TryGetValue(Key(root), out var entry)) return null;
        lock (entry.Gate) return entry.Refs.TryGetValue(fullRef, out var current) ? current.Set : null;
    }

    /// <summary>
    /// Restores sets from the store. A stored set is used only while its tip
    /// still matches the ref; <see cref="Ensure"/> updates it from there.
    /// </summary>
    internal void Seed(string root, IEnumerable<(string FullRef, string Tip, IReadOnlyCollection<string> Shas)> sets)
    {
        var entry = _roots.GetOrAdd(Key(root), _ => new RootEntry());
        lock (entry.Gate)
        {
            entry.Tracked = true;
            foreach (var (fullRef, tip, shas) in sets)
            {
                if (entry.Refs.ContainsKey(fullRef) || !IsSha(tip)) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sha in shas) set.Add(entry.Intern(sha));
                entry.Refs[fullRef] = new RefEntry(new GitReachableSet(fullRef, tip, set));
            }
        }
    }

    /// <summary>
    /// Marks a root whose changes the producer persists; others stay in memory
    /// only. Sets computed before the root was tracked are persisted in full.
    /// </summary>
    internal void Track(string root)
    {
        var entry = _roots.GetOrAdd(Key(root), _ => new RootEntry());
        lock (entry.Gate)
        {
            if (entry.Tracked) return;
            entry.Tracked = true;
            foreach (var refEntry in entry.Refs.Values)
            {
                refEntry.PendingReplace = true;
                refEntry.Pending = [];
            }
        }
    }

    /// <summary>The unpersisted changes of a tracked root, cleared on read.</summary>
    internal IReadOnlyList<GitReachabilityChange> TakeChanges(string root)
    {
        if (!_roots.TryGetValue(Key(root), out var entry)) return [];
        lock (entry.Gate)
        {
            var changes = new List<GitReachabilityChange>();
            foreach (var (fullRef, refEntry) in entry.Refs)
            {
                if (refEntry.Pending is null) continue;
                changes.Add(new GitReachabilityChange(fullRef, refEntry.Set.Tip, refEntry.PendingReplace,
                    refEntry.PendingReplace ? refEntry.Set.Shas.ToArray() : refEntry.Pending.ToArray()));
                refEntry.Pending = null;
                refEntry.PendingReplace = false;
            }
            return changes;
        }
    }

    /// <summary>Drops a root's sets, for a relocated or removed repository.</summary>
    internal void Forget(string root) => _roots.TryRemove(Key(root), out _);

    private static GitReachableSet Store(RootEntry entry, string fullRef, GitReachableSet set, bool replace,
        IReadOnlyList<string> added)
    {
        if (!entry.Refs.TryGetValue(fullRef, out var refEntry))
        {
            refEntry = new RefEntry(set);
            entry.Refs[fullRef] = refEntry;
        }
        refEntry.Set = set;
        if (!entry.Tracked) return set;
        if (replace || refEntry.PendingReplace)
        {
            refEntry.PendingReplace = true;
            refEntry.Pending = [];
        }
        else
        {
            refEntry.Pending ??= [];
            refEntry.Pending.AddRange(added);
        }
        return set;
    }

    internal static bool IsSha(string? value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static string Key(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SilentCatch.Note(ex, "GitReachabilityIndex: root normalization failed");
            return root;
        }
    }

    private sealed class RootEntry
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, RefEntry> Refs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _intern = new(StringComparer.OrdinalIgnoreCase);
        public bool Tracked;

        /// <summary>One string instance per SHA across this root's sets.</summary>
        public string Intern(string sha)
        {
            if (_intern.TryGetValue(sha, out var known)) return known;
            _intern[sha] = sha;
            return sha;
        }
    }

    private sealed class RefEntry(GitReachableSet set)
    {
        public GitReachableSet Set = set;
        public List<string>? Pending;
        public bool PendingReplace;
    }
}
