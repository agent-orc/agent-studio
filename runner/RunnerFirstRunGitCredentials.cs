using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Finds the exact repository identity installed by the first-run host action.</summary>
internal static class RunnerFirstRunGitCredentials
{
    public static ReviewRepositoryCredential? Token(string stateDir, string origin)
    {
        foreach (var folder in Folders(stateDir, origin))
        {
            var path = Path.Combine(folder, "token");
            if (!File.Exists(path)) continue;
            var canonical = ReviewRepositoryAccessPolicy.CanonicalRepository(origin);
            if (canonical is null) continue;
            var uri = new Uri(origin);
            var token = File.ReadAllText(path).Trim();
            if (token.Length > 0)
                return new ReviewRepositoryCredential(canonical, origin,
                    uri.Scheme, uri.Authority, "x-access-token", token);
        }
        return null;
    }

    public static string? Key(string stateDir, string origin)
        => Folders(stateDir, origin)
            .Select(folder => Path.Combine(folder, "deploy-key"))
            .FirstOrDefault(File.Exists);

    public static string? Helper(string stateDir, string origin)
        => Folders(stateDir, origin)
            .Select(folder => Path.Combine(folder, "git-helper.sh"))
            .FirstOrDefault(File.Exists);

    private static IEnumerable<string> Folders(string stateDir, string origin)
    {
        var root = Path.Combine(stateDir, "first-run-git");
        if (!Directory.Exists(root)) yield break;
        foreach (var folder in Directory.EnumerateDirectories(root)
            .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var path = Path.Combine(folder, "origin");
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), origin, StringComparison.Ordinal))
                yield return folder;
        }
    }
}
