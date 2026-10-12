using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>A built board body and the strong validator derived from its bytes.</summary>
public sealed record BoardReadBody(byte[] Bytes, string ETag);

/// <summary>
/// Shares one serialized body build among readers of the same board signature.
/// The signature only decides whether a body must be rebuilt; the entity tag is
/// derived from the built bytes, so a signature that moved without changing the
/// board (an index rescan that found nothing, a runtime fact the card does not
/// show) still validates the client's copy.
/// </summary>
public sealed class BoardReadConcurrencyGate
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BoardReadBody>>> _bodies = new();
    private readonly Lock _trimLock = new();

    /// <param name="signature">Cache key: everything that can change the body.</param>
    /// <param name="scope">Route and request variant, so two variants never share a tag.</param>
    public async Task<BoardReadBody> GetOrBuildAsync(string signature, string scope,
        Func<byte[]> build, CancellationToken cancellationToken)
    {
        var entry = _bodies.GetOrAdd(signature, _ => new Lazy<Task<BoardReadBody>>(
            () => Task.Run(() =>
            {
                var bytes = build();
                return new BoardReadBody(bytes, BoardReadValidator.ContentETag(scope, bytes));
            }), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // Disconnecting one waiter does not cancel the shared build.
            var body = await entry.Value.WaitAsync(cancellationToken);
            if (_bodies.Count > 24) Trim(signature);
            return body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _bodies.TryRemove(new KeyValuePair<string, Lazy<Task<BoardReadBody>>>(signature, entry));
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
