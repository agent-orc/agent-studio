using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3562: liveness of the one batch flight per project. Formed batches
/// start in <see cref="BatchGateStartPolicy"/> order, first in, first out
/// (AGT-3710), a flight that does not
/// reach its coordinator lease in time gives up its project slot, and a
/// finished flight starts the next formed batch at once.
/// </summary>
public sealed partial class BatchGatePilotService
{
    /// <summary>One ordinary batch flight; mutable fields are guarded by <c>_runningBatches</c>.</summary>
    private sealed class BatchFlight(BatchGateManifest manifest, DateTimeOffset startedAtUtc,
        CancellationTokenSource beforeLease, bool followsCompletedFlight)
    {
        public BatchGateManifest Manifest { get; } = manifest;
        public bool FollowsCompletedFlight { get; } = followsCompletedFlight;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public CancellationTokenSource BeforeLease { get; } = beforeLease;
        public Task? Task { get; set; }
        public bool LeaseClaimed { get; set; }
        public bool Cancelled { get; set; }
    }

    // Project to its current ordinary flight, beside _runningBatches.
    private readonly Dictionary<string, BatchFlight> _batchFlights = new(StringComparer.OrdinalIgnoreCase);
    // Batch id to a flight cancelled at its lease deadline that has not exited yet.
    private readonly Dictionary<string, (Task Task, BatchGateDetachedFlight View)> _detachedFlights =
        new(StringComparer.Ordinal);
    // Project to the batch whose flight was cancelled last; it starts after the others.
    private readonly Dictionary<string, string> _leaseDeadlineDeferred = new(StringComparer.OrdinalIgnoreCase);
    // Exit continuations: each completes after cleanup and the follow-up start.
    private readonly Dictionary<BatchFlight, Task> _flightExits = new();
    private readonly object _startPass = new();

    private IReadOnlyList<BatchGateManifest> OrderForStart(IReadOnlyList<BatchGateManifest> formed)
        => BatchGateFlightQueue.Order(_store, formed, LeaseDeadlineDeferred());

    private Func<string, string?> LeaseDeadlineDeferred()
    {
        Dictionary<string, string> deferred;
        lock (_runningBatches)
            deferred = new Dictionary<string, string>(_leaseDeadlineDeferred, StringComparer.OrdinalIgnoreCase);
        return project => deferred.GetValueOrDefault(project);
    }

    /// <summary>
    /// The flight passed the project reservation and takes its lease now. A
    /// flight the deadline already cancelled must not take it.
    /// </summary>
    private bool ClaimLease(BatchFlight? flight)
    {
        if (flight is null) return true;
        lock (_runningBatches)
        {
            if (flight.Cancelled) return false;
            flight.LeaseClaimed = true;
            return true;
        }
    }

    /// <summary>
    /// A flight can wait on the project reservation before it holds a
    /// coordinator lease, so lease expiry cannot free its slot. Past the
    /// deadline the flight is cancelled and detached from the project slot
    /// even if it ignores the cancellation; the batch stays formed.
    /// </summary>
    private void CancelFlightsPastLeaseDeadline()
    {
        var now = _time.GetUtcNow();
        var expired = new List<(BatchFlight Flight, int Minutes)>();
        lock (_runningBatches)
            foreach (var (project, flight) in _batchFlights.ToArray())
            {
                if (flight.Task is not { IsCompleted: false } task
                    || !_runningBatches.TryGetValue(project, out var running)
                    || !ReferenceEquals(running, task)) continue;
                var minutes = _settings.Get(project).BatchGate.FlightLeaseDeadlineMinutes;
                if (!MonolithTransitionAdapter.BatchFlightLeaseDeadlinePassed(
                        flight.StartedAtUtc, flight.LeaseClaimed, now, minutes)) continue;
                flight.Cancelled = true;
                _runningBatches.Remove(project);
                _batchFlights.Remove(project);
                _startedBatchIds.Remove(flight.Manifest.BatchId);
                _detachedFlights[flight.Manifest.BatchId] = (task, new BatchGateDetachedFlight(
                    flight.Manifest.BatchId, project, flight.StartedAtUtc, now, minutes,
                    MonolithTransitionAdapter.BatchFlightLeaseDeadlineReason(minutes)));
                _leaseDeadlineDeferred[project] = flight.Manifest.BatchId;
                expired.Add((flight, minutes));
            }
        foreach (var (flight, minutes) in expired)
        {
            try { flight.BeforeLease.Cancel(); }
            catch (ObjectDisposedException ex)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Batch flight exited before its lease deadline cancellation");
            }
            RecordLeaseDeadline(flight, minutes);
        }
    }

    /// <summary>The flights of <paramref name="project"/> detached at their lease deadline that have not exited.</summary>
    private BatchGateDetachedFlight[] DetachedFlights(string project)
    {
        lock (_runningBatches)
            return _detachedFlights.Values
                .Where(detached => string.Equals(detached.View.Project, project, StringComparison.OrdinalIgnoreCase))
                .Select(detached => detached.View).OrderBy(view => view.DetachedAtUtc).ToArray();
    }

    /// <summary>
    /// Exit continuations a flight wait observes. A flight detached at its
    /// lease deadline may never exit, so its exit counts only once the flight
    /// itself has finished; a hung detached flight never holds a wait.
    /// Callers hold <c>_runningBatches</c>.
    /// </summary>
    private IEnumerable<Task> AwaitedFlightExits()
        => _flightExits.Where(exit => !exit.Key.Cancelled || exit.Key.Task is { IsCompleted: true })
            .Select(exit => exit.Value);

    private void RecordLeaseDeadline(BatchFlight flight, int minutes)
    {
        var manifest = flight.Manifest;
        var reason = MonolithTransitionAdapter.BatchFlightLeaseDeadlineReason(minutes);
        _logger.LogWarning(
            "batch-flight-lease-deadline batch={BatchId} project={Project} startedAt={StartedAtUtc} deadlineMinutes={Minutes}",
            manifest.BatchId, manifest.Scope.Project, flight.StartedAtUtc, minutes);
        try
        {
            if (_store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
                State(manifest, BatchPhase.Formed, null, 0, reason);
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
                    Kind = BatchGateStartPolicy.LeaseDeadlineEventKind,
                    Actor = TimelineActors.System, Summary = reason,
                    Details = new Dictionary<string, string>
                    {
                        ["batchId"] = manifest.BatchId,
                        ["startedAtUtc"] = flight.StartedAtUtc.ToString("O"),
                        ["deadlineMinutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "batch-flight-lease-deadline-record-failed batch={BatchId}",
                manifest.BatchId);
        }
    }

    private bool StartsNextAfter(BatchFlight flight, bool cancelledAtLeaseDeadline)
    {
        var batchId = flight.Manifest.BatchId;
        var state = _store.LatestState(batchId);
        return MonolithTransitionAdapter.BatchFlightStartsNext(cancelledAtLeaseDeadline,
            state.Phase == BatchPhase.Formed, state.Phase == BatchPhase.Abandoned,
            _store.ListRuns(batchId).Count > 0, state.SuccessorBatchId is not null,
            flight.FollowsCompletedFlight);
    }

    /// <summary>
    /// Releases the project slot of a finished flight. A flight that completed
    /// or failed starts the next eligible formed batch of its project at once
    /// instead of on the next hosted tick (<see cref="BatchGateStartPolicy.StartsNextAfterFlight"/>).
    /// </summary>
    private void FinishBatchFlight(BatchFlight flight, Task done, CancellationToken ct)
    {
        var manifest = flight.Manifest;
        var project = manifest.Scope.Project;
        if (done.Exception is { } error)
            _logger.LogError(error, "batch-gate-flight-failed batch={BatchId}", manifest.BatchId);
        bool detached;
        lock (_runningBatches)
        {
            detached = flight.Cancelled;
            if (_runningBatches.TryGetValue(project, out var current) && ReferenceEquals(current, done))
            {
                _runningBatches.Remove(project);
                _batchFlights.Remove(project);
            }
            var restarted = _batchFlights.TryGetValue(project, out var successor)
                && successor.Manifest.BatchId == manifest.BatchId;
            if (!detached && !restarted) _startedBatchIds.Remove(manifest.BatchId);
            if (_detachedFlights.TryGetValue(manifest.BatchId, out var cancelled)
                && ReferenceEquals(cancelled.Task, done))
                _detachedFlights.Remove(manifest.BatchId);
        }
        flight.BeforeLease.Dispose();
        try
        {
            if (_replanAfterBatch.TryRemove(manifest.BatchId, out _))
                _ = Task.Run(() => TickAsync(CancellationToken.None));
            else if (!ct.IsCancellationRequested && StartsNextAfter(flight, detached))
                StartClosedBatches(ct, project, followsCompletedFlight: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "batch-gate-next-start-failed batch={BatchId}", manifest.BatchId);
        }
        finally
        {
            lock (_runningBatches) _flightExits.Remove(flight);
        }
    }
}
