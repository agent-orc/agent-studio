using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

public sealed record GitHubAppHostKey(string RunnerId, string Access);

/// <summary>
/// Which host may fetch repository tokens when the local profile has no service
/// identities. The local API is reachable through the hosts' SSH tunnels, so a
/// key cannot be issued over it: an operator with access to this machine runs
/// <c>scripts/github-app-host-key.sh</c>, which stores only the key's SHA-256 here
/// and hands the key to the host. Deleting the file revokes the host.
/// </summary>
public sealed class GitHubAppHostKeys(string securityFolder)
{
    private string Folder => Path.Combine(securityFolder, "github-app", "host-keys");

    /// <summary>The highest access this key grants the host, or null.</summary>
    public string? Granted(string runnerId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !ValidRunnerId(runnerId)) return null;
        var presented = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        foreach (var access in new[] { RepositoryAccess.Write, RepositoryAccess.Read })
        {
            var path = Path.Combine(Folder, $"{runnerId}.{access}.sha256");
            if (!File.Exists(path)) continue;
            var stored = Encoding.ASCII.GetBytes(File.ReadAllText(path).Trim().ToLowerInvariant());
            if (CryptographicOperations.FixedTimeEquals(presented, stored)) return access;
        }
        return null;
    }

    /// <summary>Who holds a key, for the Credentials page. Hashes stay here.</summary>
    public IReadOnlyList<GitHubAppHostKey> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var keys = new List<GitHubAppHostKey>();
        foreach (var access in new[] { RepositoryAccess.Write, RepositoryAccess.Read })
        {
            var suffix = $".{access}.sha256";
            foreach (var path in Directory.GetFiles(Folder, "*" + suffix))
                keys.Add(new GitHubAppHostKey(Path.GetFileName(path)[..^suffix.Length], access));
        }
        return keys.OrderBy(key => key.RunnerId, StringComparer.Ordinal).ToArray();
    }

    private static bool ValidRunnerId(string runnerId)
        => runnerId.Length is > 0 and <= 100
           && runnerId[0] != '.'
           && runnerId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
