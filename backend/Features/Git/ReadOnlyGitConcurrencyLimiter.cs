namespace AgentStudio.Git;

/// <summary>
/// Process-wide ceiling for the read-only git projections used to enrich board
/// cards. The board can cover many repositories, but it must not turn a cache
/// miss into an unbounded process fan-out.
///
/// <para>
/// Since AGT-2726 this is a backstop, not the admission mechanism. The real
/// gate is the background git index: single-flight per repository, executed on
/// <see cref="StateIndex.GitBackgroundExecutor"/>'s bounded, dedicated threads.
/// In the normal path this semaphore is never contended, because fewer threads
/// can reach it than it has slots. It stays because a future caller that
/// computes a projection outside the indexer must still be bounded - and
/// because a blocking wait here is now confined to the executor's own threads
/// rather than to thread-pool threads request handling needs.
/// </para>
/// </summary>
internal static class ReadOnlyGitConcurrencyLimiter
{
    internal const int MaxConcurrency = 4;
    private static readonly SemaphoreSlim Slots = new(MaxConcurrency, MaxConcurrency);

    public static TValue Run<TValue>(Func<TValue> operation)
    {
        Slots.Wait();
        try
        {
            return operation();
        }
        finally
        {
            Slots.Release();
        }
    }
}
