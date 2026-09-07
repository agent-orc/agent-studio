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

    /// <summary>
    /// npm writes this file as the last step of a successful <c>npm ci</c>. Its
    /// absence beside a populated <c>node_modules</c> means the tree was never
    /// installed completely, or was truncated after the install (AGT-2720): the
    /// CAC-18 entry carried a matching <see cref="MarkerFileName"/> over 2,580 of
    /// 25,748 files, so the hash alone could never detect the corruption.
    /// </summary>
    public const string InstallCompletionFileName = ".package-lock.json";

    private static readonly string[] NpmLockfileNames =
        ["package-lock.json", "npm-shrinkwrap.json"];

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

        // Checked before the hash marker on purpose: an entry that lost its
        // install-completion file is corrupt, and naming it `install-incomplete`
        // is the diagnosis the operator needs. `lock-changed` would be a lie.
        if (RequiresInstallCompletionFile(present)
            && !File.Exists(Path.Combine(dependencyDirectory, InstallCompletionFileName)))
            return Evidence(scope, "miss", "install-incomplete", hash, present, installRan);

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

    /// <summary>
    /// Only npm writes <see cref="InstallCompletionFileName"/>. A scope locked by
    /// a different package manager must not be declared incomplete for missing a
    /// file its toolchain never creates.
    /// </summary>
    private static bool RequiresInstallCompletionFile(IReadOnlyList<string> presentLockNames)
        => presentLockNames.Any(name => NpmLockfileNames.Contains(
            Path.GetFileName(name),
            StringComparer.OrdinalIgnoreCase));

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
    /// <summary>The only directory a restore ever reads. Written by rename only.</summary>
    private const string ContentDirectoryName = "content";
    private const string IncomingDirectoryName = "content.incoming";
    private const string RetiredDirectoryName = "content.retired";
    private const string TrashSuffix = ".trash-";

    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly IReadOnlyList<ReviewDependencyScopeDto> _scopes;
    private readonly IReadOnlyList<string> _preserveGlobs;
    private readonly Action<string>? _log;

    private DependencyCacheSession(
        string workspace,
        string cacheRoot,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        IReadOnlyList<string> preserveGlobs,
        Action<string>? log)
    {
        _workspace = workspace;
        _cacheRoot = cacheRoot;
        _scopes = scopes;
        _preserveGlobs = preserveGlobs;
        _log = log;
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
            log);

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

    public IReadOnlyList<string> Restore()
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        var contentRoot = Path.Combine(_cacheRoot, ContentDirectoryName);

        foreach (var relative in CacheDirectories(contentRoot))
        {
            MoveDirectory(
                ResolveWithin(contentRoot, relative),
                ResolveWithin(_workspace, relative),
                "restore",
                relative,
                messages);
        }

        foreach (var marker in MarkerPaths())
        {
            MoveFile(
                ResolveWithin(contentRoot, marker),
                ResolveWithin(_workspace, marker),
                "restore",
                marker,
                messages);
        }

        return Summarize("restore", messages, stopwatch);
    }

    /// <summary>
    /// Transactional save: everything moves into a temporary sibling first and
    /// the entry is swapped by rename only after every item transferred. The
    /// AGT-2720 root cause was the opposite order - the previous entry was
    /// deleted in place, a recursive delete died half-way, and the surviving
    /// stump kept a valid <c>.nm-state</c>. Every later gate then read a
    /// <c>lock-unchanged</c> hit on a truncated <c>node_modules</c> and died in
    /// vite before the first test. An interrupted save must leave the previous
    /// entry untouched instead.
    /// </summary>
    public IReadOnlyList<string> Save()
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        var contentRoot = Path.Combine(_cacheRoot, ContentDirectoryName);
        var incomingRoot = Path.Combine(_cacheRoot, IncomingDirectoryName);
        // Staging debris from an interrupted save is never an entry, so it is
        // discarded rather than merged into this one.
        PurgeDirectory(incomingRoot, "save", messages);
        SweepAbandonedTrash();

        var transferred = 0;
        var incomplete = false;
        foreach (var relative in CacheDirectories(_workspace))
        {
            if (MoveDirectory(
                    ResolveWithin(_workspace, relative),
                    ResolveWithin(incomingRoot, relative),
                    "save",
                    relative,
                    messages))
            {
                transferred++;
            }
            else
            {
                incomplete = true;
            }
        }

        foreach (var marker in MarkerPaths())
        {
            if (!MoveFile(
                    ResolveWithin(_workspace, marker),
                    ResolveWithin(incomingRoot, marker),
                    "save",
                    marker,
                    messages))
            {
                incomplete = true;
            }
        }

        if (incomplete || transferred == 0)
        {
            var reason = incomplete ? "incomplete-transfer" : "nothing-to-save";
            messages.Add($"dependency-cache save state=discarded reason={reason}");
            PurgeDirectory(incomingRoot, "save", messages);
            return Summarize("save", messages, stopwatch);
        }

        CarryOverUnsavedItems(contentRoot, incomingRoot, messages);
        Promote(contentRoot, incomingRoot, messages);
        return Summarize("save", messages, stopwatch);
    }

    /// <summary>
    /// Drops this repository's cache entry so the next gate reinstalls from the
    /// lockfile. Called when a gate died in its own toolchain: the entry is the
    /// prime suspect and a broken tree must never survive into the retry.
    /// </summary>
    public IReadOnlyList<string> Evict(string reason)
    {
        var messages = new List<string>();
        foreach (var name in new[] { ContentDirectoryName, IncomingDirectoryName, RetiredDirectoryName })
            PurgeDirectory(Path.Combine(_cacheRoot, name), "evict", messages);

        var summary =
            $"dependency-cache evicted repository={Path.GetFileName(_cacheRoot)} " +
            $"scopes={_scopes.Count} reason={Token(reason)}";
        messages.Add(summary);
        _log?.Invoke(summary);
        return messages;
    }

    /// <summary>
    /// Moves anything the previous entry still holds and this save did not
    /// replace into the staging tree, so swapping the whole entry never silently
    /// drops a scope that was not part of this run's plan.
    /// </summary>
    private void CarryOverUnsavedItems(
        string contentRoot,
        string incomingRoot,
        ICollection<string> messages)
    {
        if (!Directory.Exists(contentRoot)) return;
        foreach (var relative in CacheDirectories(contentRoot))
        {
            var destination = ResolveWithin(incomingRoot, relative);
            if (Directory.Exists(destination)) continue;
            MoveDirectory(
                ResolveWithin(contentRoot, relative),
                destination,
                "save",
                relative,
                messages);
        }

        foreach (var marker in MarkerPaths())
        {
            var destination = ResolveWithin(incomingRoot, marker);
            if (File.Exists(destination)) continue;
            MoveFile(ResolveWithin(contentRoot, marker), destination, "save", marker, messages);
        }
    }

    private void Promote(string contentRoot, string incomingRoot, ICollection<string> messages)
    {
        var retiredRoot = Path.Combine(_cacheRoot, RetiredDirectoryName);
        PurgeDirectory(retiredRoot, "save", messages);
        var retired = false;
        try
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Move(contentRoot, retiredRoot);
                retired = true;
            }
            Directory.CreateDirectory(_cacheRoot);
            Directory.Move(incomingRoot, contentRoot);
            messages.Add("dependency-cache save state=promoted");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The swap is the only window in which the entry can be missing.
            // Put the previous entry back rather than leaving no entry at all.
            if (retired && !Directory.Exists(contentRoot))
            {
                try { Directory.Move(retiredRoot, contentRoot); }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    Report($"dependency-cache save state=failed reason=entry-unrecoverable", messages);
                }
            }
            Report(
                $"dependency-cache save state=failed reason={exception.GetType().Name}",
                messages);
            return;
        }

        PurgeDirectory(retiredRoot, "save", messages);
    }

    private IReadOnlyList<string> MarkerPaths()
        => _scopes
            .Select(scope => Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName))
            .ToArray();

    private IReadOnlyList<string> Summarize(
        string operation,
        List<string> messages,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var summary =
            $"dependency-cache {operation} repository={Path.GetFileName(_cacheRoot)} " +
            $"scopes={_scopes.Count} durationMs={stopwatch.ElapsedMilliseconds}";
        messages.Add(summary);
        _log?.Invoke(summary);
        return messages;
    }

    /// <summary>
    /// Renames before deleting so a recursive delete that dies half-way leaves
    /// its debris under a name no restore ever reads.
    /// </summary>
    private void PurgeDirectory(string path, string operation, ICollection<string> messages)
    {
        if (!Directory.Exists(path)) return;
        var target = path + TrashSuffix + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(path, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            target = path;
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report(
                $"dependency-cache {operation} item={Path.GetFileName(path)} state=purge-failed " +
                $"reason={exception.GetType().Name}",
                messages);
        }
    }

    /// <summary>
    /// A rename-then-delete whose delete failed leaves a trash sibling behind.
    /// Retry it on the next save so the entry directory cannot grow without
    /// bound on a host where deletes intermittently fail.
    /// </summary>
    private void SweepAbandonedTrash()
    {
        if (!Directory.Exists(_cacheRoot)) return;
        IEnumerable<string> abandoned;
        try
        {
            abandoned = Directory.EnumerateDirectories(_cacheRoot, "*" + TrashSuffix + "*").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in abandoned)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still held; the next save tries again.
            }
        }
    }

    private void Report(string message, ICollection<string> messages)
    {
        messages.Add(message);
        _log?.Invoke(message);
    }

    private static string Token(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_' ? ch : '-')
            .ToArray())
            .Trim('-');
        return normalized.Length == 0 ? "unspecified" : normalized;
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

    /// <summary>
    /// Returns false only when an item that exists at the source did not reach
    /// the destination, so a save can tell a complete transfer from a partial one.
    /// </summary>
    private bool MoveDirectory(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return true;
        try
        {
            if (Directory.Exists(destination))
            {
                if (operation == "restore")
                {
                    messages.Add($"dependency-cache restore skipped item={relative} reason=destination-exists");
                    return true;
                }
                PurgeDirectory(destination, operation, messages);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report(
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}",
                messages);
            return false;
        }
    }

    private bool MoveFile(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!File.Exists(source)) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: operation == "save");
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report(
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}",
                messages);
            return false;
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

    private static string Combine(string left, string right)
        => string.IsNullOrWhiteSpace(left) ? right : left.TrimEnd('/') + "/" + right;

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
