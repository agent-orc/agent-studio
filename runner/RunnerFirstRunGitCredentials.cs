using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Finds the exact repository identity installed by the first-run host action.</summary>
internal static class RunnerFirstRunGitCredentials
{
    public static ReviewRepositoryCredential? Token(string stateDir, string origin)
    {
        var folder = AccessFolder(stateDir, origin);
        if (folder is null) return null;
        var path = Path.Combine(folder, "token");
        if (!File.Exists(path)) return null;
        var canonical = ReviewRepositoryAccessPolicy.CanonicalRepository(origin);
        if (canonical is null) return null;
        var uri = new Uri(origin);
        var token = File.ReadAllText(path).Trim();
        return token.Length > 0
            ? new ReviewRepositoryCredential(canonical, origin,
                uri.Scheme, uri.Authority, "x-access-token", token) : null;
    }

    public static string? Key(string stateDir, string origin)
    {
        var folder = AccessFolder(stateDir, origin);
        var path = folder is null ? null : Path.Combine(folder, "deploy-key");
        return path is not null && File.Exists(path) ? path : null;
    }

    public static string? Helper(string stateDir, string origin)
    {
        var folder = AccessFolder(stateDir, origin);
        var path = folder is null || !File.Exists(Path.Combine(folder, "token"))
            ? null : Path.Combine(folder, "git-helper.sh");
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Uses the key only for a Git command targeting the registered origin.</summary>
    internal static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?>? Environment)
        ForProjectGit(string stateDir, string origin, IReadOnlyList<string> arguments)
    {
        var keyPath = Key(stateDir, origin);
        if (keyPath is null || arguments.Count < 2)
            return (arguments, null);
        var remote = PlatformRepositoryGitProof.GitRepositoryUrl(origin, keyPath);
        IReadOnlyList<string>? selected = null;
        if (arguments[0] == "clone" && arguments[1] == origin)
            selected = ["clone", remote, .. arguments.Skip(2)];
        else if (arguments[0] == "fetch" && arguments[1] == "origin")
        {
            if (arguments.Count == 3 && arguments[2].StartsWith("refs/heads/", StringComparison.Ordinal))
                selected = ["fetch", remote, arguments[2] + ":refs/remotes/origin/"
                    + arguments[2]["refs/heads/".Length..]];
            else if (arguments.Contains("--prune"))
                selected = ["fetch", "--prune", remote, "+refs/heads/*:refs/remotes/origin/*"];
        }
        else if (arguments[0] == "push" && arguments[1] == "origin")
            selected = ["push", remote, .. arguments.Skip(2)];
        else if (arguments[0] == "ls-remote")
        {
            var index = arguments.ToList().FindIndex(1,
                value => value == "origin" || value == origin);
            if (index >= 0)
            {
                var copy = arguments.ToArray();
                copy[index] = remote;
                selected = copy;
            }
        }
        return selected is null ? (arguments, null)
            : (selected, PlatformRepositoryGitProof.CandidateGitEnvironment(origin, keyPath));
    }

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

    private static string? AccessFolder(string stateDir, string origin)
        => Folders(stateDir, origin).FirstOrDefault(folder =>
            File.Exists(Path.Combine(folder, "token"))
            || File.Exists(Path.Combine(folder, "deploy-key")));
}
