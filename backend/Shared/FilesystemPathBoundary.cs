namespace AgentStudio.Shared;

/// <summary>Rejects links in a delivered relative path before opening a live file.</summary>
public static class FilesystemPathBoundary
{
    public static bool IsRegularFileUnder(string root, string path)
        => IsPathUnderWithoutLinks(root, path) && File.Exists(path);

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
