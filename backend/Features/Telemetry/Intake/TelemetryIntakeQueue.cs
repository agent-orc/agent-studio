using System.Threading.Channels;

namespace AgentStudio.Telemetry;

public enum TelemetryEnqueueResult { Accepted, Busy, TooLarge }

/// <summary>
/// Hand-off between OTLP requests and the background writer. Enqueueing never
/// waits: when the records or the estimated bytes already waiting would exceed
/// <see cref="TelemetryOptions.MaxQueuedRecords"/> or
/// <see cref="TelemetryOptions.MaxQueuedBytes"/>, the request is refused and
/// the sender retries later (OTLP/HTTP 429 with Retry-After). The budget is
/// released when the writer has handled a batch, stored or not.
/// </summary>
public sealed class TelemetryIntakeQueue
{
    private readonly Channel<TelemetryIngestBatch> _channel =
        Channel.CreateUnbounded<TelemetryIngestBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly long _maxRecords;
    private readonly long _maxBytes;
    private long _pendingRecords;
    private long _pendingBytes;
    private long _refusedBatches;

    public TelemetryIntakeQueue(TelemetryOptions options)
    {
        _maxRecords = options.MaxQueuedRecords;
        _maxBytes = options.MaxQueuedBytes;
    }

    public long PendingRecords => Interlocked.Read(ref _pendingRecords);

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    public long RefusedBatches => Interlocked.Read(ref _refusedBatches);

    public TelemetryEnqueueResult TryEnqueue(TelemetryIngestBatch batch)
    {
        var records = batch.RecordCount;
        var bytes = batch.EstimatedBytes;
        if (records == 0) return TelemetryEnqueueResult.Accepted;
        if (records > _maxRecords || bytes > _maxBytes) return TelemetryEnqueueResult.TooLarge;
        var pendingRecords = Interlocked.Add(ref _pendingRecords, records);
        var pendingBytes = Interlocked.Add(ref _pendingBytes, bytes);
        if (pendingRecords > _maxRecords || pendingBytes > _maxBytes || !_channel.Writer.TryWrite(batch))
        {
            Release(records, bytes);
            Interlocked.Increment(ref _refusedBatches);
            return TelemetryEnqueueResult.Busy;
        }
        return TelemetryEnqueueResult.Accepted;
    }

    internal bool TryDequeue(out TelemetryIngestBatch batch) => _channel.Reader.TryRead(out batch!);

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);

    internal void Release(long records, long bytes)
    {
        Interlocked.Add(ref _pendingRecords, -records);
        Interlocked.Add(ref _pendingBytes, -bytes);
    }
}
