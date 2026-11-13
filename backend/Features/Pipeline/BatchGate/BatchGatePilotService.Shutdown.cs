using AgentStudio.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3755: a flight exit schedules a follow-up tick on a pool thread. When the
/// host stops while that tick runs, the root service provider is disposed under
/// it. That tick must end quietly: an unobserved faulted task here was rethrown
/// on the finalizer thread and took down a whole test host.
/// </summary>
public sealed partial class BatchGatePilotService : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    internal bool Stopping => _shutdown.IsCancellationRequested;

    /// <summary>Stops follow-up ticks; flights still running finish on their own tokens.</summary>
    internal void BeginShutdown()
    {
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException ex)
        {
            SilentCatch.Note(ex, "Batch gate pilot shutdown raced with disposal");
        }
    }

    // The container disposes this singleton with the root provider.
    public void Dispose() => BeginShutdown();

    /// <summary>Starts a tick in the background; the returned task never faults.</summary>
    internal Task ScheduleFollowUpTick()
    {
        if (Stopping) return Task.CompletedTask;
        var tick = Task.Run(() => TickAsync(_shutdown.Token));
        return tick.ContinueWith(ObserveFollowUpTick, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ObserveFollowUpTick(Task done)
    {
        if (done.Exception is not { } error) return;
        if (ShutdownFault(error))
        {
            _logger.LogDebug("batch-gate-follow-up-tick-dropped reason=host-stopped: {Reason}",
                error.GetBaseException().Message);
            return;
        }
        _logger.LogError(error, "batch-gate-follow-up-tick-failed");
    }

    private bool ShutdownFault(AggregateException error)
        => error.Flatten().InnerExceptions.All(inner =>
            inner is ObjectDisposedException
            || inner is OperationCanceledException && Stopping);

    /// <summary>
    /// Resolves an optional collaborator. A disposed provider means the host
    /// has stopped: the pilot stops ticking and the caller ends with a
    /// cancellation instead of an <see cref="ObjectDisposedException"/>.
    /// </summary>
    private T? Service<T>() where T : class
    {
        if (_services is null) return null;
        try { return _services.GetService<T>(); }
        catch (ObjectDisposedException ex)
        {
            BeginShutdown();
            throw new OperationCanceledException(
                "The batch gate pilot host stopped while a tick was running.", ex, _shutdown.Token);
        }
    }
}
