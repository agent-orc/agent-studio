namespace AgentStudio.Security;

/// <summary>
/// Fixed storage for login keys that arrive after the exact-key tables fill. Collisions
/// can delay another client, but cannot erase an active failure count or retry wait.
/// </summary>
internal sealed class LoginOverflowBudget
{
    private const int BucketCount = 1024;
    private readonly Bucket[] _buckets = new Bucket[BucketCount];
    private readonly DateTime[] _retryAt = new DateTime[BucketCount];

    public bool CanHash(string key, string delayKey, DateTime now, TimeSpan window, int maxFailures, TimeSpan delay)
    {
        ref var bucket = ref GetBucket(key);
        ResetExpiredWindow(ref bucket, now, window);
        if (bucket.Failures < Math.Max(1, maxFailures)) return true;
        return PassedDelay(delayKey, now, delay);
    }

    public void RecordFailure(string key, string delayKey, DateTime now, TimeSpan window, int maxFailures, TimeSpan delay)
    {
        ref var bucket = ref GetBucket(key);
        ResetExpiredWindow(ref bucket, now, window);
        if (bucket.Failures == 0) bucket.WindowStartedAt = now;
        if (bucket.Failures < int.MaxValue) bucket.Failures++;
        if (bucket.Failures >= Math.Max(1, maxFailures))
            _retryAt[BucketIndex(delayKey)] = now + PositiveDelay(delay);
    }

    public bool PassedDelay(string key, DateTime now, TimeSpan delay)
    {
        ref var retryAt = ref _retryAt[BucketIndex(key)];
        if (retryAt == default || retryAt > now)
        {
            if (retryAt == default) retryAt = now + PositiveDelay(delay);
            return false;
        }
        retryAt = now + PositiveDelay(delay);
        return true;
    }

    private ref Bucket GetBucket(string key)
        => ref _buckets[BucketIndex(key)];

    private static int BucketIndex(string key)
        => (int)((uint)StringComparer.Ordinal.GetHashCode(key) % BucketCount);

    private static void ResetExpiredWindow(ref Bucket bucket, DateTime now, TimeSpan window)
    {
        if (bucket.Failures > 0 && now - bucket.WindowStartedAt > window)
            bucket = default;
    }

    private static TimeSpan PositiveDelay(TimeSpan delay)
        => delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);

    private struct Bucket
    {
        public int Failures;
        public DateTime WindowStartedAt;
    }
}
