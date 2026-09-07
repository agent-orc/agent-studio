namespace AgentStudio.Git;

/// <summary>
/// Cheap invalidation key for the Git inventory. It deliberately reads only
/// filesystem metadata and never starts Git, so it is safe on an HTTP poll.
/// </summary>
internal readonly record struct GitRefSignature(
    long PackedRefsWriteTicks,
    long PackedRefsLength,
    long RefsWriteTicks,
    long RefsLength,
    long HeadWriteTicks,
    long HeadLength,
    ulong SelectedRefsSignature)
{
    public static GitRefSignature Capture(string? repositoryPath)
    {
        var gitDirectory = ResolveGitDirectory(repositoryPath);
        if (gitDirectory is null) return default;
        var packed = FileFacts(Path.Combine(gitDirectory, "packed-refs"));
        var refs = DirectoryFacts(Path.Combine(gitDirectory, "refs"));
        var head = FileFacts(Path.Combine(gitDirectory, "HEAD"));
        return new(packed.WriteTicks, packed.Length, refs.WriteTicks, refs.Length,
            head.WriteTicks, head.Length, SelectedRefsHash(gitDirectory));
    }

    private static string? ResolveGitDirectory(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath)) return null;
        try
        {
            var dotGit = Path.Combine(Path.GetFullPath(repositoryPath), ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;
            var marker = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var path = marker[prefix.Length..].Trim();
            return Path.GetFullPath(path, repositoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SilentCatch.Note(ex, "GitRefSignature: git directory resolution failed");
            return null;
        }
    }

    private static (long WriteTicks, long Length) FileFacts(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefSignature: file metadata read failed");
            return default;
        }
    }

    private static (long WriteTicks, long Length) DirectoryFacts(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return default;
            return (info.LastWriteTimeUtc.Ticks, info.EnumerateFileSystemInfos().LongCount());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefSignature: directory metadata read failed");
            return default;
        }
    }

    private static ulong SelectedRefsHash(string gitDirectory)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        try
        {
            var paths = new List<string>();
            AddFiles(Path.Combine(gitDirectory, "refs", "heads"), paths, recursive: true);
            AddFiles(Path.Combine(gitDirectory, "refs", "tags"), paths, recursive: true);
            var origin = Path.Combine(gitDirectory, "refs", "remotes", "origin");
            AddFiles(origin, paths, recursive: false);
            AddFiles(Path.Combine(origin, "release"), paths, recursive: true);
            AddFiles(Path.Combine(origin, "releases"), paths, recursive: true);

            foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                foreach (var character in path.AsSpan())
                {
                    hash ^= character;
                    hash *= prime;
                }
                hash ^= unchecked((ulong)info.LastWriteTimeUtc.Ticks);
                hash *= prime;
                hash ^= unchecked((ulong)info.Length);
                hash *= prime;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GitRefSignature: selected ref metadata read failed");
            return 0;
        }
        return hash;
    }

    private static void AddFiles(string path, ICollection<string> paths, bool recursive)
    {
        if (File.Exists(path))
        {
            paths.Add(path);
            return;
        }
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(
                     path,
                     "*",
                     recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            paths.Add(file);
    }
}
