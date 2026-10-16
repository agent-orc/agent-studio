using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tasks;

/// <summary>
/// Per-project write mutex for the workspace lane tree (F21).
///
/// <para>The lane tree under a single watch path
/// (<c>0-backlog/</c>, <c>1-preparation/</c>, ..., <c>3-progress/</c>,
/// <c>3a-failed-pickup/</c>, ..., <c>7-archive/</c>) is touched by six
/// independent writers: <c>TaskTransitionService.MoveAsync</c>, the
/// batch-move endpoint, <c>TaskStateMachine</c> (move/delete/change-project),
/// the boot-time <c>CrashRecoveryService</c>, the boot-time
/// <c>StaleProgressArchiver</c>, the per-tick <c>ProjectRunner</c>
/// pickup/dead-letter path, and the drag-and-drop API. Without
/// serialisation, two writers can race against the same folder slug and
/// produce the symptoms tracked under this ticket: orphan/empty folders
/// in <c>3a-failed-pickup</c>, half-renamed siblings with <c>-2</c>
/// collision suffixes, and a reader catching a mid-rename folder without
/// <c>task.json</c>.</para>
///
/// <para>This registry hands out a per-project (per watch-path)
/// <see cref="SemaphoreSlim"/> with concurrency 1. All writers acquire it
/// at the leaf level (the <c>Directory.Move</c> / <c>Directory.Delete</c>
/// call site itself). A guarded review side effect may hold the same mutex
/// through its lane move; nested acquisition in that async flow retains the
/// outer lock. Readers go through <see cref="TaskScannerService"/>'s cache
/// and do not take the mutex.</para>
///
/// <para>The architecture document at
/// <c>docs/system/architecture/runner-lanes/progress-lane-writers.md</c> lists every writer
/// and the move it makes; that file is the load-bearing reference when
/// adding a seventh writer.</para>
/// </summary>
public sealed class LaneMutexRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<Dictionary<string, int>?> _heldByFlow = new();
    private readonly ILogger<LaneMutexRegistry> _logger;
    private readonly TimeSpan _defaultTimeout;

    public LaneMutexRegistry(ILogger<LaneMutexRegistry> logger)
    {
        _logger = logger;
        _defaultTimeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Fallback singleton for code paths that don't have DI wired (unit
    /// test fixtures that build <see cref="TaskStateMachine"/> directly).
    /// Production wiring resolves the configured singleton; this fallback
    /// has its own per-process semaphore map so tests still see actual
    /// serialisation if they exercise it.
    /// </summary>
    public static readonly LaneMutexRegistry NullSingleton =
        new(NullLogger<LaneMutexRegistry>.Instance);

    /// <summary>
    /// Acquire the lane mutex for one project (keyed by watch-path).
    /// Returns an <see cref="IDisposable"/>; dispose to release.
    ///
    /// <para>On <paramref name="timeout"/> expiry the call logs a warning
    /// and returns a no-op disposable so the caller proceeds without
    /// exclusion rather than blocking the runner forever. A timeout here
    /// is itself a bug signal: 30 s is far longer than any legitimate
    /// folder rename, so it usually means another writer is wedged.</para>
    /// </summary>
    /// <param name="watchPath">The workspace root. Empty/whitespace
    /// returns a no-op disposable (e.g. when a test fixture has no
    /// watch path configured).</param>
    /// <param name="timeout">Optional override (default 30 s).</param>
    public IDisposable Acquire(string? watchPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(watchPath))
        {
            return NoOpDisposable.Instance;
        }

        var key = Normalise(watchPath);
        var held = _heldByFlow.Value;
        if (held is not null && held.TryGetValue(key, out var depth))
        {
            _heldByFlow.Value = new Dictionary<string, int>(held, StringComparer.OrdinalIgnoreCase)
            {
                [key] = depth + 1,
            };
            return new NestedRelease(this, held);
        }
        var sem = _mutexes.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        var effectiveTimeout = timeout ?? _defaultTimeout;
        var waitStarted = System.Diagnostics.Stopwatch.StartNew();

        if (!sem.Wait(effectiveTimeout))
        {
            waitStarted.Stop();
            _logger.LogWarning(
                "LaneMutexRegistry: timed out after {Seconds}s waiting for lane mutex on {WatchPath}; proceeding without exclusion. " +
                "Another writer may be wedged; investigate before this becomes routine.",
                effectiveTimeout.TotalSeconds, watchPath);
            return NoOpDisposable.Instance;
        }

        waitStarted.Stop();
        BatchMoveOperationTelemetry.RecordLaneLockWait(waitStarted.Elapsed);
        var next = held is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(held, StringComparer.OrdinalIgnoreCase);
        next[key] = 1;
        _heldByFlow.Value = next;
        return new Release(sem, this, held);
    }

    private static string Normalise(string watchPath)
    {
        // Normalise separators and trim trailing slashes so two callers
        // with cosmetically different paths still share a semaphore.
        var trimmed = watchPath.Replace('\\', '/').TrimEnd('/');
        return trimmed.ToLowerInvariant();
    }

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _sem;
        private readonly LaneMutexRegistry _owner;
        private readonly Dictionary<string, int>? _previous;
        private readonly long _acquiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        public Release(SemaphoreSlim sem, LaneMutexRegistry owner, Dictionary<string, int>? previous)
        { _sem = sem; _owner = owner; _previous = previous; }
        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            if (sem is null) return;
            _owner._heldByFlow.Value = _previous;
            BatchMoveOperationTelemetry.RecordLaneLockHeld(
                System.Diagnostics.Stopwatch.GetElapsedTime(_acquiredAt));
            sem.Release();
        }
    }

    private sealed class NestedRelease : IDisposable
    {
        private readonly LaneMutexRegistry _owner;
        private readonly Dictionary<string, int> _previous;
        private bool _disposed;
        public NestedRelease(LaneMutexRegistry owner, Dictionary<string, int> previous)
        { _owner = owner; _previous = previous; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._heldByFlow.Value = _previous;
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
