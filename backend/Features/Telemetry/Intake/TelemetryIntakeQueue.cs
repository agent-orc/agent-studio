namespace AgentStudio.Telemetry;

public enum TelemetryEnqueueResult { Accepted, Busy, TooLarge, TooManySeries, Unavailable }

/// <summary>
/// Hand-off between OTLP requests and the background writer. Enqueueing does
/// not wait for queue capacity: when the records or the estimated bytes already waiting would exceed
/// <see cref="TelemetryOptions.MaxQueuedRecords"/> or
/// <see cref="TelemetryOptions.MaxQueuedBytes"/>, the request is refused and
/// the sender retries later (OTLP/HTTP 429 with Retry-After). One project
/// environment may fill only <see cref="TelemetryOptions.MaxQueueSharePercentPerEnvironment"/>
/// of each budget, so it cannot keep the queue full for the others. The budget
/// is released only after the writer commits the staged batch.
/// </summary>
public sealed class TelemetryIntakeQueue
{
    private readonly LinkedList<TelemetryIngestBatch> _batches = new();
    private TaskCompletionSource<bool> _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly TelemetryBatchJournal? _journal;
    private readonly TelemetryStore? _store;
    private readonly ITelemetryValueMasker? _masker;
    private readonly List<TelemetryIngestBatch> _outstanding = [];
    private readonly Dictionary<TelemetryEnvironmentKey, (long Records, long Bytes)> _environments = [];
    private readonly long _maxRecords;
    private readonly long _maxBytes;
    private readonly long _maxRecordsPerEnvironment;
    private readonly long _maxBytesPerEnvironment;
    private long _pendingRecords;
    private long _pendingBytes;
    private long _refusedBatches;

    public TelemetryIntakeQueue(TelemetryOptions options, ITelemetryValueMasker? masker = null, TelemetryStore? store = null)
    {
        _store = store;
        _masker = masker;
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
                _outstanding.Add(recovered);
                _batches.AddLast(recovered);
            }
            if (_batches.Count > 0) _available.TrySetResult(true);
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
                if (batch.Metrics.Count > 0 && _store?.WouldRejectSeries(batch.Environment,
                    _outstanding.Where(item => item.Environment == batch.Environment)
                        .SelectMany(item => item.Metrics).Concat(batch.Metrics.Select(item => _masker is null
                            ? item : item with { Attributes = item.Attributes.MapValues(_masker.Mask) }))) == true)
                    return TelemetryEnqueueResult.TooManySeries;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                Interlocked.Increment(ref _refusedBatches);
                return TelemetryEnqueueResult.Unavailable;
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
            _outstanding.Add(batch);
            _batches.AddLast(batch);
            _available.TrySetResult(true);
            return TelemetryEnqueueResult.Accepted;
        }
    }

    internal bool TryDequeue(out TelemetryIngestBatch batch)
    {
        lock (_gate)
        {
            if (_batches.First is null) { batch = null!; return false; }
            batch = _batches.First.Value;
            _batches.RemoveFirst();
            if (_batches.Count == 0) _available = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return _batches.Count > 0 ? ValueTask.FromResult(true)
            : new ValueTask<bool>(_available.Task.WaitAsync(cancellationToken));
    }

    internal void Release(TelemetryIngestBatch batch)
    {
        _journal?.Complete(batch);
        lock (_gate)
        {
            _outstanding.Remove(batch);
            Add(batch.Environment, -batch.RecordCount, -batch.EstimatedBytes);
        }
    }

    internal void Retry(TelemetryIngestBatch batch)
    {
        lock (_gate)
        {
            _batches.AddFirst(batch);
            _available.TrySetResult(true);
        }
    }

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
