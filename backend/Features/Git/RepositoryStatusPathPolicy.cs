namespace AgentStudio.Git;

/// <summary>
/// Decides which repository-relative paths a <c>git status --porcelain=v1</c>
/// listing covers. The default untracked mode collapses a new folder into one
/// <c>docs/new-folder/</c> entry, so an exact path comparison misses every file
/// inside it: a managed write then overwrote an agent's uncommitted Dossier
/// descriptor and never saw its own change (AGT-3217). Directory entries are
/// matched by prefix instead of switching status to <c>--untracked-files=all</c>,
/// which walks every untracked file on a hot path.
/// </summary>
public static class RepositoryStatusPathPolicy
{
    /// <summary>
    /// Returns the target paths that equal a status entry or lie inside a
    /// status directory entry (trailing slash). Rename entries
    /// (<c>old -&gt; new</c>) cover both sides.
    /// </summary>
    public static IReadOnlyList<string> CoveredPaths(
        IEnumerable<string> statusPaths,
        IReadOnlyCollection<string> targetPaths,
        StringComparer comparer)
    {
        var entries = statusPaths
            .SelectMany(SplitRename)
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .ToArray();
        return targetPaths
            .Select(Normalize)
            .Where(target => entries.Any(entry => Covers(entry, target, comparer)))
            .Distinct(comparer)
            .ToArray();
    }

    private static bool Covers(string entry, string target, StringComparer comparer)
    {
        if (!entry.EndsWith('/')) return comparer.Equals(entry, target);
        return target.Length > entry.Length
            && comparer.Equals(entry, target[..entry.Length]);
    }

    private static IEnumerable<string> SplitRename(string path)
    {
        var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        return arrow < 0 ? [path] : [path[..arrow], path[(arrow + 4)..]];
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        return trimmed.TrimStart('/');
    }
}
