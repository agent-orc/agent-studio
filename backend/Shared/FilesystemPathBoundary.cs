namespace AgentStudio.Shared;

/// <summary>Outcome of <see cref="FilesystemPathBoundary.CheckCheckoutFile"/>.</summary>
public enum CheckoutFileStatus
{
    Readable,
    Refused,
    Missing,
    TooLarge,
}

/// <summary>Rejects links in a delivered relative path before opening a live file.</summary>
public static class FilesystemPathBoundary
{
    public static bool IsRegularFileUnder(string root, string path)
        => IsPathUnderWithoutLinks(root, path) && File.Exists(path);

    /// <summary>
    /// Classifies a file a route is about to read from a repository checkout.
    /// Only a regular file inside <paramref name="root"/>, reached without a
    /// link, outside the checkout's Git metadata and at most
    /// <paramref name="maxBytes"/> long is <see cref="CheckoutFileStatus.Readable"/>.
    /// </summary>
    public static CheckoutFileStatus CheckCheckoutFile(string root, string path, long maxBytes)
    {
        if (!IsPathUnderWithoutLinks(root, path) || IsUnderGitMetadata(root, path))
            return CheckoutFileStatus.Refused;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Directory.Exists(path) ? CheckoutFileStatus.Refused : CheckoutFileStatus.Missing;
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return CheckoutFileStatus.Refused;
            return info.Length > maxBytes ? CheckoutFileStatus.TooLarge : CheckoutFileStatus.Readable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return CheckoutFileStatus.Refused;
        }
    }

    /// <summary>
    /// True when a segment of <paramref name="path"/> below <paramref name="root"/>
    /// names Git metadata: <c>.git</c> in any case, with the trailing dots or
    /// spaces Windows ignores, or its 8.3 short name.
    /// </summary>
    public static bool IsUnderGitMetadata(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(IsGitMetadataSegment);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool IsGitMetadataSegment(string segment)
    {
        var name = segment.TrimEnd('.', ' ');
        if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length > 4
            && name.StartsWith("GIT~", StringComparison.OrdinalIgnoreCase)
            && name.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    public static bool IsPathUnderWithoutLinks(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative is ".." || Path.IsPathRooted(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            if (Directory.Exists(fullRoot)
                ? (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0
                : File.Exists(fullRoot) || new DirectoryInfo(fullRoot).LinkTarget is not null)
                return false;

            var current = fullRoot;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return false;
                }
                catch (FileNotFoundException)
                {
                    return new FileInfo(current).LinkTarget is null
                        && new DirectoryInfo(current).LinkTarget is null;
                }
                catch (DirectoryNotFoundException)
                {
                    return new FileInfo(current).LinkTarget is null
                        && new DirectoryInfo(current).LinkTarget is null;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
