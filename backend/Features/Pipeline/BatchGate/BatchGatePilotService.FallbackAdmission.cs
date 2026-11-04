using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3466: per-task fallback gates share the gate host with the batch, a
/// formed batch that never started keeps its members together when the base
/// moves, and every per-task gate records why it left the batch route.
/// </summary>
public sealed partial class BatchGatePilotService
{
    private readonly PerTaskGateSlots _perTaskGateSlots = new();

    /// <summary>Per-task gates of this process that hold a gate host slot or prepare one.</summary>
    internal int PerTaskGatesRunning => _perTaskGateSlots.Running;

    /// <summary>Per-task gates of this process that wait for <see cref="PerTaskGatesRunning"/> to drop.</summary>
    internal int PerTaskGatesWaiting => _perTaskGateSlots.Waiting;

    /// <summary>
    /// Waits until a per-task gate may run under
    /// <see cref="MonolithTransitionAdapter.PerTaskGateSlotFree"/>: at most one
    /// slot fewer than the gate host has, the batch slot stays free. The limit
    /// is read again whenever a gate ends and every 30 seconds, so a slot count
    /// the gate host reports later applies to waiting gates too.
    /// </summary>
    private async Task<IDisposable> EnterPerTaskGateAsync(BatchGatePendingRecord pending, CancellationToken ct)
    {
        var waiting = false;
        try
        {
            while (true)
            {
                var released = _perTaskGateSlots.Released;
                var slots = ConfiguredGateSlots();
                if (_perTaskGateSlots.TryEnter(slots)) return new PerTaskGateSlot(_perTaskGateSlots);
                if (!waiting)
                {
                    waiting = true;
                    _perTaskGateSlots.Wait(+1);
                    _logger.LogInformation(
                        "batch-fallback-waiting-for-gate-slot task={TaskKey} attempt={AttemptId} running={Running} slots={Slots}",
                        pending.Subject.TaskKey, pending.ReviewAttemptId, _perTaskGateSlots.Running, slots);
                }
                await Task.WhenAny(released, Task.Delay(TimeSpan.FromSeconds(30), ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (waiting) _perTaskGateSlots.Wait(-1);
        }
    }

    private void RecordFallbackCause(BatchGatePendingRecord pending, PerTaskFallbackCause cause)
    {
        try
        {
            if (_store.RecordFallbackCause(new BatchGateFallbackCause(pending.Subject.Project,
                    pending.Subject.TaskKey, pending.ReviewAttemptId, pending.Subject.RunAttempt,
                    cause.Rule, cause.Event, cause.BatchId, _time.GetUtcNow())))
                _logger.LogInformation(
                    "batch-fallback-cause task={TaskKey} attempt={AttemptId} rule={Rule} event={Event}",
                    pending.Subject.TaskKey, pending.ReviewAttemptId, cause.Rule, cause.Event);
        }
        catch (IOException ex)
        {
            // The cause is state evidence; it never blocks the gate.
            _logger.LogWarning(ex, "batch-fallback-cause-record-failed attempt={AttemptId}", pending.ReviewAttemptId);
        }
    }

    /// <summary>
    /// A formed batch that never replayed is only a membership on an old base.
    /// When the base moved, the same members close again on the new base and
    /// keep their batch: before AGT-3466 they returned to planning with their
    /// old review times, past every deadline, and the planner sent the ones it
    /// did not batch at once to per-task gates. Returns false when a member is
    /// no longer eligible; the caller then returns the members to planning.
    /// </summary>
    private bool ReformOnMovedBase(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending, string newBase, long fence)
    {
        if (_store.ListRuns(manifest.BatchId).Count > 0
            || manifest.Members.Any(member => _store.TryReadReplay(manifest.BatchId, member.TaskKey) is not null))
            return false;
        var options = _settings.Get(manifest.Scope.Project).BatchGate;
        // The members are owned by this manifest; ownership moves with them.
        var members = manifest.Members.Select(member =>
            Refresh(pending[member.TaskKey]).Subject with { OwningBatchId = null }).ToArray();
        if (members.Any(member => BatchGatePolicy.Exclusion(member, manifest.Scope, options) is not null))
            return false;
        var successor = manifest with
        {
            BatchId = Guid.NewGuid().ToString("N"),
            BaseSha = newBase,
            MembershipDigest = BatchGatePolicy.MembershipDigest(newBase, manifest.Scope, members),
            ClosedAtUtc = _time.GetUtcNow(),
            Members = members,
        };
        State(manifest, BatchPhase.Abandoned, null, fence,
            $"base moved before replay; re-formed as {successor.BatchId} on {Short(newBase)}");
        ReturnToPending(manifest);
        _store.CloseManifest(successor);
        foreach (var member in members)
        {
            var item = pending[member.TaskKey];
            var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
            if (task is not null)
                BatchGateOwnershipStore.Write(task.FolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId, member, successor.BatchId));
        }
        _logger.LogInformation(
            "batch-gate-reformed batch={BatchId} successor={SuccessorId} base={BaseSha} members={Members}",
            manifest.BatchId, successor.BatchId, newBase, string.Join(',', members.Select(member => member.TaskKey)));
        return true;
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>Counts the per-task gates of this process; a release wakes the waiting ones.</summary>
    private sealed class PerTaskGateSlots
    {
        private readonly object _lock = new();
        private int _running;
        private int _waiting;
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Running { get { lock (_lock) return _running; } }

        public int Waiting { get { lock (_lock) return _waiting; } }

        public Task Released { get { lock (_lock) return _released.Task; } }

        public void Wait(int delta)
        {
            lock (_lock) _waiting += delta;
        }

        public bool TryEnter(int slotsTotal)
        {
            lock (_lock)
            {
                if (!MonolithTransitionAdapter.PerTaskGateSlotFree(_running, slotsTotal)) return false;
                _running++;
                return true;
            }
        }

        public void Exit()
        {
            TaskCompletionSource released;
            lock (_lock)
            {
                _running--;
                released = _released;
                _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            released.TrySetResult();
        }
    }

    private sealed class PerTaskGateSlot(PerTaskGateSlots slots) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Exit();
        }
    }
}
