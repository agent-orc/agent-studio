using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3857: measures the time a formed batch waits while its project runs no
/// batch gate, and names the cause of every interval: a per-task holder,
/// restart recovery, or the planner. Each tick observes every project with a
/// formed batch or an open interval. An open interval a previous process left
/// ends at its last observation; the gap up to this process's first
/// observation is restart recovery.
/// </summary>
public sealed partial class BatchGatePilotService
{
    private void ObserveLaneIdle(string? plannerHold = null)
    {
        try { ObserveLaneIdleCore(plannerHold); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "batch-lane-idle-observation-failed");
        }
    }

    private void ObserveLaneIdleCore(string? plannerHold)
    {
        var now = _time.GetUtcNow();
        var formed = _store.ListRecoverableManifests()
            .Where(manifest => manifest.StackedOnBatchId is null
                && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .GroupBy(manifest => manifest.Scope.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.MinBy(manifest => BatchGateFlightQueue.QueuedSince(_store, manifest))!,
                StringComparer.OrdinalIgnoreCase);
        var open = _store.ListOpenLaneIdle()
            .ToDictionary(item => item.Project, StringComparer.OrdinalIgnoreCase);
        foreach (var project in formed.Keys.Union(open.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var oldest = formed.GetValueOrDefault(project);
            var cause = oldest is null ? null : BatchLaneIdlePolicy.Classify(LaneIdleFacts(project, now, plannerHold));
            RecordLaneIdle(project, cause, oldest?.BatchId, open.GetValueOrDefault(project), now);
        }
    }

    private BatchLaneIdleFacts LaneIdleFacts(string project, DateTimeOffset now, string? plannerHold)
    {
        var claim = _store.ReadFlightClaim(project);
        var ownerGone = claim is not null && claim.Owner != _claimOwner && OwnerGone(claim.Owner);
        var live = claim is not null && !ownerGone
            && MonolithTransitionAdapter.BatchFlightClaimLive(claim.Facts(now));
        bool stackRunning;
        lock (_runningStacks)
            stackRunning = _runningStacks.TryGetValue(project, out var stack) && !stack.IsCompleted;
        var pushPending = _reservations?.GetWait(project) is not null;
        // A reserving flight whose reservation is free (or already its own) is
        // starting, not waiting: only a held reservation or a pending push holds it.
        var reserving = live && claim!.Stage == BatchFlightStage.Reserving
            && (pushPending || ReservationHeldByOther(project, claim.BatchId));
        var gating = live && !reserving;
        var queued = gating && claim!.Stage == BatchFlightStage.Gating
            && _store.ListRuns(claim.BatchId).Any(run => _queueStalls.IsWaiting(run.BatchRunId)
                || _queueStalls.IsWaiting(run.BatchRunId + "-retry"));
        var previous = claim is not null && claim.Owner != _claimOwner
            && (claim.End is null ? !live : !claim.End.Settled);
        var recovering = _store.ListRecoverableManifests().Any(manifest =>
            string.Equals(manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase)
            && _recoveryDelays.ContainsKey(manifest.BatchId));
        int perTask;
        lock (_fallbackFlights)
            perTask = _fallbackFlights.Values.Count(flight => string.Equals(flight.Pending.Subject.Project, project, StringComparison.OrdinalIgnoreCase));
        var hold = plannerHold ?? (_hostOutage.RunsHeld ? "the gate host outage holds batch runs" : null);
        return new BatchLaneIdleFacts(true, gating || stackRunning, queued, reserving, previous,
            recovering, pushPending, perTask, hold);
    }

    private bool ReservationHeldByOther(string project, string batchId)
    {
        if (_reservations is null) return false;
        lock (_flights)
            if (_flights.TryGetValue(batchId, out var flight) && flight.Reservation is not null)
                return false;
        using var probe = _reservations.TryAcquire(project);
        return probe is null;
    }

    private void RecordLaneIdle(string project, BatchLaneIdleCause? cause, string? batchId,
        BatchLaneIdleInterval? open, DateTimeOffset now)
    {
        if (open is not null && open.Observer != _claimOwner)
        {
            // A previous process saw the lane idle last; nobody observed the downtime.
            var closed = new List<BatchLaneIdleInterval> { open with { EndedAtUtc = open.LastObservedAtUtc } };
            if (now > open.LastObservedAtUtc)
                closed.Add(new BatchLaneIdleInterval(project, BatchLaneIdleCauses.RestartRecovery,
                    BatchGateLaneIdleRestart.Detail, batchId ?? open.BatchId, open.LastObservedAtUtc,
                    now, now, _claimOwner));
            _store.CloseLaneIdle(project, closed, clearOpen: cause is null);
            if (cause is not null) OpenLaneIdle(project, cause, batchId, now);
            return;
        }
        if (open is not null && cause is not null && open.Cause == cause.Cause)
        {
            _store.WriteOpenLaneIdle(open with { Detail = cause.Detail, BatchId = batchId, LastObservedAtUtc = now });
            return;
        }
        if (open is not null)
            _store.CloseLaneIdle(project, [open with { LastObservedAtUtc = now, EndedAtUtc = now }],
                clearOpen: cause is null);
        if (cause is not null) OpenLaneIdle(project, cause, batchId, now);
    }

    private void OpenLaneIdle(string project, BatchLaneIdleCause cause, string? batchId, DateTimeOffset now)
    {
        _store.WriteOpenLaneIdle(new BatchLaneIdleInterval(project, cause.Cause, cause.Detail, batchId,
            now, now, null, _claimOwner));
        _logger.LogInformation("batch-lane-idle project={Project} cause={Cause} batch={BatchId} detail={Detail}",
            project, cause.Cause, batchId, cause.Detail);
    }

    /// <summary>The lane idle measure of <paramref name="project"/> for the state response.</summary>
    public BatchLaneIdleSummary LaneIdle(string project)
    {
        var now = _time.GetUtcNow();
        return BatchLaneIdleSummary.From(_store.ListLaneIdle(project, now - TimeSpan.FromDays(7)),
            _store.ReadOpenLaneIdle(project), _claimOwner, now);
    }
}
