using System.Threading.Channels;

namespace AgentStudio.Telemetry;

public enum TelemetryEnqueueResult { Accepted, Busy, TooLarge, Unavailable }

/// <summary>
/// Hand-off between OTLP requests and the background writer. Enqueueing never
/// waits: when the records or the estimated bytes already waiting would exceed
/// <see cref="TelemetryOptions.MaxQueuedRecords"/> or
/// <see cref="TelemetryOptions.MaxQueuedBytes"/>, the request is refused and
/// the sender retries later (OTLP/HTTP 429 with Retry-After). One project
/// environment may fill only <see cref="TelemetryOptions.MaxQueueSharePercentPerEnvironment"/>
/// of each budget, so it cannot keep the queue full for the others. The budget
/// is released only after the writer commits the staged batch.
/// </summary>
public sealed class TelemetryIntakeQueue
{
    private readonly Channel<TelemetryIngestBatch> _channel =
        Channel.CreateUnbounded<TelemetryIngestBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly TelemetryBatchJournal? _journal;
    private readonly Dictionary<TelemetryEnvironmentKey, (long Records, long Bytes)> _environments = [];
    private readonly long _maxRecords;
    private readonly long _maxBytes;
    private readonly long _maxRecordsPerEnvironment;
    private readonly long _maxBytesPerEnvironment;
    private long _pendingRecords;
    private long _pendingBytes;
    private long _refusedBatches;

    public TelemetryIntakeQueue(TelemetryOptions options, ITelemetryValueMasker? masker = null)
    {
        _maxRecords = options.MaxQueuedRecords;
        _maxBytes = options.MaxQueuedBytes;
        _maxRecordsPerEnvironment = options.MaxQueuedRecordsPerEnvironment;
        _maxBytesPerEnvironment = options.MaxQueuedBytesPerEnvironment;
        if (options.StoreRoot is not null)
        {
            _journal = new TelemetryBatchJournal(options.StoreRoot, masker);
            foreach (var recovered in _journal.Recover())
            {
                Add(recovered.Environment, recovered.RecordCount, recovered.EstimatedBytes);
                _channel.Writer.TryWrite(recovered);
            }
        }
    }

    public long PendingRecords => Interlocked.Read(ref _pendingRecords);

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    public long RefusedBatches => Interlocked.Read(ref _refusedBatches);

    /// <summary>Records and estimated bytes waiting for one environment.</summary>
    public (long Records, long Bytes) Pending(TelemetryEnvironmentKey environment)
    {
        lock (_gate) return _environments.GetValueOrDefault(environment);
    }

    /// <summary>
    /// Whether a request of <paramref name="environment"/> could be queued at
    /// all; checked before its body is read, so a full queue costs no decoding.
    /// </summary>
    public bool HasRoom(TelemetryEnvironmentKey environment)
    {
        lock (_gate) return Fits(environment, 1, 1);
    }

    public TelemetryEnqueueResult TryEnqueue(TelemetryIngestBatch batch)
    {
        var records = batch.RecordCount;
        var bytes = batch.EstimatedBytes;
        if (records == 0) return TelemetryEnqueueResult.Accepted;
        if (records > _maxRecordsPerEnvironment || bytes > _maxBytesPerEnvironment) return TelemetryEnqueueResult.TooLarge;
        lock (_gate)
        {
            if (!Fits(batch.Environment, records, bytes))
            {
                Interlocked.Increment(ref _refusedBatches);
                return TelemetryEnqueueResult.Busy;
            }
            try
            {
                batch = _journal?.Stage(batch) ?? batch;
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _refusedBatches);
                return TelemetryEnqueueResult.Unavailable;
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _refusedBatches);
                return TelemetryEnqueueResult.Unavailable;
            }
            // The writer releases under the same lock, so it cannot see the batch before it is counted.
            Add(batch.Environment, records, bytes);
            _channel.Writer.TryWrite(batch);
            return TelemetryEnqueueResult.Accepted;
        }
    }

    internal bool TryDequeue(out TelemetryIngestBatch batch) => _channel.Reader.TryRead(out batch!);

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);

    internal void Release(TelemetryIngestBatch batch)
    {
        _journal?.Complete(batch);
        lock (_gate) Add(batch.Environment, -batch.RecordCount, -batch.EstimatedBytes);
    }

    internal void Retry(TelemetryIngestBatch batch) => _channel.Writer.TryWrite(batch);

    private bool Fits(TelemetryEnvironmentKey environment, long records, long bytes)
    {
        var (environmentRecords, environmentBytes) = _environments.GetValueOrDefault(environment);
        return _pendingRecords + records <= _maxRecords && _pendingBytes + bytes <= _maxBytes
            && environmentRecords + records <= _maxRecordsPerEnvironment && environmentBytes + bytes <= _maxBytesPerEnvironment;
    }

    private void Add(TelemetryEnvironmentKey environment, long records, long bytes)
    {
        Interlocked.Add(ref _pendingRecords, records);
        Interlocked.Add(ref _pendingBytes, bytes);
        var (environmentRecords, environmentBytes) = _environments.GetValueOrDefault(environment);
        var updated = (environmentRecords + records, environmentBytes + bytes);
        if (updated.Item1 <= 0) _environments.Remove(environment);
        else _environments[environment] = updated;
    }
}
