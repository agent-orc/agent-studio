using Microsoft.Data.Sqlite;

namespace AgentStudio.Telemetry;

/// <summary>
/// The only writer of the telemetry store. It drains the intake queue in
/// batches of up to <see cref="MaxRecordsPerFlush"/> records, groups them per
/// environment into one transaction each, masks log text, calls the export
/// hooks, and runs the retention sweep every
/// <see cref="TelemetryOptions.RetentionSweepMinutes"/>. Studio requests never
/// wait for this loop.
/// </summary>
public sealed class TelemetryWriter : BackgroundService
{
    public const int MaxRecordsPerFlush = 20_000;
    private readonly TelemetryIntakeQueue _queue;
    private readonly TelemetryStore _store;
    private readonly IReadOnlyList<ITelemetryExportSink> _sinks;
    private readonly ITelemetryValueMasker _masker;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelemetryWriter> _logger;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

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

    /// <summary>Writes what is queued now, up to one flush; returns the number of records handled.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        var batches = new List<TelemetryIngestBatch>();
        var records = 0;
        while (records < MaxRecordsPerFlush && _queue.TryDequeue(out var batch))
        {
            batches.Add(batch);
            records += batch.RecordCount;
        }
        if (batches.Count == 0) return 0;
        try
        {
            foreach (var group in batches.GroupBy(batch => batch.Environment))
            {
                var metrics = group.SelectMany(batch => batch.Metrics).ToList();
                var logs = group.SelectMany(batch => batch.Logs).Select(Mask).ToList();
                try
                {
                    var result = _store.Write(group.Key, metrics, logs, _clock.GetUtcNow());
                    if (result.LogsEvictedForSize > 0)
                        _logger.LogInformation("Telemetry log cap reached for {Environment}; removed {Count} oldest records",
                            group.Key, result.LogsEvictedForSize);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Telemetry write failed for {Environment}; dropped {Metrics} points and {Logs} log records",
                        group.Key, metrics.Count, logs.Count);
                    continue;
                }
                await ExportAsync(new TelemetryExportBatch(group.Key, metrics, logs), cancellationToken);
            }
        }
        finally
        {
            _queue.Release(records);
        }
        return records;
    }

    /// <summary>Applies retention to every environment database.</summary>
    public void SweepRetention()
    {
        var now = _clock.GetUtcNow();
        _nextSweep = now + TimeSpan.FromMinutes(_options.RetentionSweepMinutes);
        foreach (var environment in _store.KnownEnvironments())
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
    }

    private TelemetryLogRecord Mask(TelemetryLogRecord log)
        => log with { Body = _masker.Mask(log.Body), Attributes = log.Attributes.MapValues(_masker.Mask) };

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
}
