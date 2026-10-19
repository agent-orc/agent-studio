using System.Threading.Channels;

namespace AgentStudio.Telemetry;

public enum TelemetryEnqueueResult { Accepted, Busy, TooLarge }

/// <summary>
/// Hand-off between OTLP requests and the background writer. Enqueueing never
/// waits: when the records already waiting would exceed
/// <see cref="TelemetryOptions.MaxQueuedRecords"/>, the request is refused and
/// the sender retries later (OTLP/HTTP 429 with Retry-After). The budget is
/// released when the writer has handled a batch, stored or not.
/// </summary>
public sealed class TelemetryIntakeQueue
{
    private readonly Channel<TelemetryIngestBatch> _channel =
        Channel.CreateUnbounded<TelemetryIngestBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly long _maxRecords;
    private long _pendingRecords;
    private long _refusedBatches;

    public TelemetryIntakeQueue(TelemetryOptions options) => _maxRecords = options.MaxQueuedRecords;

    public long PendingRecords => Interlocked.Read(ref _pendingRecords);

    public long RefusedBatches => Interlocked.Read(ref _refusedBatches);

    public TelemetryEnqueueResult TryEnqueue(TelemetryIngestBatch batch)
    {
        var records = batch.RecordCount;
        if (records == 0) return TelemetryEnqueueResult.Accepted;
        if (records > _maxRecords) return TelemetryEnqueueResult.TooLarge;
        if (Interlocked.Add(ref _pendingRecords, records) > _maxRecords || !_channel.Writer.TryWrite(batch))
        {
            Interlocked.Add(ref _pendingRecords, -records);
            Interlocked.Increment(ref _refusedBatches);
            return TelemetryEnqueueResult.Busy;
        }
        return TelemetryEnqueueResult.Accepted;
    }

    internal bool TryDequeue(out TelemetryIngestBatch batch) => _channel.Reader.TryRead(out batch!);

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);

    internal void Release(long records) => Interlocked.Add(ref _pendingRecords, -records);
}
