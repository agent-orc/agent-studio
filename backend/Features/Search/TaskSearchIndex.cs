using System.Collections.Immutable;
using System.Text;
using AgentStudio.Tasks;

namespace AgentStudio.Search;

/// <summary>One indexed card: the scanned record, its lowercase match blob, and
/// the original card text the palette quotes a matching line from.</summary>
public sealed record TaskSearchEntry(TaskInfo Task, string Blob, string Text);

/// <summary>
/// Lowercase search blob per task card, rebuilt only when
/// <see cref="TaskIndexCache"/> publishes a new snapshot generation.
///
/// <para>Before this index existed, every keystroke re-read <c>prompt.md</c> and
/// <c>status.md</c> of every card, so a workspace with a few hundred cards paid
/// hundreds of file reads per query. The card list itself already came from the
/// index cache in O(1); only the text did not. This class closes that gap: the
/// request path touches memory only.</para>
///
/// <para><b>Freshness:</b> the generation stamp advances on every publish
/// (mutation, watcher event, or safety-TTL rescan), so a card edited through the
/// API is reindexed on the next query. Within one generation the entries are a
/// stable snapshot. A rebuild re-stats the two indexed files per card and reuses
/// the previously parsed text whenever the stamp is unchanged, so a generation
/// bump caused by an unrelated card does not re-read the whole workspace.</para>
/// </summary>
public sealed class TaskSearchIndex
{
    // Cards can carry very long prompts. The blob exists to answer "does this
    // card mention the query", not to mirror the file, so a per-file ceiling
    // keeps the index bounded on a workspace with a few hundred long cards.
    private const int MaxIndexedCharsPerFile = 64 * 1024;
    private static readonly string[] IndexedFiles = ["prompt.md", "status.md"];

    private readonly Func<IReadOnlyList<TaskInfo>> _readTasks;
    private readonly Func<long> _readGeneration;
    private readonly ILogger<TaskSearchIndex> _logger;

    private readonly Lock _lock = new();
    private Dictionary<string, CardText> _cards = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<TaskSearchEntry> _entries = [];
    private long _builtGeneration = long.MinValue;
    private bool _built;

    /// <summary>Rebuilds and card re-reads since start, surfaced by the search log line.</summary>
    public long Rebuilds;
    public long CardReads;

    public TaskSearchIndex(TaskScannerService scanner, TaskIndexCache cache, ILogger<TaskSearchIndex> logger)
        : this(scanner.ScanAllAutomationJobsWithArchive, () => cache.Generation, logger)
    {
    }

    internal TaskSearchIndex(
        Func<IReadOnlyList<TaskInfo>> readTasks,
        Func<long> readGeneration,
        ILogger<TaskSearchIndex> logger)
    {
        _readTasks = readTasks;
        _readGeneration = readGeneration;
        _logger = logger;
    }

    /// <summary>
    /// Returns the indexed cards for the currently published snapshot. Reads the
    /// card list first (that call is what forces the index cache to be fresh) and
    /// only then the generation stamp, so the stamp always describes the list
    /// that was just handed out.
    /// </summary>
    public ImmutableArray<TaskSearchEntry> Entries()
    {
        var tasks = _readTasks();
        var generation = _readGeneration();

        lock (_lock)
        {
            if (_built && generation == _builtGeneration) return _entries;

            var previous = _cards;
            var cards = new Dictionary<string, CardText>(tasks.Count, StringComparer.OrdinalIgnoreCase);
            var entries = ImmutableArray.CreateBuilder<TaskSearchEntry>(tasks.Count);
            foreach (var task in tasks)
            {
                var card = Resolve(task, previous);
                // Two cards sharing a folder path cannot happen in a scan, but a
                // defensive indexer assignment keeps the dictionary total.
                cards[task.FolderPath] = card;
                entries.Add(new TaskSearchEntry(task, Blob(task, card.Text), card.Text));
            }

            _cards = cards;
            _entries = entries.ToImmutable();
            _builtGeneration = generation;
            _built = true;
            Rebuilds++;
            _logger.LogDebug(
                "task-search-index-rebuilt generation={Generation} cards={Cards}", generation, cards.Count);
            return _entries;
        }
    }

    private CardText Resolve(TaskInfo task, Dictionary<string, CardText> previous)
    {
        var folder = task.FolderPath;
        var prompt = Stamp(folder, IndexedFiles[0]);
        var status = Stamp(folder, IndexedFiles[1]);
        if (previous.TryGetValue(folder, out var cached)
            && cached.PromptStamp == prompt
            && cached.StatusStamp == status)
        {
            return cached;
        }

        CardReads++;
        return new CardText(prompt, status, ReadCard(folder));
    }

    private static string ReadCard(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        var text = new StringBuilder();
        foreach (var name in IndexedFiles)
        {
            var path = Path.Combine(folder, name);
            try
            {
                if (!File.Exists(path)) continue;
                var content = File.ReadAllText(path);
                text.AppendLine(content.Length > MaxIndexedCharsPerFile
                    ? content[..MaxIndexedCharsPerFile]
                    : content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A card being rewritten while we index is picked up by the next
                // generation; one unreadable file must not fail the whole query.
                SilentCatch.Note(ex, $"TaskSearchIndex: card text unreadable at {path}");
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// Everything a query may match, lowercased once so the request path runs an
    /// ordinal <c>Contains</c> instead of a culture-aware comparison per card.
    /// </summary>
    private static string Blob(TaskInfo task, string text) => string.Join(
        '\n',
        task.Key ?? "",
        task.TaskKey,
        task.Title,
        task.State,
        text).ToLowerInvariant();

    private static long Stamp(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;
        var info = new FileInfo(Path.Combine(folder, name));
        // Length is folded in so a same-second rewrite of a different size is
        // still seen as a change on filesystems with coarse timestamps.
        return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1) : 0;
    }

    private sealed record CardText(long PromptStamp, long StatusStamp, string Text);
}
