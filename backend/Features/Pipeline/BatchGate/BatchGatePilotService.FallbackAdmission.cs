using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Per-task fallback gates share the gate host with the batch. A formed
/// batch keeps compatible members together when its base moves.
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
    /// A closed manifest is immutable. Probe each still-admitted review against
    /// the new base and close one linked successor containing only survivors.
    /// The successor starts with no replay, run or verdict evidence.
    /// </summary>
    private bool ReformOnMovedBase(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending, string repo,
        string newBase, long fence, ISet<string> conflictKeys)
    {
        if (_store.ListRuns(manifest.BatchId).Count > 0
            || manifest.Members.Any(member => _store.TryReadReplay(manifest.BatchId, member.TaskKey) is not null))
            return false;
        var options = _settings.Get(manifest.Scope.Project).BatchGate;
        var kept = new List<BatchGateSubject>();
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<(BatchGatePendingRecord Pending, AgentStudio.Git.BatchReplayResult Replay)>();
        var tip = newBase;
        foreach (var member in manifest.Members)
        {
            if (!pending.TryGetValue(member.TaskKey, out var item))
            {
                rejected[member.TaskKey] = "superseded-review";
                continue;
            }
            var subject = Refresh(item).Subject with { OwningBatchId = null };
            var exclusion = BatchGatePolicy.Exclusion(subject, manifest.Scope, options);
            if (_store.HasEject(manifest.Scope.Project, member.TaskKey)
                || exclusion is not null)
            {
                rejected[member.TaskKey] = !subject.CurrentGeneration
                    ? "superseded-review" : "ineligible:" + (exclusion?.ToString() ?? "operator-eject");
                continue;
            }
            var replay = _git.ReplayBatchMember(repo, subject.ResultRef, subject.ResultSha, tip);
            if (replay.ConflictedFiles.Count > 0)
            {
                rejected[member.TaskKey] = "merge-conflict";
                conflicts.Add((item, replay));
                continue;
            }
            switch (BatchGatePolicy.ReplayDisposition(replay))
            {
                case BatchReplayDisposition.Pause:
                    throw new IOException(replay.Error ?? "Batch base-move probe failed.");
                case BatchReplayDisposition.EjectUnattributable:
                    rejected[member.TaskKey] = replay.Route == AgentStudio.Git.BatchReplayRoute.AlreadyContained
                        ? "already-contained" : "unattributable-replay";
                    continue;
                default:
                    kept.Add(subject);
                    tip = replay.TipSha!;
                    break;
            }
        }
        var successor = kept.Count < options.MinimumSize ? null : manifest with
        {
            BatchId = Guid.NewGuid().ToString("N"),
            BaseSha = newBase,
            MembershipDigest = BatchGatePolicy.MembershipDigest(newBase, manifest.Scope, kept),
            ClosedAtUtc = _time.GetUtcNow(),
            Members = kept.ToArray(),
            EligibleKeys = kept.Select(member => member.TaskKey).ToArray(),
            ReformedFromBatchId = manifest.BatchId,
        };
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Abandoned, null, fence, _time.GetUtcNow(),
            successor is null ? "base moved before replay; compatible members below minimum size"
                : $"base moved before replay; re-formed as {successor.BatchId} on {Short(newBase)}",
            BaseMoveToSha: newBase, BaseMoveOutcome: successor is null ? "abandoned" : "kept",
            SuccessorBatchId: successor?.BatchId, BaseMoveMemberReasons: rejected));
        ReturnToPending(manifest);
        foreach (var (item, replay) in conflicts)
        {
            _store.RecordReplay(new BatchGateReplayRecord(
                manifest.BatchId, manifest.MembershipDigest, item.Subject.TaskKey,
                item.Subject.ResultSha, newBase, null, replay.Replacements,
                replay.ConflictedFiles, "conflict", _time.GetUtcNow(), Reason: replay.Error));
            conflictKeys.Add(item.Subject.TaskKey);
        }
        if (successor is not null) _store.CloseManifest(successor);
        foreach (var member in successor?.Members ?? [])
        {
            var item = pending[member.TaskKey];
            var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
            if (task is not null)
                BatchGateOwnershipStore.Write(task.FolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId, member, successor!.BatchId));
        }
        foreach (var (key, reason) in rejected)
            if (reason == "superseded-review" && pending.TryGetValue(key, out var item))
                ResolveSuperseded(item, reason);
        _logger.LogInformation(
            "batch-gate-base-move batch={BatchId} successor={SuccessorId} base={BaseSha} kept={Kept} rejected={Rejected}",
            manifest.BatchId, successor?.BatchId, newBase, kept.Count, rejected.Count);
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
