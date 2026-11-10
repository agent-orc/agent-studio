using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentStudio.Git;

namespace AgentStudio.Management;

/// <summary>A project checkout recorded in the manifest instead of being archived.</summary>
/// <param name="Path">Path relative to the projects source.</param>
/// <param name="Origin">The <c>origin</c> URL with any password or token removed; null without a remote.</param>
/// <param name="Branch">The checked-out branch, null for a detached head.</param>
/// <param name="Bundle">Archive member of the Git bundle, null when nothing exists only here.</param>
public sealed record BackupCheckout(string Path, string? Origin, string? Branch, string? Bundle);

/// <summary>
/// Project checkouts come back from their remotes; only what exists nowhere
/// else travels in the archive: <c>refs/agent-studio/*</c> (or every ref for a
/// checkout without <c>origin</c>) as a Git bundle. Worktrees are skipped:
/// their branches live in the main checkout and on the remote.
/// </summary>
public static partial class BackupCheckouts
{
    /// <summary>Directories below <paramref name="projectsRoot"/> that hold a <c>.git</c> entry, outermost first.</summary>
    public static IReadOnlyList<(string Root, bool Worktree)> Discover(string projectsRoot)
    {
        var found = new List<(string, bool)>();
        if (!Directory.Exists(projectsRoot)) return found;
        var pending = new Stack<string>();
        foreach (var child in SafeDirectories(projectsRoot)) pending.Push(child);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var git = Path.Combine(directory, ".git");
            if (Directory.Exists(git)) { found.Add((directory, false)); continue; }
            if (File.Exists(git)) { found.Add((directory, true)); continue; }
            foreach (var child in SafeDirectories(directory)) pending.Push(child);
        }
        return found.OrderBy(item => item.Item1, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Records one checkout and writes its bundle to <paramref name="bundlePath"/> when it needs one.</summary>
    public static BackupCheckout Record(string projectsRoot, string checkout, string bundleMember, string bundlePath)
    {
        var origin = Git(checkout, ["config", "--get", "remote.origin.url"]) is { Success: true } url
            ? StripCredentials(url.StandardOutput.Trim()) : null;
        if (string.IsNullOrWhiteSpace(origin)) origin = null;
        var branch = Git(checkout, ["symbolic-ref", "-q", "--short", "HEAD"]) is { Success: true } head
            ? head.StandardOutput.Trim() : null;
        if (string.IsNullOrWhiteSpace(branch)) branch = null;

        string? bundle = null;
        string[] selection = origin is null ? ["--all"] : ["--glob=refs/agent-studio/*"];
        var refs = Git(checkout, origin is null
            ? ["for-each-ref", "--count=1"]
            : ["for-each-ref", "--count=1", "refs/agent-studio/"]);
        if (refs.Success && refs.StandardOutput.Trim().Length > 0)
        {
            var created = Git(checkout, ["bundle", "create", "-q", bundlePath, .. selection]);
            if (!created.Success)
                throw new BackupException("backup-bundle-failed",
                    $"git bundle failed for {Path.GetRelativePath(projectsRoot, checkout)}: {created.StandardError.Trim()}");
            bundle = bundleMember;
        }
        return new BackupCheckout(Relative(projectsRoot, checkout), origin, branch, bundle);
    }

    /// <summary>
    /// Removes a password or token from a URL so the archive does not carry it:
    /// every user part of an HTTP URL (a token can be the user name), and the
    /// password of other schemes, whose user name (<c>ssh://git@host</c>) the
    /// clone still needs.
    /// </summary>
    public static string StripCredentials(string url)
    {
        var match = UserInfo().Match(url);
        if (!match.Success) return url;
        var scheme = match.Groups[1].Value;
        var user = match.Groups[2].Value;
        var http = scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        var colon = user.IndexOf(':');
        var kept = http ? "" : colon >= 0 ? user[..colon] + "@" : user + "@";
        return scheme + kept + url[match.Length..];
    }

    /// <summary>Restore: clones a checkout from its remote with this account's Git credentials.</summary>
    public static void Clone(string origin, string target, string? branch)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        string[] arguments = branch is null
            ? ["clone", "--", origin, target]
            : ["clone", "--branch", branch, "--", origin, target];
        var result = Git(parent, arguments);
        if (!result.Success && branch is not null)
        {
            // The branch may exist only on this host: clone the default branch, the bundle brings the rest.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            result = Git(parent, ["clone", "--", origin, target]);
        }
        if (!result.Success)
            throw new BackupException("restore-clone-failed", $"git clone {origin} failed: {result.StandardError.Trim()}");
    }

    /// <summary>Restore: an empty repository for a checkout that had no remote; its bundle fills it.</summary>
    public static void Init(string target)
    {
        Directory.CreateDirectory(target);
        var result = Git(target, ["init", "-q"]);
        if (!result.Success)
            throw new BackupException("restore-clone-failed", $"git init {target} failed: {result.StandardError.Trim()}");
    }

    /// <summary>Restore: fetches the refs a bundle carries into the restored checkout.</summary>
    public static void FetchBundle(string repository, string bundle, bool allRefs, string? branch)
    {
        string[] refspec = allRefs ? ["+refs/*:refs/*"] : ["+refs/agent-studio/*:refs/agent-studio/*"];
        var fetched = Git(repository, ["fetch", "--update-head-ok", "-q", bundle, .. refspec]);
        if (!fetched.Success)
            throw new BackupException("restore-bundle-failed", $"git fetch from the bundle failed: {fetched.StandardError.Trim()}");
        if (allRefs && branch is not null)
        {
            var checkout = Git(repository, ["checkout", "-q", branch]);
            if (!checkout.Success)
                throw new BackupException("restore-bundle-failed", $"git checkout {branch} failed: {checkout.StandardError.Trim()}");
        }
    }

    internal static GitProcessResult Git(string directory, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return GitProcessBoundary.Run(start, timeout: timeout ?? GitProcessBoundary.LongOperationTimeout);
    }

    internal static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateDirectories()
                .Where(child => (child.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(child => child.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.-]*://)([^/@]*)@")]
    private static partial Regex UserInfo();
}
