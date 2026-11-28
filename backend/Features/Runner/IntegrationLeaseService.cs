
namespace AgentStudio.Runner;

/// <summary>
/// Server-owned, fenced merge queue for ADR-0052 direct-merge integration.
/// Multiple task branches may be produced in parallel, including by future
/// remote runners, but only one holder per project/integration-branch may mutate
/// the integration branch at a time. A monotonically increasing fencing token is
/// returned with every granted lease; stale holders cannot renew or release once
/// the lease has expired and a newer token has been issued.
/// </summary>
public sealed class IntegrationLeaseService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    // Acquire polls keep a waiter's place alive independently of the holder's TTL.
    public static readonly TimeSpan QueueIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(45);

    private readonly object _gate = new();
    private readonly Dictionary<IntegrationLeaseKey, IntegrationLeaseSlot> _slots = new();
    private readonly ILogger<IntegrationLeaseService> _logger;
    private readonly Func<DateTime> _utcNow;

    public IntegrationLeaseService(ILogger<IntegrationLeaseService> logger, Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IntegrationLeaseResponse TryAcquire(IntegrationLeaseAcquireRequest request)
    {
        var validation = ValidateAcquire(request);
        if (validation != null) return validation;

        var now = _utcNow();
        var key = IntegrationLeaseKey.From(request.ProjectName, request.IntegrationBranch);
        var owner = IntegrationLeaseOwner.From(request);
        var ttl = NormalizeTtl(request.RequestedTtlSeconds);

        lock (_gate)
        {
            var slot = SlotFor(key);
            PruneExpiredCurrent(slot, key, now);
            PruneExpiredQueued(slot, now);

            if (slot.Current is { } current && current.Owner.SameOwner(owner))
                return new IntegrationLeaseResponse("AlreadyOwn", true, ToDto(current), 0);

            QueueOrRefresh(slot, owner, now);

            if (slot.Current is null && slot.Queue.TryPeek(out var head) && head.Owner.SameOwner(owner))
            {
                slot.Queue.Dequeue();
                var lease = Grant(slot, key, owner, ttl, now);
                _logger.LogInformation(
                    "[integration-lease] granted {Project}/{Branch} to {TaskKey} runner={RunnerId} token={FencingToken}",
                    key.ProjectName,
                    key.IntegrationBranch,
                    owner.TaskKey,
                    owner.RunnerId,
                    lease.FencingToken);
                return new IntegrationLeaseResponse("Acquired", true, ToDto(lease), 0);
            }

            var position = QueuePosition(slot, owner);
            return new IntegrationLeaseResponse(
                "Queued",
                false,
                ToDto(slot.Current),
                position,
                slot.Current is null
                    ? $"Integration branch '{key.IntegrationBranch}' is free with earlier queued waiters; queued at position {position}."
                    : $"Integration branch '{key.IntegrationBranch}' is held by another runner; queued at position {position}.");
        }
    }

    public async Task<IntegrationLeaseGrant> WaitAcquireAsync(
        IntegrationLeaseAcquireRequest request,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        var delay = retryDelay is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(250);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var response = TryAcquire(request);
                if (response is { Granted: true, Lease: not null })
                    return IntegrationLeaseGrant.From(response.Lease);

                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException)
        {
            CancelQueued(request.ProjectName, request.IntegrationBranch, request.TaskKey, request.RunnerId);
            throw;
        }
    }

    public IntegrationLeaseResponse Renew(IntegrationLeaseHeartbeatRequest request)
    {
        var validation = ValidateLeaseReference(request.ProjectName, request.IntegrationBranch, request.LeaseId, request.FencingToken, request.RunnerId);
        if (validation != null) return validation;

        var now = _utcNow();
        var key = IntegrationLeaseKey.From(request.ProjectName, request.IntegrationBranch);
        var ttl = NormalizeTtl(request.RequestedTtlSeconds);

        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null)
                return new IntegrationLeaseResponse("NotHeld", false, null, 0, "No integration lease is currently held.");

            if (slot.Current.ExpiresAt <= now)
            {
                var expired = slot.Current;
                slot.Current = null;
                _logger.LogWarning(
                    "[integration-lease] heartbeat rejected expired {Project}/{Branch} lease={LeaseId} token={FencingToken}",
                    key.ProjectName,
                    key.IntegrationBranch,
                    expired.LeaseId,
                    expired.FencingToken);
                return new IntegrationLeaseResponse("Expired", false, ToDto(expired), 0, "The integration lease expired before the heartbeat arrived.");
            }

            if (!Matches(slot.Current, request.LeaseId, request.FencingToken, request.RunnerId))
                return new IntegrationLeaseResponse("StaleToken", false, ToDto(slot.Current), 0, "Lease id, fencing token, or runner id does not match the current holder.");

            slot.Current = slot.Current with { ExpiresAt = now.Add(ttl) };
            return new IntegrationLeaseResponse("Renewed", true, ToDto(slot.Current), 0);
        }
    }

    public IntegrationLeaseResponse Release(IntegrationLeaseReleaseRequest request)
    {
        var validation = ValidateLeaseReference(request.ProjectName, request.IntegrationBranch, request.LeaseId, request.FencingToken, request.RunnerId);
        if (validation != null) return validation;

        var key = IntegrationLeaseKey.From(request.ProjectName, request.IntegrationBranch);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null)
                return new IntegrationLeaseResponse("NotHeld", false, null, 0, "No integration lease is currently held.");

            if (!Matches(slot.Current, request.LeaseId, request.FencingToken, request.RunnerId))
                return new IntegrationLeaseResponse("StaleToken", false, ToDto(slot.Current), 0, "Lease id, fencing token, or runner id does not match the current holder.");

            var released = slot.Current;
            slot.Current = null;
            _logger.LogInformation(
                "[integration-lease] released {Project}/{Branch} from {TaskKey} runner={RunnerId} token={FencingToken}",
                key.ProjectName,
                key.IntegrationBranch,
                released.Owner.TaskKey,
                released.Owner.RunnerId,
                released.FencingToken);
            return new IntegrationLeaseResponse("Released", false, ToDto(released), QueuePosition(slot, released.Owner));
        }
    }

    public IntegrationLeaseResponse Peek(string projectName, string integrationBranch)
    {
        var key = IntegrationLeaseKey.From(projectName, integrationBranch);
        if (string.IsNullOrWhiteSpace(key.ProjectName) || string.IsNullOrWhiteSpace(key.IntegrationBranch))
            return new IntegrationLeaseResponse("Invalid", false, null, 0, "Project name and integration branch are required.");

        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
                return new IntegrationLeaseResponse("Free", false, null, 0);
            var now = _utcNow();
            PruneExpiredCurrent(slot, key, now);
            PruneExpiredQueued(slot, now);
            var outcome = slot.Current is not null ? "Held" : slot.Queue.Count > 0 ? "Queued" : "Free";
            return new IntegrationLeaseResponse(outcome, false, ToDto(slot.Current), slot.Queue.Count);
        }
    }

    public bool IsCurrent(IntegrationLeaseGrant grant)
    {
        var key = IntegrationLeaseKey.From(grant.ProjectName, grant.IntegrationBranch);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null)
                return false;
            if (slot.Current.ExpiresAt <= _utcNow())
            {
                slot.Current = null;
                return false;
            }
            return Matches(slot.Current, grant.LeaseId, grant.FencingToken, grant.RunnerId);
        }
    }

    private void CancelQueued(string projectName, string integrationBranch, string taskKey, string runnerId)
    {
        var key = IntegrationLeaseKey.From(projectName, integrationBranch);
        var ownerKey = new IntegrationLeaseOwnerKey(Normalize(taskKey), Normalize(runnerId));
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot)) return;
            slot.Queue = new Queue<IntegrationLeaseQueueEntry>(
                slot.Queue.Where(q => !q.Owner.Key.Equals(ownerKey)));
        }
    }

    private IntegrationLeaseRecord Grant(
        IntegrationLeaseSlot slot,
        IntegrationLeaseKey key,
        IntegrationLeaseOwner owner,
        TimeSpan ttl,
        DateTime now)
    {
        slot.LastFencingToken++;
        var lease = new IntegrationLeaseRecord(
            key.ProjectName,
            key.IntegrationBranch,
            owner,
            Guid.NewGuid().ToString("N"),
            slot.LastFencingToken,
            now,
            now.Add(ttl));
        slot.Current = lease;
        return lease;
    }

    private IntegrationLeaseSlot SlotFor(IntegrationLeaseKey key)
    {
        if (!_slots.TryGetValue(key, out var slot))
        {
            slot = new IntegrationLeaseSlot();
            _slots[key] = slot;
        }
        return slot;
    }

    private void PruneExpiredCurrent(IntegrationLeaseSlot slot, IntegrationLeaseKey key, DateTime now)
    {
        if (slot.Current is null || slot.Current.ExpiresAt > now) return;
        _logger.LogWarning(
            "[integration-lease] expired {Project}/{Branch} lease={LeaseId} token={FencingToken}",
            key.ProjectName,
            key.IntegrationBranch,
            slot.Current.LeaseId,
            slot.Current.FencingToken);
        slot.Current = null;
    }

    private static void PruneExpiredQueued(IntegrationLeaseSlot slot, DateTime now)
    {
        slot.Queue = new Queue<IntegrationLeaseQueueEntry>(
            slot.Queue.Where(q => q.LastSeenAt.Add(QueueIdleTimeout) > now));
    }

    private static void QueueOrRefresh(IntegrationLeaseSlot slot, IntegrationLeaseOwner owner, DateTime now)
    {
        if (slot.Queue.Any(q => q.Owner.SameOwner(owner)))
        {
            slot.Queue = new Queue<IntegrationLeaseQueueEntry>(
                slot.Queue.Select(q => q.Owner.SameOwner(owner) ? q with { LastSeenAt = now } : q));
            return;
        }

        slot.Queue.Enqueue(new IntegrationLeaseQueueEntry(owner, now, now));
    }

    private static int QueuePosition(IntegrationLeaseSlot slot, IntegrationLeaseOwner owner)
    {
        var position = 1;
        foreach (var entry in slot.Queue)
        {
            if (entry.Owner.SameOwner(owner)) return position;
            position++;
        }
        return 0;
    }

    private static bool Matches(IntegrationLeaseRecord lease, string leaseId, long fencingToken, string runnerId)
        => string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
           && lease.FencingToken == fencingToken
           && string.Equals(lease.Owner.RunnerId, Normalize(runnerId), StringComparison.Ordinal);

    private static TimeSpan NormalizeTtl(int? seconds)
    {
        var ttl = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultTtl;
        if (ttl < MinTtl) return MinTtl;
        if (ttl > MaxTtl) return MaxTtl;
        return ttl;
    }

    private static IntegrationLeaseResponse? ValidateAcquire(IntegrationLeaseAcquireRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName)
            || string.IsNullOrWhiteSpace(request.IntegrationBranch)
            || string.IsNullOrWhiteSpace(request.TaskKey)
            || string.IsNullOrWhiteSpace(request.RunnerId))
        {
            return new IntegrationLeaseResponse("Invalid", false, null, 0,
                "ProjectName, IntegrationBranch, TaskKey, and RunnerId are required.");
        }
        return null;
    }

    private static IntegrationLeaseResponse? ValidateLeaseReference(
        string projectName,
        string integrationBranch,
        string leaseId,
        long fencingToken,
        string runnerId)
    {
        if (string.IsNullOrWhiteSpace(projectName)
            || string.IsNullOrWhiteSpace(integrationBranch)
            || string.IsNullOrWhiteSpace(leaseId)
            || string.IsNullOrWhiteSpace(runnerId)
            || fencingToken <= 0)
        {
            return new IntegrationLeaseResponse("Invalid", false, null, 0,
                "ProjectName, IntegrationBranch, LeaseId, FencingToken, and RunnerId are required.");
        }
        return null;
    }

    private static IntegrationLeaseInfoDto? ToDto(IntegrationLeaseRecord? lease) => lease is null
        ? null
        : new IntegrationLeaseInfoDto(
            lease.ProjectName,
            lease.IntegrationBranch,
            lease.Owner.TaskKey,
            lease.Owner.RunnerId,
            lease.Owner.Hostname,
            lease.Owner.Pid,
            lease.Owner.BackendName,
            lease.LeaseId,
            lease.FencingToken,
            lease.AcquiredAt,
            lease.ExpiresAt);

    private static string Normalize(string? value) => (value ?? "").Trim();

    private sealed record IntegrationLeaseKey(string ProjectName, string IntegrationBranch)
    {
        public static IntegrationLeaseKey From(string projectName, string integrationBranch)
            => new(Normalize(projectName), Normalize(integrationBranch));
    }

    private sealed record IntegrationLeaseOwnerKey(string TaskKey, string RunnerId);

    private sealed record IntegrationLeaseOwner(
        string TaskKey,
        string RunnerId,
        string Hostname,
        int Pid,
        string BackendName)
    {
        public IntegrationLeaseOwnerKey Key => new(TaskKey, RunnerId);

        public bool SameOwner(IntegrationLeaseOwner other) => Key.Equals(other.Key);

        public static IntegrationLeaseOwner From(IntegrationLeaseAcquireRequest request)
            => new(
                Normalize(request.TaskKey),
                Normalize(request.RunnerId),
                Normalize(request.Hostname),
                request.Pid,
                Normalize(request.BackendName));
    }

    private sealed record IntegrationLeaseQueueEntry(IntegrationLeaseOwner Owner, DateTime QueuedAt, DateTime LastSeenAt);

    private sealed record IntegrationLeaseRecord(
        string ProjectName,
        string IntegrationBranch,
        IntegrationLeaseOwner Owner,
        string LeaseId,
        long FencingToken,
        DateTime AcquiredAt,
        DateTime ExpiresAt);

    private sealed class IntegrationLeaseSlot
    {
        public IntegrationLeaseRecord? Current { get; set; }
        public Queue<IntegrationLeaseQueueEntry> Queue { get; set; } = new();
        public long LastFencingToken { get; set; }
    }
}

public sealed record IntegrationLeaseGrant(
    string ProjectName,
    string IntegrationBranch,
    string TaskKey,
    string RunnerId,
    string LeaseId,
    long FencingToken,
    DateTime ExpiresAt)
{
    public static IntegrationLeaseGrant From(IntegrationLeaseInfoDto dto)
        => new(
            dto.ProjectName,
            dto.IntegrationBranch,
            dto.TaskKey,
            dto.RunnerId,
            dto.LeaseId,
            dto.FencingToken,
            dto.ExpiresAt);
}
