using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3694: what a per-task fallback waits for, read live for the batch state
/// and the card. <paramref name="Kind"/> is one of <see cref="PerTaskFallbackWaitKinds"/>.
/// </summary>
public sealed record BatchGateFallbackWait(
    string TaskKey, string ReviewAttemptId, string Kind, DateTimeOffset SinceUtc, string Detail,
    string? EngineRunId = null, string? EngineStatus = null, bool Cancelled = false);

/// <summary>
/// AGT-3694: every wait of a per-task fallback is visible and bounded. On
/// 10 November 2026 two cards stayed in Auto Review for twelve hours after a
/// restart: their start budget was spent, an engine decision on the gate
/// outcome stayed open, and every tick returned before the budget check
/// without a trace. The pilot now names each wait (an engine decision, a
/// flight stage, a resume) and, past the bound of
/// <see cref="PerTaskFallbackWaitPolicy"/>, raises a stall event and ends it:
/// a stalled engine decision is superseded and the card escalates as GateInfra;
/// a stalled flight is cancelled, then detached, and its next start escalates.
/// </summary>
public sealed partial class BatchGatePilotService
{
    /// <summary>One per-task fallback flight of this process; mutable fields are guarded by <c>_fallbackFlights</c>.</summary>
    private sealed class FallbackFlight(BatchGatePendingRecord pending, DateTimeOffset startedAtUtc)
    {
        public BatchGatePendingRecord Pending { get; } = pending;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public CancellationTokenSource Stall { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string Stage { get; set; } = PerTaskFallbackWaitKinds.Preparing;
        public DateTimeOffset StageSinceUtc { get; set; } = startedAtUtc;
        public DateTimeOffset? CancelledAtUtc { get; set; }
        public string? StallReason { get; set; }
    }

    // Review attempt to a stalled flight that ignored its cancellation. It no
    // longer blocks a resume; the resumed flight escalates instead of gating.
    private readonly Dictionary<string, FallbackFlight> _detachedFallbackFlights = new(StringComparer.Ordinal);

    private void FallbackStage(BatchGatePendingRecord pending, string stage)
    {
        lock (_fallbackFlights)
        {
            if (!_fallbackFlights.TryGetValue(pending.ReviewAttemptId, out var flight)
                || flight.Stage == stage) return;
            flight.Stage = stage;
            flight.StageSinceUtc = _time.GetUtcNow();
        }
    }

    private bool FallbackFlightStalled(string reviewAttemptId)
    {
        lock (_fallbackFlights)
            return _fallbackFlights.TryGetValue(reviewAttemptId, out var flight)
                && flight.Stall.IsCancellationRequested
                || _detachedFallbackFlights.ContainsKey(reviewAttemptId);
    }

    private FallbackFlight? DetachedFallback(string reviewAttemptId)
    {
        lock (_fallbackFlights)
        {
            if (!_detachedFallbackFlights.TryGetValue(reviewAttemptId, out var flight)) return null;
            if (!flight.Task.IsCompleted) return flight;
            _detachedFallbackFlights.Remove(reviewAttemptId);
            return null;
        }
    }

    /// <summary>
    /// Once per tick: a flight stage past its bound raises one stall event. A
    /// stage the flight owns is cancelled; if the flight has not stopped after
    /// the cancellation grace it is detached, so the active marker resumes and
    /// the resumed flight escalates the card as GateInfra.
    /// </summary>
    private void ObserveFallbackFlights()
    {
        var now = _time.GetUtcNow();
        var stalls = new List<(FallbackFlight Flight, string Reason, string Outcome)>();
        lock (_fallbackFlights)
            foreach (var (attempt, flight) in _fallbackFlights.ToArray())
            {
                if (flight.Task.IsCompleted) continue;
                if (flight.CancelledAtUtc is { } cancelledAt)
                {
                    if (!MonolithTransitionAdapter.PerTaskFallbackDetachesCancelledFlight(cancelledAt, now)) continue;
                    _fallbackFlights.Remove(attempt);
                    _detachedFallbackFlights[attempt] = flight;
                    stalls.Add((flight, $"{flight.StallReason}; the flight did not stop after its cancellation",
                        "detached"));
                    continue;
                }
                var decision = MonolithTransitionAdapter.PerTaskFallbackFlight(new FallbackFlightFacts(
                    flight.Stage, flight.StageSinceUtc, GateTimeout(flight.Pending.Subject.Project), now));
                if (!decision.Stalled) continue;
                if (MonolithTransitionAdapter.PerTaskFallbackCancelsStalledStage(flight.Stage))
                {
                    flight.CancelledAtUtc = now;
                    flight.StallReason = decision.Reason;
                    stalls.Add((flight, decision.Reason, "cancelled"));
                }
                else stalls.Add((flight, decision.Reason, "reported"));
            }
        foreach (var (flight, reason, outcome) in stalls)
        {
            RaiseFallbackStall(flight.Pending, new BatchGateFallbackStall(
                flight.Pending.Subject.Project, flight.Pending.Subject.TaskKey, flight.Pending.ReviewAttemptId,
                flight.Stage, flight.StageSinceUtc, now, reason, outcome));
            // Cancel outside the lock: the flight's continuations take it.
            if (outcome != "cancelled") continue;
            try { flight.Stall.Cancel(); }
            catch (ObjectDisposedException ex)
            {
                // The flight ended between the observation and the cancellation.
                _logger.LogDebug(ex, "batch-fallback-stall-cancel-after-exit task={TaskKey}",
                    flight.Pending.Subject.TaskKey);
            }
        }
    }

    private void MarkStalledFallbackActive(BatchGatePendingRecord pending)
    {
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task is not { State: TaskStates.AutoReview }) return;
        var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
        if (ownership is not null && ownership.ReviewAttemptId != pending.ReviewAttemptId) return;
        if (ownership is { FallbackGateActive: true }) return;
        BatchGateOwnershipStore.Write(task.FolderPath, ownership is null
            ? new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                FallbackGateActive: true, FallbackGateStarts: 1)
            : ownership with { FallbackGateActive = true, FallbackGateStarts = ownership.FallbackGateStarts + 1 });
        _logger.LogWarning("batch-fallback-preparation-stalled task={TaskKey} attempt={AttemptId}",
            pending.Subject.TaskKey, pending.ReviewAttemptId);
    }

    private TimeSpan GateTimeout(string project)
        => TimeSpan.FromMinutes(_settings.Get(project).BatchGate.GateTimeoutMinutes);

    /// <summary>An open engine decision on this generation's release or red gate outcome.</summary>
    private sealed record EngineDecisionWait(MonolithOrchestrationOccurrence Occurrence, string Outcome)
    {
        public string RunId => Occurrence.Run.RunId;
        public string Status => Occurrence.Run.Status;
        public DateTimeOffset SinceUtc => new(DateTime.SpecifyKind(Occurrence.Run.CreatedAt, DateTimeKind.Utc));
    }

    /// <summary>The supersession reason of an engine decision that passed its bound.</summary>
    internal const string EngineStallSupersedeReason = "batch-fallback-engine-stall";

    private static bool StallSuperseded(MonolithOrchestrationOccurrence occurrence)
        => occurrence.Run.Status == "cancelled"
            && occurrence.CancellationReason?.StartsWith($"superseded: {EngineStallSupersedeReason};",
                StringComparison.Ordinal) == true;

    private EngineDecisionWait? OpenEngineDecision(BatchGatePendingRecord pending, string? containedInSha,
        bool includeFailed = false, bool stallSuperseded = false)
    {
        var orchestration = Service<MonolithOrchestrationStore>();
        if (orchestration is null) return null;
        // A passed gate's release is keyed by the result SHA. A later tick can
        // find the published merge as containment and pass its SHA; the open
        // release of the gate that published it still holds the card.
        foreach (var (key, outcome) in new[]
                 {
                     (FallbackReleaseKey(pending, containedInSha), "release"),
                     (FallbackReleaseKey(pending, null), "release"),
                     (FallbackRedKey(pending), "red"),
                 }.Distinct())
            foreach (var candidate in new[] { key, key + ":engine" })
            {
                var run = orchestration.GetByIdempotencyKey(candidate);
                if (run is null || orchestration.GetOccurrence(run.RunId) is not { Shadow: false } occurrence)
                    continue;
                if (stallSuperseded ? StallSuperseded(occurrence)
                    : occurrence.Run.Status is "pending" or "leased" or "applying"
                      || includeFailed && occurrence.Run.Status == "failed")
                    return new EngineDecisionWait(occurrence, outcome);
            }
        return null;
    }

    /// <summary>
    /// The early return before the start budget, now bounded. Within the bound
    /// the card keeps waiting for the engine and its state names the decision.
    /// Past it the occurrence is superseded under the card gate, so an engine
    /// cannot apply it afterwards, and the card escalates as GateInfra. The
    /// monolith does not take the engine's decision: a passed gate is not
    /// released, the card is parked for an operator with the stalled run.
    /// Returns null while the card waits.
    /// </summary>
    private async Task<bool?> SettleEngineDecisionWaitAsync(TaskInfo task, BatchGatePendingRecord pending,
        EngineDecisionWait wait, BatchGateOwnership? ownership, bool recordInStore, CancellationToken ct)
    {
        var orchestration = Service<MonolithOrchestrationStore>() ?? throw new InvalidOperationException("MonolithOrchestrationStore is not registered.");
        var now = _time.GetUtcNow();
        var decision = MonolithTransitionAdapter.PerTaskFallbackEngineWait(new EngineDecisionWaitFacts(
            wait.Status, wait.SinceUtc,
            wait.Occurrence.Lease is { } lease
                ? new DateTimeOffset(DateTime.SpecifyKind(lease.ExpiresAt, DateTimeKind.Utc)) : null,
            orchestration.IsApplying(wait.RunId), now));
        if (!decision.Stalled)
        {
            if (_engineWaitsLogged.TryAdd(wait.RunId, 0))
                _logger.LogInformation(
                    "batch-fallback-engine-wait task={TaskKey} attempt={AttemptId} run={RunId} status={Status} outcome={Outcome} since={Since}",
                    pending.Subject.TaskKey, pending.ReviewAttemptId, wait.RunId, wait.Status, wait.Outcome,
                    wait.SinceUtc);
            return null;
        }
        await orchestration.SupersedeRunsAsync(wait.Occurrence.Run.ProjectId, wait.Occurrence.Run.TaskId,
            [wait.RunId], TaskStates.AutoReview, EngineStallSupersedeReason, ct).ConfigureAwait(false);
        // The engine applied it while this tick waited for the card gate.
        if (orchestration.Get(wait.RunId)?.Status != "cancelled") return false;
        RaiseFallbackStall(pending, new BatchGateFallbackStall(pending.Subject.Project,
            pending.Subject.TaskKey, pending.ReviewAttemptId, PerTaskFallbackWaitKinds.EngineDecision,
            wait.SinceUtc, now, decision.Reason, "escalated", wait.RunId, wait.Status,
            wait.Occurrence.IdempotencyKey));
        return await EscalateAfterEngineStallAsync(task, pending, wait,
            $"stayed {wait.Status} since {wait.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC ({decision.Reason})",
            ownership, recordInStore, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The escalation of a generation whose engine decision was superseded
    /// after a stall. A later tick that finds the superseded occurrence, for
    /// example after a refused lane move, escalates again instead of releasing
    /// the card or waiting for an engine that will not decide.
    /// </summary>
    private async Task<bool> EscalateAfterEngineStallAsync(TaskInfo task, BatchGatePendingRecord pending,
        EngineDecisionWait wait, string what, BatchGateOwnership? ownership, bool recordInStore,
        CancellationToken ct)
    {
        EngineDecisionWaitMarker.Clear(task.FolderPath, wait.RunId);
        _scanner.InvalidateCache();
        var reason = $"GateInfra: engine decision {wait.RunId} on the per-task gate {wait.Outcome} {what}; "
            + FallbackAttempts(ownership, pending);
        await EscalateFallbackAsync(task, pending, reason, infrastructure: true, recordInStore, ct,
            engineStalled: true).ConfigureAwait(false);
        RecordFallbackSuites(pending, "escalated");
        return FallbackSettled(pending);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _engineWaitsLogged =
        new(StringComparer.Ordinal);

    /// <summary>The attempts of this generation's per-task gate and the base they ran on.</summary>
    private static string FallbackAttempts(BatchGateOwnership? ownership, BatchGatePendingRecord pending)
    {
        var current = ownership?.ReviewAttemptId == pending.ReviewAttemptId ? ownership : null;
        var starts = current?.FallbackGateActive == true ? current.FallbackGateStarts : 0;
        var onBase = current is null ? "no recorded base"
            : current.FallbackOwnBase ? "its own base"
            : current.FallbackBaseSha is { } baseSha ? $"base {baseSha}" : "no recorded base";
        var candidate = current?.FallbackCandidateSha is { } sha ? $", candidate {sha}" : "";
        return $"per-task gate starts {starts} of {FallbackGateStartBudget} on {onBase}{candidate}";
    }

    private void RaiseFallbackStall(BatchGatePendingRecord pending, BatchGateFallbackStall stall)
    {
        if (!_store.RecordFallbackStall(stall)) return;
        _logger.LogWarning(
            "batch-fallback-stalled task={TaskKey} attempt={AttemptId} kind={Kind} since={Since} outcome={Outcome} run={RunId} reason={Reason}",
            stall.TaskKey, stall.ReviewAttemptId, stall.Kind, stall.SinceUtc, stall.Outcome,
            stall.EngineRunId ?? "none", stall.Reason);
        var summary = $"{stall.TaskKey}: per-task gate {StallSubject(stall)} since "
            + $"{stall.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC passed its bound ({stall.Reason}); {StallOutcome(stall)}.";
        try
        {
            var folder = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath)?.FolderPath
                ?? pending.JobFolderPath;
            var details = new Dictionary<string, string>
            {
                ["reviewAttemptId"] = stall.ReviewAttemptId,
                ["kind"] = stall.Kind,
                ["sinceUtc"] = stall.SinceUtc.ToString("O"),
                ["outcome"] = stall.Outcome,
            };
            if (stall.EngineRunId is not null) details["orchestrationRunId"] = stall.EngineRunId;
            if (stall.EngineStatus is not null) details["engineStatus"] = stall.EngineStatus;
            _timeline?.Append(folder, new TimelineEvent
            {
                Ts = stall.RaisedAtUtc.UtcDateTime,
                Kind = PerTaskFallbackWaitPolicy.StallEventKind,
                Actor = TimelineActors.System, Summary = summary, Details = details,
            });
            Service<OrchestratorLog>()?.Append(pending.WatchPath, new OrchestratorLogEntry
            {
                Ts = stall.RaisedAtUtc.UtcDateTime,
                Kind = OrchestratorLogKinds.Alert,
                Topic = $"{PerTaskFallbackWaitPolicy.StallEventKind}:{stall.ReviewAttemptId}:{stall.Kind}",
                JobId = pending.Subject.TaskKey,
                Summary = summary,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "batch-fallback-stall-record-failed task={TaskKey}", stall.TaskKey);
        }
    }

    private static string StallSubject(BatchGateFallbackStall stall)
        => stall.EngineRunId is { } runId
            ? $"waiting for engine decision {runId} ({stall.EngineStatus})"
            : $"flight in {stall.Kind}";

    private static string StallOutcome(BatchGateFallbackStall stall) => stall.Outcome switch
    {
        "escalated" => "the decision is superseded and the card escalates as GateInfra",
        "cancelled" => "the flight is cancelled; the active marker resumes it",
        "detached" => "the flight is detached; its resume escalates the card as GateInfra",
        _ => "the wait continues on work with its own bound",
    };

    /// <summary>
    /// What each pending review of the project waits for in its per-task
    /// fallback: a flight stage of this process, an open or failed engine
    /// decision, or a durable marker that the next tick resumes. Starts no Git.
    /// </summary>
    internal IReadOnlyList<BatchGateFallbackWait> FallbackWaits(string project)
    {
        var waits = new List<BatchGateFallbackWait>();
        foreach (var item in _store.ListPending().Where(item =>
                     string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase)))
        {
            FallbackFlight? flight;
            lock (_fallbackFlights)
                flight = _fallbackFlights.GetValueOrDefault(item.ReviewAttemptId)
                    ?? _detachedFallbackFlights.GetValueOrDefault(item.ReviewAttemptId);
            if (flight is { Task.IsCompleted: false })
            {
                waits.Add(new BatchGateFallbackWait(item.Subject.TaskKey, item.ReviewAttemptId,
                    flight.Stage, flight.StageSinceUtc,
                    $"{StageLabel(flight.Stage)} since {flight.StageSinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                    + (flight.CancelledAtUtc is { } cancelled
                        ? $", cancelled at {cancelled.UtcDateTime:HH:mm} UTC after a stall" : ""),
                    Cancelled: flight.CancelledAtUtc is not null));
                continue;
            }
            BatchGateOwnership? ownership;
            try { ownership = BatchGateOwnershipStore.Read(item.JobFolderPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ownership = null; }
            var contained = ownership?.ReviewAttemptId == item.ReviewAttemptId ? ownership.ContainedInSha : null;
            if (OpenEngineDecision(item, contained, includeFailed: true) is { } engine)
            {
                var failure = engine.Status == "failed"
                    ? $": {engine.Occurrence.ApplicationFailure ?? "application failed"}; operator action required"
                    : "";
                waits.Add(new BatchGateFallbackWait(item.Subject.TaskKey, item.ReviewAttemptId,
                    PerTaskFallbackWaitKinds.EngineDecision, engine.SinceUtc,
                    $"engine decision {engine.RunId} on the gate {engine.Outcome} is {engine.Status} since "
                    + $"{engine.SinceUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC{failure}",
                    engine.RunId, engine.Status));
                continue;
            }
            if (ownership?.ReviewAttemptId == item.ReviewAttemptId && ownership.FallbackGateActive)
                waits.Add(new BatchGateFallbackWait(item.Subject.TaskKey, item.ReviewAttemptId,
                    PerTaskFallbackWaitKinds.ResumePending, item.EnqueuedAtUtc,
                    $"per-task gate marker active with {ownership.FallbackGateStarts} of "
                    + $"{FallbackGateStartBudget} starts and no running gate; the next planner tick resumes it"));
        }
        return waits;
    }

    private static string StageLabel(string stage) => stage switch
    {
        PerTaskFallbackWaitKinds.PerTaskSlot => "waiting for a per-task gate slot",
        PerTaskFallbackWaitKinds.BatchSlot => "waiting while a batch runs or a formed batch starts",
        PerTaskFallbackWaitKinds.MachineGateSlot => "waiting for a machine gate slot",
        PerTaskFallbackWaitKinds.GateRunning => "per-task gate running",
        PerTaskFallbackWaitKinds.Integration => "waiting for the per-task integration",
        _ => "preparing the per-task gate subject",
    };
}
