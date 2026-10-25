namespace AgentStudio.Shared;

/// <summary>Rejects links in a delivered relative path before opening a live file.</summary>
public static class FilesystemPathBoundary
{
    public static bool IsRegularFileUnder(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative is "." or ".." || Path.IsPathRooted(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return false;

            var current = fullRoot;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return File.Exists(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
