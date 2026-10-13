using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// Resolves the git origin and default branch a remote runner needs from the
/// durable project registry. A URL entry named <c>repo</c> is mandatory and
/// authoritative. The configured project integration branch is the authoritative
/// working branch; the registered local checkout's <c>origin/HEAD</c> is only a
/// fallback when no branch is configured. Its origin is never a remote-runner
/// fallback.
/// </summary>
public static class RemoteProjectRepositoryResolver
{
    private static readonly Regex ScpStyleRemote = new(
        @"^[^\s/@:]+@[^\s/:]+:.+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RemoteProjectRepository? Resolve(ProjectRecord? project, string? configuredDefaultBranch)
    {
        if (project is null) return null;

        var repositoryUrl = ResolveRepositoryUrl(project);
        if (repositoryUrl is null) return null;
        var gitDirectory = ResolveGitDirectory(project.RepositoryPath);

        // The integration branch is the delivery target and is authoritative.
        // Local checkout metadata is only a fallback for legacy registrations
        // that do not yet carry an explicit target.
        var defaultBranch = string.IsNullOrWhiteSpace(configuredDefaultBranch)
            ? ReadDefaultBranch(gitDirectory)
            : configuredDefaultBranch.Trim();
        if (string.IsNullOrWhiteSpace(defaultBranch)) defaultBranch = "main";

        return new RemoteProjectRepository(
            project.Id,
            RepositoryIdentityContract.FromUrl(repositoryUrl)!,
            repositoryUrl,
            defaultBranch,
            "registry-url");
    }

    /// <summary>
    /// The registered checkout's own default branch (<c>origin/HEAD</c>, then
    /// local <c>HEAD</c>). Repo truth for callers that need to rank it against
    /// other sources themselves instead of taking the merged result above.
    /// </summary>
    /// <summary>
    /// The project's usable remote URL, without the branch lookup
    /// <see cref="Resolve"/> reads from the local checkout.
    /// </summary>
    public static string? ResolveRepositoryUrl(ProjectRecord? project)
    {
        var repoEntry = project?.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase));
        return IsUsableRemote(repoEntry?.Url) ? repoEntry!.Url.Trim() : null;
    }

    public static string? ReadRepositoryDefaultBranch(ProjectRecord? project)
        => project is null ? null : ReadDefaultBranch(ResolveGitDirectory(project.RepositoryPath));

    private static string? ResolveGitDirectory(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return null;

        var dotGit = Path.Combine(repositoryPath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        var pointer = File.ReadLines(dotGit).FirstOrDefault();
        const string prefix = "gitdir:";
        if (pointer is null || !pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = pointer[prefix.Length..].Trim();
        return Path.GetFullPath(path, repositoryPath);
    }

    private static string? ReadDefaultBranch(string? gitDirectory)
    {
        if (gitDirectory is null) return null;

        var remoteHead = ReadSymbolicRef(Path.Combine(gitDirectory, "refs", "remotes", "origin", "HEAD"), "refs/remotes/origin/");
        if (!string.IsNullOrWhiteSpace(remoteHead)) return remoteHead;

        return ReadSymbolicRef(Path.Combine(gitDirectory, "HEAD"), "refs/heads/");
    }

    private static string? ReadSymbolicRef(string path, string prefix)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadLines(path).FirstOrDefault()?.Trim();
        const string refPrefix = "ref:";
        if (value is null || !value.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var reference = value[refPrefix.Length..].Trim();
        return reference.StartsWith(prefix, StringComparison.Ordinal)
            ? reference[prefix.Length..]
            : null;
    }

    private static bool IsUsableRemote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (ScpStyleRemote.IsMatch(trimmed)) return true;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "https" or "http" or "ssh" or "git";
    }
}

public sealed record RemoteProjectRepository(
    string ProjectId,
    string RepositoryId,
    string RepositoryUrl,
    string DefaultBranch,
    string Source);
