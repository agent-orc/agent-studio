using System.Collections.Concurrent;

namespace AgentStudio.Shared;

/// <summary>
/// Small synchronous single-flight cache for expensive read projections. A
/// cache miss is computed once per key and generation; concurrent callers wait
/// for that same value instead of starting duplicate external work.
/// </summary>
internal sealed class GenerationSingleFlightCache<TValue>
{
    private const int DefaultMaxEntries = 256;

    private readonly ConcurrentDictionary<string, CacheEntry> _values =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<TValue>> _flights =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _latestVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly object _generationLock = new();
    private readonly object _evictionLock = new();
    private readonly LinkedList<string> _evictionOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _evictionNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    public GenerationSingleFlightCache(
        TimeProvider? timeProvider = null,
        int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxEntries = maxEntries;
    }

    public TValue GetOrCreate(string key, TimeSpan ttl, Func<TValue> valueFactory)
        => GetOrCreateVersioned(key, string.Empty, ttl, valueFactory);

    /// <summary>
    /// Caches one current version per logical key. A changed version starts a
    /// new single flight immediately without retaining every historical value.
    /// </summary>
    public TValue GetOrCreateVersioned(
        string key,
        string version,
        TimeSpan ttl,
        Func<TValue> valueFactory)
        => GetOrCreateVersioned(key, version, _ => ttl, valueFactory);

    /// <summary>
    /// Variant whose cache lifetime depends on the computed value. This lets
    /// callers single-flight transient failures without retaining them for the
    /// normal successful projection lifetime.
    /// </summary>
    public TValue GetOrCreateVersioned(
        string key,
        string version,
        Func<TValue, TimeSpan> ttlSelector,
        Func<TValue> valueFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(ttlSelector);
        ArgumentNullException.ThrowIfNull(valueFactory);

        long generation;
        lock (_generationLock)
        {
            generation = _generation;
            _latestVersions[key] = version;
        }
        var now = _timeProvider.GetUtcNow();
        if (_values.TryGetValue(key, out var cached)
            && cached.Generation == generation
            && string.Equals(cached.Version, version, StringComparison.Ordinal)
            && cached.ExpiresAt > now)
        {
            Touch(key, generation);
            return cached.Value;
        }

        // File-system paths cannot contain NUL, so this is an unambiguous
        // generation + caller-key composite while retaining the dictionary's
        // case-insensitive path semantics.
        var flightKey = $"{generation}\0{key}\0{version}";
        var candidate = new Lazy<TValue>(valueFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(flightKey, candidate);

        try
        {
            var value = flight.Value;
            var ttl = ttlSelector(value);
            lock (_generationLock)
            {
                if (generation == _generation
                    && ttl > TimeSpan.Zero
                    && _latestVersions.TryGetValue(key, out var latestVersion)
                    && string.Equals(latestVersion, version, StringComparison.Ordinal))
                {
                    _values[key] = new CacheEntry(
                        generation,
                        version,
                        _timeProvider.GetUtcNow().Add(ttl),
                        value);
                    TouchUnderGenerationLock(key);
                }
                else if (generation == _generation && ttl <= TimeSpan.Zero)
                {
                    RemoveLatestVersion(key, version);
                }
            }
            return value;
        }
        catch
        {
            lock (_generationLock)
            {
                if (generation == _generation)
                    RemoveLatestVersion(key, version);
            }
            throw;
        }
        finally
        {
            // Only the owner recorded for this generation may remove the
            // flight. The cache value is published before this removal, so a
            // following caller cannot slip through into a duplicate compute.
            if (_flights.TryGetValue(flightKey, out var current)
                && ReferenceEquals(current, flight))
            {
                _flights.TryRemove(flightKey, out _);
            }
        }
    }

    /// <summary>
    /// Returns the cached value for the key when it is current and unexpired,
    /// without starting or joining a flight. Request paths use this to read
    /// git-derived state without ever blocking on a computation the background
    /// index owns (AGT-2726).
    /// </summary>
    public bool TryPeekVersioned(string key, string version, out TValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(version);

        long generation;
        lock (_generationLock) generation = _generation;

        if (_values.TryGetValue(key, out var cached)
            && cached.Generation == generation
            && string.Equals(cached.Version, version, StringComparison.Ordinal)
            && cached.ExpiresAt > _timeProvider.GetUtcNow())
        {
            value = cached.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Starts a new generation. Already-running readers may finish with their
    /// old snapshot, but they cannot republish it into the new generation.
    /// </summary>
    public void Invalidate()
    {
        lock (_generationLock)
        {
            _generation++;
            _values.Clear();
            _latestVersions.Clear();
            lock (_evictionLock)
            {
                _evictionOrder.Clear();
                _evictionNodes.Clear();
            }
        }
    }

    internal int ValueCount => _values.Count;
    internal int TrackedKeyCount => _latestVersions.Count;

    private void Touch(string key, long generation)
    {
        lock (_generationLock)
        {
            if (generation != _generation || !_values.ContainsKey(key)) return;
            TouchUnderGenerationLock(key);
        }
    }

    private void TouchUnderGenerationLock(string key)
    {
        lock (_evictionLock)
        {
            if (_evictionNodes.TryGetValue(key, out var existing))
                _evictionOrder.Remove(existing);

            _evictionNodes[key] = _evictionOrder.AddLast(key);
            while (_evictionOrder.Count > _maxEntries
                   && _evictionOrder.First is { } oldest)
            {
                _evictionOrder.RemoveFirst();
                _evictionNodes.Remove(oldest.Value);
                _values.TryRemove(oldest.Value, out _);
                _latestVersions.TryRemove(oldest.Value, out _);
            }
        }
    }

    private void RemoveLatestVersion(string key, string version)
        => ((ICollection<KeyValuePair<string, string>>)_latestVersions).Remove(
            new KeyValuePair<string, string>(key, version));

    private sealed record CacheEntry(
        long Generation,
        string Version,
        DateTimeOffset ExpiresAt,
        TValue Value);
}
