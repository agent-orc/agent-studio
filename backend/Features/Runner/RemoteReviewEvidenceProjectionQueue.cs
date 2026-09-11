using System.Diagnostics;
using System.Threading.Channels;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Point-in-time throughput read of the evidence-projection queue over a
/// trailing window, mirroring <see cref="AutoReviewQueueTelemetry"/>. Computed
/// by the pure <see cref="Summarize"/> function so the windowing and median
/// math have direct matrix test coverage.
/// </summary>
public sealed record RemoteReviewEvidenceProjectionThroughput
{
    public double DrainRatePerMinute { get; init; }
    public double? MedianDurationMs { get; init; }
    public int SampleCount { get; init; }
    public double WindowMinutes { get; init; }
}

public readonly record struct RemoteReviewEvidenceProjectionSample(
    DateTime CompletedAtUtc,
    double ElapsedMs,
    bool Succeeded);

/// <summary>
/// Bounded rolling record of completed evidence-projection passes. Same shape
/// as <see cref="AutoReviewQueueTelemetry"/>; kept as a separate type because
/// the two queues drain independently and a shared instance would blend their
/// throughput.
/// </summary>
public sealed class RemoteReviewEvidenceProjectionTelemetry
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<RemoteReviewEvidenceProjectionSample> _samples = new();

    public RemoteReviewEvidenceProjectionTelemetry(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void RecordCompletion(DateTime completedAtUtc, TimeSpan elapsed, bool succeeded)
    {
        lock (_gate)
        {
            _samples.Enqueue(new RemoteReviewEvidenceProjectionSample(
                completedAtUtc.ToUniversalTime(), elapsed.TotalMilliseconds, succeeded));
            while (_samples.Count > _capacity) _samples.Dequeue();
        }
    }

    public RemoteReviewEvidenceProjectionThroughput Summarize(DateTime nowUtc, TimeSpan window)
    {
        List<RemoteReviewEvidenceProjectionSample> snapshot;
        lock (_gate) snapshot = [.. _samples];
        return Summarize(snapshot, nowUtc.ToUniversalTime(), window);
    }

    /// <summary>Pure projection: no I/O, no locking - takes a snapshot and a point in time so it is directly matrix-testable.</summary>
    internal static RemoteReviewEvidenceProjectionThroughput Summarize(
        IReadOnlyList<RemoteReviewEvidenceProjectionSample> samples,
        DateTime nowUtc,
        TimeSpan window)
    {
        var cutoff = nowUtc - window;
        var inWindow = samples.Where(s => s.CompletedAtUtc >= cutoff).ToList();
        var windowMinutes = Math.Max(window.TotalMinutes, 1e-9);

        double? median = null;
        if (inWindow.Count > 0)
        {
            var durations = inWindow.Select(s => s.ElapsedMs).OrderBy(x => x).ToList();
            var mid = durations.Count / 2;
            median = durations.Count % 2 == 1
                ? durations[mid]
                : (durations[mid - 1] + durations[mid]) / 2.0;
        }

        return new RemoteReviewEvidenceProjectionThroughput
        {
            DrainRatePerMinute = Math.Round(inWindow.Count / windowMinutes, 2),
            MedianDurationMs = median.HasValue ? Math.Round(median.Value, 0) : null,
            SampleCount = inWindow.Count,
            WindowMinutes = window.TotalMinutes,
        };
    }
}

/// <summary>
/// One settled report's evidence-projection work: the task-folder grade
/// markdown, aspect files, tool-gate pipeline step, and timeline entries
/// (<see cref="RemoteReviewReportEvidence"/> and
/// <see cref="RemotePipelineReviewEvidenceProjector"/>). The report endpoint
/// enqueues this immediately after settling the attempt authority so the HTTP
/// response does not wait on task-folder I/O (AGT-2762).
/// </summary>
public sealed record RemoteReviewEvidenceProjectionRequest(
    string AttemptId,
    string TaskKey,
    ReviewAttemptDto Review,
    Contract.ReviewReportRequest Report,
    string EvidenceFile,
    string ReportSha256,
    DateTime ReceivedAt,
    DateTime EnqueuedAtUtc,
    int Attempt = 0);

public interface IRemoteReviewEvidenceProjectionQueue
{
    void Enqueue(RemoteReviewEvidenceProjectionRequest request);
}

/// <summary>
/// Durable-enough hand-off from the report endpoint to the evidence-projection
/// worker. The settlement itself already persisted the fenced outcome; this
/// queue only defers the slower task-folder write so the endpoint can
/// acknowledge within the runner's report timeout regardless of host load.
/// </summary>
public sealed class RemoteReviewEvidenceProjectionQueue : IRemoteReviewEvidenceProjectionQueue
{
    private readonly Channel<RemoteReviewEvidenceProjectionRequest> _channel =
        Channel.CreateUnbounded<RemoteReviewEvidenceProjectionRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public RemoteReviewEvidenceProjectionTelemetry Telemetry { get; } = new();

    public ChannelReader<RemoteReviewEvidenceProjectionRequest> Reader => _channel.Reader;

    public void Enqueue(RemoteReviewEvidenceProjectionRequest request)
        => _channel.Writer.TryWrite(request);
}

/// <summary>
/// Drains the evidence-projection queue, one report at a time per card but
/// concurrently across cards. A failure retries with bounded exponential
/// backoff; the retry is re-enqueued rather than blocking the reader, so one
/// stuck card cannot stall every other pending projection.
/// </summary>
public sealed class RemoteReviewEvidenceProjectionWorker : BackgroundService
{
    /// <summary>Highest number of automatic retries for a failed projection write.</summary>
    internal const int MaxRetries = 5;

    /// <summary>First retry delay; doubles per attempt.</summary>
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>Cap for the doubling.</summary>
    internal static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(2);

    /// <summary>Projection passes slower than this log a warning (Changes item 5).</summary>
    internal static readonly TimeSpan SlowProjectionThreshold = TimeSpan.FromSeconds(30);

    private readonly RemoteReviewEvidenceProjectionQueue _queue;
    private readonly TaskScannerService _scanner;
    private readonly RemotePipelineReviewEvidenceProjector _projector;
    private readonly ILogger<RemoteReviewEvidenceProjectionWorker> _logger;

    /// <summary>Test seam: replaces the retry backoff so tests do not wait out the real schedule. Null in production.</summary>
    internal Func<int, TimeSpan>? RetryDelayOverride { get; set; }

    public RemoteReviewEvidenceProjectionWorker(
        RemoteReviewEvidenceProjectionQueue queue,
        TaskScannerService scanner,
        RemotePipelineReviewEvidenceProjector projector,
        ILogger<RemoteReviewEvidenceProjectionWorker> logger)
    {
        _queue = queue;
        _scanner = scanner;
        _projector = projector;
        _logger = logger;
    }

    internal static TimeSpan RetryDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt));
        var seconds = RetryBaseDelay.TotalSeconds * factor;
        return seconds >= RetryMaxDelay.TotalSeconds ? RetryMaxDelay : TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(request, stoppingToken);
                }
                catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
                {
                    SilentCatch.Note(ex, "RemoteReviewEvidenceProjectionWorker: projection cancelled on graceful shutdown.");
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "remote-review-evidence-projection-worker-task-failed attempt={AttemptId}",
                        request.AttemptId);
                }
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "RemoteReviewEvidenceProjectionWorker: graceful shutdown.");
        }
    }

    /// <summary>Processes one queued projection. Exposed for deterministic tests.</summary>
    internal async Task ProcessAsync(RemoteReviewEvidenceProjectionRequest request, CancellationToken ct)
    {
        var task = _scanner.ScanAllJobsWithArchive().FirstOrDefault(candidate =>
            string.Equals(candidate.TaskKey, request.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Key, request.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Id, request.TaskKey, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            _logger.LogWarning(
                "remote-review-evidence-projection-task-missing attempt={AttemptId} task={TaskKey}",
                request.AttemptId, request.TaskKey);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var evidenceFile = await RemoteReviewReportEvidence.WriteAsync(
                task.FolderPath,
                request.AttemptId,
                request.Review.Subject.SubjectId,
                request.Report,
                request.ReportSha256,
                request.ReceivedAt,
                ct);
            await _projector.ProjectAsync(
                task,
                request.Review,
                request.Report,
                evidenceFile,
                request.ReceivedAt,
                ct);
            sw.Stop();
            _queue.Telemetry.RecordCompletion(DateTime.UtcNow, sw.Elapsed, succeeded: true);
            var queueWaitMs = (long)Math.Max(0, (DateTime.UtcNow - request.EnqueuedAtUtc).TotalMilliseconds);
            _logger.LogInformation(
                "remote-review-evidence-projection-finished attempt={AttemptId} task={TaskKey} "
                + "queueWaitMs={QueueWaitMs} projectionMs={ProjectionMs}",
                request.AttemptId, request.TaskKey, queueWaitMs, sw.ElapsedMilliseconds);
            if (sw.Elapsed > SlowProjectionThreshold)
            {
                _logger.LogWarning(
                    "remote-review-evidence-projection-slow attempt={AttemptId} task={TaskKey} projectionMs={ProjectionMs}",
                    request.AttemptId, request.TaskKey, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            sw.Stop();
            _queue.Telemetry.RecordCompletion(DateTime.UtcNow, sw.Elapsed, succeeded: false);
            if (request.Attempt >= MaxRetries)
            {
                _logger.LogWarning(
                    exception,
                    "remote-review-evidence-projection-exhausted attempt={AttemptId} task={TaskKey} attempts={Attempts}",
                    request.AttemptId, request.TaskKey, request.Attempt);
                return;
            }

            var delay = RetryDelayOverride?.Invoke(request.Attempt) ?? RetryDelay(request.Attempt);
            _logger.LogWarning(
                exception,
                "remote-review-evidence-projection-retry attempt={AttemptId} task={TaskKey} "
                + "retryAttempt={RetryAttempt} retryInMs={RetryInMs}",
                request.AttemptId, request.TaskKey, request.Attempt, (long)delay.TotalMilliseconds);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, CancellationToken.None);
                    _queue.Enqueue(request with { Attempt = request.Attempt + 1, EnqueuedAtUtc = DateTime.UtcNow });
                }
                catch (Exception retryException)
                {
                    _logger.LogWarning(
                        retryException,
                        "remote-review-evidence-projection-retry-enqueue-failed attempt={AttemptId}",
                        request.AttemptId);
                }
            }, CancellationToken.None);
        }
    }
}
