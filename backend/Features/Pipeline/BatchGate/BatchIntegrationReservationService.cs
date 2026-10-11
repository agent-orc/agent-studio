using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>Serializes a code integration with a batch run for the same project.</summary>
public sealed class BatchIntegrationReservationService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string project, CancellationToken ct)
    {
        var semaphore = _projects.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Grant(semaphore);
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
