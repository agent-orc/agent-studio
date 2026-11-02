using System.Security.Cryptography;
using System.Text;

namespace AgentRunner;

/// <summary>
/// Short, private temp directory of one fenced ReviewAttempt (AGT-3429).
///
/// The attempt directory itself is far too deep for <c>TMPDIR</c>:
/// <c>review-work/review-review_&lt;32 hex&gt;-f4/baseline-runtime-&lt;12 hex&gt;/tmp</c>
/// already used 107 bytes, and a Unix socket path holds at most 107 bytes plus
/// the terminating NUL. Chrome's process singleton opens
/// <c>$TMPDIR/org.chromium.Chromium.XXXXXX/SingletonSocket</c> and aborted with
/// "Socket path too long", so every Karma baseline failed with
/// <c>ChromeHeadless failed 2 times (cannot start)</c>.
///
/// The temp root is therefore <c>/tmp/rv-&lt;12 hex&gt;</c>, named by a digest
/// of the attempt directory: its length does not depend on the attempt id, the
/// fence, or the review work root. The candidate uses <c>c</c> below it and each
/// baseline or clean repeat its 12-character command segment, so attempts and
/// roles stay apart. An <c>.owner</c> file names the attempt directory; the
/// retention sweep uses it to remove a temp root whose attempt is gone.
/// </summary>
internal static class ReviewAttemptTemp
{
    /// <summary>Bytes a Unix socket path may use: <c>sun_path</c> is 108 bytes including the NUL.</summary>
    internal const int UnixSocketPathLimit = 107;

    internal const string DirectoryPrefix = "rv-";
    internal const string CandidateDirectoryName = "c";
    internal const string OwnerFileName = ".owner";
    private const int AttemptDigestLength = 12;
    private const int RoleSegmentLength = 12;

    /// <summary>
    /// <c>/tmp</c> on Unix, never <see cref="Path.GetTempPath"/>: that reads the
    /// runner's own <c>TMPDIR</c>, which may itself be long.
    /// </summary>
    internal static string DefaultBase()
        => OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";

    internal static string Root(string tempBase, string attemptRoot)
    {
        var owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(attemptRoot));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))
            .ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(tempBase), DirectoryPrefix + digest[..AttemptDigestLength]);
    }

    internal static string Candidate(string tempRoot)
        => Path.Combine(tempRoot, CandidateDirectoryName);

    /// <summary>
    /// Temp directory of a baseline or clean-repeat runtime. The segment is the
    /// same 12-character prefix that names its <c>baseline-runtime-*</c> folder.
    /// </summary>
    internal static string Role(string tempRoot, string commandHash)
        => Path.Combine(tempRoot, commandHash[..RoleSegmentLength]);

    /// <summary>
    /// Creates the temp root for <paramref name="attemptRoot"/> with owner-only
    /// access. A fresh attempt (<paramref name="mustBeNew"/>) refuses an existing
    /// root as dirty, exactly like an existing attempt directory. An adopted
    /// attempt recreates a root that a reboot removed and only accepts an
    /// existing one that it owns.
    /// </summary>
    internal static void Ensure(string tempRoot, string attemptRoot, bool mustBeNew)
    {
        if (Directory.Exists(tempRoot) || File.Exists(tempRoot))
        {
            if (mustBeNew)
                throw new ReviewInfrastructureException(
                    "DirtyBefore",
                    $"Review attempt temp directory already exists: {tempRoot}");
            if (!OwnedBy(tempRoot, attemptRoot))
                throw new ReviewInfrastructureException(
                    "DirtyBefore",
                    $"Review attempt temp directory is not owned by this attempt: {tempRoot}");
            return;
        }

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(tempRoot);
        else
            Directory.CreateDirectory(
                tempRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0)
            throw new ReviewInfrastructureException(
                "DirtyBefore",
                $"Review attempt temp directory is a link: {tempRoot}");
        File.WriteAllText(Path.Combine(tempRoot, OwnerFileName), Owner(attemptRoot));
    }

    /// <summary>Removes the temp root without following links. True when it is gone.</summary>
    internal static bool Remove(string tempRoot)
    {
        if (!Directory.Exists(tempRoot) && !File.Exists(tempRoot)) return true;
        try
        {
            ResilientDirectory.Delete(tempRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return !Directory.Exists(tempRoot) && !File.Exists(tempRoot);
    }

    /// <summary>
    /// Removes temp roots whose attempt directory below
    /// <paramref name="reviewWorkDir"/> no longer exists. A root owned by another
    /// review work root, by an active namespace, or by an attempt that still
    /// exists is left alone; the attempt sweep removes the last kind together
    /// with its attempt.
    /// </summary>
    internal static int SweepOrphans(
        string tempBase,
        string reviewWorkDir,
        IReadOnlySet<string> activeAttemptNames,
        Action<string> log)
    {
        if (!Directory.Exists(tempBase)) return 0;
        var workRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reviewWorkDir))
                       + Path.DirectorySeparatorChar;
        string[] candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(tempBase, DirectoryPrefix + "*").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            var owner = ReadOwner(candidate);
            if (owner is null
                || !owner.StartsWith(workRoot, StringComparison.Ordinal)
                || activeAttemptNames.Contains(Path.GetFileName(owner))
                || Directory.Exists(owner)
                || !string.Equals(Root(tempBase, owner), candidate, StringComparison.Ordinal))
                continue;
            if (!Remove(candidate)) continue;
            removed++;
            log($"removed orphaned review temp path={candidate} owner={owner}");
        }
        return removed;
    }

    private static bool OwnedBy(string tempRoot, string attemptRoot)
        => Directory.Exists(tempRoot)
           && (File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) == 0
           && string.Equals(ReadOwner(tempRoot), Owner(attemptRoot), StringComparison.Ordinal);

    private static string? ReadOwner(string tempRoot)
    {
        try
        {
            if ((File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0) return null;
            var path = Path.Combine(tempRoot, OwnerFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Owner(string attemptRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(attemptRoot));
}
