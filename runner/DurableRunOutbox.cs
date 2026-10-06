using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record RunOutboxAuthority(
    string RunId,
    string TaskKey,
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence);

public sealed record RunOutboxItem(
    long Sequence,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    DateTime CreatedAt);

public sealed record RunOutboxSnapshot(
    long LastSequence,
    long LastAcknowledgedSequence,
    int BacklogCount,
    long? OldestUnacknowledgedSequence,
    string FinalHandoffState,
    string? EnvelopeDigest);

/// <summary>
/// Host-local write-ahead journal for every fact that must survive a runner
/// process, Task Server, or network restart. Journal records are append-only
/// and fsynced before they become visible to the caller. Acknowledgements use
/// an atomic replace and are also fsynced.
/// </summary>
public sealed class DurableRunOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, byte> ActiveRuns =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _journalPath;
    private readonly string _ackPath;
    private readonly List<RunOutboxItem> _items;
    private long _lastSequence;
    private long _lastAcknowledgedSequence;
    private string _finalHandoffState = "collecting";
    private string? _envelopeDigest;
    private ResultHandoffAck? _handoffAcknowledgement;
    private ArtifactTransferReportRequest? _pendingArtifactReport;

    private DurableRunOutbox(
        string directory,
        RunOutboxAuthority authority,
        List<RunOutboxItem> items,
        RunOutboxAckState? ack)
    {
        _directory = directory;
        _journalPath = Path.Combine(directory, "journal.jsonl");
        _ackPath = Path.Combine(directory, "ack.json");
        Authority = authority;
        _items = items;
        _lastSequence = items.Count == 0 ? 0 : items.Max(item => item.Sequence);
        if (ack is not null)
        {
            _lastAcknowledgedSequence = ack.LastAcknowledgedSequence;
            _finalHandoffState = ack.FinalHandoffState;
            _envelopeDigest = ack.EnvelopeDigest;
            _handoffAcknowledgement = ack.HandoffAcknowledgement;
            _pendingArtifactReport = ack.PendingArtifactReport;
        }
    }

    public RunOutboxAuthority Authority { get; }
    public string DirectoryPath => _directory;
    public long LastSequence { get { lock (_gate) return _lastSequence; } }
    public long LastAcknowledgedSequence { get { lock (_gate) return _lastAcknowledgedSequence; } }
    public ResultHandoffAck? HandoffAcknowledgement
    {
        get
        {
            lock (_gate) return _handoffAcknowledgement;
        }
    }
    public ArtifactTransferReportRequest? PendingArtifactReport
    {
        get { lock (_gate) return _pendingArtifactReport; }
    }
    public long? OldestUnacknowledgedSequence
    {
        get
        {
            lock (_gate)
                return _items.FirstOrDefault(item => item.Sequence > _lastAcknowledgedSequence)?.Sequence;
        }
    }

    public IReadOnlyList<RunOutboxItem> Pending
    {
        get
        {
            lock (_gate)
                return _items.Where(item => item.Sequence > _lastAcknowledgedSequence).ToArray();
        }
    }

    public IReadOnlyList<RunOutboxItem> Items
    {
        get
        {
            lock (_gate) return _items.ToArray();
        }
    }

    public RunOutboxSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var pending = _items.Where(item => item.Sequence > _lastAcknowledgedSequence).ToArray();
                return new RunOutboxSnapshot(
                    _lastSequence,
                    _lastAcknowledgedSequence,
                    pending.Length,
                    pending.FirstOrDefault()?.Sequence,
                    _finalHandoffState,
                    _envelopeDigest);
            }
        }
    }

    public static bool IsActive(string runId) => ActiveRuns.ContainsKey(runId);

    public IDisposable MarkActive()
    {
        if (!ActiveRuns.TryAdd(Authority.RunId, 0))
            throw new InvalidOperationException(
                $"Run '{Authority.RunId}' already has an active executor in this process.");
        return new ActiveRunRegistration(Authority.RunId);
    }

    public static DurableRunOutbox Open(string root, RunOutboxAuthority authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var directory = Path.Combine(root, SafeSegment(authority.RunId));
        Directory.CreateDirectory(directory);
        var authorityPath = Path.Combine(directory, "authority.json");
        if (File.Exists(authorityPath))
        {
            var existing = JsonSerializer.Deserialize<RunOutboxAuthority>(
                File.ReadAllText(authorityPath), Json)
                ?? throw new InvalidDataException($"Outbox authority is unreadable: {authorityPath}");
            if (existing != authority)
                throw new InvalidDataException($"Outbox authority does not match run '{authority.RunId}'.");
        }
        else
        {
            WriteAtomic(authorityPath, JsonSerializer.Serialize(authority, Json));
        }

        var journalPath = Path.Combine(directory, "journal.jsonl");
        var items = new List<RunOutboxItem>();
        if (File.Exists(journalPath))
        {
            RepairTornJournalTail(journalPath);
            foreach (var line in File.ReadLines(journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                items.Add(JsonSerializer.Deserialize<RunOutboxItem>(line, Json)
                          ?? throw new InvalidDataException($"Outbox journal contains an empty record: {journalPath}"));
            }
            if (items.Select(item => item.Sequence).Distinct().Count() != items.Count
                || !items.Select(item => item.Sequence).SequenceEqual(
                    items.Select(item => item.Sequence).OrderBy(sequence => sequence)))
                throw new InvalidDataException($"Outbox sequence is not strictly monotonic: {journalPath}");
        }

        var ackPath = Path.Combine(directory, "ack.json");
        RunOutboxAckState? ack = null;
        if (File.Exists(ackPath))
            ack = JsonSerializer.Deserialize<RunOutboxAckState>(File.ReadAllText(ackPath), Json)
                  ?? throw new InvalidDataException($"Outbox acknowledgement is unreadable: {ackPath}");
        return new DurableRunOutbox(directory, authority, items, ack);
    }

    public static IReadOnlyList<DurableRunOutbox> OpenAll(string root)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<DurableRunOutbox>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
        {
            var authorityPath = Path.Combine(directory, "authority.json");
            if (!File.Exists(authorityPath)) continue;
            var authority = JsonSerializer.Deserialize<RunOutboxAuthority>(
                File.ReadAllText(authorityPath), Json)
                ?? throw new InvalidDataException($"Outbox authority is unreadable: {authorityPath}");
            result.Add(Open(root, authority));
        }
        return result;
    }

    public RunOutboxItem Enqueue(string kind, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        using var _ = JsonDocument.Parse(payloadJson);
        lock (_gate)
        {
            var sequence = checked(_lastSequence + 1);
            var item = new RunOutboxItem(
                sequence,
                kind.Trim(),
                payloadJson,
                $"{Authority.RunId}:{sequence}",
                DateTime.UtcNow);
            AppendAndFlush(_journalPath, JsonSerializer.Serialize(item, Json) + Environment.NewLine);
            _items.Add(item);
            _lastSequence = sequence;
            return item;
        }
    }

    public void Acknowledge(long sequence)
    {
        lock (_gate)
        {
            if (sequence <= _lastAcknowledgedSequence) return;
            if (sequence > _lastSequence)
                throw new InvalidOperationException($"Cannot acknowledge outbox sequence {sequence}; last sequence is {_lastSequence}.");
            _lastAcknowledgedSequence = sequence;
            PersistAck();
        }
    }

    public void RecordHandoffState(string state, string? envelopeDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        lock (_gate)
        {
            _finalHandoffState = state.Trim();
            _envelopeDigest = envelopeDigest ?? _envelopeDigest;
            PersistAck();
        }
    }

    public void RecordPendingArtifactReport(ArtifactTransferReportRequest report)
    {
        lock (_gate)
        {
            _pendingArtifactReport = report;
            _finalHandoffState = "artifact-replay";
            PersistAck();
        }
    }

    public void ClearPendingArtifactReport()
    {
        lock (_gate)
        {
            _pendingArtifactReport = null;
            PersistAck();
        }
    }

    public void RecordHandoffAcknowledgement(ResultHandoffAck acknowledgement)
    {
        lock (_gate)
        {
            if (!string.Equals(
                    acknowledgement.RunId,
                    Authority.RunId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Handoff acknowledgement belongs to a different RunAttempt.");
            if (!string.Equals(
                    acknowledgement.State,
                    "acknowledged",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Handoff acknowledgement is not durable.");
            var finalItem = _items.SingleOrDefault(
                item => item.Sequence == acknowledgement.AcknowledgedSequence);
            if (finalItem is null
                || !string.Equals(finalItem.Kind, "final-result", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Handoff acknowledgement does not identify the journaled final result.");
            }
            var envelope = JsonSerializer.Deserialize<ImmutableResultEnvelope>(
                               finalItem.PayloadJson,
                               Json)
                           ?? throw new InvalidDataException(
                               "Journaled final result envelope is empty.");
            var expectedDigest = ResultEnvelopeDigest.Compute(envelope);
            if (!string.Equals(
                    acknowledgement.EnvelopeDigest,
                    expectedDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Handoff acknowledgement digest does not match the journaled final result.");
            }
            _handoffAcknowledgement = acknowledgement;
            _lastAcknowledgedSequence = Math.Max(
                _lastAcknowledgedSequence,
                acknowledgement.AcknowledgedSequence);
            _finalHandoffState = acknowledgement.State;
            _envelopeDigest = acknowledgement.EnvelopeDigest;
            PersistAck();
        }
    }

    public async Task ReplayAsync(
        Func<RunOutboxItem, CancellationToken, Task> sender,
        CancellationToken ct)
    {
        foreach (var item in Pending)
        {
            ct.ThrowIfCancellationRequested();
            await sender(item, ct);
            Acknowledge(item.Sequence);
        }
    }

    private void PersistAck()
        => WriteAtomic(_ackPath, JsonSerializer.Serialize(
            new RunOutboxAckState(
                _lastAcknowledgedSequence,
                _finalHandoffState,
                _envelopeDigest,
                _handoffAcknowledgement,
                DateTime.UtcNow,
                _pendingArtifactReport),
            Json));

    private static void AppendAndFlush(string path, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void RepairTornJournalTail(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n') return;
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(lastNewline + 1L);
        stream.Flush(flushToDisk: true);
    }

    private static string SafeSegment(string value)
    {
        var characters = value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-').ToArray();
        var result = new string(characters).Trim('-', '.');
        return result.Length == 0 ? "run" : result;
    }

    private sealed record RunOutboxAckState(
        long LastAcknowledgedSequence,
        string FinalHandoffState,
        string? EnvelopeDigest,
        ResultHandoffAck? HandoffAcknowledgement,
        DateTime PersistedAt,
        ArtifactTransferReportRequest? PendingArtifactReport = null);

    private sealed class ActiveRunRegistration(string runId) : IDisposable
    {
        private string? _runId = runId;

        public void Dispose()
        {
            var activeRunId = Interlocked.Exchange(ref _runId, null);
            if (activeRunId is not null)
                ActiveRuns.TryRemove(activeRunId, out _);
        }
    }
}

public sealed class DurableHandoffGate(
    string expectedRunId,
    string expectedEnvelopeDigest)
{
    public void RequireAcknowledged(ResultHandoffAck? acknowledgement)
    {
        if (acknowledgement is null
            || !string.Equals(acknowledgement.State, "acknowledged", StringComparison.Ordinal)
            || !string.Equals(
                acknowledgement.RunId,
                expectedRunId,
                StringComparison.Ordinal)
            || !string.Equals(
                acknowledgement.EnvelopeDigest,
                expectedEnvelopeDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Coding worktree cleanup requires a durable acknowledgement for the matching immutable result envelope.");
        }
    }
}
