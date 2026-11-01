namespace AgentStudio.Git;

/// <summary>
/// The workspace <c>.security/</c> folder holds the access state, secret
/// bindings and the built-in secret store. It is local state and never part of
/// a Git commit, whichever service writes to the repository.
/// </summary>
public static class SecurityStorePathPolicy
{
    public const string DirectoryName = ".security";

    /// <summary>Ignore rule for <c>.gitignore</c> and <c>info/exclude</c>.</summary>
    public const string IgnorePattern = "/.security/";

    /// <summary>True for the folder itself or any path below it, relative to the repository root.</summary>
    public static bool IsSecurityStorePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        var first = normalized.Split('/', 2)[0];
        return string.Equals(first, DirectoryName, StringComparison.OrdinalIgnoreCase);
    }
}
