using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// Shares one serialized body build among readers of the same board generation.
/// </summary>
public sealed class BoardReadConcurrencyGate
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _bodies = new();
    private readonly Lock _trimLock = new();
    private long _builds;

    /// <summary>Body builds started so far (diagnostics and tests).</summary>
    internal long Builds => Interlocked.Read(ref _builds);

    public async Task<byte[]> GetOrBuildAsync(string key, Func<byte[]> build, CancellationToken cancellationToken)
    {
        var entry = _bodies.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(
            () =>
            {
                Interlocked.Increment(ref _builds);
                return Task.Run(build);
            },
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // Disconnecting one waiter does not cancel the shared build.
            var body = await entry.Value.WaitAsync(cancellationToken);
            if (_bodies.Count > 24) Trim(key);
            return body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _bodies.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, entry));
            throw;
        }
    }

    private void Trim(string current)
    {
        lock (_trimLock)
        {
            if (_bodies.Count <= 24) return;
            foreach (var key in _bodies
                .Where(entry => entry.Key != current && entry.Value.IsValueCreated
                    && entry.Value.Value.IsCompleted)
                .Select(entry => entry.Key)
                .Take(_bodies.Count - 12))
            {
                _bodies.TryRemove(key, out _);
            }
        }
    }
}
