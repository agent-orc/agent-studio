namespace AgentStudio.Magazine;

/// <summary>
/// The workspace <c>.magazine/</c> folder holds the magazine articles and
/// every revision (MAG-1). Agents refresh articles often, so the folder is
/// server state like <c>.security/</c>: it is ignored, never swept into a
/// workspace commit, and outside the runtime retention that prunes
/// <c>.runtime/</c>. Backups include it with the rest of the workspace.
/// </summary>
public static class MagazineStorePathPolicy
{
    public const string DirectoryName = ".magazine";

    /// <summary>Ignore rule for <c>.gitignore</c> and <c>info/exclude</c>.</summary>
    public const string IgnorePattern = "/.magazine/";

    /// <summary>True for the folder itself or any path below it, relative to the repository root.</summary>
    public static bool IsMagazineStorePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        var first = normalized.Split('/', 2)[0];
        return string.Equals(first, DirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The store root: <c>&lt;TaskRepository&gt;/.magazine</c>.</summary>
    public static string Root(IConfiguration configuration) => Path.Combine(
        string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? Path.Combine(AppContext.BaseDirectory, "workspace")
            : configuration["TaskRepository"]!, DirectoryName);
}
