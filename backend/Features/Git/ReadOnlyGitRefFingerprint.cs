using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// Captures the repository refs that drive board merge/publish projections
/// without spawning git. Supports normal checkouts and linked worktrees.
/// </summary>
internal static class ReadOnlyGitRefFingerprint
{
    /// <summary>
    /// Resolves the shared Git metadata directory without spawning Git. Linked
    /// worktrees return the same directory, which makes it a stable repository
    /// identity for process-admission gates.
    /// </summary>
    internal static string? ResolveCommonDirectory(string repoRoot)
        => ResolveMetadataDirectories(repoRoot)?.Common;

    public static string Capture(
        string repoRoot,
        IEnumerable<string> branchNames,
        bool includeTags = false)
        => CaptureDetailed(repoRoot, branchNames, includeTags).Value;

    public static GitRefFingerprint CaptureDetailed(
        string repoRoot,
        IEnumerable<string> branchNames,
        bool includeTags = false)
    {
        var metadata = ResolveMetadataDirectories(repoRoot);
        if (metadata is null) return new GitRefFingerprint("missing", RequiresShortFallback: true);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer)
        {
            Path.Combine(metadata.Worktree, "HEAD"),
            Path.Combine(metadata.Common, "refs", "remotes", "origin", "HEAD"),
        };

        foreach (var branch in branchNames)
            AddBranchPaths(metadata.Common, branch, paths);

        // A configured integration branch may be absent, in which case
        // GitService falls back through origin/HEAD or HEAD. Include the target
        // of those symbolic refs so a commit on that fallback invalidates too.
        var reliable = AddSymbolicTarget(
            metadata.Common,
            Path.Combine(metadata.Worktree, "HEAD"),
            paths);
        reliable &= AddSymbolicTarget(
            metadata.Common,
            Path.Combine(metadata.Common, "refs", "remotes", "origin", "HEAD"),
            paths);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Path.GetFullPath(repoRoot));
        foreach (var path in paths.OrderBy(p => p, comparer))
            reliable &= AppendFile(hash, metadata.Common, path);

        // packed-refs may contain hundreds or thousands of unrelated task refs.
        // Git rewrites it atomically, so file metadata is a constant-time change
        // stamp; do not hash the complete file on every board heartbeat.
        reliable &= AppendFileMetadata(
            hash,
            metadata.Common,
            Path.Combine(metadata.Common, "packed-refs"));

        // Loose version tags are created/replaced atomically. Hash directory
        // metadata recursively (never tag-file contents), so a tag under an
        // existing namespace such as refs/tags/v1/* is observed cheaply. Packed
        // tags are covered by packed-refs above.
        if (includeTags)
        {
            reliable &= AppendDirectoryTreeMetadata(
                hash,
                metadata.Common,
                Path.Combine(metadata.Common, "refs", "tags"));
        }

        var commonReftableRoot = Path.Combine(metadata.Common, "reftable");
        var worktreeReftableRoot = Path.Combine(metadata.Worktree, "reftable");
        var usesReftable = IsReftableDirectory(commonReftableRoot)
            || IsReftableDirectory(worktreeReftableRoot);
        if (usesReftable)
        {
            reliable &= AppendFileMetadata(
                hash,
                metadata.Common,
                Path.Combine(commonReftableRoot, "tables.list"));
            if (!string.Equals(metadata.Worktree, metadata.Common, StringComparison.OrdinalIgnoreCase))
            {
                reliable &= AppendFileMetadata(
                    hash,
                    metadata.Worktree,
                    Path.Combine(worktreeReftableRoot, "tables.list"));
            }
        }

        return new GitRefFingerprint(
            Convert.ToHexString(hash.GetHashAndReset()),
            RequiresShortFallback: usesReftable || !reliable);
    }

    private static void AddBranchPaths(string commonDir, string? branchName, HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return;
        var branch = branchName.Trim().Replace('\\', '/');
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch["refs/heads/".Length..];
        else if (branch.StartsWith("origin/", StringComparison.Ordinal))
            branch = branch["origin/".Length..];

        AddSafeRefPath(commonDir, Path.Combine("refs", "heads"), branch, paths);
        AddSafeRefPath(commonDir, Path.Combine("refs", "remotes", "origin"), branch, paths);
        // The Studio integration lane (AGT-2996) carries merges before the
        // local branch does.
        AddSafeRefPath(commonDir, Path.Combine("refs", "agent-studio", "integration"), branch, paths);
    }

    private static bool AddSymbolicTarget(
        string commonDir,
        string symbolicRefPath,
        HashSet<string> paths)
    {
        try
        {
            if (!File.Exists(symbolicRefPath)) return true;
            var text = File.ReadAllText(symbolicRefPath).Trim();
            const string prefix = "ref: ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return true;
            var target = text[prefix.Length..].Replace('\\', '/');
            AddSafeRelativePath(commonDir, target, paths);

            // ResolveIntegrationBranch turns origin/HEAD into a branch name and
            // then requires the corresponding local branch. Fingerprint both
            // mirrors, otherwise moving refs/heads/trunk could leave a warm
            // fallback projection stale when configured develop is absent.
            const string originPrefix = "refs/remotes/origin/";
            if (target.StartsWith(originPrefix, StringComparison.Ordinal))
                AddBranchPaths(commonDir, target[originPrefix.Length..], paths);
            const string localPrefix = "refs/heads/";
            if (target.StartsWith(localPrefix, StringComparison.Ordinal))
                AddBranchPaths(commonDir, target[localPrefix.Length..], paths);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: symbolic ref read failed");
            return false;
        }
    }

    private static void AddSafeRefPath(
        string commonDir,
        string prefix,
        string branch,
        HashSet<string> paths)
        => AddSafeRelativePath(commonDir, Path.Combine(prefix, branch), paths);

    private static void AddSafeRelativePath(string commonDir, string relativePath, HashSet<string> paths)
    {
        try
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(commonDir);
            var candidate = Path.GetFullPath(Path.Combine(root, normalized));
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (candidate.StartsWith(rootPrefix, comparison)) paths.Add(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: invalid ref path");
        }
    }

    private static bool AppendFile(IncrementalHash hash, string commonDir, string path)
    {
        Append(hash, Path.GetRelativePath(commonDir, path).Replace('\\', '/'));
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
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: ref read failed");
            return false;
        }
    }

    private static bool AppendFileMetadata(IncrementalHash hash, string commonDir, string path)
    {
        Append(hash, Path.GetRelativePath(commonDir, path).Replace('\\', '/'));
        try
        {
            if (!File.Exists(path))
            {
                Append(hash, "missing");
                return true;
            }

            var info = new FileInfo(path);
            Append(hash, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "unreadable");
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: ref metadata read failed");
            return false;
        }
    }

    private static bool AppendDirectoryMetadata(IncrementalHash hash, string commonDir, string path)
    {
        Append(hash, Path.GetRelativePath(commonDir, path).Replace('\\', '/'));
        try
        {
            if (!Directory.Exists(path))
            {
                Append(hash, "missing");
                return true;
            }

            var info = new DirectoryInfo(path);
            Append(hash, $"{info.CreationTimeUtc.Ticks}:{info.LastWriteTimeUtc.Ticks}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "unreadable");
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: directory metadata read failed");
            return false;
        }
    }

    private static bool AppendDirectoryTreeMetadata(
        IncrementalHash hash,
        string commonDir,
        string root)
    {
        if (!AppendDirectoryMetadata(hash, commonDir, root)) return false;
        if (!Directory.Exists(root)) return true;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            var directories = Directory.EnumerateDirectories(root, "*", options)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            var reliable = true;
            foreach (var directory in directories)
                reliable &= AppendDirectoryMetadata(hash, commonDir, directory);
            return reliable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "tag-directories-unreadable");
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: tag directory enumeration failed");
            return false;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static bool IsReftableDirectory(string path)
        => Directory.Exists(path) || File.Exists(Path.Combine(path, "tables.list"));

    private static MetadataDirectories? ResolveMetadataDirectories(string repoRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
            var dotGit = Path.Combine(repoRoot, ".git");
            string worktreeDir;
            if (Directory.Exists(dotGit))
            {
                worktreeDir = Path.GetFullPath(dotGit);
            }
            else if (File.Exists(dotGit))
            {
                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                const string prefix = "gitdir:";
                if (line is null || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;
                var configured = line[prefix.Length..].Trim();
                worktreeDir = Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(repoRoot, configured));
            }
            else if (File.Exists(Path.Combine(repoRoot, "HEAD")))
            {
                worktreeDir = Path.GetFullPath(repoRoot);
            }
            else
            {
                return null;
            }

            var commonDir = worktreeDir;
            var commonFile = Path.Combine(worktreeDir, "commondir");
            if (File.Exists(commonFile))
            {
                var configured = File.ReadAllText(commonFile).Trim();
                if (configured.Length > 0)
                {
                    commonDir = Path.GetFullPath(Path.IsPathRooted(configured)
                        ? configured
                        : Path.Combine(worktreeDir, configured));
                }
            }
            return new MetadataDirectories(worktreeDir, commonDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: git metadata resolution failed");
            return null;
        }
    }

    private sealed record MetadataDirectories(string Worktree, string Common);
}

internal readonly record struct GitRefFingerprint(
    string Value,
    bool RequiresShortFallback);
