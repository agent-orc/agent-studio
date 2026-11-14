using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// Reusable dedicated threads that drain the pipes of synchronous Git children
/// (AGT-3567). <see cref="GitNetworkProcessRunner"/> reads standard output and
/// standard error of every child concurrently and off the ThreadPool, so a busy
/// ThreadPool can never stall a synchronous Git call. It used to start two new
/// threads per child for that. <see cref="Thread.Start()"/> returns only once
/// the new thread runs, and on a loaded runner host those starts took longer
/// than the Git processes themselves: 54 of 126 seconds of Git time in
/// <c>MergeIntoDevelopRunnerTests</c>.
///
/// <para>A drain takes an idle thread from this cache, or starts a new one when
/// none is idle. The number of threads is therefore never bounded, and a read
/// that blocks never waits for another one. The most recently idle thread is
/// reused first; a thread idle for <see cref="IdleLifetime"/> ends.</para>
/// </summary>
internal static class GitPipeDrainThreads
{
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentStack<Worker> Idle = new();

    /// <summary>Runs <paramref name="work"/> on a dedicated thread and completes with its result.</summary>
    internal static Task<T> Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // A synchronous wait on the task completes inline; any other
        // continuation must not run on the drain thread.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }

        while (Idle.TryPop(out var worker))
        {
            if (worker.TryAssign(Execute)) return completion.Task;
        }
        Worker.Start(Execute);
        return completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> on a dedicated thread.</summary>
    internal static Task Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(() =>
        {
            work();
            return true;
        });
    }

    private sealed class Worker
    {
        private const int Busy = 0;
        private const int Waiting = 1;
        private const int Retired = 2;

        private readonly SemaphoreSlim _assigned = new(0);
        private Action? _work;
        private int _state = Busy;

        internal static void Start(Action first)
        {
            var worker = new Worker { _work = first };
            new Thread(worker.Loop) { IsBackground = true, Name = "git-pipe-drain" }.Start();
        }

        /// <summary>False when the worker retired after it was pushed as idle.</summary>
        internal bool TryAssign(Action work)
        {
            if (Interlocked.CompareExchange(ref _state, Busy, Waiting) != Waiting) return false;
            _work = work;
            _assigned.Release();
            return true;
        }

        private void Loop()
        {
            while (true)
            {
                var work = _work!;
                _work = null;
                work();

                Volatile.Write(ref _state, Waiting);
                Idle.Push(this);
                if (_assigned.Wait(IdleLifetime)) continue;
                if (Interlocked.CompareExchange(ref _state, Retired, Waiting) == Waiting) return;
                // Assigned between the timeout and the retirement: the work is on its way.
                _assigned.Wait();
            }
        }
    }
}
