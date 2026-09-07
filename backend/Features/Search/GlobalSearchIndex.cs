using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Search;

/// <summary>One indexed commit. <paramref name="Haystack"/> is the lowercased
/// concatenation of sha, short sha, and subject, so a query match is a single
/// ordinal substring scan with no per-query allocation.</summary>
public sealed record IndexedCommit(string Sha, string ShortSha, string Subject, string Haystack);

/// <summary>One indexed card. <paramref name="Haystack"/> holds the lowercased
/// key, title, state, and prompt/status text; <paramref name="Text"/> keeps the
/// original prompt/status text so a matched card can show a readable snippet.</summary>
public sealed record IndexedTask(TaskInfo Task, string Text, string Haystack);

/// <summary>Whether a domain read came from cache and how long it took.</summary>
public sealed record IndexReadStats(bool CacheHit, long DurationMs);

/// <summary>
/// Search-side indexes for the global palette. Every domain is materialized
/// once per underlying revision and matched in memory afterwards, so a new
/// query string never spawns a git process or re-reads a card from disk.
///
/// <para><b>Git domains</b> (files, commits) reuse <see cref="GitService.MemoizeByHead"/>
/// with a cache key that carries the repository root only. The old key embedded
/// the query, so every keystroke re-ran <c>git ls-files</c> and <c>git log</c>
/// across every registered repository; that is the whole cost of the 17-22 s
/// searches this index replaces. A new commit moves HEAD and invalidates the
/// entry transparently.</para>
///
/// <para><b>Tasks</b> are keyed on <see cref="TaskScannerService.SnapshotGeneration"/>,
/// the stamp <see cref="TaskIndexCache"/> advances on every published snapshot.
/// A rebuild reuses the cached blob of every card whose prompt.md/status.md
/// mtime and length are unchanged, so even a rebuild reads only what actually
/// changed, and <see cref="GlobalSearchIndexWarmer"/> normally absorbs the
/// rebuild before an operator types.</para>
/// </summary>
public sealed class GlobalSearchIndex(
    TaskScannerService scanner,
    GitService git,
    ILogger<GlobalSearchIndex> logger)
{
    /// <summary>Newest commits indexed per repository. A repository with a long
    /// history is bounded here rather than in the request, so the palette cost
    /// does not grow with history depth.</summary>
    internal const int CommitWindow = 2_000;

    /// <summary>Upper bound on indexed paths per repository. Truncation is
    /// logged, never silent.</summary>
    internal const int FileWindow = 200_000;

    /// <summary>Per-card cap on indexed prompt/status text. A pathological card
    /// cannot inflate the whole index.</summary>
    internal const int TaskTextWindow = 64 * 1024;

    private static readonly char[] GitFieldSeparator = ['\x1f'];

    private readonly Lock _taskLock = new();
    private readonly ConcurrentDictionary<string, TaskTextEntry> _taskText = new(StringComparer.OrdinalIgnoreCase);
    private long _taskGeneration = -1;
    private IReadOnlyList<IndexedTask> _tasks = [];
    private long _gitSpawns;
    private long _taskReads;

    /// <summary>Git child processes this index has spawned. The palette contract
    /// is that a query typed against an unchanged revision adds none.</summary>
    internal long GitSpawns => Interlocked.Read(ref _gitSpawns);

    /// <summary>Card files this index has read. A query against an unchanged
    /// snapshot generation adds none.</summary>
    internal long TaskReads => Interlocked.Read(ref _taskReads);

    /// <summary>Tracked and untracked paths of <paramref name="root"/> at its
    /// current HEAD. One <c>git ls-files</c> per revision, not per query.</summary>
    public (IReadOnlyList<string> Paths, IndexReadStats Stats) GetFiles(string root, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var miss = false;
        var paths = git.MemoizeByHead(root, $"global-search-file-index|{root}", () =>
        {
            miss = true;
            return LoadFiles(root, ct);
        });
        return (paths, new IndexReadStats(!miss, timer.ElapsedMilliseconds));
    }

    /// <summary>The newest <see cref="CommitWindow"/> commits of
    /// <paramref name="root"/> at its current HEAD. One <c>git log</c> per
    /// revision, not per query.</summary>
    public (IReadOnlyList<IndexedCommit> Commits, IndexReadStats Stats) GetCommits(string root, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var miss = false;
        var commits = git.MemoizeByHead(root, $"global-search-commit-index|{root}", () =>
        {
            miss = true;
            return LoadCommits(root, ct);
        });
        return (commits, new IndexReadStats(!miss, timer.ElapsedMilliseconds));
    }

    /// <summary>
    /// Every automation card, archive included, with a lowercased search blob.
    /// Returns the already-built list when the task snapshot has not moved;
    /// otherwise rebuilds incrementally against the per-card blob cache.
    /// </summary>
    public IReadOnlyList<IndexedTask> GetTasks()
    {
        // Take the snapshot first, then read its stamp. The stamp only advances
        // on a publish and only a snapshot accessor triggers one, so checking it
        // before the scan would let a card that was mutated but not yet read by
        // anyone else stay invisible to search. Warm, this accessor is O(1).
        var cards = scanner.ScanAllAutomationJobsWithArchive();
        // Generation 0 means nothing has been published yet (or no index cache
        // is wired at all), which is not a stamp we may cache against.
        var generation = scanner.SnapshotGeneration;
        lock (_taskLock)
        {
            if (generation > 0 && _taskGeneration == generation) return _tasks;
        }

        var built = new List<IndexedTask>(cards.Count);
        var live = new HashSet<string>(cards.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            live.Add(card.FolderPath);
            built.Add(BuildIndexedTask(card));
        }
        foreach (var stale in _taskText.Keys.Where(key => !live.Contains(key)))
            _taskText.TryRemove(stale, out _);

        lock (_taskLock)
        {
            // Two concurrent rebuilds are harmless but must not let the older
            // one overwrite the newer published list.
            if (generation >= _taskGeneration)
            {
                _tasks = built;
                _taskGeneration = generation;
            }
            return _tasks;
        }
    }

    /// <summary>Absorbs the rebuild ahead of the first query so the request path
    /// stays free of card reads. Returns the number of indexed cards.</summary>
    public int Warm() => GetTasks().Count;

    private IndexedTask BuildIndexedTask(TaskInfo card)
    {
        var stamp = TaskTextStamp(card.FolderPath);
        if (_taskText.TryGetValue(card.FolderPath, out var cached) && cached.Stamp == stamp)
            return new IndexedTask(card, cached.Text, Haystack(card, cached.Text));

        var text = ReadTaskText(card.FolderPath);
        _taskText[card.FolderPath] = new TaskTextEntry(stamp, text);
        return new IndexedTask(card, text, Haystack(card, text));
    }

    private static string Haystack(TaskInfo card, string text) => string.Join(
        '\n',
        card.Key ?? "",
        card.TaskKey,
        card.Title ?? "",
        card.State ?? "",
        text).ToLowerInvariant();

    /// <summary>
    /// Cheap change signal for a card's indexed sources: mtime plus length of
    /// prompt.md and status.md. Two stat calls beat re-reading the files, and a
    /// false "unchanged" needs a same-length rewrite within the same tick.
    /// </summary>
    private static long TaskTextStamp(string folderPath)
    {
        long stamp = 17;
        foreach (var name in TaskTextFiles)
        {
            var info = new FileInfo(Path.Combine(folderPath, name));
            if (!info.Exists) continue;
            stamp = stamp * 31 + info.LastWriteTimeUtc.Ticks;
            stamp = stamp * 31 + info.Length;
        }
        return stamp;
    }

    private static readonly string[] TaskTextFiles = ["prompt.md", "status.md"];

    private string ReadTaskText(string folderPath)
    {
        var text = new System.Text.StringBuilder();
        foreach (var name in TaskTextFiles)
        {
            var path = Path.Combine(folderPath, name);
            if (!File.Exists(path)) continue;
            Interlocked.Increment(ref _taskReads);
            try { text.AppendLine(File.ReadAllText(path)); }
            catch (IOException ex)
            {
                SilentCatch.Note(ex, $"GlobalSearchIndex: {name} was unreadable while indexing; the next snapshot generation retries it.");
            }
        }
        return text.Length > TaskTextWindow ? text.ToString(0, TaskTextWindow) : text.ToString();
    }

    private List<string> LoadFiles(string root, CancellationToken ct)
    {
        Interlocked.Increment(ref _gitSpawns);
        var output = RunGit(root, ["ls-files", "--cached", "--others", "--exclude-standard"], ct);
        var paths = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (paths.Count >= FileWindow)
            {
                logger.LogWarning(
                    "global-search-file-index-truncated root={Root} window={Window}", root, FileWindow);
                break;
            }
            paths.Add(line.TrimEnd('\r').Replace('\\', '/'));
        }
        return paths;
    }

    private List<IndexedCommit> LoadCommits(string root, CancellationToken ct)
    {
        Interlocked.Increment(ref _gitSpawns);
        var output = RunGit(
            root,
            ["log", "--all", "--no-merges", $"--max-count={CommitWindow}", "--pretty=format:%H%x1f%h%x1f%s"],
            ct);
        var commits = new List<IndexedCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(GitFieldSeparator);
            if (parts.Length < 3) continue;
            commits.Add(new IndexedCommit(
                parts[0], parts[1], parts[2], $"{parts[0]}\x1f{parts[1]}\x1f{parts[2]}".ToLowerInvariant()));
        }
        return commits;
    }

    /// <summary>
    /// Runs a read-only git command. Cancellation kills the child, so a palette
    /// query the operator abandoned stops paying for repositories still walking.
    /// </summary>
    internal static string RunGit(string root, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        // Killing the child on cancellation is what makes an abandoned query
        // stop costing anything: both pipes close and the reads below return.
        using var killOnCancel = ct.Register(() =>
        {
            try { process.Kill(true); }
            catch (InvalidOperationException ex)
            {
                SilentCatch.Note(ex, "GlobalSearchIndex: the git child already exited before the cancellation kill.");
            }
        });
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            throw new TimeoutException("git search exceeded 30 seconds");
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }

    private sealed record TaskTextEntry(long Stamp, string Text);
}
