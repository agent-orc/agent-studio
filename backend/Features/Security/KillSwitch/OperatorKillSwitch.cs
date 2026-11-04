using System.Globalization;

namespace AgentStudio.Security;

/// <summary>A notice to the Owner about one use of the kill switch.</summary>
public sealed record OperatorKillNotice(string Kind, string Trigger, string Summary, bool Acute, object Payload);

public interface IOperatorKillNotifier
{
    /// <summary>Returns <c>false</c> when the notice could not be delivered now and must be sent again later.</summary>
    Task<bool> NotifyAsync(OperatorKillNotice notice, CancellationToken ct);
}

/// <summary>The Owner's notice goes to the operator feed, like the audit trail's acute alerts.</summary>
public sealed class OperatorFeedKillNotifier(AgentMessageBusBridge bus) : IOperatorKillNotifier
{
    public Task<bool> NotifyAsync(OperatorKillNotice notice, CancellationToken ct)
        => bus.EmitOperatorKillSwitchAsync(notice.Kind, notice.Trigger, notice.Summary, notice.Acute, notice.Payload, ct);
}

/// <summary>The out-of-band command runs while the API is stopped; its notice waits for the API's next start.</summary>
public sealed class DeferredOperatorKillNotifier : IOperatorKillNotifier
{
    public Task<bool> NotifyAsync(OperatorKillNotice notice, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// The operator kill switch (security model SM-2, decisions D1 and D7). One
/// coordinator for the Studio action, the out-of-band command, the per-session
/// kill, and the drill: it ends access in the access store first (the next
/// Studio request is refused), writes the audit entry with its own event type,
/// revokes the operator's Operations Server principals (retried until they are
/// revoked), and notifies the Owner. Standing grants (SM-4), approval requests
/// (SM-5), and host certificates (SM-6) join this sequence when those slices exist.
/// </summary>
public sealed class OperatorKillSwitch(
    AccessSecurityStore store,
    OperatorKillSwitchLog log,
    IOperatorPrincipalRevocation revocation,
    IOperatorKillNotifier notifier,
    TimeProvider clock,
    ILogger? logger = null)
{
    public const int MaxReasonLength = 500;
    private static readonly TimeSpan DrillProbeInterval = TimeSpan.FromMilliseconds(50);

    public OperatorKillSwitchLog Log => log;
    public bool OperationsServerConfigured => revocation.Configured;

    /// <summary>Ends every operator principal, every session, and the operator's Operations Server principals.</summary>
    public async Task<OperatorKillRecord> EndAllAsync(OperatorKillActor actor, string trigger, string? reason, string? requestId,
        CancellationToken ct)
    {
        reason = NormalizeReason(reason);
        var ended = store.EndOperatorAccess(null, actor);
        var record = new OperatorKillRecord
        {
            Id = "kill_" + Guid.NewGuid().ToString("N"), Kind = OperatorKillKinds.All, Trigger = trigger, Actor = actor.Actor,
            Reason = reason, At = ended.At, OperatorAgentIds = ended.OperatorAgentIds, SessionIds = ended.SessionIds,
            StandingGrantIds = ended.StandingGrantIds, ApprovalIds = ended.ApprovalIds, AllOperators = true, RequestId = requestId,
            OperationsServer = new OperatorKillPropagation(revocation.Configured
                ? OperatorKillPropagationStates.Pending : OperatorKillPropagationStates.NotConfigured),
        };
        store.AppendOperatorKillAudit(actor.Actor, OperatorKillEvents.All, "ok", "operator-agents:all", AuditDetails(record));
        log.Replace(record);
        logger?.LogWarning("Kill switch: all operator access ended ({Trigger}); {Agents} operator agents, {Sessions} sessions",
            trigger, ended.OperatorAgentIds.Count, ended.SessionIds.Count);
        record = await PropagateAsync(record, ct);
        return await NotifyAsync(record, ct);
    }

    /// <summary>Ends one runaway session; the principal and its other sessions keep working.</summary>
    public async Task<OperatorKillRecord> KillSessionAsync(string agentId, string sessionId, string ownerId, string? reason,
        CancellationToken ct)
    {
        reason = NormalizeReason(reason);
        var actor = OperatorKillActor.Owner(ownerId);
        var ended = store.KillOperatorSession(agentId, sessionId, ownerId);
        var record = new OperatorKillRecord
        {
            Id = "kill_" + Guid.NewGuid().ToString("N"), Kind = OperatorKillKinds.Session, Trigger = OperatorKillTriggers.Studio,
            Actor = actor.Actor, Reason = reason, At = ended.At, OperatorAgentIds = ended.OperatorAgentIds, SessionIds = ended.SessionIds,
            ApprovalIds = ended.ApprovalIds,
        };
        store.AppendOperatorKillAudit(actor.Actor, OperatorKillEvents.Session, "ok", $"operator-agent:{agentId}", AuditDetails(record));
        log.Replace(record);
        return await NotifyAsync(record, ct);
    }

    /// <summary>
    /// Exercises the switch against a fresh test principal: its session is
    /// admitted, the same access-store kill and Operations Server revocation
    /// run for it alone, and the time until both refuse it is measured. Real
    /// operator sessions must stay live. The result is recorded and audited.
    /// </summary>
    public async Task<OperatorKillDrillResult> RunDrillAsync(string trigger, string? ownerId, CancellationToken ct)
    {
        var id = "drill_" + Guid.NewGuid().ToString("N");
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var start = clock.GetTimestamp();
        string? agentId = null;
        TimeSpan? studioRefused = null;
        TimeSpan? operationsAfter = null;
        var operationsState = revocation.Configured ? OperatorKillPropagationStates.Pending : OperatorKillPropagationStates.NotConfigured;
        var kept = false;
        var ended = false;
        string? failure;
        try
        {
            var before = store.LiveOperatorSessionIds();
            var drill = store.StartDrillOperatorSession(ownerId);
            agentId = drill.AgentId;
            if (store.AuthenticateServiceAccount(drill.Secret) is null)
                failure = "drill-session-not-admitted";
            else
            {
                start = clock.GetTimestamp();
                store.EndOperatorAccess([drill.AgentId], OperatorKillActor.Drill);
                ended = true;
                studioRefused = await WaitUntilRefusedAsync(drill.Secret, start, ct);
                if (revocation.Configured)
                {
                    var outcome = await revocation.RevokeAsync([drill.AgentId], all: false, id, ct);
                    operationsAfter = clock.GetElapsedTime(start);
                    operationsState = outcome.Ok ? OperatorKillPropagationStates.Revoked : OperatorKillPropagationStates.Pending;
                }
                kept = before.IsSubsetOf(store.LiveOperatorSessionIds());
                failure = OperatorKillSwitchPolicy.DrillFailure(studioRefused, operationsState, operationsAfter, kept);
            }
        }
        catch (SecurityOperationException exception)
        {
            failure = exception.Code;
        }
        finally
        {
            // A drill that stopped before its kill step must not leave a live test principal behind.
            if (agentId is not null && !ended) store.EndOperatorAccess([agentId], OperatorKillActor.Drill);
        }
        var result = new OperatorKillDrillResult(id, trigger, startedAt, Milliseconds(clock.GetElapsedTime(start)),
            studioRefused is { } studio ? Milliseconds(studio) : null, operationsState,
            operationsAfter is { } operations ? Milliseconds(operations) : null, kept, failure is null, failure, agentId);
        store.AppendOperatorKillAudit(ownerId is null ? OperatorKillActors.Drill : AgentStudio.Shared.TimelineActors.Human(ownerId),
            OperatorKillEvents.Drill, result.Passed ? "passed" : "failed",
            agentId is null ? "operator-agents:drill" : $"operator-agent:{agentId}", result);
        log.Update(state => state with { Drills = [.. state.Drills, result] });
        var summary = result.Passed
            ? $"Kill switch drill passed: the test session was refused after {result.DurationMs.ToString("0", CultureInfo.InvariantCulture)} ms."
            : $"Kill switch drill failed: {failure}.";
        try { await notifier.NotifyAsync(new OperatorKillNotice(OperatorKillKinds.Drill, trigger, summary, !result.Passed, result), ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogError(exception, "Kill switch drill notice could not reach the operator feed");
        }
        return result;
    }

    /// <summary>
    /// Applies the out-of-band latch when one exists. The running API applies
    /// and removes it; with <paramref name="removeLatch"/> <c>false</c> (the
    /// command while the API is stopped) the latch stays, marked applied, so an
    /// API that starts later still refuses every session until it re-enforced the kill.
    /// </summary>
    public async Task<OperatorKillRecord?> ApplyOutOfBandRequestAsync(bool removeLatch, CancellationToken ct)
    {
        var request = OperatorKillSwitchLog.ReadRequest(store.OperatorKillRequestPath);
        if (request is null) return null;
        var record = log.Read().Kills.FirstOrDefault(kill => kill.RequestId == request.Id);
        if (record is null)
            record = await EndAllAsync(OperatorKillActor.OutOfBand, OperatorKillTriggers.OutOfBand, request.Reason, request.Id, ct);
        else
        {
            var enforced = store.EnforceOperatorKill(request.RequestedAt);
            if (enforced.OperatorAgentIds.Count > 0)
                store.AppendOperatorKillAudit(OperatorKillActors.OutOfBand, OperatorKillEvents.Enforced, "ok", "operator-agents:all",
                    new
                    {
                        killId = record.Id, requestId = request.Id, operatorAgentIds = enforced.OperatorAgentIds,
                        sessionIds = enforced.SessionIds, standingGrantIds = enforced.StandingGrantIds, approvalIds = enforced.ApprovalIds,
                    });
        }
        if (removeLatch) File.Delete(store.OperatorKillRequestPath);
        else if (request.AppliedRecordId is null)
            OperatorKillSwitchLog.WriteRequest(store.OperatorKillRequestPath, request with { AppliedRecordId = record.Id });
        return record;
    }

    /// <summary>Tries again every pending Operations Server revocation whose backoff passed.</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var record in log.Read().Kills.Where(kill => OperatorKillSwitchPolicy.RetryDue(kill.OperationsServer, now)).ToList())
            await PropagateAsync(record, ct);
    }

    /// <summary>Sends the notices the out-of-band command could not deliver while the API was stopped.</summary>
    public async Task NotifyPendingAsync(CancellationToken ct)
    {
        foreach (var record in log.Read().Kills.Where(kill => kill.NotifiedAt is null).OrderBy(kill => kill.At).ToList())
            await NotifyAsync(record, ct);
    }

    public OperatorKillSwitchStatusResponse Status(int drillIntervalHours, int limit = 20)
    {
        var state = log.Read();
        return new OperatorKillSwitchStatusResponse(state.Kills.Take(limit).ToList(), state.Drills.Take(limit).ToList(),
            state.NextDrillAt, drillIntervalHours, revocation.Configured, store.OperatorKillRequested);
    }

    private async Task<OperatorKillRecord> PropagateAsync(OperatorKillRecord record, CancellationToken ct)
    {
        if (record.OperationsServer.State != OperatorKillPropagationStates.Pending) return record;
        var outcome = await revocation.RevokeAsync(record.AllOperators ? [] : record.OperatorAgentIds, record.AllOperators, record.Id, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = record.OperationsServer;
        var propagation = outcome.Ok
            ? previous with
            {
                State = OperatorKillPropagationStates.Revoked, Attempts = previous.Attempts + 1, LastAttemptAt = now, LastError = null,
                RevokedPrincipalIds = outcome.RevokedPrincipalIds, CancelledOperations = outcome.CancelledOperations, CompletedAt = now,
            }
            : previous with { Attempts = previous.Attempts + 1, LastAttemptAt = now, LastError = outcome.Error };
        record = record with { OperationsServer = propagation };
        // One entry when the revocation completes and one for its first failure; retries in between are not repeated.
        if (outcome.Ok || previous.Attempts == 0)
            store.AppendOperatorKillAudit(record.Actor, OperatorKillEvents.OperationsServer, outcome.Ok ? "ok" : "failed",
                record.AllOperators ? "operator-agents:all" : "operator-agents:" + string.Join(",", record.OperatorAgentIds),
                new { killId = record.Id, revokedPrincipalIds = propagation.RevokedPrincipalIds, cancelledOperations = propagation.CancelledOperations, error = outcome.Error });
        if (!outcome.Ok)
            logger?.LogError("Kill switch: the Operations Server revocation for {KillId} failed ({Error}); retrying", record.Id, outcome.Error);
        log.Replace(record);
        return record;
    }

    private async Task<OperatorKillRecord> NotifyAsync(OperatorKillRecord record, CancellationToken ct)
    {
        var summary = record.Kind == OperatorKillKinds.All
            ? $"Kill switch: all operator access ended ({record.OperatorAgentIds.Count} operator agents, {record.SessionIds.Count} live sessions, {record.Trigger})."
            : $"Kill switch: operator session {record.SessionIds.FirstOrDefault()} ended.";
        try
        {
            if (!await notifier.NotifyAsync(new OperatorKillNotice(record.Kind, record.Trigger, summary, record.Kind == OperatorKillKinds.All,
                    new { killId = record.Id, record.Kind, record.Trigger, record.OperatorAgentIds, record.SessionIds, operationsServer = record.OperationsServer.State }), ct))
                return record;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogError(exception, "Kill switch notice for {KillId} could not reach the operator feed", record.Id);
            return record;
        }
        record = record with { NotifiedAt = clock.GetUtcNow().UtcDateTime };
        log.Replace(record);
        return record;
    }

    private async Task<TimeSpan?> WaitUntilRefusedAsync(string secret, long start, CancellationToken ct)
    {
        var probes = (int)(OperatorKillSwitchPolicy.RefusalBudget / DrillProbeInterval);
        for (var probe = 0; probe <= probes; probe++)
        {
            if (store.AuthenticateServiceAccount(secret) is null) return clock.GetElapsedTime(start);
            await Task.Delay(DrillProbeInterval, ct);
        }
        return null;
    }

    private static object AuditDetails(OperatorKillRecord record) => new
    {
        killId = record.Id, record.Kind, record.Trigger, record.Reason, record.OperatorAgentIds, record.SessionIds,
        record.StandingGrantIds, record.ApprovalIds, record.AllOperators, record.RequestId, operationsServer = record.OperationsServer.State,
    };

    private static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxReasonLength ? trimmed[..MaxReasonLength] : trimmed;
    }

    private static double Milliseconds(TimeSpan value) => Math.Round(value.TotalMilliseconds, 1);
}
