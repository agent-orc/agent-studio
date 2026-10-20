using Microsoft.Data.Sqlite;

namespace AgentStudio.Telemetry;

/// <summary>
/// The only writer of the telemetry store. It drains the intake queue in
/// batches of up to <see cref="MaxRecordsPerFlush"/> records,
/// <see cref="MaxBytesPerFlush"/> estimated bytes, or
/// <see cref="TelemetryOptions.MaxStoredBytesPerRequest"/> stored bytes, groups them per environment
/// into one transaction each, masks log bodies and the attribute values of logs
/// and metrics, calls the export hooks, and runs the retention sweep and the
/// global minute- and hour-bucket caps every <see cref="TelemetryOptions.RetentionSweepMinutes"/>.
/// Studio requests never wait for this loop.
/// </summary>
public sealed class TelemetryWriter : BackgroundService
{
    public const int MaxRecordsPerFlush = 20_000;
    public const long MaxBytesPerFlush = 32L * 1024 * 1024;
    private readonly TelemetryIntakeQueue _queue;
    private readonly TelemetryStore _store;
    private readonly IReadOnlyList<ITelemetryExportSink> _sinks;
    private readonly ITelemetryValueMasker _masker;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelemetryWriter> _logger;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private bool _retryAfterFailure;

    public TelemetryWriter(TelemetryIntakeQueue queue, TelemetryStore store, IEnumerable<ITelemetryExportSink> sinks,
        ITelemetryValueMasker masker, TelemetryOptions options, TimeProvider clock, ILogger<TelemetryWriter> logger)
    {
        _queue = queue;
        _store = store;
        _sinks = sinks.ToList();
        _masker = masker;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave host startup before the first retention sweep touches the disk.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await DrainOnceAsync(stoppingToken) > 0)
                {
                    // Keep draining while a burst is still queued.
                }
                if (_clock.GetUtcNow() >= _nextSweep) SweepRetention();
                if (_retryAfterFailure)
                {
                    _retryAfterFailure = false;
                    await Task.Delay(TimeSpan.FromSeconds(1), _clock, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed flush must not stop the host; the next loop retries with new data.
                _logger.LogError(ex, "Telemetry writer loop failed");
            }
            // Wake on new data or after a minute for the sweep; cancel whichever wait lost.
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(
                _queue.WaitToReadAsync(wake.Token).AsTask(),
                Task.Delay(TimeSpan.FromMinutes(1), _clock, wake.Token));
            await wake.CancelAsync();
        }
    }

    /// <summary>Commits staged batches individually; a failed batch stays staged and is retried.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        long bytes = 0, stored = 0;
        var mask = new FlushMask(_masker);
        while (handled < MaxRecordsPerFlush && bytes < MaxBytesPerFlush && stored < _options.MaxStoredBytesPerRequest
               && _queue.TryDequeue(out var batch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metrics = batch.Metrics.Select(sample => sample with
                    { Attributes = mask.Attributes(sample.Attributes) }).ToList();
                var logs = batch.Logs.Select(log => log with
                    { Body = _masker.Mask(log.Body), SeverityText = _masker.Mask(log.SeverityText),
                        EventName = log.EventName is null ? null : _masker.Mask(log.EventName),
                        Attributes = mask.Attributes(log.Attributes) }).ToList();
                var result = _store.Write(batch.Environment, metrics, logs, _clock.GetUtcNow(), batch.StageId);
                if (result.LogsEvictedForSize > 0)
                    _logger.LogInformation("Telemetry log cap reached for {Environment}; removed {Count} oldest records",
                        batch.Environment, result.LogsEvictedForSize);
                _queue.Release(batch);
                if (batch.StageId is { } committedId)
                {
                    try { _store.ForgetCommittedBatch(batch.Environment, committedId); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Telemetry replay marker cleanup failed for {Environment}", batch.Environment);
                    }
                }
                await ExportAsync(new TelemetryExportBatch(batch.Environment, metrics, logs), cancellationToken);
                handled += batch.RecordCount;
                bytes += batch.EstimatedBytes;
                stored += batch.StoredBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telemetry write failed for {Environment}; staged batch will be retried", batch.Environment);
                _queue.Retry(batch);
                _retryAfterFailure = true;
                break;
            }
        }
        return handled;
    }

    /// <summary>Applies retention to every environment database, then the global minute- and hour-bucket caps.</summary>
    public void SweepRetention()
    {
        var now = _clock.GetUtcNow();
        _nextSweep = now + TimeSpan.FromMinutes(_options.RetentionSweepMinutes);
        var environments = _store.KnownEnvironments();
        foreach (var environment in environments)
        {
            try
            {
                var removed = _store.EnforceRetention(environment, now);
                if (removed.MinuteBuckets + removed.HourBuckets + removed.Series + removed.Logs > 0)
                    _logger.LogInformation(
                        "Telemetry retention for {Environment} removed {Minutes} minute buckets, {Hours} hour buckets, {Series} series, {Logs} log records",
                        environment, removed.MinuteBuckets, removed.HourBuckets, removed.Series, removed.Logs);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Telemetry retention failed for {Environment}", environment);
            }
        }
        try
        {
            var trimmed = _store.EnforceMinuteBucketCap(environments);
            if (trimmed.Buckets > 0)
                _logger.LogWarning(
                    "Telemetry minute buckets exceeded {Limit} bytes; removed {Buckets} buckets older than {Cutoff:o} (hour buckets kept)",
                    _options.MaxMinuteBucketBytes, trimmed.Buckets, trimmed.Cutoff);
            trimmed = _store.EnforceHourBucketCap(environments);
            if (trimmed.Buckets > 0)
                _logger.LogWarning("Telemetry hour buckets exceeded {Limit} bytes; removed {Buckets} buckets older than {Cutoff:o}",
                    _options.MaxHourBucketBytes, trimmed.Buckets, trimmed.Cutoff);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Telemetry bucket cap failed");
        }
    }

    private async Task ExportAsync(TelemetryExportBatch batch, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.ExportAsync(batch, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Telemetry export sink {Sink} failed for {Environment}", sink.Name, batch.Environment);
            }
        }
    }

    /// <summary>
    /// Masks attribute values for one flush. A shared resource and scope set is
    /// masked once and stays shared, and a value string that many records refer
    /// to is masked once.
    /// </summary>
    private sealed class FlushMask(ITelemetryValueMasker masker)
    {
        private readonly Dictionary<TelemetryAttributes, TelemetryAttributes> _sets = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _values = new(ReferenceEqualityComparer.Instance);

        public TelemetryAttributes Attributes(TelemetryAttributes attributes)
        {
            if (!attributes.IsShared) return attributes.MapValues(Value);
            if (!_sets.TryGetValue(attributes, out var masked)) _sets[attributes] = masked = attributes.MapValues(Value);
            return masked;
        }

        private string Value(string value)
        {
            if (!_values.TryGetValue(value, out var masked)) _values[value] = masked = masker.Mask(value);
            return masked;
        }
    }
}
