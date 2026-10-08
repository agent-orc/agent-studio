using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

public static class BatchMoveJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Completed or Failed;
}

public sealed record BatchMoveJobItemResult
{
    public int Index { get; init; }
    public string JobId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public double DurationMs { get; init; }
}

public sealed record BatchMoveJobMetrics
{
    public double TotalDurationMs { get; init; }
    public double ItemMoveDurationMs { get; init; }
    public int LaneLockAcquisitions { get; init; }
    public double LaneLockWaitMs { get; init; }
    public double LaneLockHeldMs { get; init; }
    public int ScannerInvalidations { get; init; }
    public int ScannerRefreshes { get; init; }
    public double ScannerRefreshMs { get; init; }
    public int GitProcesses { get; init; }
    public double GitProcessMs { get; init; }
}

public sealed record BatchMoveJobResponse
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = BatchMoveJobStates.Queued;
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<BatchMoveJobItemResult> Results { get; init; } = [];
    public BatchMoveJobMetrics Metrics { get; init; } = new();
    public string? Message { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

internal sealed class BatchMoveJob
{
    private readonly object _gate = new();
    private readonly List<BatchMoveJobItemResult> _results = [];
    private string _status = BatchMoveJobStates.Queued;
    private int _succeeded;
    private int _failed;
    private BatchMoveJobMetrics _metrics = new();
    private string? _message;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public BatchMoveJob(
        string id,
        IReadOnlyList<BatchMoveItem> items,
        IReadOnlyList<string?> projectNames,
        string cause)
    {
        Id = id;
        Items = items;
        ProjectNames = projectNames;
        Cause = cause;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public IReadOnlyList<BatchMoveItem> Items { get; }
    public IReadOnlyList<string?> ProjectNames { get; }
    public string Cause { get; }
    public DateTimeOffset CreatedAt { get; }

    public void Start()
    {
        lock (_gate)
        {
            _status = BatchMoveJobStates.Running;
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Record(BatchMoveJobItemResult result)
    {
        lock (_gate)
        {
            _results.Add(result);
            if (string.Equals(result.Status, "moved", StringComparison.Ordinal)) _succeeded++;
            else _failed++;
        }
    }

    public void Finish(BatchMoveJobMetrics metrics)
    {
        lock (_gate)
        {
            _metrics = metrics;
            _status = BatchMoveJobStates.Completed;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string message, BatchMoveJobMetrics? metrics = null)
    {
        lock (_gate)
        {
            _message = message;
            if (metrics is not null) _metrics = metrics;
            _status = BatchMoveJobStates.Failed;
            _finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public BatchMoveJobResponse Snapshot()
    {
        lock (_gate)
        {
            return new BatchMoveJobResponse
            {
                Id = Id,
                Status = _status,
                Total = Items.Count,
                Completed = _results.Count,
                Succeeded = _succeeded,
                Failed = _failed,
                Results = _results.ToArray(),
                Metrics = _metrics,
                Message = _message,
                CreatedAt = CreatedAt,
                StartedAt = _startedAt,
                FinishedAt = _finishedAt,
            };
        }
    }
}

public interface IBatchMoveItemExecutor
{
    Task<BatchMoveItemResult> ExecuteAsync(
        BatchMoveItem item,
        CancellationToken cancellationToken,
        string cause);
}

public sealed class BatchMoveItemExecutor(
    TaskTransitionService transitions,
    TaskScannerService scanner,
    RunLeaseService leases,
    AttemptAuthorityService authority,
    TimelineLog timeline) : IBatchMoveItemExecutor
{
    public async Task<BatchMoveItemResult> ExecuteAsync(
        BatchMoveItem item,
        CancellationToken cancellationToken,
        string cause)
    {
        await LeaseEndpoints.ClaimGate.WaitAsync(cancellationToken);
        try
        {
            var card = scanner.FindJob(item.JobId, item.WatchPath);
            var live = card is not null && item.TargetState != card.State
                ? leases.Peek(card.TaskKey).Lease : null;
            var intent = RunDispositionPolicy.ParseMoveIntent(item.RunIntent);
            if (live is not null && intent is null)
                return new BatchMoveItemResult { JobId = item.JobId, Status = "rejected", Message = "run-intent-required" };
            if (live is not null && intent == RunMoveIntent.Steer
                && card!.PendingIntent?.RunIntent != "steer")
                return new BatchMoveItemResult { JobId = item.JobId, Status = "rejected", Message = "steer-follow-up-required" };

            var result = await transitions.MoveBatchItemAsync(item, cancellationToken, cause);
            if (result.Status == "moved" && live?.AttemptId is { } attemptId
                && intent == RunMoveIntent.Revoke
                && authority.RevokeRunForOperatorMove(card!.TaskKey, attemptId,
                    item.Reason ?? "Operator batch move revoked the run."))
            {
                var moved = scanner.FindJob(item.JobId, item.WatchPath);
                if (moved is not null)
                    timeline.Append(moved.FolderPath, TimelineEventKinds.RunAttemptRevoked, cause,
                        $"Run {attemptId} was revoked by the operator batch move. Its result remains reference material; it cannot deliver this card.",
                        attemptId,
                        details: new Dictionary<string, string>
                        {
                            ["attemptId"] = attemptId,
                            ["fence"] = live.FencingToken.ToString(),
                            ["runIntent"] = "revoke",
                        });
            }
            return result;
        }
        finally
        {
            LeaseEndpoints.ClaimGate.Release();
        }
    }
}

/// <summary>
/// Owns the in-memory batch-move queue and its progress snapshots. One reader
/// processes a batch item at a time, so a 55-card UI action occupies one
/// background worker instead of 55 HTTP request threads. Each item still uses
/// <see cref="TaskTransitionService"/> independently and therefore acquires
/// the project lane mutex only for that card's bounded state mutation.
/// </summary>
public sealed class BatchMoveJobCoordinator : BackgroundService
{
    private const int RetainedJobLimit = 256;
    private readonly Channel<BatchMoveJob> _queue = Channel.CreateUnbounded<BatchMoveJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, BatchMoveJob> _jobs = new(StringComparer.Ordinal);
    private readonly IBatchMoveItemExecutor _executor;
    private readonly ILogger<BatchMoveJobCoordinator> _logger;

    public BatchMoveJobCoordinator(
        IBatchMoveItemExecutor executor,
        ILogger<BatchMoveJobCoordinator> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public BatchMoveJobResponse Enqueue(
        IReadOnlyList<BatchMoveItem> items,
        IReadOnlyList<string?> projectNames,
        string cause)
    {
        PruneCompletedJobs();
        var id = $"batch-{Guid.NewGuid():N}";
        var job = new BatchMoveJob(id, items.ToArray(), projectNames.ToArray(), cause);
        if (!_jobs.TryAdd(id, job) || !_queue.Writer.TryWrite(job))
        {
            _jobs.TryRemove(id, out _);
            throw new InvalidOperationException("The batch move queue is unavailable.");
        }

        _logger.LogInformation("batch-move-queued batchId={BatchId} total={Total}", id, items.Count);
        return job.Snapshot();
    }

    public bool TryGet(string id, out BatchMoveJobResponse? response)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            response = job.Snapshot();
            return true;
        }

        response = null;
        return false;
    }

    public bool TryGetProjectNames(string id, out IReadOnlyList<string?> projectNames)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            projectNames = job.ProjectNames;
            return true;
        }

        projectNames = [];
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(BatchMoveJob job, CancellationToken stoppingToken)
    {
        job.Start();
        var total = Stopwatch.StartNew();
        var itemMoveMs = 0.0;
        using var operationTelemetry = BatchMoveOperationTelemetry.Begin();
        using var gitTelemetry = GitProcessTelemetry.BeginRequest(
            $"tasks/batch-move/{job.Id}", _logger, includeNested: true);

        try
        {
            for (var index = 0; index < job.Items.Count; index++)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var item = job.Items[index];
                var itemTimer = Stopwatch.StartNew();
                BatchMoveItemResult result;
                try
                {
                    result = await _executor
                        .ExecuteAsync(item, stoppingToken, job.Cause)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "batch-move-item-unhandled batchId={BatchId} index={Index} jobId={JobId}",
                        job.Id,
                        index,
                        item.JobId);
                    result = new BatchMoveItemResult
                    {
                        JobId = item.JobId,
                        Status = "failed",
                        Message = ex.Message,
                    };
                }
                itemTimer.Stop();
                itemMoveMs += itemTimer.Elapsed.TotalMilliseconds;
                job.Record(new BatchMoveJobItemResult
                {
                    Index = index,
                    JobId = result.JobId,
                    Status = result.Status,
                    Message = result.Message,
                    DurationMs = itemTimer.Elapsed.TotalMilliseconds,
                });

                if (!string.Equals(result.Status, "moved", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "batch-move-item-failed batchId={BatchId} index={Index} jobId={JobId} status={Status} message={Message}",
                        job.Id,
                        index,
                        result.JobId,
                        result.Status,
                        result.Message);
                }

                // MoveAsync is predominantly synchronous for archive moves.
                // Yield between cards so unrelated request continuations are
                // never monopolized by a long batch on a single worker turn.
                await Task.Yield();
            }

            total.Stop();
            var metrics = BuildMetrics(total, itemMoveMs);
            job.Finish(metrics);
            var snapshot = job.Snapshot();
            _logger.LogInformation(
                "batch-move-completed batchId={BatchId} completed={Completed} succeeded={Succeeded} failed={Failed} totalMs={TotalMs:0.###} itemMs={ItemMs:0.###} lockWaitMs={LockWaitMs:0.###} lockHeldMs={LockHeldMs:0.###} scannerInvalidations={ScannerInvalidations} scannerRefreshes={ScannerRefreshes} scannerRefreshMs={ScannerRefreshMs:0.###} gitProcesses={GitProcesses} gitMs={GitMs:0.###}",
                job.Id,
                snapshot.Completed,
                snapshot.Succeeded,
                snapshot.Failed,
                metrics.TotalDurationMs,
                metrics.ItemMoveDurationMs,
                metrics.LaneLockWaitMs,
                metrics.LaneLockHeldMs,
                metrics.ScannerInvalidations,
                metrics.ScannerRefreshes,
                metrics.ScannerRefreshMs,
                metrics.GitProcesses,
                metrics.GitProcessMs);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            total.Stop();
            job.Fail("The server stopped before the batch completed.", BuildMetrics(total, itemMoveMs));
        }
        catch (Exception ex)
        {
            total.Stop();
            job.Fail(ex.Message, BuildMetrics(total, itemMoveMs));
            _logger.LogError(ex, "batch-move-failed batchId={BatchId}", job.Id);
        }

        BatchMoveJobMetrics BuildMetrics(Stopwatch timer, double moveMs)
        {
            var operation = BatchMoveOperationTelemetry.CurrentTally();
            var git = GitProcessTelemetry.CurrentTally();
            return new BatchMoveJobMetrics
            {
                TotalDurationMs = timer.Elapsed.TotalMilliseconds,
                ItemMoveDurationMs = moveMs,
                LaneLockAcquisitions = operation?.LaneLockAcquisitions ?? 0,
                LaneLockWaitMs = operation?.LaneLockWaitMs ?? 0,
                LaneLockHeldMs = operation?.LaneLockHeldMs ?? 0,
                ScannerInvalidations = operation?.ScannerInvalidations ?? 0,
                ScannerRefreshes = operation?.ScannerRefreshes ?? 0,
                ScannerRefreshMs = operation?.ScannerRefreshMs ?? 0,
                GitProcesses = git?.Spawns ?? 0,
                GitProcessMs = git?.GitMs ?? 0,
            };
        }
    }

    private void PruneCompletedJobs()
    {
        if (_jobs.Count < RetainedJobLimit) return;
        var removable = _jobs.Values
            .Select(job => job.Snapshot())
            .Where(snapshot => BatchMoveJobStates.IsTerminal(snapshot.Status))
            .OrderBy(snapshot => snapshot.FinishedAt)
            .Take(Math.Max(1, _jobs.Count - RetainedJobLimit + 1));
        foreach (var snapshot in removable) _jobs.TryRemove(snapshot.Id, out _);
    }
}
