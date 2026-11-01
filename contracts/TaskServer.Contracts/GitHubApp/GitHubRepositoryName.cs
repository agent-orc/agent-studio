namespace AgentStudio.TaskServer.Contracts.GitHubApp;

/// <summary>The owner and name of a github.com repository, in the casing the URL used.</summary>
public sealed record GitHubRepositoryName(string Owner, string Name)
{
    public const string Host = "github.com";

    /// <summary>Case-insensitive key, the form <see cref="ReviewRepositoryAccessPolicy.CanonicalRepository"/> produces.</summary>
    public string Canonical => $"{Host}/{Owner.ToLowerInvariant()}/{Name.ToLowerInvariant()}";

    public string CloneUrl => $"https://{Host}/{Owner}/{Name}.git";

    /// <summary>Null for a local path, another host, or anything that is not exactly owner and name.</summary>
    public static GitHubRepositoryName? TryParse(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return null;
        var value = repositoryUrl.Trim();
        string path;
        const string scpPrefix = "git@github.com:";
        if (value.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = value[scpPrefix.Length..];
        }
        else
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "ssh")
                || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
                return null;
            path = uri.AbsolutePath;
        }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/');
        if (parts.Length != 2 || !parts.All(ValidSegment)) return null;
        return new GitHubRepositoryName(parts[0], parts[1]);
    }

    private static bool ValidSegment(string segment)
        => segment.Length is > 0 and <= 100
           && segment is not ("." or "..")
           && segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
