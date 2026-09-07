using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// Admission control for the read-only git projections that back board cards.
///
/// <para>
/// Two levels, both acquired in the same order so they cannot deadlock. The
/// per-repository lock keeps two computations for the same repository from
/// duplicating each other's work; the cross-repository budget keeps a
/// multi-repository workspace from turning one refresh into a process fan-out.
/// </para>
///
/// <para>
/// Both waits block the calling thread, which is only correct because every
/// caller now runs on background index work (AGT-2726). While this was a single
/// process-wide semaphore acquired from request threads, a board refresh could
/// hold four thread-pool threads in <c>Wait()</c> and leave
/// <c>tasks/grouped</c> queued behind work it had no part in. Do not call this
/// from a request path.
/// </para>
/// </summary>
internal static class ReadOnlyGitConcurrencyLimiter
{
    /// <summary>
    /// How many repositories may be read at once, matching the git index's own
    /// cross-repository budget.
    /// </summary>
    internal const int MaxConcurrency = GitStateIndex.MaxConcurrentRepositories;

    private static readonly SemaphoreSlim Slots = new(MaxConcurrency, MaxConcurrency);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryGates =
        new(StringComparer.OrdinalIgnoreCase);

    public static TValue Run<TValue>(string repositoryRoot, Func<TValue> operation)
    {
        var gate = RepositoryGates.GetOrAdd(
            string.IsNullOrWhiteSpace(repositoryRoot) ? "" : repositoryRoot,
            static _ => new SemaphoreSlim(1, 1));

        gate.Wait();
        try
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
        finally
        {
            gate.Release();
        }
    }
}
