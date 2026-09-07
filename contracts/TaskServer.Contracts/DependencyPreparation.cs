using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Deterministic lockfile decision shared by disposable build gates and Remote
/// Review workspaces. A successful install stamps the current digest beside the
/// dependency directory; a different digest always forces a new install.
/// <para>
/// AGT-2720: the stamp alone is not proof that a dependency tree is usable. The
/// coding-agent-chat cache entry kept a valid <c>.nm-state</c> next to a tree of
/// 2,580 files instead of 25,748, with an empty <c>vite/dist/client/</c> and no
/// <c>node_modules/.package-lock.json</c>. Every gate from 10.08. on read
/// <c>hit / lock-unchanged</c>, skipped <c>npm ci</c>, and died inside vite
/// before the first test, while the Linux review host installed fresh and passed
/// 412 times. An entry is therefore valid only when the marker states that an
/// install actually completed AND the installer's own completion file is present;
/// anything else is a miss that reinstalls instead of a hit that cannot work.
/// </para>
/// </summary>
public static class DependencyPreparationState
{
    public const string MarkerFileName = ".nm-state";
    public const string DependencyDirectoryName = "node_modules";

    /// <summary>
    /// Written by npm inside <c>node_modules</c> at the end of a successful
    /// install. Its absence next to a populated tree means the install was
    /// interrupted or the tree was copied incompletely.
    /// </summary>
    public const string NpmInstallCompleteFileName = ".package-lock.json";

    private const string LockField = "lock";
    private const string InstallCompleteField = "install-complete";

    /// <summary>Lockfile names whose scope is installed by npm.</summary>
    private static readonly string[] NpmLockfiles = ["package-lock.json", "npm-shrinkwrap.json"];

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

        if (IsNpmScope(present)
            && !File.Exists(Path.Combine(dependencyDirectory, NpmInstallCompleteFileName)))
        {
            return Evidence(scope, "miss", "deps-incomplete", hash, present, installRan);
        }

        var marker = Path.Combine(installRoot, MarkerFileName);
        if (!File.Exists(marker))
            return Evidence(scope, "miss", "marker-missing", hash, present, installRan);

        string content;
        try
        {
            content = File.ReadAllText(marker);
        }
        catch
        {
            return Evidence(scope, "miss", "marker-unreadable", hash, present, installRan);
        }

        var stamped = ReadMarker(content);
        if (!stamped.InstallComplete)
            return Evidence(scope, "miss", "install-incomplete", hash, present, installRan);

        return string.Equals(stamped.LockHash, hash, StringComparison.Ordinal)
            ? Evidence(scope, "hit", "lock-unchanged", hash, present, installRan)
            : Evidence(scope, "miss", "lock-changed", hash, present, installRan);
    }

    /// <summary>
    /// Records that an install completed successfully for <paramref name="lockHash"/>.
    /// Callers must invoke this only after the install command exited zero; the
    /// completion claim is what <see cref="Evaluate"/> trusts. The marker is
    /// written through a temporary sibling so an interrupted stamp cannot leave a
    /// truncated file that reads as a completed install.
    /// </summary>
    public static void Stamp(string installRoot, string lockHash)
    {
        if (string.IsNullOrWhiteSpace(installRoot)
            || string.IsNullOrWhiteSpace(lockHash)
            || !Directory.Exists(installRoot))
            return;
        var marker = Path.Combine(installRoot, MarkerFileName);
        var staging = marker + ".writing";
        try
        {
            File.WriteAllText(
                staging,
                $"{LockField}={lockHash}{Environment.NewLine}{InstallCompleteField}=1{Environment.NewLine}");
            File.Move(staging, marker, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staging); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parses a marker. A legacy bare-hash marker carries no completion claim, so
    /// it is reported as incomplete and forces exactly one reinstall per scope.
    /// </summary>
    private static (string LockHash, bool InstallComplete) ReadMarker(string content)
    {
        var lockHash = "";
        var installComplete = false;
        foreach (var line in content.Split('\n'))
        {
            var field = line.Trim();
            if (field.Length == 0) continue;
            var separator = field.IndexOf('=');
            if (separator <= 0) continue;
            var key = field[..separator].Trim();
            var value = field[(separator + 1)..].Trim();
            if (string.Equals(key, LockField, StringComparison.Ordinal)) lockHash = value;
            else if (string.Equals(key, InstallCompleteField, StringComparison.Ordinal))
                installComplete = value is "1" or "true";
        }
        return (lockHash, installComplete);
    }

    private static bool IsNpmScope(IReadOnlyList<string> presentLockNames)
        => presentLockNames.Any(name => NpmLockfiles.Contains(name, StringComparer.OrdinalIgnoreCase));

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
/// <para>
/// AGT-2720: saving is transactional. The incoming tree is moved into a
/// temporary sibling of the entry and only then renamed onto it, so an
/// interrupted save leaves the previous entry intact instead of publishing a
/// half-moved tree as the entry. <see cref="Evict"/> drops a scope whose tree the
/// caller proved unusable, so a broken entry cannot survive into the retry.
/// </para>
/// </summary>
public sealed class DependencyCacheSession
{
    private const string StagingSuffix = ".incoming";
    private const string RetiredSuffix = ".retired";

    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly IReadOnlyList<ReviewDependencyScopeDto> _scopes;
    private readonly IReadOnlyList<string> _preserveGlobs;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _evicted = new(StringComparer.OrdinalIgnoreCase);

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
        DiscardStagingDebris();
        return Transfer(restore: true);
    }

    public IReadOnlyList<string> Save() => Transfer(restore: false);

    /// <summary>
    /// Drops the cached tree of the named scopes (all session scopes when
    /// <paramref name="workingSubdirs"/> is null) and stops the following
    /// <see cref="Save"/> from writing those scopes back. The caller uses this
    /// when the workspace copy proved unusable, so the next run installs fresh
    /// instead of restoring the same broken tree.
    /// </summary>
    public IReadOnlyList<string> Evict(string reason, IReadOnlyList<string>? workingSubdirs = null)
    {
        var normalized = reason.Trim().Length == 0 ? "unspecified" : reason.Trim();
        var targets = workingSubdirs is null
            ? _scopes.Select(scope => scope.WorkingSubdir).ToArray()
            : workingSubdirs
                .Select(subdir => NormalizeRelative(subdir, allowEmpty: true) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var contentRoot = Path.Combine(_cacheRoot, "content");
        var messages = new List<string>();
        foreach (var subdir in targets)
        {
            foreach (var item in EvictableItems(subdir)) _evicted.Add(item);
            foreach (var item in EvictableItems(subdir))
            {
                var cached = ResolveWithin(contentRoot, item);
                try
                {
                    if (Directory.Exists(cached)) Directory.Delete(cached, recursive: true);
                    else if (File.Exists(cached)) File.Delete(cached);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var failure =
                        $"dependency-cache evict item={item} state=failed " +
                        $"reason={exception.GetType().Name}";
                    messages.Add(failure);
                    _log?.Invoke(failure);
                }
            }

            var message =
                $"dependency-cache evicted scope={DisplayScope(subdir)} reason={normalized}";
            messages.Add(message);
            _log?.Invoke(message);
        }

        return messages;
    }

    private IEnumerable<string> EvictableItems(string workingSubdir)
    {
        yield return Combine(workingSubdir, DependencyPreparationState.DependencyDirectoryName);
        yield return Combine(workingSubdir, ".angular");
        yield return Combine(workingSubdir, DependencyPreparationState.MarkerFileName);
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
            if (!restore && _evicted.Contains(relative))
            {
                messages.Add($"dependency-cache save skipped item={relative} reason=evicted");
                continue;
            }

            if (restore)
            {
                RestoreDirectory(
                    ResolveWithin(sourceRoot, relative),
                    ResolveWithin(destinationRoot, relative),
                    relative,
                    messages);
            }
            else
            {
                SaveDirectory(
                    ResolveWithin(sourceRoot, relative),
                    ResolveWithin(destinationRoot, relative),
                    relative,
                    messages);
            }
        }

        foreach (var scope in _scopes)
        {
            var marker = Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName);
            if (!restore && _evicted.Contains(marker))
            {
                messages.Add($"dependency-cache save skipped item={marker} reason=evicted");
                continue;
            }
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

    /// <summary>
    /// Repairs the siblings an interrupted save left behind, before anything is
    /// restored. The retired tree is the last known-complete entry, so it is put
    /// back whenever the entry name is empty; a staging tree may have been only
    /// partially moved and is always discarded. The result is that an interrupted
    /// save leaves the previous entry intact.
    /// </summary>
    private void DiscardStagingDebris()
    {
        var contentRoot = Path.Combine(_cacheRoot, "content");
        if (!Directory.Exists(contentRoot)) return;
        var relatives = CacheDirectories(contentRoot)
            .Concat(_scopes.Select(scope => Combine(
                scope.WorkingSubdir,
                DependencyPreparationState.DependencyDirectoryName)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in relatives)
        {
            var entry = ResolveWithin(contentRoot, relative);
            var staging = entry + StagingSuffix;
            var retired = entry + RetiredSuffix;
            try
            {
                if (Directory.Exists(retired) && !Directory.Exists(entry))
                {
                    Directory.Move(retired, entry);
                    _log?.Invoke(
                        $"dependency-cache sweep item={relative} state=previous-entry-restored");
                }
                if (Directory.Exists(retired)) Directory.Delete(retired, recursive: true);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke(
                    $"dependency-cache sweep item={relative} state=failed " +
                    $"reason={exception.GetType().Name}");
            }
        }
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

    private void RestoreDirectory(
        string source,
        string destination,
        string relative,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return;
        try
        {
            if (Directory.Exists(destination))
            {
                messages.Add($"dependency-cache restore skipped item={relative} reason=destination-exists");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            messages.Add($"dependency-cache restore item={relative} state=moved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail("restore", relative, exception, messages);
        }
    }

    /// <summary>
    /// Publishes one directory into the cache entry transactionally: the workspace
    /// tree is moved into a staging sibling first, the previous entry is renamed
    /// aside, and only then does the staging tree take the entry name. A crash at
    /// any point leaves either the previous or the new complete tree under the
    /// entry name, never a partially moved one.
    /// </summary>
    private void SaveDirectory(
        string source,
        string destination,
        string relative,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return;
        var staging = destination + StagingSuffix;
        var retired = destination + RetiredSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(retired)) Directory.Delete(retired, recursive: true);

            Directory.Move(source, staging);
            var replaced = Directory.Exists(destination);
            if (replaced) Directory.Move(destination, retired);
            Directory.Move(staging, destination);
            if (replaced)
            {
                try { Directory.Delete(retired, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _log?.Invoke(
                        $"dependency-cache save item={relative} state=retired-leftover " +
                        $"reason={exception.GetType().Name}");
                }
            }
            messages.Add($"dependency-cache save item={relative} state=moved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestorePreviousEntry(destination, staging, retired);
            Fail("save", relative, exception, messages);
        }
    }

    /// <summary>
    /// Undoes a failed transactional save: the staging tree is dropped and the
    /// retired entry is put back when the rename onto the entry name did not
    /// complete.
    /// </summary>
    private static void RestorePreviousEntry(string destination, string staging, string retired)
    {
        try
        {
            if (Directory.Exists(retired) && !Directory.Exists(destination))
                Directory.Move(retired, destination);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The sweep on the next restore removes whatever survived here. The
            // entry name itself still holds a complete tree either way.
            SilentlyIgnore(exception);
        }
    }

    private static void SilentlyIgnore(Exception exception) => _ = exception;

    private void Fail(
        string operation,
        string relative,
        Exception exception,
        ICollection<string> messages)
    {
        var message =
            $"dependency-cache {operation} item={relative} state=failed " +
            $"reason={exception.GetType().Name}";
        messages.Add(message);
        _log?.Invoke(message);
    }

    private void MoveFile(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!File.Exists(source)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: operation == "save");
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}";
            messages.Add(message);
            _log?.Invoke(message);
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

    private static string DisplayScope(string workingSubdir)
        => string.IsNullOrWhiteSpace(workingSubdir) ? "." : workingSubdir;

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
