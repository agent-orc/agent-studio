namespace AgentRunner;

/// <summary>
/// AGT-2869: the running daemon's own reconciliation pass for a finalization
/// that could not reach the Task Server.
///
/// <para>
/// Startup reconciliation already proves that a persisted slot plus the
/// worker's durable result is everything needed to upload the artifacts, push
/// the delivery refs, record the completion, and hand the card back. What was
/// missing is that the live daemon used it: a Task Server that restarted during
/// a worker's finalization left the slot in <c>finalizing</c> with nobody
/// driving it until an operator restarted the service. This pass runs on the
/// ordinary poll loop and re-drives exactly those slots, with the same
/// idempotent <see cref="RemoteTaskRunner.ReattachAsync"/> step.
/// </para>
/// </summary>
public sealed class CodingFinalizationReconciler
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly RunnerStateStore _state;
    private readonly Action<string> _log;
    private readonly HashSet<string> _unreachableAnnounced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lateAnnounced = new(StringComparer.Ordinal);

    public CodingFinalizationReconciler(
        RunnerOptions options,
        TaskServerClient client,
        RunnerStateStore state,
        Action<string> log)
    {
        _options = options;
        _client = client;
        _state = state;
        _log = log;
    }

    /// <summary>
    /// Re-drives every deferred finalization whose backoff has elapsed and whose
    /// Task Server answers again. Returns the attempts this pass started, so the
    /// caller can hold them in its own slot list and log their exit codes exactly
    /// like a claimed run.
    /// </summary>
    public async Task<IReadOnlyList<FinalizationRedrive>> DriveAsync(
        IReadOnlyCollection<string> activeTaskKeys,
        RunnerProcessInventoryTracker inventory,
        CancellationToken shutdown)
    {
        var started = new List<FinalizationRedrive>();
        var serverAnswer = ServerAnswer.Unknown;
        foreach (var slot in _state.LoadAll())
        {
            shutdown.ThrowIfCancellationRequested();
            if (slot.Finalization is null) continue;
            var active = activeTaskKeys.Contains(slot.TaskKey, StringComparer.OrdinalIgnoreCase);
            var resultReady = DurableAgentProcess.InspectForReattach(slot).Result is not null;
            var now = DateTime.UtcNow;

            // The availability probe is one HTTP call per pass, and only when a
            // slot is actually due. A slot that is still inside its backoff must
            // not cost a probe at all.
            if (FinalizationRetryPolicy.Decide(
                    slot.Phase, slot.Finalization, resultReady, active, serverAnswered: true, now)
                == FinalizationRetryAction.Redrive
                && serverAnswer == ServerAnswer.Unknown)
            {
                var unreachable = await _client.ProbeServerAvailableAsync(shutdown);
                serverAnswer = unreachable is null ? ServerAnswer.Yes : ServerAnswer.No;
                if (unreachable is not null && _unreachableAnnounced.Add(slot.AttemptId))
                {
                    _log(
                        $"coding-finalization-waiting task={slot.TaskKey} attempt={slot.AttemptId} "
                        + $"retry={slot.Finalization.Attempts} reason={unreachable}");
                }
            }

            var action = FinalizationRetryPolicy.Decide(
                slot.Phase,
                slot.Finalization,
                resultReady,
                active,
                serverAnswered: serverAnswer == ServerAnswer.Yes,
                now);
            if (action != FinalizationRetryAction.Redrive) continue;

            _unreachableAnnounced.Remove(slot.AttemptId);
            var runner = new RemoteTaskRunner(_options, _client, _log, _state, inventory);
            if (SettledByOutboxRecovery(slot))
            {
                _log(
                    $"coding-slot-reconciliation scope=poll task={slot.TaskKey} "
                    + $"attempt={slot.AttemptId} outcome=settled "
                    + "reason=durable outbox recovery already delivered this attempt");
                if (!await runner.ReleaseSettledAsync(
                        slot,
                        "durable outbox recovery already delivered this attempt"))
                {
                    Reschedule(slot, "lease release deferred");
                }
                continue;
            }

            if (FinalizationRetryPolicy.BeyondRunTimeout(
                    slot.Finalization,
                    TimeSpan.FromSeconds(_options.RunTimeoutSeconds),
                    now)
                && _lateAnnounced.Add(slot.AttemptId))
            {
                _log(
                    $"coding-finalization-late task={slot.TaskKey} attempt={slot.AttemptId} "
                    + $"pendingSince={slot.Finalization.PendingSinceUtc:o} "
                    + "reason=result transfer has outlived the run timeout; retries continue");
            }

            _log(
                $"coding-slot-reconciliation scope=poll task={slot.TaskKey} "
                + $"attempt={slot.AttemptId} outcome=redriven "
                + $"retry={slot.Finalization.Attempts} "
                + $"pendingSince={slot.Finalization.PendingSinceUtc:o} "
                + $"reason={slot.Finalization.LastReason}");
            _client.RestoreRunAuthority(slot.TaskKey, slot.RunId, slot.LeaseInstanceId, slot.Lease);
            // Hold the next attempt before the re-drive starts: a re-drive that
            // dies on anything other than a transport fault must not be due
            // again on the very next poll tick.
            var held = _state.Save(slot with
            {
                Finalization = FinalizationRetryPolicy.HoldFor(slot.Finalization, now),
            });
            started.Add(new FinalizationRedrive(
                held.TaskKey,
                runner.ReattachAsync(held, CancellationToken.None, shutdown)));
        }
        return started;
    }

    /// <summary>
    /// True when this attempt's durable outbox has already been replayed to the
    /// Task Server by <see cref="DurableHandoffRecovery"/>, which runs earlier
    /// in the same poll tick. There is nothing left to deliver, only the lease
    /// and the slot file to clear.
    /// </summary>
    private bool SettledByOutboxRecovery(PersistedRunnerSlot slot)
    {
        if (!_client.UsesDurableTaskServer) return false;
        var runId = slot.RunId ?? slot.AttemptId;
        foreach (var outbox in DurableRunOutbox.OpenAll(Path.Combine(_options.WorkDir, "outbox")))
        {
            if (!string.Equals(outbox.Authority.RunId, runId, StringComparison.Ordinal)) continue;
            var snapshot = outbox.Snapshot;
            return string.Equals(snapshot.FinalHandoffState, "completed", StringComparison.Ordinal)
                   && snapshot.BacklogCount == 0;
        }
        return false;
    }

    private void Reschedule(PersistedRunnerSlot slot, string reason)
        => _state.Save(slot with
        {
            Phase = FinalizationRetryPolicy.Phase,
            Finalization = FinalizationRetryPolicy.Schedule(
                slot.Finalization,
                reason,
                slot.Finalization?.Teardown,
                DateTime.UtcNow),
        });

    private enum ServerAnswer
    {
        Unknown,
        Yes,
        No,
    }
}

/// <summary>One re-driven finalization the daemon now owns again.</summary>
public sealed record FinalizationRedrive(string TaskKey, Task<int> Execution);
