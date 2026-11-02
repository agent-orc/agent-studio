namespace AgentStudio.Readiness;

/// <summary>One line of <c>/proc/self/mountinfo</c>: where a file system is mounted and its type.</summary>
public sealed record MountEntry(string MountPoint, string FileSystemType);

/// <summary>
/// File-system facts of the readiness report: mounts, writable state folders
/// and Git lock files. None of them starts a process.
/// </summary>
public static class ReadinessFileProbes
{
    public const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>
    /// Parses mountinfo (man 5 proc): field 5 is the mount point, the first field
    /// after the <c>-</c> separator the file-system type. Octal escapes such as
    /// <c>\040</c> stand for spaces.
    /// </summary>
    public static IReadOnlyList<MountEntry> ParseMountInfo(string text)
    {
        var mounts = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 5 || separator < 5 || separator + 1 >= fields.Length) continue;
            mounts.Add(new MountEntry(Unescape(fields[4]), fields[separator + 1]));
        }
        return mounts;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 3 < value.Length
                && value.Substring(index + 1, 3).All(digit => digit is >= '0' and <= '7'))
            {
                builder.Append((char)Convert.ToInt32(value.Substring(index + 1, 3), 8));
                index += 3;
            }
            else builder.Append(value[index]);
        }
        return builder.ToString();
    }

    /// <summary>The mount that holds <paramref name="path"/>: the longest mount point that is the path or one of its parents.</summary>
    public static MountEntry? MountFor(IReadOnlyList<MountEntry> mounts, string path)
    {
        var target = Normalize(path);
        return mounts
            .Where(mount => Contains(Normalize(mount.MountPoint), target))
            .OrderByDescending(mount => Normalize(mount.MountPoint).Length)
            .FirstOrDefault();
    }

    private static string Normalize(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static bool Contains(string mountPoint, string path)
        => mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);

    /// <summary>
    /// Docker writes <c>/.dockerenv</c>; Podman and containerd leave an overlay
    /// root. Either means the root file system is the container layer.
    /// </summary>
    public static bool RunsInContainer(IReadOnlyList<MountEntry> mounts, bool dockerEnvExists)
        => dockerEnvExists || MountFor(mounts, "/")?.FileSystemType == "overlay";

    public static DataFolderFact DataFolder(string name, string path, string hostFolder,
        IReadOnlyList<MountEntry>? mounts, string? mountTableError)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return new DataFolderFact(name, full, hostFolder, false, null, null, mountTableError);
        var mount = mounts is null ? null : MountFor(mounts, full);
        return new DataFolderFact(name, full, hostFolder, true, mount?.MountPoint, mount?.FileSystemType, mountTableError);
    }

    /// <summary>Creates and removes a probe file, the way a card or session write would.</summary>
    public static StateFolderFact StateFolder(string name, string path)
    {
        var full = Path.GetFullPath(path);
        var probe = Path.Combine(full, $".studio-readiness-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
                stream.WriteByte(1);
            File.Delete(probe);
            return new StateFolderFact(name, full, true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new StateFolderFact(name, full, false, error.Message);
        }
    }

    /// <summary>
    /// The lock files a killed git leaves behind (AGT-3409): index, HEAD,
    /// packed-refs and ref locks in the repository's own Git directory.
    /// </summary>
    public static IReadOnlyList<GitLockFact> GitLocks(string repositoryPath, DateTimeOffset now)
    {
        var gitDir = GitDirectory(repositoryPath);
        if (gitDir is null) return [];
        var candidates = new List<string>
        {
            Path.Combine(gitDir, "index.lock"),
            Path.Combine(gitDir, "HEAD.lock"),
            Path.Combine(gitDir, "packed-refs.lock"),
        };
        var refs = Path.Combine(gitDir, "refs");
        try
        {
            if (Directory.Exists(refs))
                candidates.AddRange(Directory.EnumerateFiles(refs, "*.lock", SearchOption.AllDirectories));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(error, "ReadinessFileProbes: ref locks unreadable");
        }
        return candidates.Where(File.Exists)
            .Select(file => new GitLockFact(file, now - new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)))
            .ToArray();
    }

    private static string? GitDirectory(string repositoryPath)
    {
        var dotGit = Path.Combine(repositoryPath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;
        // A linked worktree: ".git" holds "gitdir: <path>".
        var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
        if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        var target = line["gitdir:".Length..].Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(repositoryPath, target));
        return Directory.Exists(full) ? full : null;
    }
}
