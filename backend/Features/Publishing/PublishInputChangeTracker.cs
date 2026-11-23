using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Publishing;

/// <summary>
/// Tracks uncommitted files that influence publish-target derivation without a
/// Git process or a repository-wide scan on every snapshot. A recursive watcher
/// supplies an O(1) generation for manifest edits/additions; the small workflow
/// directory and root package manifest are fingerprinted directly to close the
/// watcher delivery race for the most common edits.
/// </summary>
internal sealed class PublishInputChangeTracker : IDisposable
{
    private const int DefaultMaxWatchedRepositories = 64;

    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", "dist", "build", "out",
            "coverage", ".orchestrator", "test-results", "playwright-report",
        };

    private readonly Dictionary<string, WatchEntry> _repositories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly LinkedList<string> _lru = new();
    private readonly int _maxWatchedRepositories;
    private long _nextWatcherEpoch;
    private bool _disposed;

    public PublishInputChangeTracker(int maxWatchedRepositories = DefaultMaxWatchedRepositories)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWatchedRepositories, 1);
        _maxWatchedRepositories = maxWatchedRepositories;
    }

    public PublishInputFingerprint Capture(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new PublishInputFingerprint("missing", RequiresShortFallback: true);

        string root;
        try { root = Path.GetFullPath(repoRoot); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "PublishInputChangeTracker: invalid repository path");
            return new PublishInputFingerprint("invalid", RequiresShortFallback: true);
        }

        using var lease = Acquire(root);
        var repository = lease.Repository;
        var before = repository.Snapshot();
        var reliable = before.Reliable;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, root);
        AppendWatcherState(hash, before);

        reliable &= AppendFile(hash, root, Path.Combine(root, "package.json"));
        reliable &= AppendFile(hash, root, Path.Combine(root, ".agent-studio", "project.yml"));
        reliable &= AppendWorkflowDirectory(hash, root);

        // If an event arrived while the direct fingerprint was being built,
        // include its new generation so this capture cannot look identical to
        // the preceding stable one.
        var after = repository.Snapshot();
        reliable &= after.Reliable;
        AppendWatcherState(hash, after);
        return new PublishInputFingerprint(
            Convert.ToHexString(hash.GetHashAndReset()),
            RequiresShortFallback: !reliable);
    }

    public void Dispose()
    {
        WatchEntry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _repositories.Values.ToArray();
            _repositories.Clear();
            _lru.Clear();
        }

        foreach (var entry in entries)
            entry.Repository.Dispose();
    }

    internal void SimulateWatcherErrorForTests(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        lock (_gate)
        {
            if (_repositories.TryGetValue(root, out var entry))
                entry.Repository.MarkUnreliable();
        }
    }

    private WatchLease Acquire(string root)
    {
        List<WatchedRepository>? retired = null;
        WatchEntry entry;
        lock (_gate)
        {
            if (_disposed)
                return new WatchLease(null, WatchedRepository.Unreliable(root));

            _repositories.TryGetValue(root, out var existingEntry);
            if (existingEntry is not null
                && existingEntry.ActiveLeases == 0
                && !existingEntry.Repository.Snapshot().Reliable)
            {
                _repositories.Remove(root);
                _lru.Remove(existingEntry.Node);
                retired = [existingEntry.Repository];
                existingEntry = null;
            }

            if (existingEntry is null)
            {
                var repository = new WatchedRepository(
                    root,
                    ++_nextWatcherEpoch,
                    IsRelevantPath);
                entry = new WatchEntry(repository, _lru.AddLast(root));
                _repositories[root] = entry;
            }
            else
            {
                entry = existingEntry;
                _lru.Remove(entry.Node);
                _lru.AddLast(entry.Node);
            }

            entry.ActiveLeases++;
            var evicted = TrimUnderLock();
            if (evicted is not null)
            {
                retired ??= [];
                retired.AddRange(evicted);
            }
        }

        if (retired is not null)
        {
            foreach (var removed in retired) removed.Dispose();
        }
        return new WatchLease(this, entry.Repository);
    }

    private void Release(string root)
    {
        List<WatchedRepository>? evicted;
        lock (_gate)
        {
            if (_repositories.TryGetValue(root, out var entry) && entry.ActiveLeases > 0)
                entry.ActiveLeases--;
            evicted = TrimUnderLock();
        }
        if (evicted is not null)
        {
            foreach (var removed in evicted) removed.Dispose();
        }
    }

    private List<WatchedRepository>? TrimUnderLock()
    {
        List<WatchedRepository>? evicted = null;
        while (_repositories.Count > _maxWatchedRepositories)
        {
            var candidate = _lru.First;
            while (candidate is not null
                   && _repositories[candidate.Value].ActiveLeases > 0)
            {
                candidate = candidate.Next;
            }
            if (candidate is null) break;

            _lru.Remove(candidate);
            var entry = _repositories[candidate.Value];
            _repositories.Remove(candidate.Value);
            evicted ??= [];
            evicted.Add(entry.Repository);
        }
        return evicted;
    }

    private static bool IsRelevantPath(string root, string fullPath, bool structuralChange)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "PublishInputChangeTracker: invalid change path");
            return true;
        }

        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        if (segments.Take(segments.Length - 1).Any(IgnoredDirectoryNames.Contains))
            return false;

        var fileName = segments[^1];
        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return true;

        if (segments.Length == 3
            && segments[0].Equals(".github", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("workflows", StringComparison.OrdinalIgnoreCase)
            && (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // FileSystemWatcher reports a directory rename/delete as one structural
        // event, not necessarily as events for every nested manifest. Event args
        // do not portably identify files vs directories after deletion, so all
        // non-ignored structural events conservatively invalidate. Idle polling
        // remains O(1); only actual filesystem churn advances the generation.
        return structuralChange;
    }

    private static bool AppendWorkflowDirectory(IncrementalHash hash, string root)
    {
        var directory = Path.Combine(root, ".github", "workflows");
        Append(hash, ".github/workflows");
        if (!Directory.Exists(directory))
        {
            Append(hash, "missing");
            return true;
        }

        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            var reliable = true;
            foreach (var file in files)
                reliable &= AppendFile(hash, root, file);
            return reliable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "unreadable");
            SilentCatch.Note(ex, "PublishInputChangeTracker: workflow enumeration failed");
            return false;
        }
    }

    private static bool AppendFile(IncrementalHash hash, string root, string path)
    {
        Append(hash, Path.GetRelativePath(root, path).Replace('\\', '/'));
        try
        {
            if (!File.Exists(path))
            {
                Append(hash, "missing");
                return true;
            }

            var info = new FileInfo(path);
            Append(hash, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "unreadable");
            SilentCatch.Note(ex, "PublishInputChangeTracker: input read failed");
            return false;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void AppendWatcherState(IncrementalHash hash, WatcherSnapshot snapshot)
        => Append(
            hash,
            $"{snapshot.Epoch}:{snapshot.Version}:{(snapshot.Reliable ? 1 : 0)}");

    private sealed class WatchedRepository : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;
        private readonly object _stateLock = new();
        private readonly long _epoch;
        private long _version;
        private bool _reliable;

        public WatchedRepository(
            string root,
            long epoch,
            Func<string, string, bool, bool> isRelevant)
        {
            Root = root;
            _epoch = epoch;
            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 32 * 1024,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                    EnableRaisingEvents = false,
                };
                _watcher.Changed += (_, e) => Changed(e.FullPath, structuralChange: false, isRelevant);
                _watcher.Created += (_, e) => Changed(e.FullPath, structuralChange: true, isRelevant);
                _watcher.Deleted += (_, e) => Changed(e.FullPath, structuralChange: true, isRelevant);
                _watcher.Renamed += (_, e) =>
                {
                    Changed(e.OldFullPath, structuralChange: true, isRelevant);
                    Changed(e.FullPath, structuralChange: true, isRelevant);
                };
                _watcher.Error += (_, _) => MarkUnreliable();
                _reliable = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SilentCatch.Note(ex, "PublishInputChangeTracker: watcher creation failed");
                _reliable = false;
            }
        }

        private WatchedRepository(string root)
        {
            Root = root;
            _epoch = -1;
            _reliable = false;
        }

        public string Root { get; }

        public WatcherSnapshot Snapshot()
        {
            lock (_stateLock)
                return new WatcherSnapshot(_epoch, _version, _reliable);
        }

        public static WatchedRepository Unreliable(string root) => new(root);

        public void Dispose()
        {
            MarkUnreliable();
            _watcher?.Dispose();
        }

        public void MarkUnreliable()
        {
            lock (_stateLock)
            {
                _reliable = false;
                _version++;
            }
        }

        private void Changed(
            string path,
            bool structuralChange,
            Func<string, string, bool, bool> isRelevant)
        {
            if (isRelevant(Root, path, structuralChange))
            {
                lock (_stateLock) _version++;
            }
        }
    }

    private sealed class WatchEntry(
        WatchedRepository repository,
        LinkedListNode<string> node)
    {
        public WatchedRepository Repository { get; } = repository;
        public LinkedListNode<string> Node { get; } = node;
        public int ActiveLeases { get; set; }
    }

    private sealed class WatchLease : IDisposable
    {
        private PublishInputChangeTracker? _owner;
        private readonly bool _disposeRepository;
        private int _disposed;

        public WatchLease(PublishInputChangeTracker? owner, WatchedRepository repository)
        {
            _owner = owner;
            _disposeRepository = owner is null;
            Repository = repository;
        }

        public WatchedRepository Repository { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                if (_disposeRepository) Repository.Dispose();
                return;
            }
            current.Release(Repository.Root);
        }
    }
}

internal readonly record struct WatcherSnapshot(
    long Epoch,
    long Version,
    bool Reliable);

internal readonly record struct PublishInputFingerprint(
    string Value,
    bool RequiresShortFallback);
