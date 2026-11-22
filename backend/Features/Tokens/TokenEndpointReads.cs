using System.Collections.Concurrent;

namespace AgentStudio.Tokens;

/// <summary>Single-flight diagnostic reads with a bounded HTTP wait and retryable JSON failures.</summary>
public sealed class TokenEndpointReads(TimeProvider time)
{
    private sealed class Read
    {
        public Lazy<Task<object>> Work { get; }
        public long CompletedAtTicks;

        public Read(Func<Task<object>> load, TimeProvider time)
        {
            Work = new(async () =>
            {
                try { return await load(); }
                finally { Interlocked.Exchange(ref CompletedAtTicks, time.GetUtcNow().UtcTicks); }
            });
        }
    }
    private readonly object _admission = new();
    private readonly ConcurrentDictionary<string, Read> _reads = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private const int Capacity = 64;

    public async Task<IResult> ReadAsync(string key, Func<Task<object>> load,
        CancellationToken ct = default, TimeSpan? wait = null)
    {
        Read read;
        lock (_admission)
        {
            var now = time.GetUtcNow();
            foreach (var pair in _reads)
                if (pair.Value.Work.IsValueCreated && pair.Value.Work.Value.IsCompleted
                    && Interlocked.Read(ref pair.Value.CompletedAtTicks) is var completed && completed > 0
                    && now.UtcTicks - completed >= Ttl.Ticks)
                    _reads.TryRemove(pair);
            if (!_reads.ContainsKey(key) && _reads.Count >= Capacity)
                return Unavailable("Token diagnostics are busy. Retry shortly.");
            read = _reads.GetOrAdd(key, _ => new Read(load, time));
        }
        try
        {
            var value = await read.Work.Value.WaitAsync(wait ?? TimeSpan.FromSeconds(5), ct);
            return Results.Ok(value);
        }
        catch (TimeoutException)
        {
            // Keep the single flight running. A retry joins it instead of starting another scan.
            return Unavailable("Token diagnostics are still loading. Retry shortly.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _reads.TryRemove(new KeyValuePair<string, Read>(key, read));
            return Unavailable("Token diagnostics could not be loaded. Retry shortly.");
        }
    }

    private static IResult Unavailable(string detail)
        => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Token diagnostics unavailable", detail: detail);
}
