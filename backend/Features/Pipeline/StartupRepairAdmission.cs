using AgentStudio.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>The admissions a deferred start-up repair keeps closed while it runs.</summary>
[Flags]
public enum StartupAdmission
{
    None = 0,
    Integration = 1,
    Push = 2,
}

/// <summary>
/// Deferred start-up repairs and the admissions they protect (AGT-4239).
///
/// <para>
/// Git-backed commit and attribution repairs used to run synchronously before
/// the HTTP listener. On a large workspace one of them walked the whole task
/// tree once per card and kept the host unhealthy for longer than the deploy
/// window. They now run after the host has started, one after another on a
/// dedicated thread, and only the admissions they protect wait for them:
/// a repair enqueued here closes its admissions immediately, so a merge or a
/// push requested before the repair has run, or while it runs, waits with a
/// visible reason (<see cref="WaitingReason"/>) and proceeds once it ends.
/// </para>
/// <para>
/// A failed repair is logged and still releases its admissions, exactly as a
/// failed synchronous boot sweep never blocked start-up before.
/// </para>
/// </summary>
public sealed class StartupRepairAdmission
{
    private readonly Lock _gate = new();
    private readonly List<Repair> _repairs = [];
    private readonly ILogger<StartupRepairAdmission> _logger;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _drainStarted;

    public StartupRepairAdmission(ILogger<StartupRepairAdmission> logger) => _logger = logger;

    /// <summary>Completes when every enqueued repair has ended.</summary>
    public Task Drained => _drained.Task;

    /// <summary>Queues one repair and closes its admissions until it ends.</summary>
    public void Enqueue(string name, StartupAdmission closes, Action work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (Volatile.Read(ref _drainStarted) != 0)
                throw new InvalidOperationException("Start-up repairs are already draining.");
            _repairs.Add(new Repair(name, closes, work));
        }
        _logger.LogInformation("startup-repair-queued repair={Repair} closes={Admissions}", name, closes);
    }

    /// <summary>
    /// The reason an admission is closed now, for example
    /// <c>startup-repair:CommitMetadataBackfill (running)</c>, or null when open.
    /// </summary>
    public string? WaitingReason(StartupAdmission admission)
    {
        lock (_gate)
        {
            var blocking = _repairs.FirstOrDefault(repair => !repair.Done.Task.IsCompleted
                && (repair.Closes & admission) != 0);
            return blocking is null ? null : $"startup-repair:{blocking.Name} ({blocking.Status})";
        }
    }

    /// <summary>Waits until no pending repair closes <paramref name="admission"/>.</summary>
    public async Task WaitAsync(StartupAdmission admission, CancellationToken ct)
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                var blocking = _repairs.FirstOrDefault(repair => !repair.Done.Task.IsCompleted
                    && (repair.Closes & admission) != 0);
                if (blocking is null) return;
                pending = blocking.Done.Task;
            }
            await pending.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Repair names with their state, in queue order.</summary>
    public IReadOnlyList<(string Repair, string Status)> Snapshot()
    {
        lock (_gate) return _repairs.Select(repair => (repair.Name, repair.Status)).ToList();
    }

    /// <summary>
    /// Runs every queued repair once, in order, on the calling thread. Waits for
    /// <paramref name="beforeFirst"/> first (the task index publication, so the
    /// repairs read the cached snapshot instead of walking the disk per card).
    /// Cancellation leaves the remaining repairs queued and releases nothing.
    /// </summary>
    public void Drain(StartupPhaseLog? phases, Func<CancellationToken, Task>? beforeFirst, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _drainStarted, 1) == 1) return;
        try
        {
            List<Repair> repairs;
            lock (_gate) repairs = [.. _repairs];
            if (repairs.Count == 0)
            {
                _drained.TrySetResult();
                return;
            }
            // A wait that never ends is named by the watchdog like any phase.
            if (beforeFirst is not null)
                using (phases?.Begin("repairs:waiting-for-task-index"))
                    beforeFirst(ct).GetAwaiter().GetResult();
            foreach (var repair in repairs)
            {
                ct.ThrowIfCancellationRequested();
                Run(repair, phases);
            }
            _logger.LogInformation("startup-repairs-complete repairs={Count}", repairs.Count);
            _drained.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("startup-repairs-stopped reason=host-stopping");
            _drained.TrySetCanceled(ct);
        }
    }

    private void Run(Repair repair, StartupPhaseLog? phases)
    {
        lock (_gate) repair.Status = "running";
        try
        {
            if (phases is null) repair.Work();
            else phases.Run("repair:" + repair.Name, repair.Work);
            lock (_gate) repair.Status = "completed";
        }
        catch (Exception ex)
        {
            lock (_gate) repair.Status = "failed";
            _logger.LogError(ex, "startup-repair-failed repair={Repair}; its admissions open as before", repair.Name);
        }
        finally
        {
            repair.Done.TrySetResult();
        }
    }

    private sealed class Repair(string name, StartupAdmission closes, Action work)
    {
        public string Name { get; } = name;
        public StartupAdmission Closes { get; } = closes;
        public Action Work { get; } = work;
        public string Status { get; set; } = "queued";
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
