namespace AgentStudio.Runner;

/// <summary>
/// Compatibility facade for the runner lease API. Canonical identity, lease,
/// fence, epoch, heartbeat, and restart persistence are owned by
/// <see cref="AttemptAuthorityService"/>; this type preserves the established
/// lease wire contract while callers migrate to explicit Attempt IDs. Epochs
/// identify claim generations; an older epoch remains valid while its exact
/// current lease drains.
/// </summary>
public sealed class RunLeaseService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(PickupLockFile.LeaseTtlSeconds);

    private readonly AttemptAuthorityService _authority;
    private readonly Func<DateTime> _utcNow;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public RunLeaseService(ILogger<RunLeaseService> logger, AttemptAuthorityService authority)
    {
        _authority = authority;
        _utcNow = () => DateTime.UtcNow;
    }

    public RunLeaseService(ILogger<RunLeaseService> logger, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _authority = new AttemptAuthorityService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AttemptAuthorityService>.Instance,
            _utcNow);
    }

    public RunLeaseResponse TryAcquire(RunLeaseAcquireRequest request)
    {
        if (Blank(request.TaskKey) || Blank(request.RunnerId))
            return new RunLeaseResponse("Invalid", false, null, "TaskKey and RunnerId are required.");

        var result = _authority.AcquireRun(
            request.TaskKey,
            Blank(request.RepositoryId) ? $"legacy:{Normalize(request.TaskKey)}" : request.RepositoryId!,
            request.SourceRunAttemptId,
            request.RunnerId,
            request.Hostname,
            request.RequestedTtlSeconds,
            Blank(request.IdempotencyKey) ? NewDelivery("lease-acquire") : request.IdempotencyKey!,
            request.RunnerName,
            request.BackendName,
            request.Pid,
            request.ClientId,
            request.LeaseInstanceId,
            request.BriefVersion);

        return result.Status switch
        {
            AttemptWriteStatus.Accepted => new RunLeaseResponse("Acquired", true, ToLease(result.RunAttempt)),
            AttemptWriteStatus.Duplicate when result.RunAttempt?.Lease is not null
                => new RunLeaseResponse("AlreadyOwn", true, ToLease(result.RunAttempt)),
            AttemptWriteStatus.InvalidState => new RunLeaseResponse("Held", false, ToLease(result.RunAttempt), result.Message),
            AttemptWriteStatus.Invalid => new RunLeaseResponse("Invalid", false, null, result.Message),
            _ => new RunLeaseResponse(result.Status.ToString(), false, ToLease(result.RunAttempt), result.Message),
        };
    }

    /// <summary>
    /// Replays a daemon acquire delivery before the server selects a Ready
    /// task. A null result means the delivery has never acquired a task; any
    /// non-null result is authoritative and must not fall through to selection.
    /// </summary>
    public RunLeaseResponse? TryReplayAcquire(string runnerId, string idempotencyKey)
    {
        var result = _authority.ReplayRunAcquire(runnerId, idempotencyKey);
        if (result.Status == AttemptWriteStatus.NotFound) return null;
        return result.Status switch
        {
            AttemptWriteStatus.Duplicate when result.RunAttempt?.Lease is not null
                => new RunLeaseResponse("AlreadyOwn", true, ToLease(result.RunAttempt)),
            AttemptWriteStatus.Invalid
                => new RunLeaseResponse("Invalid", false, null, result.Message),
            _ => new RunLeaseResponse(
                result.Status.ToString(), false, ToLease(result.RunAttempt), result.Message),
        };
    }

    public RunLeaseResponse Renew(RunLeaseHeartbeatRequest request, Func<string?>? beforeRenew = null)
    {
        var reference = ResolveReference(
            request.TaskKey, request.AttemptId, request.FencingToken, request.AuthorityEpoch,
            request.IdempotencyKey, "lease-renew");
        if (reference is null)
            return new RunLeaseResponse("NotHeld", false, null, "No canonical RunAttempt is held for this task.");

        var result = _authority.RenewRun(
            reference, request.RunnerId, request.RequestedTtlSeconds, request.LeaseId, beforeRenew);
        return MapMutation(result, "Renewed");
    }

    public RunLeaseResponse Release(RunLeaseReleaseRequest request)
    {
        var before = Current(request.TaskKey);
        var reference = ResolveReference(
            request.TaskKey, request.AttemptId, request.FencingToken, request.AuthorityEpoch,
            request.IdempotencyKey, "lease-release");
        if (reference is null)
            return new RunLeaseResponse("NotHeld", false, null, "No canonical RunAttempt is held for this task.");

        var result = _authority.ReleaseRun(reference, request.RunnerId, request.LeaseId);
        var mapped = MapMutation(result, "Released");
        return mapped with { Lease = mapped.Lease ?? ToLease(before) };
    }

    public RunLeaseResponse Peek(string taskKey)
    {
        if (Blank(taskKey)) return new RunLeaseResponse("Invalid", false, null, "TaskKey is required.");
        var run = Current(taskKey);
        if (run is not { State: AttemptLifecycleState.Leased, Lease: not null }
            || run.Lease.ExpiresAt <= _utcNow())
            return new RunLeaseResponse("Free", false, null);
        return new RunLeaseResponse("Held", false, ToLease(run));
    }

    /// <summary>
    /// Read-side inspection that retains the most recent expired or released
    /// owner for health and historical attribution. It never grants authority:
    /// only <see cref="Peek"/> and <see cref="IsCurrent"/> describe a live lease.
    /// </summary>
    public RunLeaseInspection Inspect(string taskKey)
    {
        if (Blank(taskKey)) return new RunLeaseInspection("none", null);
        var run = Current(taskKey);
        if (run?.Lease is null) return new RunLeaseInspection("none", null);
        var active = run.State == AttemptLifecycleState.Leased
                     && run.Lease.ExpiresAt > _utcNow();
        var state = active
            ? "active"
            : run.Lease.ExpiresAt <= _utcNow() ? "expired" : "released";
        return new RunLeaseInspection(state, ToLease(run));
    }

    /// <summary>
    /// The §8.2C write gate: a state-affecting write (log-chunk ingestion, timeline
    /// append, run completion, lane transition, integration, cleanup) is allowed
    /// only while this exact lease is still current. After a TTL takeover raised the
    /// fencing token, an old holder's token no longer matches, so this returns false
    /// and the caller rejects the stale write instead of corrupting task state.
    /// </summary>
    public bool IsCurrent(string taskKey, string leaseId, long fencingToken, string runnerId)
    {
        var run = Current(taskKey);
        return run is { State: AttemptLifecycleState.Leased, Lease: not null }
               && run.LastFence == fencingToken
               && run.Lease.ExpiresAt > _utcNow()
               && string.Equals(run.Lease.LeaseId, leaseId, StringComparison.Ordinal)
               && string.Equals(run.Lease.ExecutorId, Normalize(runnerId), StringComparison.Ordinal);
    }

    public AttemptWriteReference? CurrentWriteReference(string taskKey, string? idempotencyKey = null)
    {
        var run = Current(taskKey);
        return run is not { Lease: not null }
            ? null
            : new AttemptWriteReference(
            run.AttemptId,
            run.LastFence,
            run.AuthorityEpoch,
            Blank(idempotencyKey) ? NewDelivery("write") : idempotencyKey!);
    }

    private AttemptWriteReference? ResolveReference(
        string taskKey,
        string? attemptId,
        long fence,
        long? epoch,
        string? idempotencyKey,
        string operation)
    {
        var run = Blank(attemptId) ? Current(taskKey) : _authority.GetRun(attemptId!);
        if (run is null) return null;
        return new AttemptWriteReference(
            run.AttemptId,
            fence,
            epoch.GetValueOrDefault(run.AuthorityEpoch),
            Blank(idempotencyKey) ? NewDelivery(operation) : idempotencyKey!);
    }

    private RunAttemptDto? Current(string taskKey) => _authority.GetTaskProjection(taskKey).CurrentRunAttempt;

    private static RunLeaseResponse MapMutation(AttemptWriteResult result, string success)
    {
        var outcome = result.Status switch
        {
            AttemptWriteStatus.Accepted => success,
            AttemptWriteStatus.Duplicate => success,
            AttemptWriteStatus.LeaseExpired => "Expired",
            AttemptWriteStatus.StaleFence or AttemptWriteStatus.AuthorityEpochMismatch or AttemptWriteStatus.Superseded => "StaleToken",
            AttemptWriteStatus.NotFound => "NotHeld",
            _ => result.Status.ToString(),
        };
        var message = result.RunAttempt?.TerminalOutcome == "operator-revoked"
                ? "The operator revoked this run."
                : result.Message;
        return new RunLeaseResponse(outcome, outcome == "Renewed", ToLease(result.RunAttempt), message);
    }

    private static RunLeaseInfoDto? ToLease(RunAttemptDto? run)
    {
        if (run?.Lease is null) return null;
        var lease = run.Lease;
        return new RunLeaseInfoDto(
            run.TaskKey,
            lease.ExecutorId,
            string.IsNullOrWhiteSpace(lease.ExecutorDisplayName) ? lease.ExecutorId : lease.ExecutorDisplayName,
            lease.HostId,
            lease.ProcessId,
            lease.BackendName ?? "remote",
            lease.LeaseId,
            lease.Fence,
            lease.AcquiredAt,
            lease.ExpiresAt,
            run.AttemptId,
            run.AuthorityEpoch)
        {
            LastHeartbeatAt = lease.LastHeartbeat,
            ClientId = string.IsNullOrWhiteSpace(lease.ClientId) ? null : lease.ClientId,
        };
    }

    private static string NewDelivery(string operation) => $"{operation}:{Guid.NewGuid():N}";
    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

public sealed record RunLeaseInspection(string State, RunLeaseInfoDto? Lease);
