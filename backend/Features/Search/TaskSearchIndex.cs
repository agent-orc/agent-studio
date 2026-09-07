using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using AgentStudio.Tasks;

namespace AgentStudio.Search;

/// <summary>
/// One indexed card: the in-memory task record, the concatenated body text of
/// its indexed documents, and a small pre-lowercased identifier blob.
/// </summary>
/// <param name="Task">The card as published by <see cref="TaskIndexCache"/>.</param>
/// <param name="Text">
/// Original-cased body text (<c>prompt.md</c> + <c>status.md</c>), capped. Kept in
/// original casing because the palette subtitle shows the first matching line;
/// a lowercased copy would render the operator's own prompt back at them in
/// lower case, and keeping both copies would double the resident corpus.
/// </param>
/// <param name="Header">
/// Lowercased key/title/state/project blob. Almost every palette query is an
/// identifier, so matching this tiny string first short-circuits the body scan.
/// </param>
public sealed record TaskSearchEntry(TaskInfo Task, string Text, string Header);

/// <summary>
/// In-memory full-text index over the workspace cards, so a palette query never
/// touches disk.
///
/// <para><b>Why this exists:</b> the previous implementation read
/// <c>prompt.md</c> and <c>status.md</c> for every card - live and archived -
/// on every keystroke-driven query. At 963 cards that is roughly 2,000 file
/// probes and reads per query, which alone put the task domain well past the
/// 100 ms budget.</para>
///
/// <para><b>Freshness model:</b> the index is versioned by
/// <see cref="TaskIndexCache.Generation"/>, which advances once per published
/// snapshot. A request reads <see cref="TaskIndexCache.Generation"/> only - it
/// never forces a workspace rescan and never reads a file. When the generation
/// has moved on, the current entries are served immediately and a single
/// background rebuild is admitted (stale-while-revalidate): a palette that is
/// one board mutation behind is a far better trade than a palette that blocks
/// on a disk walk.</para>
///
/// <para><b>Rebuild cost:</b> body text is memoized per card by
/// (length, last-write) of each indexed document, so a rebuild after an
/// unrelated lane move re-reads nothing and costs two stat calls per card.</para>
/// </summary>
public sealed class TaskSearchIndex(
    TaskScannerService scanner,
    TaskIndexCache cache,
    ILogger<TaskSearchIndex> logger)
{
    private static readonly string[] IndexedDocuments = ["prompt.md", "status.md"];

    /// <summary>
    /// Per-card body cap. Generated dossiers and long status logs would
    /// otherwise let a single card dominate the resident index; a match past
    /// 64 KB of one card is not what the command palette is for.
    /// </summary>
    private const int MaxIndexedCharsPerCard = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly Lock _buildGate = new();
    private ImmutableArray<TaskSearchEntry> _entries = ImmutableArray<TaskSearchEntry>.Empty;
    private Dictionary<string, CardText> _texts = new(StringComparer.Ordinal);
    private long _builtGeneration = -1;
    private bool _hasSnapshot;
    private bool _rebuilding;

    public long Hits;
    public long ColdBuilds;
    public long StaleServes;
    public long Rebuilds;

    /// <summary>
    /// Returns the current entries. Warm and current: an O(1) reference return.
    /// Warm but behind: the previous entries plus one admitted background
    /// rebuild. Cold: a single synchronous build, shared by concurrent callers.
    /// </summary>
    public ImmutableArray<TaskSearchEntry> GetEntries()
    {
        lock (_gate)
        {
            if (_hasSnapshot)
            {
                if (_builtGeneration == cache.Generation)
                {
                    Interlocked.Increment(ref Hits);
                    return _entries;
                }
                if (!_rebuilding)
                {
                    _rebuilding = true;
                    _ = Task.Run(RebuildInBackground);
                }
                Interlocked.Increment(ref StaleServes);
                return _entries;
            }
        }

        // Cold start only. Concurrent callers queue here rather than each
        // walking the workspace; the second one through finds the snapshot.
        lock (_buildGate)
        {
            lock (_gate)
            {
                if (_hasSnapshot) return _entries;
            }
            Interlocked.Increment(ref ColdBuilds);
            Build();
            lock (_gate) return _entries;
        }
    }

    private void RebuildInBackground()
    {
        try
        {
            lock (_buildGate)
            {
                Interlocked.Increment(ref Rebuilds);
                Build();
            }
        }
        catch (Exception ex)
        {
            // A failed rebuild must not poison the palette: the previous
            // entries stay published and the next generation change retries.
            logger.LogWarning(ex, "task-search-index-rebuild-failed");
        }
        finally
        {
            lock (_gate) _rebuilding = false;
        }
    }

    private void Build()
    {
        var timer = Stopwatch.StartNew();
        // Captured before the scan on purpose. If a mutation lands mid-scan we
        // under-report the generation we indexed and pay one extra rebuild;
        // capturing it afterwards could stamp stale text as current.
        var generation = cache.Generation;
        var tasks = scanner.ScanAllAutomationJobsWithArchive();

        var texts = new Dictionary<string, CardText>(tasks.Count, StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<TaskSearchEntry>(tasks.Count);
        var reads = 0;
        Dictionary<string, CardText> previous;
        lock (_gate) previous = _texts;

        foreach (var task in tasks)
        {
            var stamp = StampDocuments(task.FolderPath);
            if (!previous.TryGetValue(task.FolderPath, out var cached) || cached.Stamp != stamp)
            {
                cached = new CardText(stamp, ReadIndexedText(task.FolderPath));
                reads++;
            }
            texts[task.FolderPath] = cached;
            entries.Add(new TaskSearchEntry(task, cached.Text, BuildHeader(task)));
        }

        var built = entries.ToImmutable();
        lock (_gate)
        {
            _entries = built;
            _texts = texts;
            _builtGeneration = generation;
            _hasSnapshot = true;
        }
        timer.Stop();
        logger.LogDebug(
            "task-search-index-built cards={Cards} reread={Reread} generation={Generation} durationMs={DurationMs}",
            built.Length, reads, generation, timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// Identity of a card's indexed documents: total length plus last-write
    /// ticks per document. Cheap enough to take for every card on every
    /// rebuild, and specific enough that an edited document always re-reads.
    /// </summary>
    private static string StampDocuments(string folderPath)
    {
        var stamp = new StringBuilder();
        foreach (var name in IndexedDocuments)
        {
            var info = new FileInfo(Path.Combine(folderPath, name));
            if (info.Exists) stamp.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            stamp.Append('|');
        }
        return stamp.ToString();
    }

    private static string ReadIndexedText(string folderPath)
    {
        var text = new StringBuilder();
        foreach (var name in IndexedDocuments)
        {
            if (text.Length >= MaxIndexedCharsPerCard) break;
            var path = Path.Combine(folderPath, name);
            if (!File.Exists(path)) continue;
            try
            {
                text.AppendLine(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A card being written while we index is normal; the next
                // rebuild picks it up because the stamp will have moved.
                SilentCatch.Note(ex, $"TaskSearchIndex: skipped unreadable {name}");
            }
        }
        return text.Length > MaxIndexedCharsPerCard
            ? text.ToString(0, MaxIndexedCharsPerCard)
            : text.ToString();
    }

    private static string BuildHeader(TaskInfo task) =>
        string.Join('\n', task.Key, task.TaskKey, task.Title, task.State, task.ProjectName, task.TaskType)
            .ToLowerInvariant();

    private sealed record CardText(string Stamp, string Text);
}
