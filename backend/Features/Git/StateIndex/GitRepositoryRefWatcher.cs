namespace AgentStudio.Git;

/// <summary>
/// Turns filesystem activity in a repository's git metadata into index
/// triggers. Only the paths that can change a board answer are watched -
/// <c>HEAD</c>, <c>packed-refs</c>, <c>refs/</c>, and linked-worktree HEADs -
/// never <c>objects/</c>, which every fetch rewrites in bulk and which cannot
/// move a ref on its own.
/// </summary>
internal sealed class GitRepositoryRefWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ILogger _logger;

    private GitRepositoryRefWatcher(ILogger logger) => _logger = logger;

    /// <summary>
    /// Watches one repository, or returns null when its git metadata cannot be
    /// resolved (not a checkout, or an unreadable path). A repository without a
    /// watcher still refreshes through the periodic sweep.
    /// </summary>
    internal static GitRepositoryRefWatcher? TryCreate(
        string repositoryRoot,
        Action<string> onChanged,
        ILogger logger)
    {
        var common = ReadOnlyGitRefFingerprint.ResolveCommonDirectory(repositoryRoot);
        if (common is null || !Directory.Exists(common)) return null;

        var watcher = new GitRepositoryRefWatcher(logger);
        try
        {
            // HEAD and packed-refs live directly in the common directory. Git
            // rewrites both atomically (write temp + rename), so Created and
            // Renamed matter as much as Changed.
            watcher.Add(common, recursive: false, name =>
                string.Equals(name, "HEAD", StringComparison.Ordinal)
                || string.Equals(name, "packed-refs", StringComparison.Ordinal),
                onChanged, "head");

            watcher.Add(Path.Combine(common, "refs"), recursive: true, _ => true, onChanged, "refs");

            // Linked worktrees keep their own HEAD under worktrees/<name>/HEAD.
            // A task worktree switching branches is board-visible state.
            watcher.Add(Path.Combine(common, "worktrees"), recursive: true, name =>
                string.Equals(name, "HEAD", StringComparison.Ordinal),
                onChanged, "worktree-head");

            // Some layouts store refs in a reftable rather than loose files.
            watcher.Add(Path.Combine(common, "reftable"), recursive: false, _ => true, onChanged, "reftable");

            if (watcher._watchers.Count == 0)
            {
                watcher.Dispose();
                return null;
            }
            return watcher;
        }
        catch (Exception ex)
        {
            watcher.Dispose();
            logger.LogWarning(
                ex,
                "git-index watcher could not attach to {Repository}; falling back to the periodic sweep",
                repositoryRoot);
            return null;
        }
    }

    private void Add(
        string directory,
        bool recursive,
        Func<string, bool> nameMatches,
        Action<string> onChanged,
        string trigger)
    {
        if (!Directory.Exists(directory)) return;
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                // A ref burst is coalesced by the index debounce, not here. The
                // buffer only has to survive until the events are read.
                InternalBufferSize = 64 * 1024,
            };

            void Handle(string name)
            {
                if (!nameMatches(name)) return;
                try
                {
                    onChanged(trigger);
                }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "GitRepositoryRefWatcher: trigger dispatch failed");
                }
            }

            watcher.Changed += (_, e) => Handle(e.Name is null ? "" : Path.GetFileName(e.Name));
            watcher.Created += (_, e) => Handle(e.Name is null ? "" : Path.GetFileName(e.Name));
            watcher.Deleted += (_, e) => Handle(e.Name is null ? "" : Path.GetFileName(e.Name));
            watcher.Renamed += (_, e) => Handle(e.Name is null ? "" : Path.GetFileName(e.Name));
            // A dropped-event buffer overflow is exactly the case the sweep is
            // for; signal unconditionally rather than guessing what was lost.
            watcher.Error += (_, _) =>
            {
                try { onChanged(trigger + "-overflow"); }
                catch (Exception ex) { SilentCatch.Note(ex, "GitRepositoryRefWatcher: overflow dispatch failed"); }
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            _logger.LogDebug(ex, "git-index watcher could not watch {Directory}", directory);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "GitRepositoryRefWatcher: dispose failed");
            }
        }
        _watchers.Clear();
    }
}
