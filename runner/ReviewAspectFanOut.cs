using System.Runtime.ExceptionServices;

namespace AgentRunner;

/// <summary>
/// Runs the agent aspects of one Remote Review attempt with a bounded number in
/// flight (AGT-3400 PK7, option X-B). Items start in plan order. The first
/// failure stops the attempt the way the serial loop did: it cancels the
/// aspects still running, starts no further aspect, and is the one exception
/// the caller sees. A cancellation caused by that stop is not a second failure.
/// With a parallelism of 1 this is the serial loop.
/// </summary>
internal static class ReviewAspectFanOut
{
    public const int MaximumParallelism = 4;

    /// <summary>Clamps a configured value to 1 (serial) through <see cref="MaximumParallelism"/>.</summary>
    public static int Normalize(int configured)
        => Math.Clamp(configured, 1, MaximumParallelism);

    public static async Task RunAsync<T>(
        IReadOnlyList<T> items,
        int parallelism,
        Func<T, CancellationToken, Task> run,
        CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(Normalize(parallelism));
        Exception? failure = null;

        await Task.WhenAll(items.Select(RunOneAsync).ToArray());

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        ct.ThrowIfCancellationRequested();

        async Task RunOneAsync(T item)
        {
            try
            {
                await slots.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped before this item started; the stop's cause is reported once.
                return;
            }

            try
            {
                stop.Token.ThrowIfCancellationRequested();
                await run(item, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Cancelled by a sibling's failure or by the caller, not a failure of its own.
                return;
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref failure, exception, null) is null)
                    await stop.CancelAsync();
            }
            finally
            {
                slots.Release();
            }
        }
    }
}
