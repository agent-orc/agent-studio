using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.CliHosting;

namespace AgentRunner;

/// <summary>
/// Buffers consolidated CLI output lines and ships them to the server's
/// log-ingestion endpoint in batches. During a live run the server's own live
/// view is read locally on the runner host; this ingestion is what makes the
/// output durable and visible on the local board after the fact. Failures to
/// ship never abort the run - the console still shows the output.
///
/// <para>
/// The pending queue is bounded. A task server outage is exactly when this
/// buffer is at risk of unbounded growth (every failed flush re-queues its
/// batch while new output keeps arriving), and the daemon lives for days, so a
/// hard cap on retained lines is what keeps a backlog from turning a transient
/// outage into a memory blow-up. When the cap is exceeded the oldest lines are
/// dropped and counted; the live console output already carried them.
/// </para>
/// </summary>
public sealed class LogShipper
{
    // A backlog beyond this many lines is dropped oldest-first. At the 5 s flush
    // cadence this is minutes of very chatty output; the durable copy is best
    // effort, so bounding memory wins over retaining an unbounded backlog.
    private const int MaxPendingLines = 20_000;

    // Cap a single flush so a large backlog drains over several cadences instead
    // of building one giant request (and, on the v1 path, one giant burst of
    // per-line POSTs) that would hold the whole batch live at once.
    private const int MaxBatchLines = 2_000;
    private const int MaxLineChars = 64 * 1024;

    private readonly TaskServerClient _client;
    private readonly string _taskKey;
    private readonly RunLeaseInfoDto _lease;
    private readonly Action<string> _diag;
    private readonly DurableRunOutbox? _outbox;
    private readonly DurableLeaseAuthority? _authority;
    private readonly ConcurrentQueue<CliOutputLine> _pending = new();
    private int _pendingCount;
    private long _dropped;
    private long _reportedDropped;
    private int _transportInterrupted;
    private CliOutputLine? _reconnectEvidence;

    public LogShipper(
        TaskServerClient client,
        string taskKey,
        RunLeaseInfoDto lease,
        Action<string> diag,
        DurableRunOutbox? outbox = null,
        DurableLeaseAuthority? authority = null)
    {
        _client = client;
        _taskKey = taskKey;
        _lease = lease;
        _diag = diag;
        _outbox = outbox;
        _authority = authority;
    }

    /// <summary>Approximate number of lines currently buffered (test/diagnostic seam).</summary>
    internal int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>Total lines dropped to honour the cap (test/diagnostic seam).</summary>
    internal long DroppedCount => Volatile.Read(ref _dropped);

    public void Add(string stream, string text)
    {
        if (text.Length > MaxLineChars)
        {
            if (!JsonLineTruncator.TryTruncateObject(text, MaxLineChars, out var bounded))
                bounded = text[..MaxLineChars] + " [runner: event payload truncated]";
            text = bounded;
        }
        if (_outbox is not null)
        {
            _outbox.Enqueue(
                "log",
                JsonSerializer.Serialize(
                    new CliOutputLine(DateTime.UtcNow, stream, text),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        _pending.Enqueue(new CliOutputLine(DateTime.UtcNow, stream, text));
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
            TrimToCap();
    }

    private void TrimToCap()
    {
        while (Volatile.Read(ref _pendingCount) > MaxPendingLines && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Drain the buffer and post it. Safe to call repeatedly; a no-op when empty.</summary>
    public async Task<bool> FlushAsync(CancellationToken ct)
    {
        if (_outbox is not null)
        {
            if (_authority is { ReplayAllowed: false })
                return false;
            try
            {
                await _outbox.ReplayAsync(
                    (item, token) => _client.SendOutboxItemAsync(_outbox.Authority, item, token),
                    ct);
                if (Volatile.Read(ref _transportInterrupted) == 1
                    && _reconnectEvidence is not null)
                {
                    _outbox.Enqueue(
                        "log",
                        JsonSerializer.Serialize(
                            _reconnectEvidence,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    await _outbox.ReplayAsync(
                        (item, token) => _client.SendOutboxItemAsync(_outbox.Authority, item, token),
                        ct);
                    Interlocked.Exchange(ref _transportInterrupted, 0);
                    _reconnectEvidence = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _transportInterrupted, 1) == 0)
                {
                    Add("system", "[runner] runner transport disconnected; bounded replay pending");
                    _reconnectEvidence = new CliOutputLine(
                        DateTime.UtcNow,
                        "system",
                        "[runner] runner transport reconnected; replay acknowledged");
                }
                _diag($"log ingest failed, will retry: {ex.Message}");
                return false;
            }
        }
        var batch = new List<CliOutputLine>();
        while (batch.Count < MaxBatchLines && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            batch.Add(line);
        }
        if (batch.Count == 0) return true;

        try
        {
            var delivery = string.Join("\n", batch.Select(x => $"{x.Timestamp:o}|{x.Stream}|{x.Text}"));
            await _client.IngestLogsAsync(new LogIngestRequest(
                _taskKey,
                batch,
                RunnerId: _lease.RunnerId,
                LeaseId: _lease.LeaseId,
                FencingToken: _lease.FencingToken,
                AttemptId: _lease.AttemptId,
                Fence: _lease.FencingToken,
                AuthorityEpoch: _lease.AuthorityEpoch,
                IdempotencyKey: $"logs:{_lease.AttemptId}:{WireDigest.Hash(delivery)}"), ct);

            if (Volatile.Read(ref _transportInterrupted) == 1
                && _reconnectEvidence is not null)
            {
                await _client.IngestLogsAsync(new LogIngestRequest(
                    _taskKey,
                    [_reconnectEvidence],
                    _lease.RunnerId,
                    _lease.LeaseId,
                    _lease.FencingToken),
                    ct);
                Interlocked.Exchange(ref _transportInterrupted, 0);
                _reconnectEvidence = null;
            }
            var dropped = Volatile.Read(ref _dropped);
            var reported = Volatile.Read(ref _reportedDropped);
            if (dropped > reported)
            {
                _diag($"log buffer capped: dropped {dropped - reported} backlogged line(s) to bound memory");
                Volatile.Write(ref _reportedDropped, dropped);
            }
            return true;
        }
        catch (Exception ex)
        {
            // Re-queue so the next flush retries; the run must not fail because a
            // log batch could not be shipped. Re-queueing counts against the cap,
            // so a persistent outage sheds the oldest backlog instead of growing.
            foreach (var line in batch)
            {
                _pending.Enqueue(line);
                Interlocked.Increment(ref _pendingCount);
            }
            TrimToCap();
            if (Interlocked.Exchange(ref _transportInterrupted, 1) == 0)
            {
                Add("system", "[runner] runner transport disconnected; bounded replay pending");
                _reconnectEvidence = new CliOutputLine(
                    DateTime.UtcNow,
                    "system",
                    "[runner] runner transport reconnected; replay acknowledged");
            }
            _diag($"log ingest failed, will retry: {ex.Message}");
            return false;
        }
    }

    /// <summary>Flush on a fixed cadence until cancelled, then flush once more.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        await FlushAsync(CancellationToken.None);
    }
}
