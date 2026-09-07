using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// The one place background git work runs. A small pool of dedicated,
/// below-normal-priority threads drains a queue of units of work; nothing here
/// touches the thread pool.
///
/// <para>
/// That distinction is the whole point of AGT-2726. The previous design queued
/// each refresh with <c>Task.Run</c>, and each refresh then blocked its
/// thread-pool thread on a semaphore and on <c>Process.WaitForExit</c>. With
/// refreshes starting about six times a minute and running for seconds, the
/// pool's throttled growth (one new thread per ~500 ms once the minimum is
/// reached) meant ASP.NET request threads queued behind git work they had
/// nothing to do with - which is exactly how an endpoint that spawns no git
/// process at all measured a 95.8 s wall time.
/// </para>
///
/// <para>
/// The pool size is also the process budget: it caps how many git processes can
/// be in flight from background indexing at once, across all repositories.
/// </para>
/// </summary>
public sealed class GitBackgroundExecutor : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Thread> _threads = [];
    private readonly ILogger<GitBackgroundExecutor> _logger;
    private int _disposed;

    /// <param name="index">
    /// Supplies the repository budget. The pool is one thread wider than that
    /// budget on purpose: <c>MaxConcurrentRepositories</c> index runs can be in
    /// flight while the board's own per-card assembly
    /// (<c>tasks/list-refresh</c>) still has a slot. Sizing the pool exactly to
    /// the repository budget would let a workspace with many repositories starve
    /// the board refresh behind index runs - a regression against the
    /// <c>Task.Run</c> dispatch this replaced, which could never be starved
    /// because it was never bounded.
    /// </param>
    public GitBackgroundExecutor(GitStateIndex index, ILogger<GitBackgroundExecutor> logger)
        : this(index.Options.MaxConcurrentRepositories + 1, logger)
    {
    }

    internal GitBackgroundExecutor(int degreeOfParallelism, ILogger<GitBackgroundExecutor> logger)
    {
        _logger = logger;
        DegreeOfParallelism = Math.Clamp(degreeOfParallelism, 1, 9);
        for (var i = 0; i < DegreeOfParallelism; i++)
        {
            var thread = new Thread(Consume)
            {
                IsBackground = true,
                Name = $"git-index-{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
            _threads.Add(thread);
        }
    }

    internal int DegreeOfParallelism { get; }

    /// <summary>
    /// Queue depth, including the items currently running. Surfaced so the
    /// Admin panel can distinguish "the index is behind" from "the index is
    /// idle and the data really is that old".
    /// </summary>
    public int QueueDepth => _queue.Count + Volatile.Read(ref _running);

    private int _running;

    /// <summary>
    /// Submits one unit of background git work. Returns false when the executor
    /// is shutting down, so the caller can release whatever bookkeeping it took
    /// before submitting instead of leaking a permanently "in flight" flag.
    /// </summary>
    public bool Submit(string label, Action<CancellationToken> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            _queue.Add(new WorkItem(label, work));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // CompleteAdding raced with this submission during shutdown.
            return false;
        }
    }

    private void Consume()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_stopping.Token))
            {
                Interlocked.Increment(ref _running);
                try
                {
                    item.Work(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A single failed unit must never take the worker thread
                    // with it; the next queued repository still needs indexing.
                    _logger.LogWarning(ex, "git background work {Label} failed", item.Label);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            SilentCatch.Note(ex, "GitBackgroundExecutor: worker stopped on shutdown");
        }
        catch (InvalidOperationException ex)
        {
            SilentCatch.Note(ex, "GitBackgroundExecutor: queue completed while worker was blocked");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _queue.CompleteAdding(); }
        catch (ObjectDisposedException ex) { SilentCatch.Note(ex, "GitBackgroundExecutor: queue already torn down"); }
        _stopping.Cancel();
        foreach (var thread in _threads)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { SilentCatch.Note(ex, "GitBackgroundExecutor: worker join failed"); }
        }
        _threads.Clear();
        try { _queue.Dispose(); } catch (Exception ex) { SilentCatch.Note(ex, "GitBackgroundExecutor: queue dispose failed"); }
        _stopping.Dispose();
    }

    private readonly record struct WorkItem(string Label, Action<CancellationToken> Work);
}
