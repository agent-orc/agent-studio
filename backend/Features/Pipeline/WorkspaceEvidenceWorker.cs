using Microsoft.Extensions.Configuration;

namespace AgentStudio.Pipeline;

/// <summary>
/// Hosted driver for the Transition-Committer. On boot it runs a one-shot
/// catch-up commit for drift that accumulated while the backend was down, then
/// ticks: it drains the <see cref="WorkspaceEvidenceQueue"/> into the
/// <see cref="WorkspaceEvidenceBatcher"/> and flushes any repo whose debounce
/// window elapsed. All timing/commit logic lives in the batcher (virtual-time
/// testable); this class only wires the channel, the boot pass, and the tick
/// cadence, and it swallows every error so a git problem can never break the
/// board or the transition that produced the evidence. Swallowed is not silent:
/// a flush failure that repeats for more than 15 minutes raises the
/// <c>evidence-flush-stalled</c> pipeline health alarm (AGT-3000).
/// </summary>
public sealed class WorkspaceEvidenceWorker : BackgroundService
{
    private readonly WorkspaceEvidenceQueue _queue;
    private readonly WorkspaceEvidenceBatcher _batcher;
    private readonly AgentStudio.Tasks.TaskScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkspaceEvidenceWorker> _logger;
    private readonly TimeProvider _time;
    private readonly IEvidenceFlushAlarm? _alarm;
    private readonly EvidenceFlushStallTracker _stalls = new();

    public WorkspaceEvidenceWorker(
        WorkspaceEvidenceQueue queue,
        WorkspaceEvidenceBatcher batcher,
        AgentStudio.Tasks.TaskScannerService scanner,
        IConfiguration config,
        ILogger<WorkspaceEvidenceWorker> logger,
        TimeProvider? time = null,
        IEvidenceFlushAlarm? alarm = null)
    {
        _queue = queue;
        _batcher = batcher;
        _scanner = scanner;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _alarm = alarm;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { RunCatchUp(); }
        catch (Exception ex) { _logger.LogWarning(ex, "workspace-evidence boot catch-up failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain unconditionally so a disabled switch cannot let the
                // channel grow unbounded; only fold work in while enabled.
                var enabled = _batcher.Enabled;
                while (_queue.Reader.TryRead(out var request))
                {
                    if (enabled) _batcher.Ingest(request);
                }

                if (enabled)
                {
                    foreach (var flush in _batcher.FlushDue())
                    {
                        if (flush.Result.DidCommit)
                            _logger.LogInformation(
                                "workspace-evidence-flushed repo={Repo} transitions={Count} sha={Sha}",
                                flush.GitRoot, flush.TransitionCount, flush.Result.Sha);
                        else if (!flush.Result.Success)
                            _logger.LogWarning(
                                "workspace-evidence-flush-failed repo={Repo} error={Error}",
                                flush.GitRoot, flush.Result.Error);
                        ObserveStall(flush);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "workspace-evidence tick failed");
            }

            try { await Task.Delay(ResolveTick(), _time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Graceful shutdown: drain whatever is still queued and commit every
        // pending repo now, ignoring the debounce window, so evidence caught
        // mid-debounce at stop time is not deferred to the next boot's catch-up.
        try
        {
            if (_batcher.Enabled)
            {
                while (_queue.Reader.TryRead(out var request)) _batcher.Ingest(request);
                foreach (var flush in _batcher.FlushAll())
                {
                    if (flush.Result.DidCommit)
                        _logger.LogInformation(
                            "workspace-evidence-final-flush repo={Repo} transitions={Count} sha={Sha}",
                            flush.GitRoot, flush.TransitionCount, flush.Result.Sha);
                    else if (!flush.Result.Success)
                        _logger.LogWarning(
                            "workspace-evidence-final-flush-failed repo={Repo} error={Error}",
                            flush.GitRoot, flush.Result.Error);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "workspace-evidence final flush failed"); }
    }

    private void RunCatchUp()
    {
        if (!_batcher.Enabled) return;

        var watchPaths = _scanner.GetWatchPaths()
            .Select(w => w.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (watchPaths.Count == 0) return;

        foreach (var flush in _batcher.CatchUp(watchPaths))
        {
            if (flush.Result.DidCommit)
                _logger.LogInformation(
                    "workspace-evidence-catchup-committed repo={Repo} sha={Sha}",
                    flush.GitRoot, flush.Result.Sha);
            else if (!flush.Result.Success)
                _logger.LogWarning(
                    "workspace-evidence-catchup-failed repo={Repo} error={Error}",
                    flush.GitRoot, flush.Result.Error);
            ObserveStall(flush);
        }
    }

    /// <summary>
    /// Feeds one flush outcome to the stall tracker and forwards a stall or
    /// recovery to the pipeline health alarm. The alarm sink de-duplicates, so
    /// every failing tick past the threshold may report.
    /// </summary>
    internal void ObserveStall(WorkspaceEvidenceFlushResult flush)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (signal, stall) = _stalls.Observe(flush.GitRoot, flush.Result.Success, flush.Result.Error, now);
        if (_alarm is null) return;
        try
        {
            if (signal == EvidenceFlushStallSignal.Stalled && stall is not null)
                _alarm.EvidenceFlushStalled(stall);
            else if (signal == EvidenceFlushStallSignal.Recovered)
                _alarm.EvidenceFlushRecovered(flush.GitRoot, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-evidence stall alarm failed repo={Repo}", flush.GitRoot);
        }
    }

    private TimeSpan ResolveTick()
    {
        var seconds = _config.GetValue<int?>("WorkspaceEvidence:TickSeconds") ?? 2;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }
}
