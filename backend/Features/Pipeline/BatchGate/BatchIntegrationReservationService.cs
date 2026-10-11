using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>Serializes a code integration with a batch run for the same project.</summary>
public sealed class BatchIntegrationReservationService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPushes> _pushes =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingPushes
    {
        public HashSet<string> Jobs { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Cleared { get; set; } = Completed();

        private static TaskCompletionSource Completed()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    public async Task<IDisposable> AcquireAsync(string project, CancellationToken ct)
    {
        var semaphore = _projects.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Grant(semaphore);
    }

    public async Task<IDisposable> AcquireBatchAsync(string project, CancellationToken ct)
    {
        while (true)
        {
            var grant = await AcquireAsync(project, ct).ConfigureAwait(false);
            var pending = _pushes.GetOrAdd(project, _ => new PendingPushes());
            Task wait;
            lock (pending)
            {
                if (pending.Jobs.Count == 0) return grant;
                wait = pending.Cleared.Task;
            }
            grant.Dispose();
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // Called while the code merge owns the reservation, before its push enters
    // the background queue. A failed push remains pending for the backstop.
    public void MarkPushPending(string project, string jobId)
    {
        var pending = _pushes.GetOrAdd(project, _ => new PendingPushes());
        lock (pending)
        {
            if (pending.Jobs.Count == 0)
                pending.Cleared = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Jobs.Add(jobId);
        }
    }

    public void MarkPushVerified(string project, string jobId)
    {
        if (!_pushes.TryGetValue(project, out var pending)) return;
        lock (pending)
        {
            pending.Jobs.Remove(jobId);
            if (pending.Jobs.Count == 0) pending.Cleared.TrySetResult();
        }
    }

    private sealed class Grant(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release();
        }
    }
}
