using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace AgentStudio.Runner;

/// <summary>
/// Point-in-time throughput read of the auto-review post-processing queue
/// over a trailing window: how fast cards are actually leaving the queue
/// (<see cref="DrainRatePerMinute"/>) and how long a review pass typically
/// takes (<see cref="MedianDurationMs"/>). Computed by the pure
/// <see cref="AutoReviewQueueTelemetry.Summarize"/> function so the windowing
/// and median math have direct matrix test coverage.
/// </summary>
public sealed record AutoReviewQueueThroughput
{
    /// <summary>Completed review passes per minute over the trailing window (deferrals excluded - they re-enter the queue, they do not drain it).</summary>
    public double DrainRatePerMinute { get; init; }

    /// <summary>Median wall-clock duration of a review pass in the trailing window, in milliseconds. Null when no pass completed in-window.</summary>
    public double? MedianDurationMs { get; init; }

    /// <summary>Completed passes (drained or deferred) observed inside the trailing window.</summary>
    public int SampleCount { get; init; }

    public double WindowMinutes { get; init; }
}

/// <summary>
/// Bounded rolling record of completed auto-review post-processing passes,
/// used to derive drain rate and median duration without an external metrics
/// store. Capacity bounds memory; the window bounds relevance (an old sample
/// still inside the capacity but outside the window does not count).
/// </summary>
public sealed class AutoReviewQueueTelemetry
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<AutoReviewQueueTelemetrySample> _samples = new();

    public AutoReviewQueueTelemetry(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void RecordCompletion(DateTime completedAtUtc, TimeSpan elapsed, bool drained)
    {
        lock (_gate)
        {
            _samples.Enqueue(new AutoReviewQueueTelemetrySample(
                completedAtUtc.ToUniversalTime(), elapsed.TotalMilliseconds, drained));
            while (_samples.Count > _capacity) _samples.Dequeue();
        }
    }

    public AutoReviewQueueThroughput Summarize(DateTime nowUtc, TimeSpan window)
    {
        List<AutoReviewQueueTelemetrySample> snapshot;
        lock (_gate) snapshot = [.. _samples];
        return Summarize(snapshot, nowUtc.ToUniversalTime(), window);
    }

    /// <summary>Pure projection: no I/O, no locking - takes a snapshot and a point in time so it is directly matrix-testable.</summary>
    internal static AutoReviewQueueThroughput Summarize(
        IReadOnlyList<AutoReviewQueueTelemetrySample> samples,
        DateTime nowUtc,
        TimeSpan window)
    {
        var cutoff = nowUtc - window;
        var inWindow = samples.Where(s => s.CompletedAtUtc >= cutoff).ToList();
        var windowMinutes = Math.Max(window.TotalMinutes, 1e-9);
        var drainedCount = inWindow.Count(s => s.Drained);

        double? median = null;
        if (inWindow.Count > 0)
        {
            var durations = inWindow.Select(s => s.ElapsedMs).OrderBy(x => x).ToList();
            var mid = durations.Count / 2;
            median = durations.Count % 2 == 1
                ? durations[mid]
                : (durations[mid - 1] + durations[mid]) / 2.0;
        }

        return new AutoReviewQueueThroughput
        {
            DrainRatePerMinute = Math.Round(drainedCount / windowMinutes, 2),
            MedianDurationMs = median.HasValue ? Math.Round(median.Value, 0) : null,
            SampleCount = inWindow.Count,
            WindowMinutes = window.TotalMinutes,
        };
    }
}

public readonly record struct AutoReviewQueueTelemetrySample(
    DateTime CompletedAtUtc,
    double ElapsedMs,
    bool Drained);

public sealed record AutoReviewPostProcessingRequest(
    string ProjectName,
    string JobId,
    string WatchPath,
    DateTime EnqueuedAtUtc,
    string Source,
    /// <summary>
    /// How many times this card has already been re-driven after a deferral
    /// (see <see cref="PostProcessingCardStatus.Deferred"/>). Zero for every
    /// request produced by a real run boundary, recovery sweep or operator
    /// requeue; only the retry path increments it.
    /// </summary>
    int Attempt = 0);

public interface IAutoReviewPostProcessingQueue
{
    bool Enqueue(AutoReviewPostProcessingRequest request);
}

/// <summary>
/// Event-driven hand-off from the run-boundary post-processing path to the
/// auto-review decision engine. The durable state still lives in
/// <c>4-auto-review</c>; this queue only removes the old "wait for the next
/// poll tick" delay.
/// </summary>
public sealed class AutoReviewPostProcessingQueue : IAutoReviewPostProcessingQueue
{
    private readonly object _pendingLock = new();
    private readonly List<AutoReviewPostProcessingRequest> _pending = [];
    private readonly Channel<AutoReviewPostProcessingRequest> _channel =
        Channel.CreateUnbounded<AutoReviewPostProcessingRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private DateTime _lastStartedAt = DateTime.MinValue;

    public AutoReviewQueueTelemetry Telemetry { get; } = new();

    public ChannelReader<AutoReviewPostProcessingRequest> Reader => _channel.Reader;

    /// <summary>Number of cards waiting in queue (not yet picked up for processing).</summary>
    public int PendingCount { get { lock (_pendingLock) return _pending.Count; } }

    /// <summary>
    /// UTC time when a card was most recently dequeued and handed to a processing
    /// slot. Null if no card has ever been picked up. Used by the stagnation
    /// watchdog to detect a queue that has depth but no drain progress.
    /// </summary>
    public DateTime? LastStartedAt
    {
        get { lock (_pendingLock) return _lastStartedAt == DateTime.MinValue ? null : _lastStartedAt; }
    }

    public bool Enqueue(AutoReviewPostProcessingRequest request)
    {
        ClearWaitState(request.ProjectName, request.JobId);
        lock (_pendingLock) _pending.Add(request);
        if (_channel.Writer.TryWrite(request)) return true;
        MarkStarted(request);
        return false;
    }

    /// <summary>
    /// One-based position in the existing post-processing slot queue. This is a
    /// read projection of queue membership, not a second scheduling state.
    /// </summary>
    public int? PositionOf(string projectName, string jobId)
    {
        lock (_pendingLock)
        {
            var position = _pending.FindIndex(request =>
                string.Equals(request.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            return position < 0 ? null : position + 1;
        }
    }

    internal void MarkStarted(AutoReviewPostProcessingRequest request)
    {
        lock (_pendingLock)
        {
            _lastStartedAt = DateTime.UtcNow;
            var index = _pending.FindIndex(candidate =>
                candidate.EnqueuedAtUtc == request.EnqueuedAtUtc
                && string.Equals(candidate.ProjectName, request.ProjectName, StringComparison.Ordinal)
                && string.Equals(candidate.JobId, request.JobId, StringComparison.Ordinal)
                && string.Equals(candidate.Source, request.Source, StringComparison.Ordinal));
            if (index >= 0) _pending.RemoveAt(index);
        }
        ClearWaitState(request.ProjectName, request.JobId);
    }

    private readonly ConcurrentDictionary<string, AutoReviewQueueWaitState> _waitStates =
        new(StringComparer.OrdinalIgnoreCase);

    private static string WaitKey(string projectName, string jobId) => projectName + "␟" + jobId;

    /// <summary>
    /// Records why a card is not in <see cref="_pending"/> right now even
    /// though it is still sitting in <c>4-auto-review</c>: it was deferred and
    /// is waiting out its backoff before the next re-drive. Without this, the
    /// liveStatus queue projection reads as empty for the entire gap between
    /// one deferred pass and the next (AGT-2842) instead of naming the wait.
    /// </summary>
    public void SetWaitState(string projectName, string jobId, AutoReviewQueueWaitState state)
        => _waitStates[WaitKey(projectName, jobId)] = state;

    public void ClearWaitState(string projectName, string jobId)
        => _waitStates.TryRemove(WaitKey(projectName, jobId), out _);

    public AutoReviewQueueWaitState? WaitStateOf(string projectName, string jobId)
        => _waitStates.TryGetValue(WaitKey(projectName, jobId), out var state) ? state : null;
}

/// <summary>
/// Last known reason a card is waiting between post-processing passes rather
/// than actively queued or running. Read by <see cref="AgentStudio.Tasks.TaskLiveStatusProjection"/>
/// to fill the gap <see cref="AutoReviewPostProcessingQueue.PositionOf"/>
/// leaves once a deferred card has been dequeued but has not yet been
/// re-enqueued for its next attempt.
/// </summary>
public sealed record AutoReviewQueueWaitState(
    string Reason,
    string? Detail,
    int Attempt,
    DateTime NextRetryAtUtc);

/// <summary>
/// Drains the event-driven auto-review queue. Processing is intentionally
/// outside the runner's active-job latch: a coding runner may pick the next
/// task while this worker runs aspect review and the final orchestrator
/// decision for the completed one.
/// </summary>
public sealed class AutoReviewPostProcessingWorker : BackgroundService
{
    /// <summary>
    /// Floor for the concurrent-card cap. The effective cap is derived from
    /// machine capacity (<see cref="DeriveMaxParallelism"/>): remote runners
    /// deliver waves of completed cards, and a fixed small cap let the
    /// 4-auto-review lane back up while cores sat idle. Effective concurrency
    /// still follows queue depth naturally - the cap only bounds it. Override
    /// with <c>PostProcessing:MaxParallelism</c> (read from appsettings like the
    /// gate timeouts). Per card the step order stays sequential - only across
    /// cards is there parallelism; build-heavy steps keep serializing on the
    /// machine-wide build-test-gate lock, so a wider admission mainly lets the
    /// LLM-bound steps of distinct cards overlap instead of queueing behind a
    /// card that is waiting for the gate.
    /// </summary>
    public const int DefaultMaxParallelism = 3;

    /// <summary>
    /// Upper bound for the derived cap: beyond this, more concurrent cards no
    /// longer add throughput (the gate lock and the orchestrator's per-hour
    /// call budget become the binding constraints) but do add workspace churn.
    /// </summary>
    public const int MaxDerivedParallelism = 12;

    /// <summary>Capacity-derived concurrent-card cap; clamped to [<see cref="DefaultMaxParallelism"/>, <see cref="MaxDerivedParallelism"/>].</summary>
    internal static int DeriveMaxParallelism(int processorCount) =>
        Math.Clamp(processorCount / 2, DefaultMaxParallelism, MaxDerivedParallelism);

    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly ReviewDecisionOrchestrator _reviewDecisionOrchestrator;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoReviewPostProcessingWorker> _logger;
    private readonly V1ReviewExecutorRegistry? _reviewExecutorRegistry;
    private readonly AutoReviewDeliveryResumeService? _deliveryResume;

    /// <summary>
    /// Test seam: when set, one request is handed to this delegate instead of the
    /// real <see cref="ProcessAsync"/>, so the bounded-parallel drain (max-N +
    /// in-flight dedup + failure isolation) can be exercised deterministically
    /// without a full orchestrator run. Null in production.
    /// </summary>
    internal Func<AutoReviewPostProcessingRequest, CancellationToken, Task>? ProcessOverride { get; set; }

    /// <summary>
    /// Test seam: replaces the deferral backoff, so the re-drive path can be
    /// exercised without waiting out the real 30s-and-doubling schedule. Null
    /// in production.
    /// </summary>
    internal Func<int, TimeSpan>? DeferralDelayOverride { get; set; }

    public AutoReviewPostProcessingWorker(
        AutoReviewPostProcessingQueue queue,
        ReviewDecisionOrchestrator reviewDecisionOrchestrator,
        TaskScannerService scanner,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AutoReviewPostProcessingWorker> logger,
        V1ReviewExecutorRegistry? reviewExecutorRegistry = null,
        AutoReviewDeliveryResumeService? deliveryResume = null)
    {
        _queue = queue;
        _reviewDecisionOrchestrator = reviewDecisionOrchestrator;
        _scanner = scanner;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
        _reviewExecutorRegistry = reviewExecutorRegistry;
        _deliveryResume = deliveryResume;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded parallelism across cards: several completed cards are
        // post-processed at once (up to maxParallelism), while each card's own step
        // sequence stays serial. An in-flight set keeps the same card from being
        // processed twice concurrently, and each card's failure is isolated so it
        // never tears down the pool. The workspace-wide backstop sweep remains the
        // safety net for anything a slot missed.
        var maxParallelism = Math.Max(1,
            _configuration.GetValue("PostProcessing:MaxParallelism",
                DeriveMaxParallelism(Environment.ProcessorCount)));
        var slots = new SemaphoreSlim(maxParallelism, maxParallelism);
        var inFlight = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var running = new ConcurrentDictionary<Task, byte>();

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var key = CardKey(request);

                // Same card already in flight: drop the duplicate enqueue. The durable
                // 4-auto-review state plus the backstop sweep / startup recovery
                // re-drive it if it still needs work, so nothing is lost and no card
                // is post-processed twice at the same time.
                if (!inFlight.TryAdd(key, 0))
                {
                    _queue.MarkStarted(request);
                    _logger.LogDebug(
                        "auto-review-postprocessing-dedup project={Project} job={JobId} reason=already-in-flight",
                        request.ProjectName, request.JobId);
                    continue;
                }

                await slots.WaitAsync(stoppingToken);
                _queue.MarkStarted(request);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await RunOneAsync(request, stoppingToken);
                    }
                    catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
                    {
                        SilentCatch.Note(__ex, "AutoReviewPostProcessingWorker: card post-processing cancelled on graceful shutdown.");
                    }
                    catch (Exception ex)
                    {
                        // Failure isolation: one card's fault must not abort the others.
                        _logger.LogWarning(ex,
                            "auto-review-postprocessing-worker-task-failed project={Project} job={JobId}",
                            request.ProjectName, request.JobId);
                    }
                    finally
                    {
                        inFlight.TryRemove(key, out _);
                        slots.Release();
                    }
                }, stoppingToken);

                running[task] = 0;
                _ = task.ContinueWith(
                    t => running.TryRemove(t, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            await Task.WhenAll(running.Keys);
        }
        catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(__ex, "AutoReviewPostProcessingQueue: Graceful shutdown. The ReviewDecisionOrchestrator boot/backstop");
            // Graceful shutdown. The ReviewDecisionOrchestrator boot/backstop
            // sweep remains the recovery path for anything left in 4-auto-review.
            try { await Task.WhenAll(running.Keys); }
            catch (Exception __drainEx) { SilentCatch.Note(__drainEx, "AutoReviewPostProcessingWorker: draining in-flight card post-processings on shutdown."); }
        }
    }

    private Task RunOneAsync(AutoReviewPostProcessingRequest request, CancellationToken ct)
        => ProcessOverride != null ? ProcessOverride(request, ct) : ProcessAsync(request, ct);

    private static string CardKey(AutoReviewPostProcessingRequest request)
        => request.ProjectName + "" + request.JobId;

    /// <summary>
    /// Processes one queued review request. Exposed for deterministic tests;
    /// production reaches it through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task ProcessAsync(AutoReviewPostProcessingRequest request, CancellationToken ct)
    {
        if (!_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing is disabled, so the queued attempt cannot continue.");
            _logger.LogInformation(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=review-decision-orchestrator-disabled",
                request.ProjectName, request.JobId);
            return;
        }

        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing cannot continue because the task repository is not configured.");
            _logger.LogWarning(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=missing-task-repository",
                request.ProjectName, request.JobId);
            return;
        }

        var sw = Stopwatch.StartNew();
        var queueWaitMs = (long)Math.Max(0, (DateTime.UtcNow - request.EnqueuedAtUtc).TotalMilliseconds);
        MarkReviewDecisionRunning(request);
        _logger.LogInformation(
            "auto-review-postprocessing-started project={Project} job={JobId} source={Source} queueWaitMs={QueueWaitMs}",
            request.ProjectName, request.JobId, request.Source, queueWaitMs);

        try
        {
            var outcome = await _reviewDecisionOrchestrator.ProcessCardAsync(
                workspace, request.ProjectName, request.JobId, request.WatchPath, ct);
            outcome = await ResumeDeliveryIfOwedAsync(request, outcome, ct);
            sw.Stop();
            // completion latency = run finished (enqueue) -> post-processing done;
            // the queue-wait share separates "stau" from "step cost" in the metric.
            _logger.LogInformation(
                "auto-review-postprocessing-finished project={Project} job={JobId} elapsedMs={ElapsedMs} queueWaitMs={QueueWaitMs} completionLatencyMs={CompletionLatencyMs} status={Status} reason={Reason}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds, queueWaitMs,
                queueWaitMs + sw.ElapsedMilliseconds, outcome.Status, outcome.Reason);

            // A deferred card re-enters the queue (ScheduleDeferralRetry below), so it has
            // not drained; every other terminal status has left the queue for good.
            _queue.Telemetry.RecordCompletion(
                DateTime.UtcNow, sw.Elapsed, drained: outcome.Status != PostProcessingCardStatus.Deferred);

            ApplyOutcome(request, outcome, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing was interrupted during shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _queue.Telemetry.RecordCompletion(DateTime.UtcNow, sw.Elapsed, drained: true);
            TerminalizeActiveLifecycle(
                request,
                "Post Processing failed before reaching a terminal decision: " + ex.GetType().Name + ".");
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-failed project={Project} job={JobId} elapsedMs={ElapsedMs}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Turns the engine's verdict about the pass into lifecycle state.
    /// <list type="bullet">
    /// <item><b>Decided</b> - a decision path owned the card. If its lifecycle
    /// is nevertheless still active the verdict never landed, so the old
    /// safety net still terminalizes it, now naming the path that ran.</item>
    /// <item><b>Deferred</b> - a legitimate hand-off (the canonical review
    /// executor owns the card) or a transient limit. The card is parked in
    /// <c>awaiting-review</c> without a blocking reason and re-driven with
    /// backoff, and its decision check is closed as <c>skipped</c> - no
    /// decision ran, so neither <c>completed</c> nor a lingering
    /// <c>running</c> would be honest. This is the case that used to be
    /// blocked terminally.</item>
    /// <item><b>Blocked</b> - a real precondition failure, terminalized with
    /// the concrete reason rather than the generic sentence.</item>
    /// </list>
    /// </summary>
    internal void ApplyOutcome(
        AutoReviewPostProcessingRequest request,
        PostProcessingCardResult outcome,
        CancellationToken ct)
    {
        switch (outcome.Status)
        {
            case PostProcessingCardStatus.Deferred:
                var deferralReason = RefineCanonicalWaitReason(
                    outcome.Reason, ResolveReviewExecutorAvailability(outcome.Reason));
                ParkActiveLifecycle(request, deferralReason);
                ScheduleDeferralRetry(request, deferralReason, ct);
                return;

            case PostProcessingCardStatus.Blocked:
                TerminalizeActiveLifecycle(
                    request,
                    "Post Processing stopped before a terminal decision: " + outcome.Reason + ".");
                return;

            default:
                TerminalizeActiveLifecycle(
                    request,
                    "Post Processing returned without a terminal decision (" + outcome.Reason + ").");
                return;
        }
    }

    /// <summary>
    /// Highest number of automatic re-drives for a deferred card whose blocking
    /// condition is still genuinely unresolved. The card is durable in
    /// <c>4-auto-review</c> and the boot/backstop sweep re-drives it anyway, so
    /// this only shortens the wait; exhausting it leaves the card resting in
    /// <c>awaiting-review</c> and never blocks it. A wait whose blocking
    /// condition is observably satisfied resets the counter instead of
    /// exhausting it - see <see cref="ScheduleDeferralRetry"/>.
    /// </summary>
    internal const int MaxDeferralRetries = 5;

    /// <summary>First deferral re-drive delay; doubles per attempt.</summary>
    internal static readonly TimeSpan DeferralRetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Cap for the doubling, so a long wait still re-checks regularly.</summary>
    internal static readonly TimeSpan DeferralRetryMaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Tighter cap applied instead of <see cref="DeferralRetryMaxDelay"/> once a
    /// review executor is registered for the canonical-review-executor wait
    /// (<see cref="PostProcessingCardResult.IsCanonicalReviewWait"/>):
    /// the wait is healthy and self-resolving, so the card should keep
    /// re-checking - and keep its liveStatus queue reason fresh - at least once
    /// a minute rather than backing off to a ten-minute silence (AGT-2842).
    /// </summary>
    internal static readonly TimeSpan CanonicalReviewExecutorRegisteredMaxDelay = TimeSpan.FromSeconds(60);

    internal static TimeSpan DeferralRetryDelay(int attempt) => DeferralRetryDelay(attempt, DeferralRetryMaxDelay);

    internal static TimeSpan DeferralRetryDelay(int attempt, TimeSpan maxDelay)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt));
        var seconds = DeferralRetryBaseDelay.TotalSeconds * factor;
        return seconds >= maxDelay.TotalSeconds
            ? maxDelay
            : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Whether a registered review executor should tighten this deferral's
    /// backoff cap, and the detail sentence for the exposed wait reason. The
    /// generic exponential backoff assumes nobody else is going to act on this
    /// card. A canonical-review-executor wait is different: another owner (the
    /// fenced ReviewAttempt executor) is expected to claim and settle it
    /// independently of this queue, so a registered executor turns "still
    /// waiting" from a symptom into the normal, healthy state. This asks the
    /// registry directly rather than reading the reason string as proof of
    /// anything - the queue should stay quiet even if a future caller reuses
    /// the same reason token for a different wait. Pulled out of
    /// <see cref="ScheduleDeferralRetry"/> so the cap decision is directly
    /// unit-testable without spawning the real retry timer (AGT-2842).
    /// </summary>
    internal V1ReviewExecutorRegistry.ReviewExecutorAvailability? ResolveReviewExecutorAvailability(string reason)
        => PostProcessingCardResult.IsCanonicalReviewWait(reason)
           && _reviewExecutorRegistry is not null
            ? _reviewExecutorRegistry.EvaluateReviewExecutorAvailability()
            : null;

    /// <summary>
    /// Drives the restart-safe delivery resume for the two deferrals a terminal
    /// <c>Pass</c> has already earned (AGT-2860). A card whose review passed but
    /// whose integration or lane transition was killed by a restart is not
    /// waiting for anyone - this backend owes it the rest of the sequence - so
    /// the pass that would have deferred it settles it instead and the card
    /// drains out of the queue. Every other deferral is returned unchanged.
    /// </summary>
    internal async Task<PostProcessingCardResult> ResumeDeliveryIfOwedAsync(
        AutoReviewPostProcessingRequest request,
        PostProcessingCardResult outcome,
        CancellationToken ct)
    {
        if (_deliveryResume is null
            || outcome.Status != PostProcessingCardStatus.Deferred
            || !PostProcessingCardResult.IsDeliveryResumeWait(outcome.Reason))
        {
            return outcome;
        }

        var info = _scanner.FindJob(request.JobId, request.WatchPath);
        if (info is null) return outcome;

        try
        {
            var resumed = await _deliveryResume.ResumeAsync(info, "post-processing-deferral", ct);
            if (!resumed.Resumed) return outcome;

            // The pass did reach a terminal decision for this card, so its
            // lifecycle is closed here as completed. Leaving it to the caller's
            // safety net would terminalize it as a failure and say the verdict
            // never landed, which is the opposite of what just happened.
            CloseResumedLifecycle(request, resumed);
            return PostProcessingCardResult.Decided(DeliveryResumedReason + resumed.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-delivery-resume-failed project={Project} job={JobId} reason={Reason}",
                request.ProjectName, request.JobId, outcome.Reason);
            return outcome;
        }
    }

    /// <summary>
    /// Names the missing thing instead of the role that is present. The
    /// classifier can see that the ReviewAttempt has not settled, but only the
    /// registry knows whether that is because no review executor is registered
    /// at all or because a registered one is simply still working. On
    /// 17.09.2026 every deferred card said
    /// <c>awaiting-canonical-review-executor</c> while the executor was
    /// registered and busy, which sent the incident hunt in the wrong direction
    /// (AGT-2860).
    /// </summary>
    internal static string RefineCanonicalWaitReason(
        string reason,
        V1ReviewExecutorRegistry.ReviewExecutorAvailability? availability)
        => string.Equals(
               reason, PostProcessingCardResult.AwaitingCanonicalReviewVerdict, StringComparison.Ordinal)
           && availability is { AnyRegistered: false }
            ? PostProcessingCardResult.AwaitingReviewExecutorRegistration
            : reason;

    /// <summary>Prefix of the <c>Decided</c> reason a resumed delivery reports.</summary>
    internal const string DeliveryResumedReason = "remote-delivery-resumed:";

    private void CloseResumedLifecycle(
        AutoReviewPostProcessingRequest request,
        AutoReviewResumeOutcome resumed)
    {
        try
        {
            // Re-resolve: the transition moved the card's folder.
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info is null) return;
            PostProcessingLifecycleStore.Terminalize(
                info.FolderPath,
                DateTime.UtcNow,
                failed: false,
                "Post Processing resumed the interrupted remote delivery ("
                + resumed.Reason + ") and completed the transition.",
                _logger,
                onlyWhenActive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-resume-lifecycle-close-failed project={Project} job={JobId}",
                request.ProjectName, request.JobId);
        }
    }

    private void ScheduleDeferralRetry(
        AutoReviewPostProcessingRequest request,
        string reason,
        CancellationToken ct)
    {
        var availability = ResolveReviewExecutorAvailability(reason);
        var maxDelay = availability?.AnyRegistered == true
            ? CanonicalReviewExecutorRegisteredMaxDelay
            : DeferralRetryMaxDelay;

        // AGT-2860: the budget exists for a wait nobody is resolving. Neither of
        // these is that. A registered executor means the blocking condition the
        // budget was counting against is observably satisfied, and a card with a
        // terminal Pass attempt is owed work by this backend, not by anyone
        // else. In both cases the counter resets rather than stranding the card
        // in 4-auto-review with nothing left to pick it up. The genuinely idle
        // executor keeps AGT-2842's growing backoff and its exhaustion.
        var blockingConditionResolved = availability?.AnyRegistered == true
                                        || PostProcessingCardResult.IsDeliveryResumeWait(reason);
        if (request.Attempt >= MaxDeferralRetries && !blockingConditionResolved)
        {
            _logger.LogInformation(
                "auto-review-postprocessing-deferral-exhausted project={Project} job={JobId} reason={Reason} attempts={Attempts}",
                request.ProjectName, request.JobId, reason, request.Attempt);
            _queue.SetWaitState(
                request.ProjectName,
                request.JobId,
                new AutoReviewQueueWaitState(reason, availability?.Detail, request.Attempt, DateTime.UtcNow));
            return;
        }

        var attempt = request.Attempt >= MaxDeferralRetries ? 0 : request.Attempt;
        var delay = DeferralDelayOverride?.Invoke(attempt) ?? DeferralRetryDelay(attempt, maxDelay);
        _logger.LogInformation(
            "auto-review-postprocessing-deferred project={Project} job={JobId} reason={Reason} attempt={Attempt} retryInMs={RetryInMs}",
            request.ProjectName, request.JobId, reason, attempt, (long)delay.TotalMilliseconds);
        _queue.SetWaitState(
            request.ProjectName,
            request.JobId,
            new AutoReviewQueueWaitState(reason, availability?.Detail, attempt, DateTime.UtcNow + delay));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                _queue.Enqueue(request with
                {
                    Attempt = attempt + 1,
                    EnqueuedAtUtc = DateTime.UtcNow,
                    Source = "deferral-retry",
                });
            }
            catch (OperationCanceledException __ex)
            {
                SilentCatch.Note(__ex, "AutoReviewPostProcessingWorker: deferral retry dropped on shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "auto-review-postprocessing-retry-enqueue-failed project={Project} job={JobId}",
                    request.ProjectName, request.JobId);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Closes an active post-processing lifecycle without a failure and without
    /// a verdict: the pass legitimately skipped the card, so the
    /// <c>post-orchestrator-decision</c> check is closed as <c>skipped</c> and
    /// the card rests in <c>awaiting-review</c> with no blocking reason and
    /// stays pickable. Closing it terminally is what keeps a backend restart
    /// from finding a phantom <c>running</c> check that nothing ever finishes.
    /// </summary>
    private void ParkActiveLifecycle(AutoReviewPostProcessingRequest request, string reason)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null)
            {
                // The card moved or was renamed mid-pass, so the check this
                // worker opened cannot be closed. Say so instead of leaving a
                // silent "running" behind.
                _logger.LogWarning(
                    "auto-review-postprocessing-lifecycle-park-unresolved project={Project} job={JobId} reason={Reason}",
                    request.ProjectName, request.JobId, reason);
                return;
            }
            var updated = PostProcessingLifecycleStore.Skip(
                info.FolderPath,
                DateTime.UtcNow,
                "Post Processing skipped the decision: " + reason + ".",
                _logger);
            if (updated && string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.AwaitingReview);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-park-failed project={Project} job={JobId}",
                request.ProjectName,
                request.JobId);
        }
    }

    private void MarkReviewDecisionRunning(AutoReviewPostProcessingRequest request)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null || info.State != TaskStates.AutoReview) return;

            var now = DateTime.UtcNow;
            if (PostProcessingLifecycleStore.BeginPostProcessing(
                    info.FolderPath,
                    now,
                    PipelineCatalogue.OrchestratorDecisionStepId,
                    "Auto-review decision started from the run-boundary post-processing queue.",
                    _logger,
                    replaceChecks: false))
            {
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-write-failed project={Project} job={JobId}",
                request.ProjectName, request.JobId);
        }
    }

    private void TerminalizeActiveLifecycle(
        AutoReviewPostProcessingRequest request,
        string detail)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null)
            {
                _logger.LogWarning(
                    "auto-review-postprocessing-lifecycle-terminalize-unresolved project={Project} job={JobId} detail={Detail}",
                    request.ProjectName, request.JobId, detail);
                return;
            }
            var updated = PostProcessingLifecycleStore.Terminalize(
                info.FolderPath,
                DateTime.UtcNow,
                failed: true,
                detail,
                _logger,
                onlyWhenActive: true);
            if (updated && string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingBlocked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-terminalize-failed project={Project} job={JobId}",
                request.ProjectName,
                request.JobId);
        }
    }
}
