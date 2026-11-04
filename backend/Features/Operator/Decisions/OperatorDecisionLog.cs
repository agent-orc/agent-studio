using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Operator;

/// <summary>One page of the decision log read after a cursor.</summary>
public sealed record OperatorDecisionPage(
    IReadOnlyList<OperatorDecisionEvent> Events, long Cursor, long Head, long? Oldest, bool HasMore, bool Gap);

/// <summary>
/// The installation's decision log (OP-2). Events are appended as JSON lines
/// to <c>decision-events.jsonl</c> with a monotonic sequence that is the
/// cursor. A reader that resumes after a cursor receives every later event
/// once. Events older than the retention window (at least 24 hours) are
/// dropped; the sequence keeps counting, and a cursor older than the oldest
/// kept event reads as a gap. <c>decision-facts.json</c> holds the fact keys
/// already published and the cursor they were saved at, so a restart neither
/// loses nor repeats a fact.
/// </summary>
public sealed class OperatorDecisionLog
{
    public const string EventsFileName = "decision-events.jsonl";
    public const string FactsFileName = "decision-facts.json";
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromHours(24);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _folder;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly List<OperatorDecisionEvent> _events = [];
    private TaskCompletionSource _appended = NewSignal();
    private long _head;
    private bool _loaded;

    public OperatorDecisionLog(string? folder, TimeSpan retention, TimeProvider? clock = null, ILogger? logger = null)
    {
        _folder = folder;
        _retention = retention < MinimumRetention ? MinimumRetention : retention;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public TimeSpan Retention => _retention;

    public long Head { get { lock (_gate) { LoadLocked(); return _head; } } }

    private string? EventsPath => _folder is null ? null : Path.Combine(_folder, EventsFileName);
    private string? FactsPath => _folder is null ? null : Path.Combine(_folder, FactsFileName);

    /// <summary>Appends one event per fact, in order, and wakes every waiting reader.</summary>
    public IReadOnlyList<OperatorDecisionEvent> Append(IReadOnlyList<OperatorDecisionFact> facts)
    {
        if (facts.Count == 0) return [];
        TaskCompletionSource wake;
        var appended = new List<OperatorDecisionEvent>(facts.Count);
        lock (_gate)
        {
            LoadLocked();
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var fact in facts)
            {
                var sequence = _head + 1;
                appended.Add(new OperatorDecisionEvent(
                    "opd_" + Guid.NewGuid().ToString("N")[..16],
                    sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    fact.Type, now, fact.Project, fact.CardKey, fact.Cards ?? [], fact.Reason,
                    OperatorOperations.For(fact.Type), fact.Evidence, fact.Key, fact.ObservedAtUtc));
                _head = sequence;
            }
            WriteLinesLocked(appended);
            _events.AddRange(appended);
            TrimLocked(now);
            wake = _appended;
            _appended = NewSignal();
        }
        wake.TrySetResult();
        return appended;
    }

    /// <summary>
    /// Events after <paramref name="after"/> that <paramref name="shows"/>
    /// accepts, oldest first, at most <paramref name="limit"/>. The page
    /// cursor passes events the reader does not see, so the next read starts
    /// after them.
    /// </summary>
    public OperatorDecisionPage Read(long after, int limit, Func<OperatorDecisionEvent, bool> shows)
    {
        lock (_gate)
        {
            LoadLocked();
            var oldest = _events.Count == 0 ? (long?)null : _events[0].Sequence;
            // A cursor beyond the head comes from another log (a reset
            // installation); the reader starts again at the oldest event.
            var gap = after > _head || after < _head && (oldest is null || after < oldest.Value - 1);
            if (after > _head) after = 0;
            var result = new List<OperatorDecisionEvent>();
            var cursor = after;
            var hasMore = false;
            foreach (var item in _events)
            {
                if (item.Sequence <= after) continue;
                if (result.Count >= limit) { hasMore = true; break; }
                cursor = item.Sequence;
                if (shows(item)) result.Add(item);
            }
            return new OperatorDecisionPage(result, hasMore ? cursor : _head, _head, oldest, hasMore, gap);
        }
    }

    /// <summary>Completes when an event was appended or <paramref name="timeout"/> passed.</summary>
    public async Task WaitForAppendAsync(long seen, TimeSpan timeout, CancellationToken ct)
    {
        Task signal;
        lock (_gate)
        {
            LoadLocked();
            if (_head > seen) return;
            signal = _appended.Task;
        }
        await Task.WhenAny(signal, Task.Delay(timeout, _clock, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The fact keys already published: the saved set plus the keys of every
    /// event appended after it was saved, so a crash between the append and
    /// the save never publishes a fact twice.
    /// </summary>
    public IReadOnlySet<string> LoadPublishedFacts()
    {
        lock (_gate)
        {
            LoadLocked();
            var saved = ReadFactsLocked();
            var keys = new HashSet<string>(saved?.Keys ?? [], StringComparer.Ordinal);
            foreach (var item in _events)
                if (item.Sequence > (saved?.ThroughCursor ?? 0) && item.Type != OperatorDecisionTypes.WatcherHeartbeat)
                    keys.Add(item.Fact);
            return keys;
        }
    }

    public void SavePublishedFacts(IReadOnlySet<string> keys)
    {
        lock (_gate)
        {
            LoadLocked();
            if (FactsPath is not { } path) return;
            WriteAtomic(path, JsonSerializer.Serialize(
                new PublishedFacts(_head, [.. keys.Order(StringComparer.Ordinal)], _head), Json));
        }
    }

    private void LoadLocked()
    {
        if (_loaded) return;
        _loaded = true;
        if (EventsPath is { } path && File.Exists(path))
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<OperatorDecisionEvent>(line, Json) is { Sequence: > 0 } item
                        && item.Sequence > _head)
                    {
                        _events.Add(item);
                        _head = item.Sequence;
                    }
                }
                catch (JsonException exception)
                {
                    // A line torn by a crash is skipped; the next append starts a new line.
                    _logger?.LogWarning(exception, "operator decision log: unreadable line skipped");
                }
            }
        var saved = ReadFactsLocked();
        _head = Math.Max(_head, Math.Max(saved?.ThroughCursor ?? 0, saved?.Head ?? 0));
        TrimLocked(_clock.GetUtcNow().UtcDateTime);
    }

    private void WriteLinesLocked(IReadOnlyList<OperatorDecisionEvent> items)
    {
        if (EventsPath is not { } path) return;
        Directory.CreateDirectory(_folder!);
        var text = new StringBuilder();
        if (EndsWithoutNewline(path)) text.Append('\n');
        foreach (var item in items) text.Append(JsonSerializer.Serialize(item, Json)).Append('\n');
        using var stream = new FileStream(path, new FileStreamOptions
            { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read });
        stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Drops events older than the retention window. It runs once the oldest
    /// event is an hour past the window, so steady appends do not rewrite the
    /// file each time.
    /// </summary>
    private void TrimLocked(DateTime now)
    {
        var cutoff = now - _retention;
        if (_events.Count == 0 || _events[0].Time >= cutoff - TimeSpan.FromHours(1)) return;
        _events.RemoveRange(0, _events.TakeWhile(item => item.Time < cutoff).Count());
        if (EventsPath is not { } path) return;
        // The head survives in the facts file even when every event expired.
        if (FactsPath is { } facts)
        {
            var saved = ReadFactsLocked();
            WriteAtomic(facts, JsonSerializer.Serialize(
                new PublishedFacts(saved?.ThroughCursor ?? 0, saved?.Keys ?? [], _head), Json));
        }
        WriteAtomic(path, string.Concat(_events.Select(item => JsonSerializer.Serialize(item, Json) + "\n")));
    }

    private PublishedFacts? ReadFactsLocked()
    {
        if (FactsPath is not { } path || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PublishedFacts>(File.ReadAllText(path), Json);
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(exception, "operator decision log: unreadable published facts, rebuilt from the log");
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PublishedFacts(long ThroughCursor, IReadOnlyList<string> Keys, long? Head = null);
}
