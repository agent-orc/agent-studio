using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Pure decisions behind the derived source data store (AGT-3318). The store
/// collects rows and applies the returned decision; the matrix tests drive
/// these functions directly.
/// </summary>
internal static class SourceProducerLeasePolicy
{
    internal const int MinimumTtlSeconds = 30;
    internal const int MaximumTtlSeconds = 3600;

    internal sealed record Current(string ProducerId, long Epoch, DateTime ExpiresAt);

    internal enum Outcome { Granted, Renewed, TakenOver, Held }

    internal sealed record Decision(Outcome Outcome, long Epoch, DateTime ExpiresAt);

    /// <summary>
    /// One lease per repository. The holder renews under its epoch. Another
    /// producer waits until the lease expires and then takes it with the next
    /// epoch, which fences every later publish of the previous holder.
    /// </summary>
    internal static Decision Decide(Current? current, string producerId, int requestedTtlSeconds, DateTime now)
    {
        var expiresAt = now.AddSeconds(Math.Clamp(requestedTtlSeconds, MinimumTtlSeconds, MaximumTtlSeconds));
        if (current is null) return new Decision(Outcome.Granted, 1, expiresAt);
        if (string.Equals(current.ProducerId, producerId, StringComparison.Ordinal))
            return new Decision(Outcome.Renewed, current.Epoch, expiresAt);
        return current.ExpiresAt > now
            ? new Decision(Outcome.Held, current.Epoch, current.ExpiresAt)
            : new Decision(Outcome.TakenOver, current.Epoch + 1, expiresAt);
    }

    /// <summary>A publish is admitted only from the current holder under the current epoch.</summary>
    internal static bool AdmitsPublish(Current? current, string producerId, long epoch)
        => current is not null
           && current.Epoch == epoch
           && string.Equals(current.ProducerId, producerId, StringComparison.Ordinal);
}

/// <summary>
/// Size-bounded eviction. Objects are immutable and keyed by content, so they
/// carry no TTL; once the stored bytes exceed the cap, the least recently read
/// objects go first until the store is back under the low-water mark.
/// </summary>
internal static class SourceFactEvictionPolicy
{
    internal const double LowWaterFraction = 0.9;

    internal sealed record StoredObject(string Table, long RowId, long SizeBytes, DateTime LastReadAt);

    internal static IReadOnlyList<StoredObject> SelectVictims(
        IReadOnlyList<StoredObject> objects,
        long maximumBytes)
    {
        var total = objects.Sum(item => item.SizeBytes);
        if (maximumBytes <= 0 || total <= maximumBytes) return [];
        var target = (long)(maximumBytes * LowWaterFraction);
        var victims = new List<StoredObject>();
        foreach (var candidate in objects
                     .OrderBy(item => item.LastReadAt)
                     .ThenBy(item => item.Table, StringComparer.Ordinal)
                     .ThenBy(item => item.RowId))
        {
            if (total <= target) break;
            victims.Add(candidate);
            total -= candidate.SizeBytes;
        }
        return victims;
    }
}

internal static class SourceReachabilityPolicy
{
    /// <summary>
    /// Containment is monotonic under fast-forward, so a positive fact stays
    /// current. A negative fact is current only at the head it was computed at.
    /// </summary>
    internal static bool IsCurrent(SourceReachabilityFact fact, string? currentHeadSha)
        => fact.Contained
           || (currentHeadSha is not null
               && string.Equals(fact.RefHeadSha, currentHeadSha, StringComparison.OrdinalIgnoreCase));
}

internal static class SourceTimingPercentile
{
    /// <summary>Nearest-rank percentile; 0 for an empty sample.</summary>
    internal static long NearestRank(IReadOnlyList<long> samples, double percentile)
    {
        if (samples.Count == 0) return 0;
        var ordered = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100d * ordered.Length);
        return ordered[Math.Clamp(rank, 1, ordered.Length) - 1];
    }
}
