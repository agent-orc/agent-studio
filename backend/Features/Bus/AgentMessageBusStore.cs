using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Bus;

/// <summary>
/// File-backed, in-memory projection of the Agent Message Bus. Disk is the
/// source of truth: many small JSONL files under
/// <c>{workspace}/logs/bus/{project|_workspace}/{yyyy-mm-dd}.jsonl</c>. Queries
/// served from a per-(workspace,project) in-memory snapshot so UI-polled
/// endpoints never trigger a full scan.
/// </summary>
/// <remarks>
/// <para>
/// The store loads a project lazily on first access, then keeps the projection
/// in memory and updates it incrementally on every <see cref="AppendAsync"/>.
/// External writers that bypass this store will not be visible until
/// <see cref="InvalidateProjection"/> is called for the (workspace, project)
/// pair.
/// </para>
/// <para>
/// Thread-safety: writes are serialised per-file via a <see cref="SemaphoreSlim"/>
/// and the projection list is replaced under a per-projection lock. Reads
/// snapshot the underlying list reference so a reader and an appender cannot
/// trip on each other.
/// </para>
/// </remarks>
public sealed class AgentMessageBusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<ProjectionKey, Projection> _projections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ProjectionKey, ConcurrentDictionary<BusTokenReceiptKey, byte>> _tokenReceipts = new();

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    /// <summary>
    /// Optional sink that observes every successful append. Used by the
    /// in-memory aggregation cache so token rollups stay O(1) on update.
    /// Set once at startup; the store itself does not own the sink.
    /// </summary>
    public Action<string, AgentMessage>? OnAppended { get; set; }

    public void InvalidateProjection(string workspaceRoot, string? project)
    {
        var key = new ProjectionKey(workspaceRoot, project);
        _projections.TryRemove(key, out _);
        _tokenReceipts.TryRemove(key, out _);
    }

    /// <summary>
    /// Eagerly load the per-(workspace, project) projection so the first
    /// caller does not pay the cold-start jsonl deserialisation cost. Used
    /// by boot-time warmup in Program.cs to keep /api/tasks/grouped fast on
    /// the very first request after a restart; without this, the verifier
    /// run by update-service can time out while the Runbook bus (100K+ lines)
    /// is being deserialised inline on the request thread.
    /// </summary>
    public int WarmProject(string workspaceRoot, string? project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var projection = GetOrLoad(workspaceRoot, project, ct);
        return projection.Snapshot().Count;
    }

    /// <summary>
    /// Append one message to its day-file and update the in-memory projection.
    /// Atomic at the line level: a single JSON serialise-and-write to the file
    /// in append mode under a per-file semaphore. If serialisation throws,
    /// nothing is written.
    /// </summary>
    public async Task AppendAsync(string workspaceRoot, AgentMessage message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(message);
        if (!AgentMessageValidator.TryValidate(message, out var error))
            throw new InvalidOperationException($"AgentMessage rejected: {error}");

        var day = DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc).ToUniversalTime().Date;
        var path = AgentMessageBusPaths.DayFile(workspaceRoot, message.Project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(message, JsonOptions);
        if (line.Contains('\n')) line = line.Replace("\r", "").Replace("\n", " ");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        ConcurrentDictionary<BusTokenReceiptKey, byte>? receiptKeys = null;
        BusTokenReceiptKey? receiptKey = null;
        try
        {
            // Completion replay can mint a new bus message id for the same
            // usage frame. Check under the day-file lock before appending so
            // both the file and the live projection remain idempotent.
            if (BusTokenReceiptKey.From(message) is { } fingerprint)
            {
                var projectionKey = new ProjectionKey(workspaceRoot, message.Project);
                receiptKeys = _tokenReceipts.GetOrAdd(projectionKey, _ =>
                {
                    var existing = new ConcurrentDictionary<BusTokenReceiptKey, byte>();
                    foreach (var prior in GetOrLoad(workspaceRoot, message.Project, ct).Snapshot())
                    {
                        if (BusTokenReceiptKey.From(prior) is { } priorKey)
                            existing.TryAdd(priorKey, 0);
                    }
                    return existing;
                });
                if (!receiptKeys.TryAdd(fingerprint, 0)) return;
                receiptKey = fingerprint;
            }
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (receiptKey is { } fingerprint) receiptKeys?.TryRemove(fingerprint, out _);
            throw;
        }
        finally
        {
            sem.Release();
        }

        var key = new ProjectionKey(workspaceRoot, message.Project);
        if (_projections.TryGetValue(key, out var existing))
        {
            existing.Append(message);
        }

        // Notify observers (aggregation cache, SignalR push, ...). Observer
        // failures must not break the write path; the bus contract is best-effort.
        var sink = OnAppended;
        if (sink != null)
        {
            try { sink(workspaceRoot, message); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "AgentMessageBusStore: observers are best-effort"); /* observers are best-effort */ }
        }
    }

    private readonly record struct BusTokenReceiptKey(
        string RunId, string? JobId, string? Topic, string Participant,
        long TimestampTicks, string? Model, long Input, long Output,
        long CacheRead, long CacheWrite)
    {
        public static BusTokenReceiptKey? From(AgentMessage message)
        {
            if (message.Kind != "token-usage" || string.IsNullOrWhiteSpace(message.RunId)
                || message.Tokens is not { } usage) return null;
            return new BusTokenReceiptKey(message.RunId, message.JobId, message.Topic,
                message.ParticipantId, message.CreatedAt.ToUniversalTime().Ticks,
                usage.Model, usage.Input, usage.Output, usage.CacheRead ?? 0,
                usage.CacheWrite ?? 0);
        }
    }

    /// <summary>
    /// Returns messages for one project filtered by <paramref name="query"/>,
    /// sorted by id (ULID/UUID v7 lexical order = creation order). The first
    /// call for a (workspace, project) pair loads the projection from disk;
    /// subsequent calls hit memory only.
    /// </summary>
    public IReadOnlyList<AgentMessage> Query(string workspaceRoot, string? project, AgentMessageQuery query, CancellationToken ct = default)
    {
        var projection = GetOrLoad(workspaceRoot, project, ct);
        var messages = projection.Snapshot();
        IEnumerable<AgentMessage> filtered = messages;

        if (!string.IsNullOrEmpty(query.JobId)) filtered = filtered.Where(m => m.JobId == query.JobId);
        if (!string.IsNullOrEmpty(query.RunId)) filtered = filtered.Where(m => m.RunId == query.RunId);
        if (!string.IsNullOrEmpty(query.ParticipantId)) filtered = filtered.Where(m => m.ParticipantId == query.ParticipantId);
        if (!string.IsNullOrEmpty(query.Kind)) filtered = filtered.Where(m => m.Kind == query.Kind);
        if (!string.IsNullOrEmpty(query.Severity)) filtered = filtered.Where(m => m.Severity == query.Severity);
        if (!string.IsNullOrEmpty(query.CorrelationId)) filtered = filtered.Where(m => m.CorrelationId == query.CorrelationId);
        if (query.Since is { } since) filtered = filtered.Where(m => m.CreatedAt >= since);
        if (query.Until is { } until) filtered = filtered.Where(m => m.CreatedAt <= until);

        if (!string.IsNullOrEmpty(query.Cli))
        {
            var participants = projection.Participants;
            filtered = filtered.Where(m =>
                participants.TryGetValue(m.ParticipantId, out var p) && string.Equals(p.Cli, query.Cli, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.Skill))
        {
            var participants = projection.Participants;
            filtered = filtered.Where(m =>
                participants.TryGetValue(m.ParticipantId, out var p) && string.Equals(p.Skill, query.Skill, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.Tag))
        {
            filtered = filtered.Where(m => m.Tags is not null && m.Tags.Contains(query.Tag));
        }

        if (query.Limit is { } limit and > 0)
        {
            filtered = filtered.TakeLast(limit);
        }

        return filtered.ToList();
    }

    /// <summary>Most-recent <paramref name="limit"/> messages for the project, newest last.</summary>
    public IReadOnlyList<AgentMessage> Recent(string workspaceRoot, string? project, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<AgentMessage>();
        var projection = GetOrLoad(workspaceRoot, project, ct);
        var messages = projection.Snapshot();
        if (messages.Count <= limit) return messages;
        return messages.Skip(messages.Count - limit).ToList();
    }

    public AgentMessage? GetById(string workspaceRoot, string? project, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var projection = GetOrLoad(workspaceRoot, project, ct);
        return projection.GetById(id);
    }

    public AgentMessageSummary Summarize(string workspaceRoot, string project, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var projection = GetOrLoad(workspaceRoot, project, ct);
        var messages = projection.Snapshot();

        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var byParticipant = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTime? first = null, last = null;
        foreach (var m in messages)
        {
            byKind[m.Kind] = byKind.GetValueOrDefault(m.Kind) + 1;
            byParticipant[m.ParticipantId] = byParticipant.GetValueOrDefault(m.ParticipantId) + 1;
            var sev = m.Severity ?? "Info";
            bySeverity[sev] = bySeverity.GetValueOrDefault(sev) + 1;
            if (first is null || m.CreatedAt < first) first = m.CreatedAt;
            if (last is null || m.CreatedAt > last) last = m.CreatedAt;
        }

        return new AgentMessageSummary(
            project,
            messages.Count,
            first,
            last,
            byKind,
            byParticipant,
            bySeverity);
    }

    public async Task RegisterParticipantAsync(string workspaceRoot, AgentParticipant participant, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(participant);
        if (!AgentMessageValidator.TryValidate(participant, out var error))
            throw new InvalidOperationException($"AgentParticipant rejected: {error}");

        var path = AgentMessageBusPaths.ParticipantFile(workspaceRoot, participant.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(participant, JsonOptions);
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }

        // Refresh participant cache for any loaded projection in this workspace.
        foreach (var kv in _projections)
        {
            if (string.Equals(kv.Key.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                kv.Value.UpsertParticipant(participant);
            }
        }
    }

    public IReadOnlyList<AgentParticipant> ListParticipants(string workspaceRoot)
    {
        var dir = AgentMessageBusPaths.ParticipantsDir(workspaceRoot);
        if (!Directory.Exists(dir)) return Array.Empty<AgentParticipant>();
        var list = new List<AgentParticipant>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var raw = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<AgentParticipant>(raw, JsonOptions);
                if (p is not null && AgentMessageValidator.TryValidate(p, out _)) list.Add(p);
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "AgentMessageBusStore: skip malformed"); /* skip malformed */ }
        }
        return list;
    }

    private Projection GetOrLoad(string workspaceRoot, string? project, CancellationToken ct)
    {
        var key = new ProjectionKey(workspaceRoot, project);
        return _projections.GetOrAdd(key, k => LoadFromDisk(k, ct));
    }

    private Projection LoadFromDisk(ProjectionKey key, CancellationToken ct)
    {
        var projection = new Projection();
        var dir = AgentMessageBusPaths.ProjectDir(key.WorkspaceRoot, key.Project);
        if (Directory.Exists(dir))
        {
            // Sort by file name so dates load in order. Within a file, lexical
            // id sort restores creation order regardless of file ordering.
            var files = Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                LoadFile(file, projection);
            }
        }

        // Participants are workspace-scoped; load once per projection so
        // CLI/skill filters can resolve participantId without a second cache.
        foreach (var p in ListParticipants(key.WorkspaceRoot))
        {
            projection.UpsertParticipant(p);
        }

        projection.SortByIdIfNeeded();
        return projection;
    }

    private static void LoadFile(string path, Projection projection)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AgentMessage? msg = null;
            try
            {
                msg = JsonSerializer.Deserialize<AgentMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Malformed line: skip. Strict-mode validation is at append time;
                // legacy or partially-written lines must not break the projection.
                continue;
            }
            if (msg is null) continue;
            if (!AgentMessageValidator.TryValidate(msg, out _)) continue;
            // Cold load: O(1) in-place add. LoadFromDisk sorts only if an
            // out-of-order id was observed during replay. Using Append() here
            // was O(N^2) - see AppendInitial remarks.
            projection.AppendInitial(msg);
        }
    }

    private readonly record struct ProjectionKey(string WorkspaceRoot, string? Project);

    private sealed class Projection
    {
        private readonly object _lock = new();
        private List<AgentMessage> _messages = new();
        private Dictionary<string, AgentMessage> _byId = new(StringComparer.Ordinal);
        private Dictionary<string, AgentParticipant> _participants = new(StringComparer.Ordinal);
        private bool _sortedById = true;

        public IReadOnlyDictionary<string, AgentParticipant> Participants
        {
            get { lock (_lock) return _participants; }
        }

        public IReadOnlyList<AgentMessage> Snapshot()
        {
            lock (_lock) return _messages;
        }

        public AgentMessage? GetById(string id)
        {
            lock (_lock) return _byId.TryGetValue(id, out var m) ? m : null;
        }

        public void Append(AgentMessage message)
        {
            lock (_lock)
            {
                if (_byId.ContainsKey(message.Id)) return;
                var next = new List<AgentMessage>(_messages.Count + 1);
                next.AddRange(_messages);
                // Append-keeping-order: when this message belongs at the end (typical
                // monotonic ULID), avoid an O(N log N) re-sort.
                if (next.Count == 0 || string.CompareOrdinal(next[^1].Id, message.Id) <= 0)
                {
                    next.Add(message);
                }
                else
                {
                    var idx = next.BinarySearch(message, IdComparer.Instance);
                    if (idx < 0) idx = ~idx;
                    next.Insert(idx, message);
                }
                var byId = new Dictionary<string, AgentMessage>(_byId, StringComparer.Ordinal)
                {
                    [message.Id] = message,
                };
                _messages = next;
                _byId = byId;
            }
        }

        /// <summary>
        /// Bulk cold-load append used only by <see cref="LoadFile"/> while a
        /// projection is still thread-local and unpublished (before
        /// <c>GetOrLoad</c> installs it). Mutates the backing list/dict in
        /// place — NO copy-on-write — so replaying an N-line bus file is O(N)
        /// instead of the O(N^2) that per-message <see cref="Append"/> costs
        /// (each Append clones the whole list + dict). On a 100K-line history
        /// that quadratic was minutes of CPU + multi-GB transient garbage and
        /// was the root cause of the /api/tasks(/grouped) hang + 90% CPU.
        /// Caller MUST follow the load loop with <see cref="SortByIdIfNeeded"/>
        /// to restore id order when this detects an out-of-order append. Normal
        /// day-file replay stays in id order already, so the final O(N log N)
        /// sort can be skipped on the grouped-endpoint warmup path.
        /// </summary>
        public void AppendInitial(AgentMessage message)
        {
            // No lock: the projection instance is not yet visible to readers
            // during cold load (one LoadFromDisk thread owns it). Dedup keeps
            // parity with Append for ids that recur across daily files.
            if (_byId.ContainsKey(message.Id)) return;
            if (_sortedById
                && _messages.Count > 0
                && string.CompareOrdinal(_messages[^1].Id, message.Id) > 0)
            {
                _sortedById = false;
            }
            _messages.Add(message);
            _byId[message.Id] = message;
        }

        public void SortByIdIfNeeded()
        {
            if (_sortedById) return;
            lock (_lock)
            {
                _messages.Sort(IdComparer.Instance);
                _sortedById = true;
            }
        }

        public void UpsertParticipant(AgentParticipant participant)
        {
            lock (_lock)
            {
                var next = new Dictionary<string, AgentParticipant>(_participants, StringComparer.Ordinal)
                {
                    [participant.Id] = participant,
                };
                _participants = next;
            }
        }

        private sealed class IdComparer : IComparer<AgentMessage>
        {
            public static readonly IdComparer Instance = new();
            public int Compare(AgentMessage? x, AgentMessage? y) =>
                string.CompareOrdinal(x?.Id, y?.Id);
        }
    }
}
