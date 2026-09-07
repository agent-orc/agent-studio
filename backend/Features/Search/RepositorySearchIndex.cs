using System.Diagnostics;
using AgentStudio.Git;

namespace AgentStudio.Search;

/// <summary>A searchable checkout: display name, working-tree root, project colour.</summary>
public sealed record SearchRepository(string Name, string Root, string Color);

/// <summary>One indexed commit, already split into the fields the palette shows.</summary>
public sealed record CommitIndexEntry(string Sha, string ShortSha, string Subject);

/// <summary>An index lookup plus whether it was served from the HEAD-keyed cache.</summary>
public sealed record IndexLookup<T>(T Value, bool CacheHit);

/// <summary>
/// Per-repository path and commit indexes, keyed by HEAD only.
///
/// <para>The previous implementation memoized the <em>filtered result</em> under a
/// key that contained the query, so every new query string re-spawned
/// <c>git ls-files</c> and <c>git log</c> in every registered checkout - about
/// fifteen process pairs per keystroke-sized query. Indexing the raw material
/// instead means the git processes run once per repository per commit: a second
/// query on the same HEAD is a pure in-memory scan.</para>
///
/// <para>A new commit moves HEAD and
/// <see cref="GitService.MemoizeByHead{T}"/> drops the entry, so the index cannot
/// go stale against the checkout it describes. Uncommitted working-tree files are
/// covered because <c>ls-files</c> also lists untracked, non-ignored paths; those
/// are refreshed on the next commit rather than instantly, which is the same
/// trade the docs and git views already make.</para>
/// </summary>
public sealed class RepositorySearchIndex(GitService git)
{
    /// <summary>
    /// Commit history window per repository. Deep enough that a task key from
    /// months ago is still findable, bounded so a repository with a very long
    /// history cannot turn one index build into a multi-second stall.
    /// </summary>
    internal const int CommitWindow = 2_000;

    private const int GitTimeoutMs = 10_000;

    public IndexLookup<IReadOnlyList<string>> Paths(string root)
    {
        var miss = false;
        var value = git.MemoizeByHead(root, $"global-search-path-index|{root}", () =>
        {
            miss = true;
            return ReadPaths(root);
        });
        return new IndexLookup<IReadOnlyList<string>>(value, !miss);
    }

    public IndexLookup<IReadOnlyList<CommitIndexEntry>> Commits(string root)
    {
        var miss = false;
        var value = git.MemoizeByHead(root, $"global-search-commit-index|{root}", () =>
        {
            miss = true;
            return ReadCommits(root);
        });
        return new IndexLookup<IReadOnlyList<CommitIndexEntry>>(value, !miss);
    }

    /// <summary>Every tracked and untracked, non-ignored path, normalized to forward slashes.</summary>
    internal static IReadOnlyList<string> ReadPaths(string root) => ReadOnlyGitConcurrencyLimiter.Run(() =>
        RunGit(root, ["ls-files", "--cached", "--others", "--exclude-standard"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('\r').Replace('\\', '/'))
            .ToArray());

    /// <summary>The most recent <see cref="CommitWindow"/> non-merge commits across all refs.</summary>
    internal static IReadOnlyList<CommitIndexEntry> ReadCommits(string root) => ReadOnlyGitConcurrencyLimiter.Run(() =>
        RunGit(root, ["log", "--all", "--no-merges", $"--max-count={CommitWindow}", "--pretty=format:%H%x1f%h%x1f%s"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f'))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new CommitIndexEntry(parts[0], parts[1], parts[2]))
            .ToArray());

    private static string RunGit(string root, IReadOnlyList<string> args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(GitTimeoutMs))
        {
            process.Kill(true);
            throw new TimeoutException("git search exceeded 10 seconds");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }
}
