using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AgentStudio.Search;

/// <summary>One commit from a repository's bounded log window.</summary>
public readonly record struct IndexedCommit(string Sha, string ShortSha, string Subject);

/// <summary>
/// Search corpora that outlive a single query.
///
/// <para>The palette used to re-derive its whole corpus per keystroke: every
/// query re-read <c>prompt.md</c> and <c>status.md</c> of every card, and spawned
/// <c>git log</c> plus <c>git ls-files</c> in every registered repository. The
/// HEAD-keyed memo did not help because its key carried the query string, so a
/// new search term was always a miss - and the 2 entries per repository per
/// query also evicted the wiki and history entries that share
/// <see cref="GitService"/>'s LRU.</para>
///
/// <para>Here the corpus is keyed by what actually determines it: a repository's
/// HEAD for git content, a card's last-activity stamp for task text. A second
/// query against unchanged inputs therefore reads memory only and spawns no git
/// process. Matching happens in memory against the cached corpus.</para>
/// </summary>
public sealed class GlobalSearchIndexes(IConfiguration config, GitService git, ILogger<GlobalSearchIndexes> logger)
{
    /// <summary>
    /// Upper bound on the per-card text blob. A runaway prompt must not be able
    /// to grow the in-memory corpus without limit; 32k characters covers every
    /// prompt and status document we have seen with room to spare.
    /// </summary>
    private const int MaxTaskTextChars = 32_000;

    private const int DefaultCommitWindow = 2_000;

    private readonly ConcurrentDictionary<string, TaskTextEntry> _taskText = new(StringComparer.Ordinal);

    /// <summary>
    /// How many commits per repository enter the index. Bounded so a repository
    /// with a very long history cannot make the first search after a push wait
    /// on a full <c>git log</c>. Override with <c>Search:CommitWindow</c> when a
    /// project needs deeper commit recall (see the common-problems entry).
    /// </summary>
    public int CommitWindow { get; } = Math.Clamp(
        int.TryParse(config["Search:CommitWindow"], out var configured) ? configured : DefaultCommitWindow,
        100,
        20_000);

    /// <summary>Corpus builds that actually spawned git. Read by tests asserting cache behaviour.</summary>
    internal long CorpusBuilds;

    /// <summary>Card text blobs read from disk. Read by tests asserting the request path stays in memory.</summary>
    internal long TaskTextReads;

    /// <summary>
    /// Tracked and untracked paths of <paramref name="root"/> at its current
    /// HEAD. <c>FromCache</c> is false when this call spawned git.
    /// </summary>
    public (IReadOnlyList<string> Paths, bool FromCache) FileIndex(string root)
    {
        var built = false;
        var paths = git.MemoizeByHead(root, $"global-search-file-index|{root}", () =>
        {
            built = true;
            return BuildFileIndex(root);
        });
        return (paths, !built);
    }

    /// <summary>
    /// The newest <see cref="CommitWindow"/> commits of <paramref name="root"/>
    /// at its current HEAD. <c>FromCache</c> is false when this call spawned git.
    /// </summary>
    public (IReadOnlyList<IndexedCommit> Commits, bool FromCache) CommitIndex(string root)
    {
        var built = false;
        var commits = git.MemoizeByHead(root, $"global-search-commit-index|{root}", () =>
        {
            built = true;
            return BuildCommitIndex(root);
        });
        return (commits, !built);
    }

    /// <summary>
    /// Searchable text of one card - its prompt and status documents. Re-read
    /// only when the card's last-activity stamp moved, so a warm palette query
    /// touches no file. The blob keeps its original casing because the result
    /// subtitle quotes the matching line back to the operator; matching is
    /// case-insensitive instead, which is cheaper than carrying a second
    /// lowercase copy of the whole corpus.
    /// </summary>
    public string TaskText(TaskInfo task)
    {
        if (_taskText.TryGetValue(task.TaskKey, out var cached) && cached.Stamp == task.LastActivity)
            return cached.Text;

        var text = ReadTaskText(task);
        Interlocked.Increment(ref TaskTextReads);
        _taskText[task.TaskKey] = new TaskTextEntry(task.LastActivity, text);
        return text;
    }

    /// <summary>
    /// Drops blobs of cards that no longer exist. Deleted job folders would
    /// otherwise pin their text for the lifetime of the process.
    /// </summary>
    public void PruneTaskText(IReadOnlyCollection<TaskInfo> live)
    {
        if (_taskText.Count <= live.Count) return;
        var keep = live.Select(task => task.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _taskText.Keys)
            if (!keep.Contains(key))
                _taskText.TryRemove(key, out _);
    }

    private List<string> BuildFileIndex(string root)
    {
        Interlocked.Increment(ref CorpusBuilds);
        var output = RunGit(root, ["ls-files", "--cached", "--others", "--exclude-standard"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('\r').Replace('\\', '/'))
            .ToList();
    }

    private List<IndexedCommit> BuildCommitIndex(string root)
    {
        Interlocked.Increment(ref CorpusBuilds);
        var output = RunGit(root, [
            "log", "--all", "--no-merges", $"--max-count={CommitWindow}", "--pretty=format:%H%x1f%h%x1f%s"
        ]);
        var commits = new List<IndexedCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\x1f');
            if (parts.Length >= 3) commits.Add(new IndexedCommit(parts[0], parts[1], parts[2]));
        }
        return commits;
    }

    private static string ReadTaskText(TaskInfo task)
    {
        var text = new StringBuilder();
        foreach (var name in new[] { "prompt.md", "status.md" })
        {
            var remaining = MaxTaskTextChars - text.Length;
            if (remaining <= 0) break;
            var path = Path.Combine(task.FolderPath, name);
            if (!File.Exists(path)) continue;
            text.Append(ReadHead(path, remaining)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Reads at most <paramref name="maxChars"/> characters. Bounding the read
    /// itself rather than truncating afterwards means a pathologically large
    /// document never lands in memory in full.
    /// </summary>
    private static string ReadHead(string path, int maxChars)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[maxChars];
        return new string(buffer, 0, reader.ReadBlock(buffer, 0, maxChars));
    }

    private string RunGit(string root, IReadOnlyList<string> args)
    {
        var timer = Stopwatch.StartNew();
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            logger.LogWarning("global-search-index-timeout root={Root} command={Command}", root, args[0]);
            throw new TimeoutException($"git {args[0]} in {root} exceeded 30 seconds");
        }
        timer.Stop();
        // Index spawns are the expensive part of a cold search; record them so
        // the ambient per-request git rollup accounts for them like every other
        // git call instead of showing an unexplained gap.
        GitProcessTelemetry.Record(args[0], timer.ElapsedMilliseconds, process.ExitCode);
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }

    private sealed record TaskTextEntry(DateTime Stamp, string Text);
}
