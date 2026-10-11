using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// Single-flight cache for serialized board responses. The validator key
/// includes the task-index generation and live projection generations, so
/// waiting readers share the same build and then write independently.
/// </summary>
public sealed class BoardReadConcurrencyGate
{
    private readonly ConcurrentDictionary<string, Lazy<byte[]>> _bodies = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Lazy<byte[]>>> _cards = new();
    private readonly Lock _trimLock = new();

    public byte[] GetOrBuildCard(string generation, string taskKey, Func<byte[]> build)
    {
        var cards = _cards.GetOrAdd(generation, _ => new ConcurrentDictionary<string, Lazy<byte[]>>());
        var entry = cards.GetOrAdd(taskKey, _ => new Lazy<byte[]>(build,
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return entry.Value; }
        catch
        {
            cards.TryRemove(new KeyValuePair<string, Lazy<byte[]>>(taskKey, entry));
            throw;
        }
    }

    public byte[] GetOrBuild(string key, Func<byte[]> build)
    {
        var entry = _bodies.GetOrAdd(key, _ => new Lazy<byte[]>(build,
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var body = entry.Value;
            if (_bodies.Count > 24) Trim(key);
            return body;
        }
        catch
        {
            _bodies.TryRemove(new KeyValuePair<string, Lazy<byte[]>>(key, entry));
            throw;
        }
    }

    private void Trim(string current)
    {
        lock (_trimLock)
        {
            if (_bodies.Count <= 24) return;
            foreach (var key in _bodies
                .Where(entry => entry.Key != current && entry.Value.IsValueCreated)
                .Select(entry => entry.Key)
                .Take(_bodies.Count - 12))
            {
                _bodies.TryRemove(key, out _);
                _cards.TryRemove(key, out _);
            }
        }
    }
}
