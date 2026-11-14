using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3562, AGT-3569: liveness of the one batch flight per project. Formed
/// batches start in <see cref="BatchGateStartPolicy"/> order, first in, first
/// out (AGT-3710). A flight claims its formed batch durably in the batch
/// store before it waits for the project reservation
/// (<see cref="BatchFlightClaimPolicy"/>): the claim, not a process task,
/// gives the right to run the batch, renews a heartbeat and carries a
/// progress deadline per step. Any process's tick expires a claim that lost
/// its heartbeat or missed its deadline to a retry, a visible gate or restart
/// recovery, and a finished flight starts the next formed batch at once.
/// </summary>
public sealed partial class BatchGatePilotService
{
    public sealed record RecoveryDelay(string Reason, DateTimeOffset NextRetryAtUtc);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryDelay> _recoveryDelays = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string BatchId, string Reason), byte> _loggedRecoveryDelays = new();

    public bool HasLiveFlight(string batchId)
    {
        var manifest = TryReadManifest(batchId);
        return manifest is not null && _store.LiveFlightClaim(manifest.Scope.Project, _time.GetUtcNow()) is { } claim
            && (claim.BatchId == batchId || claim.BatchId == manifest.ParentBatchId);
    }

    public RecoveryDelay RecoveryStatus(string batchId)
        => _recoveryDelays.TryGetValue(batchId, out var delay) ? delay
            : new RecoveryDelay("recovery has not run", _time.GetUtcNow().AddSeconds(30));

    public bool RunningWithoutFlight(BatchGateManifest manifest, BatchGateState state, DateTimeOffset now)
        => IsStaleRunning(state.Phase, state.RecordedAtUtc, now, HasLiveFlight(manifest.BatchId),
            _settings.Get(manifest.Scope.Project).BatchGate.GateTimeoutMinutes);

    public static bool IsStaleRunning(BatchPhase phase, DateTimeOffset started, DateTimeOffset now,
        bool liveFlight, int gateTimeoutMinutes)
        => phase == BatchPhase.Running && !liveFlight
            && now - started > TimeSpan.FromMinutes(gateTimeoutMinutes) + BatchGateLeaseService.Ttl;

    private void RecordRecoveryDelay(BatchGateManifest manifest, string reason)
    {
        var next = new RecoveryDelay(reason, _time.GetUtcNow().AddSeconds(30));
        _recoveryDelays[manifest.BatchId] = next;
        if (_loggedRecoveryDelays.TryAdd((manifest.BatchId, reason), 0))
            _logger.LogWarning("batch-recovery-skipped batch={BatchId} reason={Reason} nextRetryAt={NextRetryAtUtc}",
                manifest.BatchId, reason, next.NextRetryAtUtc);
    }

    /// <summary>
    /// One ordinary batch flight of this process: a join handle beside the
    /// durable claim. No start decision reads it. Mutable fields are guarded
    /// by <c>_flights</c>.
    /// </summary>
    private sealed class BatchFlight(BatchGateManifest manifest, BatchFlightClaim claim,
        CancellationTokenSource cancellation, bool followsCompletedFlight)
    {
        public BatchGateManifest Manifest { get; } = manifest;
        public bool FollowsCompletedFlight { get; } = followsCompletedFlight;
        public DateTimeOffset StartedAtUtc { get; } = claim.ClaimedAtUtc;
        /// <summary>Cancels the whole flight once its claim is no longer live.</summary>
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public BatchFlightClaim Claim { get; set; } = claim;
        public Task? Task { get; set; }
        /// <summary>
        /// The project reservation the flight holds. An expiry releases it at
        /// once (the grant is idempotent), so a hung gate cannot hold the next
        /// flight behind it.
        /// </summary>
        public IDisposable? Reservation { get; set; }
        /// <summary>Set once the claim expired; the flight may still be unwinding.</summary>
        public BatchGateDetachedFlight? Detached { get; set; }
    }

    // Batch id to a flight of this process until it exits, detached or not.
    private readonly Dictionary<string, BatchFlight> _flights = new(StringComparer.Ordinal);
    // Exit continuations: each completes after cleanup and the follow-up start.
    private readonly Dictionary<BatchFlight, Task> _flightExits = new();
    // Test override of the durable lease-deadline deferral, by project.
    private readonly Dictionary<string, string> _deferredForTesting = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startPass = new();
    private readonly string _claimOwner =
        $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";

    private IReadOnlyList<BatchGateManifest> OrderForStart(IReadOnlyList<BatchGateManifest> formed)
        => BatchGateFlightQueue.Order(_store, formed, LeaseDeadlineDeferred(), _time.GetUtcNow(),
            project => _settings.Get(project).BatchGate.MaximumWaitMinutes);

    internal IReadOnlyList<BatchGateManifest> StartOrderForTesting(IReadOnlyList<BatchGateManifest> formed)
        => OrderForStart(formed);

    internal void DeferAtLeaseDeadlineForTesting(string project, string batchId)
    {
        lock (_flights) _deferredForTesting[project] = batchId;
    }

    /// <summary>The owner name this process writes into its flight claims.</summary>
    internal string FlightClaimOwner => _claimOwner;

    /// <summary>
    /// The batch of each project whose last claim expired at its reservation
    /// deadline and is retried: it starts after the other formed batches. The
    /// fact is the project's durable claim record, so every process agrees.
    /// </summary>
    private Func<string, string?> LeaseDeadlineDeferred()
    {
        var deferred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in _store.ListFlightClaims())
            if (claim is { Stage: BatchFlightStage.Reserving,
                    End: { Expiry: BatchFlightClaimExpiry.StageDeadlinePassed, Outcome: BatchFlightExpiryOutcome.Retry } })
                deferred[claim.Project] = claim.BatchId;
        lock (_flights)
            foreach (var (project, batchId) in _deferredForTesting) deferred[project] = batchId;
        return project => deferred.GetValueOrDefault(project);
    }

    private int StageDeadlineMinutes(string project, BatchFlightStage stage)
    {
        var options = _settings.Get(project).BatchGate;
        return MonolithTransitionAdapter.BatchFlightStageDeadlineMinutes(stage,
            options.FlightLeaseDeadlineMinutes, options.GateTimeoutMinutes);
    }

    /// <summary>
    /// Claims the project's flight slot for <paramref name="manifest"/> and
    /// records the claim in the batch's transition journal. Null when another
    /// claim holds the slot, an expired claim awaits its outcome, or the batch
    /// left the formed phase meanwhile.
    /// </summary>
    private BatchFlightClaim? ClaimFlight(BatchGateManifest manifest)
    {
        var project = manifest.Scope.Project;
        var minutes = StageDeadlineMinutes(project, BatchFlightStage.Reserving);
        BatchFlightClaimGrant grant;
        try { grant = _store.TryClaimFlight(project, manifest.BatchId, _claimOwner, minutes, _time.GetUtcNow()); }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "batch-flight-claim-failed batch={BatchId}", manifest.BatchId);
            return null;
        }
        if (grant.Claim is not { } claim) return null;
        var formed = _store.LatestState(manifest.BatchId);
        var journal = formed.Phase != BatchPhase.Formed ? null : _store.TryTransition(new BatchGateState(
            manifest.BatchId, manifest.MembershipDigest, BatchPhase.Formed, null, formed.CoordinatorFence,
            _time.GetUtcNow(), $"flight claimed by {claim.Owner} (claim {claim.Fence}); reservation deadline {minutes} minutes",
            FlightClaimId: claim.ClaimId));
        if (journal is { Accepted: true }) return claim;
        _store.ReleaseFlightClaim(claim, "batch no longer formed at claim", _time.GetUtcNow());
        return null;
    }

    /// <summary>
    /// Moves the flight's claim to <paramref name="stage"/> with that step's
    /// deadline. False when the claim is no longer live: the flight must stop
    /// without another mutation.
    /// </summary>
    private bool AdvanceFlight(BatchFlight? flight, BatchFlightStage stage, bool cancelOnLoss = true)
    {
        if (flight is null) return true;
        BatchFlightClaim held;
        lock (_flights) held = flight.Claim;
        var advanced = _store.AdvanceFlightClaim(held, stage,
            StageDeadlineMinutes(flight.Manifest.Scope.Project, stage), _time.GetUtcNow());
        if (advanced is null)
        {
            _logger.LogWarning("batch-flight-claim-lost batch={BatchId} stage={Stage}",
                flight.Manifest.BatchId, stage);
            if (cancelOnLoss) CancelFlight(flight);
            return false;
        }
        lock (_flights) flight.Claim = advanced;
        return true;
    }

    /// <summary>
    /// The flight of this process that runs <paramref name="manifest"/>, or the
    /// isolation parent it belongs to; null for a batch no flight of this
    /// process runs (recovery and fallback paths hold their own lease).
    /// </summary>
    private BatchFlight? FlightOf(BatchGateManifest manifest)
    {
        var batchId = manifest.BatchId;
        for (var hops = 0; hops < 8; hops++)
        {
            lock (_flights)
                if (_flights.TryGetValue(batchId, out var flight)) return flight;
            BatchGateManifest current;
            try { current = _store.ReadManifest(batchId); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Batch flight parent manifest unreadable");
                return null;
            }
            if (current.ParentBatchId is not { } parent) return null;
            batchId = parent;
        }
        return null;
    }

    /// <summary>
    /// True unless a flight of this process runs the batch and its claim is no
    /// longer live. Checked before each candidate mutation and publication.
    /// </summary>
    private bool FlightClaimLive(BatchGateManifest manifest)
    {
        if (FlightOf(manifest) is not { } flight) return true;
        BatchFlightClaim held;
        lock (_flights) held = flight.Claim;
        if (_store.IsFlightClaimLive(held, _time.GetUtcNow())) return true;
        CancelFlight(flight);
        return false;
    }

    /// <summary>
    /// True unless a flight of this process runs the batch and its claim was
    /// expired or replaced. A claim whose heartbeat only lapsed is still owned:
    /// no other process acted on it, so the flight records its own outcome
    /// (abandon or pause, never a publication). Once the claim was expired or
    /// replaced, the expiry outcome owns the batch and the flight writes nothing.
    /// </summary>
    private bool FlightClaimOwned(BatchGateManifest manifest)
    {
        if (FlightOf(manifest) is not { } flight) return true;
        BatchFlightClaim held;
        lock (_flights) held = flight.Claim;
        return _store.ReadFlightClaim(held.Project) is { End: null } current && current.SameGrant(held);
    }

    /// <summary>A gate step refreshes the gate deadline; a lost claim stops the gate before it starts.</summary>
    private void EnterGateStep(BatchGateManifest manifest)
    {
        if (FlightOf(manifest) is { } flight && !AdvanceFlight(flight, BatchFlightStage.Gating))
            throw new OperationCanceledException("The batch flight lost its claim before the gate.");
    }

    private static void CancelFlight(BatchFlight flight)
    {
        try { flight.Cancellation.Cancel(); }
        catch (ObjectDisposedException ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Batch flight exited before its claim cancellation");
        }
    }

    /// <summary>
    /// Renews the flight's claim. A refused renewal (the claim expired, was
    /// replaced, or its step passed the deadline) cancels the flight, which
    /// also stops its coordinator lease heartbeat.
    /// </summary>
    private async Task ClaimHeartbeatAsync(BatchFlight flight, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, stop).ConfigureAwait(false);
            BatchFlightClaim held;
            lock (_flights) held = flight.Claim;
            BatchFlightClaim? renewed;
            try { renewed = _store.RenewFlightClaim(held, _time.GetUtcNow()); }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "batch-flight-claim-renewal-failed batch={BatchId}", flight.Manifest.BatchId);
                continue;
            }
            if (renewed is null)
            {
                _logger.LogWarning("batch-flight-claim-not-renewed batch={BatchId}", flight.Manifest.BatchId);
                CancelFlight(flight);
                return;
            }
            lock (_flights)
                if (flight.Claim.SameGrant(renewed)) flight.Claim = renewed;
        }
    }

    /// <summary>
    /// The tick's expiry pass over every project's claim. A claim that lost its
    /// heartbeat or missed its step deadline expires; a flight of this process
    /// that held it is cancelled and detached even if it ignores the
    /// cancellation, and its batch receives the policy outcome. An outcome that
    /// waits for the stale flight's coordinator lease is applied on a later tick.
    /// </summary>
    private void ExpireFlightClaims()
    {
        foreach (var claim in _store.ListFlightClaims())
        {
            try
            {
                if (claim.End is { Settled: false }) { SettleExpiredClaim(claim); continue; }
                if (claim.End is not null) continue;
                var now = _time.GetUtcNow();
                var facts = claim.Facts(now) with { OwnerGone = OwnerGone(claim.Owner) };
                if (MonolithTransitionAdapter.BatchFlightClaimExpiryOf(facts) is not { } expiry) continue;
                var manifest = TryReadManifest(claim.BatchId);
                var outcome = manifest is null ? BatchFlightExpiryOutcome.Recover
                    : MonolithTransitionAdapter.BatchFlightOutcomeOnExpiry(claim.Stage, expiry,
                        PriorFlightExpiries(manifest));
                var reason = MonolithTransitionAdapter.BatchFlightExpiryReason(claim.Stage, expiry, outcome,
                    claim.StageDeadlineMinutes);
                var expired = _store.ExpireFlightClaim(claim, outcome, reason,
                    settled: outcome == BatchFlightExpiryOutcome.Recover, now, ownerGone: facts.OwnerGone);
                if (expired is null) continue;
                _logger.LogWarning(
                    "batch-flight-claim-expired batch={BatchId} project={Project} stage={Stage} expiry={Expiry} outcome={Outcome} owner={Owner}",
                    claim.BatchId, claim.Project, claim.Stage, expiry, outcome, claim.Owner);
                DetachFlight(expired, reason);
                if (manifest is not null) RecordClaimExpiry(manifest, expired);
                if (outcome != BatchFlightExpiryOutcome.Recover) SettleExpiredClaim(expired);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or BatchPhaseTransitionRefusedException)
            {
                _logger.LogWarning(ex, "batch-flight-claim-expiry-failed batch={BatchId}", claim.BatchId);
            }
        }
    }

    /// <summary>
    /// The owner of a claim is gone when it ran on this machine under a process
    /// id that no longer runs; a restart then frees the slot without waiting
    /// for the heartbeat to lapse.
    /// </summary>
    private bool OwnerGone(string owner)
    {
        var parts = owner.Split('/');
        if (parts.Length != 3 || owner == _claimOwner
            || !string.Equals(parts[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], out var pid) || pid == Environment.ProcessId) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private BatchGateManifest? TryReadManifest(string batchId)
    {
        try { return _store.ReadManifest(batchId); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Expired flight claim names an unreadable batch");
            return null;
        }
    }

    /// <summary>
    /// Claim expiries already recorded on the batches of any member of
    /// <paramref name="manifest"/>: a retried member re-forms into a new batch,
    /// so the budget follows the members, not the batch id.
    /// </summary>
    private int PriorFlightExpiries(BatchGateManifest manifest)
    {
        var members = manifest.Members.Select(member => (member.TaskKey, member.RunAttempt)).ToHashSet();
        return _store.ListManifests()
            .Where(other => other.Members.Any(member => members.Contains((member.TaskKey, member.RunAttempt))))
            .Sum(other => _store.StateHistory(other.BatchId)
                .Count(state => state.FlightClaimExpiry == BatchFlightClaimExpiry.StageDeadlinePassed));
    }

    /// <summary>
    /// Applies an expired claim's outcome to its batch and settles the claim.
    /// Before the coordinator lease the batch is still formed: a retry records
    /// the expiry and keeps it formed, a visible gate pauses it. Past the lease
    /// the stale flight may still hold the lease until its heartbeat lapses, so
    /// the outcome waits for it.
    /// </summary>
    private void SettleExpiredClaim(BatchFlightClaim expired)
    {
        if (expired.End is not { Settled: false, Outcome: { } outcome } end) return;
        var manifest = TryReadManifest(expired.BatchId);
        if (manifest is null) { _store.SettleFlightClaim(expired, _time.GetUtcNow()); return; }
        var phase = _store.LatestState(manifest.BatchId).Phase;
        if (phase == BatchPhase.Formed && outcome == BatchFlightExpiryOutcome.Retry)
        {
            State(manifest, BatchPhase.Formed, null, _store.LatestState(manifest.BatchId).CoordinatorFence,
                end.Reason, claimExpiry: end.Expiry);
            _store.SettleFlightClaim(expired, _time.GetUtcNow());
            return;
        }
        if (phase is BatchPhase.Publishing or BatchPhase.Paused or BatchPhase.Unknown
            or BatchPhase.Published or BatchPhase.Abandoned)
        {
            _store.SettleFlightClaim(expired, _time.GetUtcNow());
            return;
        }
        BatchCoordinatorLease? lease;
        try { lease = _leases.TryAcquire(LeaseScope(manifest), "flight-claim-expiry"); }
        catch (IOException) { return; }
        if (lease is null) return;
        try
        {
            phase = _store.LatestState(manifest.BatchId).Phase;
            if (phase is not (BatchPhase.Publishing or BatchPhase.Paused or BatchPhase.Unknown
                    or BatchPhase.Published or BatchPhase.Abandoned))
            {
                if (outcome == BatchFlightExpiryOutcome.VisibleGate)
                    State(manifest, BatchPhase.Paused, null, lease.Fence, end.Reason, claimExpiry: end.Expiry);
                else
                {
                    State(manifest, BatchPhase.Abandoned, null, lease.Fence, end.Reason, claimExpiry: end.Expiry);
                    ReturnToPending(manifest);
                }
            }
            _store.SettleFlightClaim(expired, _time.GetUtcNow());
        }
        finally { _leases.Release(lease); }
    }

    /// <summary>Cancels and detaches the flight of this process that held <paramref name="expired"/>.</summary>
    private void DetachFlight(BatchFlightClaim expired, string reason)
    {
        BatchFlight? detached = null;
        lock (_flights)
            if (_flights.TryGetValue(expired.BatchId, out var flight) && flight.Claim.SameGrant(expired)
                && flight.Task is { IsCompleted: false })
            {
                flight.Detached = new BatchGateDetachedFlight(expired.BatchId, expired.Project,
                    flight.StartedAtUtc, _time.GetUtcNow(), expired.StageDeadlineMinutes, reason);
                _startedBatchIds.Remove(expired.BatchId);
                detached = flight;
            }
        if (detached is null) return;
        CancelFlight(detached);
        IDisposable? reservation;
        lock (_flights) reservation = detached.Reservation;
        reservation?.Dispose();
    }

    /// <summary>Records the reservation a flight took; a flight detached meanwhile gives it back at once.</summary>
    private void HoldReservation(BatchFlight? flight, IDisposable? reservation)
    {
        if (flight is null || reservation is null) return;
        bool detached;
        lock (_flights)
        {
            flight.Reservation = reservation;
            detached = flight.Detached is not null;
        }
        if (detached) reservation.Dispose();
    }

    /// <summary>The flights of <paramref name="project"/> detached at a claim expiry that have not exited.</summary>
    private BatchGateDetachedFlight[] DetachedFlights(string project)
    {
        lock (_flights)
            return _flights.Values
                .Where(flight => flight.Detached is not null && flight.Task is { IsCompleted: false }
                    && string.Equals(flight.Manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
                .Select(flight => flight.Detached!).OrderBy(view => view.DetachedAtUtc).ToArray();
    }

    /// <summary>
    /// Exit continuations a flight wait observes. A detached flight may never
    /// exit, so its exit counts only once the flight itself has finished; a
    /// hung detached flight never holds a wait. Callers hold <c>_flights</c>.
    /// </summary>
    private IEnumerable<Task> AwaitedFlightExits()
        => _flightExits.Where(exit => exit.Key.Detached is null || exit.Key.Task is { IsCompleted: true })
            .Select(exit => exit.Value);

    /// <summary>Tasks of this process's flights that are not detached. Callers hold <c>_flights</c>.</summary>
    private IEnumerable<Task> AttachedFlightTasks()
        => _flights.Values.Where(flight => flight.Detached is null && flight.Task is not null)
            .Select(flight => flight.Task!);

    /// <summary>True while a live claim holds the flight slot of <paramref name="project"/>.</summary>
    private bool FlightSlotBusy(string project) => _store.LiveFlightClaim(project, _time.GetUtcNow()) is not null;

    private void RecordClaimExpiry(BatchGateManifest manifest, BatchFlightClaim expired)
    {
        var end = expired.End!;
        var reservation = expired.Stage == BatchFlightStage.Reserving
            && end.Expiry == BatchFlightClaimExpiry.StageDeadlinePassed;
        try
        {
            var pending = _store.ListPending();
            foreach (var member in manifest.Members)
            {
                var item = pending.FirstOrDefault(candidate =>
                    candidate.Subject.TaskKey == member.TaskKey
                    && candidate.Subject.RunAttempt == member.RunAttempt);
                if (item is null) continue;
                var folder = _scanner.FindJob(member.TaskKey, item.WatchPath)?.FolderPath
                    ?? item.JobFolderPath;
                _timeline?.Append(folder, new TimelineEvent
                {
                    Ts = _time.GetUtcNow().UtcDateTime,
                    Kind = reservation ? BatchGateStartPolicy.LeaseDeadlineEventKind
                        : BatchFlightClaimPolicy.ExpiredEventKind,
                    Actor = TimelineActors.System, Summary = end.Reason,
                    Details = new Dictionary<string, string>
                    {
                        ["batchId"] = manifest.BatchId,
                        ["startedAtUtc"] = expired.ClaimedAtUtc.ToString("O"),
                        ["stage"] = expired.Stage.ToString(),
                        ["deadlineMinutes"] = expired.StageDeadlineMinutes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["outcome"] = end.Outcome?.ToString() ?? string.Empty,
                    },
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "batch-flight-claim-expiry-record-failed batch={BatchId}",
                manifest.BatchId);
        }
    }

    private bool StartsNextAfter(BatchFlight flight, bool detached)
    {
        var batchId = flight.Manifest.BatchId;
        var state = _store.LatestState(batchId);
        if (state.Phase == BatchPhase.Unknown) return false;
        return MonolithTransitionAdapter.BatchFlightStartsNext(detached,
            state.Phase == BatchPhase.Formed, state.Phase == BatchPhase.Abandoned,
            _store.ListRuns(batchId).Count > 0, state.SuccessorBatchId is not null,
            flight.FollowsCompletedFlight);
    }

    /// <summary>
    /// Releases the claim and the join handle of a finished flight. A flight
    /// that completed or failed starts the next eligible formed batch of its
    /// project at once instead of on the next hosted tick
    /// (<see cref="BatchGateStartPolicy.StartsNextAfterFlight"/>).
    /// </summary>
    private void FinishBatchFlight(BatchFlight flight, Task done, CancellationToken ct)
    {
        var manifest = flight.Manifest;
        var project = manifest.Scope.Project;
        if (done.Exception is { } error)
            _logger.LogError(error, "batch-gate-flight-failed batch={BatchId}", manifest.BatchId);
        bool detached;
        BatchFlightClaim held;
        lock (_flights)
        {
            detached = flight.Detached is not null;
            held = flight.Claim;
            if (_flights.TryGetValue(manifest.BatchId, out var current) && ReferenceEquals(current, flight))
                _flights.Remove(manifest.BatchId);
            if (!detached) _startedBatchIds.Remove(manifest.BatchId);
        }
        flight.Cancellation.Dispose();
        try
        {
            try
            {
                _store.ReleaseFlightClaim(held, done.IsFaulted ? "flight failed" : "flight finished",
                    _time.GetUtcNow());
            }
            catch (IOException ex)
            {
                // The claim then expires by its heartbeat.
                _logger.LogWarning(ex, "batch-flight-claim-release-failed batch={BatchId}", manifest.BatchId);
            }
            // AGT-3568: the successor tick request is durable; one exit consumes it.
            if (_store.TryConsumeSuccessorTick(project, manifest.BatchId))
                _ = ScheduleFollowUpTick();
            else if (!ct.IsCancellationRequested && StartsNextAfter(flight, detached))
                StartClosedBatches(ct, project, followsCompletedFlight: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "batch-gate-next-start-failed batch={BatchId}", manifest.BatchId);
        }
        finally
        {
            lock (_flights) _flightExits.Remove(flight);
        }
    }
}
