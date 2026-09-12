using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Deterministic lockfile decision shared by disposable build gates and Remote
/// Review workspaces. A successful install stamps the current digest beside the
/// dependency directory; a different digest always forces a new install.
/// </summary>
public static class DependencyPreparationState
{
    public const string MarkerFileName = ".nm-state";
    public const string DependencyDirectoryName = "node_modules";

    public static ReviewDependencyCacheEvidenceDto Evaluate(
        string installRoot,
        ReviewDependencyScopeDto scope,
        bool installRan = false)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return Evidence(scope, "miss", "install-root-missing", "", [], installRan);

        var present = (scope.Lockfiles ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => File.Exists(Path.Combine(installRoot, name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (present.Length == 0)
            return Evidence(scope, "miss", "no-lockfile", "", [], installRan);

        var hash = ComputeLockHash(installRoot, present);
        var dependencyDirectory = Path.Combine(installRoot, DependencyDirectoryName);
        if (!Directory.Exists(dependencyDirectory))
            return Evidence(scope, "miss", "deps-dir-missing", hash, present, installRan);

        var marker = Path.Combine(installRoot, MarkerFileName);
        if (!File.Exists(marker))
            return Evidence(scope, "miss", "marker-missing", hash, present, installRan);

        string stamped;
        try
        {
            stamped = File.ReadAllText(marker).Trim();
        }
        catch
        {
            return Evidence(scope, "miss", "marker-unreadable", hash, present, installRan);
        }

        return string.Equals(stamped, hash, StringComparison.Ordinal)
            ? Evidence(scope, "hit", "lock-unchanged", hash, present, installRan)
            : Evidence(scope, "miss", "lock-changed", hash, present, installRan);
    }

    public static void Stamp(string installRoot, string lockHash)
    {
        if (string.IsNullOrWhiteSpace(installRoot)
            || string.IsNullOrWhiteSpace(lockHash)
            || !Directory.Exists(installRoot))
            return;
        File.WriteAllText(Path.Combine(installRoot, MarkerFileName), lockHash);
    }

    public static string ComputeLockHash(
        string installRoot,
        IReadOnlyList<string> presentLockNames)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        foreach (var name in presentLockNames.OrderBy(value => value, StringComparer.Ordinal))
        {
            var header = Encoding.UTF8.GetBytes(name + "\0");
            stream.Write(header);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(installRoot, name));
            }
            catch
            {
                bytes = [];
            }
            stream.Write(bytes);
            stream.WriteByte(0);
        }
        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static ReviewDependencyCacheEvidenceDto Evidence(
        ReviewDependencyScopeDto scope,
        string state,
        string reason,
        string lockHash,
        IReadOnlyList<string> lockfiles,
        bool installRan)
        => new(
            string.IsNullOrWhiteSpace(scope.WorkingSubdir) ? "." : scope.WorkingSubdir,
            state,
            reason,
            lockHash,
            lockfiles,
            installRan);
}

/// <summary>
/// Shared dependency-cache transfer protocol for disposable exact-subject
/// workspaces. Content is mirrored by repository-relative path, while candidate
/// and baseline roles receive separate namespaces so build outputs never cross
/// the comparison boundary.
/// </summary>
public sealed class DependencyCacheSession
{
    public const string IntegrityMarkerFileName = ".gate-cache-valid";

    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly IReadOnlyList<ReviewDependencyScopeDto> _scopes;
    private readonly IReadOnlyList<string> _preserveGlobs;
    private readonly Action<string>? _log;
    private readonly Func<string, string> _stagingRootFactory;

    public string CacheKey => Path.GetFileName(_cacheRoot);
    public bool Restored { get; private set; }
    public bool Evicted { get; private set; }
    public string? EvictionReason { get; private set; }
    public bool SavedVerified { get; private set; }
    public long RestoredSizeBytes { get; private set; }
    public TimeSpan? RestoredAge { get; private set; }

    private DependencyCacheSession(
        string workspace,
        string cacheRoot,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        IReadOnlyList<string> preserveGlobs,
        Action<string>? log,
        Func<string, string> stagingRootFactory)
    {
        _workspace = workspace;
        _cacheRoot = cacheRoot;
        _scopes = scopes;
        _preserveGlobs = preserveGlobs;
        _log = log;
        _stagingRootFactory = stagingRootFactory;
    }

    public static DependencyCacheSession Create(
        string cacheParent,
        string repositoryIdentity,
        string workspace,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        IReadOnlyList<string>? preserveGlobs = null,
        string? role = null,
        Action<string>? log = null)
        => new(
            workspace,
            CachePath(cacheParent, repositoryIdentity, role),
            NormalizeScopes(scopes),
            NormalizeGlobs(preserveGlobs),
            log,
            NewStagingRoot);

    internal static DependencyCacheSession Create(
        string cacheParent,
        string repositoryIdentity,
        string workspace,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        Func<string, string> stagingRootFactory)
        => new(
            workspace,
            CachePath(cacheParent, repositoryIdentity),
            NormalizeScopes(scopes),
            [],
            null,
            stagingRootFactory);

    public static string CachePath(
        string cacheParent,
        string repositoryIdentity,
        string? role = null)
    {
        var root = Path.Combine(cacheParent, RepositoryKey(repositoryIdentity));
        return string.IsNullOrWhiteSpace(role)
            ? root
            : Path.Combine(root, SafeSegment(role));
    }

    public IReadOnlyList<string> Restore() => Transfer(restore: true);

    /// <summary>
    /// Restores only a cache entry committed by <see cref="SaveVerified"/> and
    /// whose per-scope install marker still matches the lockfiles in the new
    /// workspace. Invalid entries are removed before any dependency directory
    /// can enter the workspace.
    /// </summary>
    public IReadOnlyList<string> RestoreVerified()
    {
        var messages = new List<string>();
        var contentRoot = Path.Combine(_cacheRoot, "content");
        if (!Directory.Exists(contentRoot))
            return Transfer(restore: true);

        var integrityMarker = Path.Combine(contentRoot, IntegrityMarkerFileName);
        RestoredSizeBytes = DirectorySize(contentRoot);
        var ageSource = File.Exists(integrityMarker) ? integrityMarker : contentRoot;
        RestoredAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(ageSource);
        if (!File.Exists(integrityMarker))
            return RefuseAndDiscard("integrity-marker-missing", ".", messages);

        foreach (var scope in _scopes)
        {
            if (scope.Lockfiles.Count == 0) continue;
            var installRoot = ResolveWithin(_workspace, scope.WorkingSubdir);
            var present = scope.Lockfiles
                .Where(name => File.Exists(Path.Combine(installRoot, name)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (present.Length == 0)
                return RefuseAndDiscard("no-lockfile", DisplayScope(scope.WorkingSubdir), messages);

            var expected = DependencyPreparationState.ComputeLockHash(installRoot, present);
            var cachedMarker = ResolveWithin(
                contentRoot,
                Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName));
            if (!File.Exists(cachedMarker))
                return RefuseAndDiscard("marker-missing", DisplayScope(scope.WorkingSubdir), messages);

            string actual;
            try
            {
                actual = File.ReadAllText(cachedMarker).Trim();
            }
            catch
            {
                return RefuseAndDiscard("marker-unreadable", DisplayScope(scope.WorkingSubdir), messages);
            }
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                return RefuseAndDiscard("lock-changed", DisplayScope(scope.WorkingSubdir), messages);
        }

        var restored = Transfer(restore: true);
        Restored = restored.Any(message =>
            message.Contains(" state=moved", StringComparison.Ordinal)
            && !message.Contains(DependencyPreparationState.MarkerFileName, StringComparison.Ordinal));
        return restored;
    }

    /// <summary>
    /// Stages the workspace's cacheable content into a temporary sibling of the
    /// live cache entry, then <see cref="Directory.Move"/>s (renames) that
    /// sibling onto the entry in one step. A failure while staging discards the
    /// sibling and leaves the previous entry untouched - a save can never leave
    /// a half-moved tree as the entry an unrelated lock-hash hit would later
    /// trust.
    /// </summary>
    public IReadOnlyList<string> Save() => SaveCore(verified: false);

    /// <summary>
    /// Commits an entry with a positive marker after the caller has observed a
    /// green install and verification run. Gate restore never trusts entries
    /// produced by any other path.
    /// </summary>
    public IReadOnlyList<string> SaveVerified() => SaveCore(verified: true);

    private IReadOnlyList<string> SaveCore(bool verified)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        var contentRoot = Path.Combine(_cacheRoot, "content");
        var stagingRoot = _stagingRootFactory(_cacheRoot);

        var movedAny = false;
        var failed = false;
        foreach (var relative in CacheDirectories(_workspace))
        {
            var outcome = MoveDirectory(
                ResolveWithin(_workspace, relative),
                ResolveWithin(stagingRoot, relative),
                "save",
                relative,
                messages);
            movedAny |= outcome == MoveOutcome.Moved;
            failed |= outcome == MoveOutcome.Failed;
        }

        foreach (var scope in _scopes)
        {
            var marker = Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName);
            var outcome = MoveFile(
                ResolveWithin(_workspace, marker),
                ResolveWithin(stagingRoot, marker),
                "save",
                marker,
                messages);
            movedAny |= outcome == MoveOutcome.Moved;
            failed |= outcome == MoveOutcome.Failed;
        }

        if (verified && movedAny && !failed)
        {
            try
            {
                Directory.CreateDirectory(stagingRoot);
                File.WriteAllText(
                    Path.Combine(stagingRoot, IntegrityMarkerFileName),
                    $"version=1\nverifiedAtUtc={DateTimeOffset.UtcNow:O}\n");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed = true;
                messages.Add(
                    $"dependency-cache save item={IntegrityMarkerFileName} state=failed " +
                    $"reason={exception.GetType().Name}");
            }
        }

        stopwatch.Stop();
        if (failed || !movedAny)
        {
            DeleteBestEffort(stagingRoot);
            var summary = failed
                ? $"dependency-cache save repository={Path.GetFileName(_cacheRoot)} state=aborted " +
                  $"reason=partial-move durationMs={stopwatch.ElapsedMilliseconds}"
                : $"dependency-cache save repository={Path.GetFileName(_cacheRoot)} state=noop " +
                  $"reason=nothing-to-save durationMs={stopwatch.ElapsedMilliseconds}";
            messages.Add(summary);
            _log?.Invoke(summary);
            return messages;
        }

        try
        {
            Directory.CreateDirectory(_cacheRoot);
            if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
            Directory.Move(stagingRoot, contentRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteBestEffort(stagingRoot);
            var failure =
                $"dependency-cache save repository={Path.GetFileName(_cacheRoot)} state=failed " +
                $"reason={exception.GetType().Name} durationMs={stopwatch.ElapsedMilliseconds}";
            messages.Add(failure);
            _log?.Invoke(failure);
            return messages;
        }

        var committed =
            $"dependency-cache save repository={Path.GetFileName(_cacheRoot)} scopes={_scopes.Count} " +
            $"state=committed durationMs={stopwatch.ElapsedMilliseconds}";
        messages.Add(committed);
        _log?.Invoke(committed);
        SavedVerified = verified;
        return messages;
    }

    /// <summary>
    /// Drops the entry for this repository entirely, e.g. after a gate run
    /// classified its node_modules as poisoned by a toolchain crash (CAC-18).
    /// The next Restore() is then a deterministic miss that reinstalls.
    /// </summary>
    public IReadOnlyList<string> Discard(string reason)
    {
        Evicted = true;
        EvictionReason = reason;
        var contentRoot = Path.Combine(_cacheRoot, "content");
        if (!Directory.Exists(contentRoot))
        {
            var noop =
                $"dependency-cache evicted repository={Path.GetFileName(_cacheRoot)} reason={reason} state=noop-no-entry";
            _log?.Invoke(noop);
            return [noop];
        }

        try
        {
            Directory.Delete(contentRoot, recursive: true);
            var message = $"dependency-cache evicted repository={Path.GetFileName(_cacheRoot)} reason={reason}";
            _log?.Invoke(message);
            return [message];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache evict repository={Path.GetFileName(_cacheRoot)} state=failed " +
                $"reason={exception.GetType().Name}";
            _log?.Invoke(message);
            return [message];
        }
    }

    /// <summary>
    /// Removes cacheable content that was already moved into the disposable
    /// workspace, then removes any still-live cache entry. This is the clean
    /// boundary used before a same-run retry.
    /// </summary>
    public IReadOnlyList<string> DiscardIncludingWorkspace(string reason)
    {
        var messages = new List<string>();
        foreach (var relative in CacheDirectories(_workspace))
        {
            var path = ResolveWithin(_workspace, relative);
            try
            {
                Directory.Delete(path, recursive: true);
                messages.Add($"dependency-cache evicted item={relative} state=workspace-removed reason={reason}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add(
                    $"dependency-cache evict item={relative} state=failed " +
                    $"reason={exception.GetType().Name}");
            }
        }
        foreach (var scope in _scopes)
        {
            var marker = ResolveWithin(
                _workspace,
                Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName));
            try
            {
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add(
                    $"dependency-cache evict item={DisplayScope(scope.WorkingSubdir)}/{DependencyPreparationState.MarkerFileName} " +
                    $"state=failed reason={exception.GetType().Name}");
            }
        }
        messages.AddRange(Discard(reason));
        return messages;
    }

    private IReadOnlyList<string> RefuseAndDiscard(
        string reason,
        string scope,
        ICollection<string> messages)
    {
        var refused =
            $"dependency-cache restore refused repository={CacheKey} scope={scope} reason={reason}";
        messages.Add(refused);
        _log?.Invoke(refused);
        foreach (var message in Discard("restore-integrity-" + reason)) messages.Add(message);
        return messages.ToArray();
    }

    private static string DisplayScope(string workingSubdir)
        => string.IsNullOrWhiteSpace(workingSubdir) ? "." : workingSubdir;

    private static long DirectorySize(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                            pending.Push(directory);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private static void DeleteBestEffort(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of an aborted staging attempt; the live
            // content root was never touched, so the previous entry survives.
        }
    }

    private IReadOnlyList<string> Transfer(bool restore)
    {
        var operation = restore ? "restore" : "save";
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        var contentRoot = Path.Combine(_cacheRoot, "content");
        var sourceRoot = restore ? contentRoot : _workspace;
        var destinationRoot = restore ? _workspace : contentRoot;

        foreach (var relative in CacheDirectories(sourceRoot))
        {
            MoveDirectory(
                ResolveWithin(sourceRoot, relative),
                ResolveWithin(destinationRoot, relative),
                operation,
                relative,
                messages);
        }

        foreach (var scope in _scopes)
        {
            var marker = Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName);
            MoveFile(
                ResolveWithin(sourceRoot, marker),
                ResolveWithin(destinationRoot, marker),
                operation,
                marker,
                messages);
        }

        stopwatch.Stop();
        var summary =
            $"dependency-cache {operation} repository={Path.GetFileName(_cacheRoot)} " +
            $"scopes={_scopes.Count} durationMs={stopwatch.ElapsedMilliseconds}";
        messages.Add(summary);
        _log?.Invoke(summary);
        return messages;
    }

    private IReadOnlyList<string> CacheDirectories(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) return [];
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in _scopes)
        {
            candidates.Add(Combine(
                scope.WorkingSubdir,
                DependencyPreparationState.DependencyDirectoryName));
            candidates.Add(Combine(scope.WorkingSubdir, ".angular"));
        }

        foreach (var pattern in _preserveGlobs)
        {
            if (!HasWildcard(pattern))
            {
                candidates.Add(pattern);
                continue;
            }
            foreach (var relative in EnumerateDirectories(sourceRoot))
            {
                if (MatchesGlob(pattern, relative)) candidates.Add(relative);
            }
        }

        return candidates
            .Where(relative => Directory.Exists(ResolveWithin(sourceRoot, relative)))
            .OrderBy(relative => relative.Count(ch => ch == '/'))
            .ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .Aggregate(
                new List<string>(),
                (selected, relative) =>
                {
                    if (!selected.Any(parent => IsSameOrChild(relative, parent)))
                        selected.Add(relative);
                    return selected;
                });
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                yield return relative;
                var name = Path.GetFileName(child);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch
                {
                    continue;
                }
                if (name is ".git" or "node_modules"
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                pending.Push(child);
            }
        }
    }

    private enum MoveOutcome { Skipped, Moved, Failed }

    private MoveOutcome MoveDirectory(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return MoveOutcome.Skipped;
        try
        {
            if (Directory.Exists(destination))
            {
                if (operation == "restore")
                {
                    messages.Add($"dependency-cache restore skipped item={relative} reason=destination-exists");
                    return MoveOutcome.Skipped;
                }
                Directory.Delete(destination, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
            return MoveOutcome.Moved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}";
            messages.Add(message);
            _log?.Invoke(message);
            return MoveOutcome.Failed;
        }
    }

    private MoveOutcome MoveFile(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!File.Exists(source)) return MoveOutcome.Skipped;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: operation == "save");
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
            return MoveOutcome.Moved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}";
            messages.Add(message);
            _log?.Invoke(message);
            return MoveOutcome.Failed;
        }
    }

    private static IReadOnlyList<ReviewDependencyScopeDto> NormalizeScopes(
        IReadOnlyList<ReviewDependencyScopeDto>? scopes)
        => (scopes ?? [])
            .Select(scope => new ReviewDependencyScopeDto(
                NormalizeRelative(scope.WorkingSubdir, allowEmpty: true) ?? "",
                scope.Lockfiles
                    .Select(lockfile => NormalizeRelative(lockfile, allowEmpty: false))
                    .Where(lockfile => lockfile is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(lockfile => lockfile, StringComparer.Ordinal)
                    .ToArray()))
            .DistinctBy(scope => scope.WorkingSubdir, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> NormalizeGlobs(IReadOnlyList<string>? globs)
        => (globs ?? [])
            .Select(glob => NormalizeRelative(glob, allowEmpty: false, allowWildcards: true))
            .Where(glob => glob is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(glob => glob, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeRelative(
        string? value,
        bool allowEmpty,
        bool allowWildcards = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return allowEmpty ? "" : null;
        var normalized = value.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || (!allowWildcards && normalized.IndexOfAny(['*', '?']) >= 0))
            return null;
        return normalized;
    }

    private static string ResolveWithin(string root, string relative)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = string.IsNullOrEmpty(relative)
            ? canonicalRoot
            : Path.GetFullPath(Path.Combine(
                canonicalRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dependency cache path escaped its configured root.");
        return candidate;
    }

    private static string RepositoryKey(string identity)
    {
        var canonical = identity.Trim();
        if (Path.IsPathFullyQualified(canonical))
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonical));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static bool MatchesGlob(string pattern, string path)
        => MatchesSegments(
            pattern.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0,
            path.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0);

    private static bool MatchesSegments(
        IReadOnlyList<string> pattern,
        int patternIndex,
        IReadOnlyList<string> path,
        int pathIndex)
    {
        if (patternIndex == pattern.Count) return pathIndex == path.Count;
        if (pattern[patternIndex] == "**")
        {
            return MatchesSegments(pattern, patternIndex + 1, path, pathIndex)
                   || (pathIndex < path.Count
                       && MatchesSegments(pattern, patternIndex, path, pathIndex + 1));
        }
        return pathIndex < path.Count
               && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                   pattern[patternIndex],
                   path[pathIndex],
                   ignoreCase: OperatingSystem.IsWindows())
               && MatchesSegments(pattern, patternIndex + 1, path, pathIndex + 1);
    }

    private static bool IsSameOrChild(string candidate, string parent)
        => candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    private static string NewStagingRoot(string cacheRoot)
        => Path.Combine(cacheRoot, "content.saving-" + Guid.NewGuid().ToString("N"));

    private static string Combine(string left, string right)
        => string.IsNullOrWhiteSpace(left) ? right : left.TrimEnd('/') + "/" + right;

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
