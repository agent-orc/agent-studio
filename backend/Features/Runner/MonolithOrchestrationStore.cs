using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>A durable, single-transition occurrence owned by the monolith task authority.</summary>
public sealed record MonolithOrchestrationOccurrence(
    OrchestrationRunDto Run,
    string IdempotencyKey,
    string ExpectedSourceState,
    string AuthorityAttemptId,
    long AuthorityFence,
    long AuthorityEpoch,
    OrchestrationLeaseDto? Lease = null,
    long LastFence = 0,
    string? CompletionKey = null,
    CompleteOrchestrationStageRequest? Completion = null,
    bool Claimable = true,
    string? CancellationReason = null);

public interface IMonolithOrchestrationApplier
{
    Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
        CompleteOrchestrationStageRequest completion, CancellationToken ct);
}

/// <summary>
/// Atomic file replacement makes every lease and stage receipt recoverable after a restart.
/// The application effect is replayed with its authority write key before a run is marked done.
/// </summary>
public sealed class MonolithOrchestrationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private readonly IMonolithOrchestrationApplier _applier;
    private readonly Dictionary<string, MonolithOrchestrationOccurrence> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _completionGates = new(StringComparer.Ordinal);

    public MonolithOrchestrationStore(IConfiguration configuration, TimeProvider time,
        IMonolithOrchestrationApplier applier)
        : this(string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? null
            : Path.Combine(configuration["TaskRepository"]!, ".orchestration-occurrences"), time, applier) { }

    /// <param name="directory">
    /// Without a TaskRepository the store keeps occurrences in memory only, like
    /// AttemptAuthorityService, so hosts without a workspace still start.
    /// </param>
    public MonolithOrchestrationStore(string? directory, TimeProvider time, IMonolithOrchestrationApplier applier)
    {
        _directory = directory;
        _time = time;
        _applier = applier;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "orch_*.json"))
        {
            var occurrence = JsonSerializer.Deserialize<MonolithOrchestrationOccurrence>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Orchestration occurrence is empty: {file}");
            _runs.Add(occurrence.Run.RunId, occurrence);
        }
    }

    public OrchestrationRunDto Create(string projectId, string taskId,
        OrchestrationStage stage, string payloadJson, string idempotencyKey,
        string expectedSourceState, string authorityAttemptId, long authorityFence, long authorityEpoch,
        bool claimable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityAttemptId);
        _ = JsonDocument.Parse(payloadJson);
        lock (_gate)
        {
            var existing = _runs.Values.FirstOrDefault(value => value.IdempotencyKey == idempotencyKey);
            if (existing is not null)
            {
                if (existing.Run.ProjectId != projectId || existing.Run.TaskId != taskId
                    || existing.Run.CurrentStage != stage || existing.Run.PayloadJson != payloadJson
                    || existing.ExpectedSourceState != expectedSourceState
                    || existing.AuthorityAttemptId != authorityAttemptId
                    || existing.AuthorityFence != authorityFence
                    || existing.AuthorityEpoch != authorityEpoch)
                    throw new InvalidOperationException("orchestration-idempotency-conflict");
                if (existing.Run.Status == "cancelled")
                {
                    var resumedAt = _time.GetUtcNow().UtcDateTime;
                    existing = existing with
                    {
                        Run = existing.Run with { Status = "pending", UpdatedAt = resumedAt, CompletedAt = null },
                        CancellationReason = null,
                    };
                    Save(existing);
                    _runs[existing.Run.RunId] = existing;
                }
                return existing.Run;
            }
            var now = _time.GetUtcNow().UtcDateTime;
            var run = new OrchestrationRunDto($"orch_{Guid.NewGuid():N}", projectId, taskId, 1,
                "pending", stage, payloadJson, 0, now, now, null, []);
            var occurrence = new MonolithOrchestrationOccurrence(run, idempotencyKey,
                expectedSourceState, authorityAttemptId, authorityFence, authorityEpoch,
                Claimable: claimable);
            Save(occurrence);
            _runs.Add(run.RunId, occurrence);
            return run;
        }
    }

    public OrchestrationRunDto CreatePeriodic(string projectId, string taskId,
        OrchestrationStage stage, string payloadJson, string subjectKey,
        string expectedSourceState, string authorityAttemptId,
        long authorityFence, long authorityEpoch, TimeSpan period)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        lock (_gate)
        {
            var active = _runs.Values.FirstOrDefault(item => item.Run.ProjectId == projectId
                && item.Run.TaskId == taskId && item.Run.CurrentStage == stage
                && item.Run.Status is "pending" or "leased" or "applying");
            if (active is not null) return active.Run;
            var bucket = _time.GetUtcNow().Ticks / period.Ticks;
            return Create(projectId, taskId, stage, payloadJson,
                $"periodic:{stage}:{projectId}:{taskId}:{subjectKey}:{bucket}",
                expectedSourceState, authorityAttemptId, authorityFence, authorityEpoch);
        }
    }

    public OrchestrationRunDto? Get(string runId)
    {
        lock (_gate) return _runs.GetValueOrDefault(runId)?.Run;
    }

    public OrchestrationRunDto? GetByIdempotencyKey(string key)
    {
        lock (_gate) return _runs.Values.FirstOrDefault(item => item.IdempotencyKey == key)?.Run;
    }

    public IReadOnlyList<OrchestrationRunDto> List(string? projectId, string? status)
    {
        lock (_gate) return _runs.Values.Select(item => item.Run)
            .Where(run => (projectId is null || run.ProjectId == projectId)
                && (status is null || run.Status == status))
            .OrderBy(run => run.CreatedAt).ThenBy(run => run.RunId).ToArray();
    }

    public IReadOnlyList<MonolithOrchestrationOccurrence> WaitingOccurrences()
    {
        lock (_gate) return _runs.Values.Where(item => item.Claimable
            && (item.Run.Status is "pending" or "leased" or "applying")).ToArray();
    }

    public IReadOnlyList<MonolithOrchestrationOccurrence> AwaitingActivation()
    {
        lock (_gate) return _runs.Values.Where(item => !item.Claimable
            && item.Run.Status == "pending").ToArray();
    }

    public void Activate(string runId)
    {
        lock (_gate)
        {
            var item = Required(runId);
            if (item.Claimable) return;
            if (item.Run.Status is not ("pending" or "cancelled"))
                throw new InvalidOperationException("orchestration-activation-state-mismatch");
            item = item with
            {
                Claimable = true,
                CancellationReason = null,
                Run = item.Run with { Status = "pending", UpdatedAt = _time.GetUtcNow().UtcDateTime,
                    CompletedAt = null },
            };
            Save(item);
            _runs[runId] = item;
        }
    }

    public void CancelUnactivated(string runId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            var item = Required(runId);
            if (item.Run.Status == "cancelled") return;
            if (item.Claimable || item.Run.Status != "pending") return;
            var now = _time.GetUtcNow().UtcDateTime;
            item = item with
            {
                CancellationReason = reason,
                Run = item.Run with { Status = "cancelled", UpdatedAt = now, CompletedAt = now },
            };
            Save(item);
            _runs[runId] = item;
        }
    }

    public OrchestrationClaimResponse Claim(OrchestrationClaimRequest request)
    {
        ValidateIdentity(request.EngineId, request.InstanceId);
        if (request.SupportedStages.Count == 0) throw new ArgumentException("Supported stages are required.");
        lock (_gate)
        {
            ExpireLeases();
            var item = _runs.Values.Where(value => value.Run.Status == "pending" && value.Claimable
                    && request.SupportedStages.Contains(value.Run.CurrentStage))
                .OrderBy(value => value.Run.UpdatedAt).ThenBy(value => value.Run.RunId).FirstOrDefault();
            if (item is null) return new OrchestrationClaimResponse("empty");
            var now = _time.GetUtcNow().UtcDateTime;
            var lease = new OrchestrationLeaseDto($"olease_{Guid.NewGuid():N}", item.Run.RunId,
                request.EngineId, request.InstanceId, checked(item.LastFence + 1), now,
                now.AddSeconds(Ttl(request.RequestedTtlSeconds)), "active");
            item = item with { Lease = lease, LastFence = lease.Fence,
                Run = item.Run with { Status = "leased", UpdatedAt = now } };
            Save(item);
            _runs[item.Run.RunId] = item;
            return new OrchestrationClaimResponse("claimed", item.Run, lease);
        }
    }

    public OrchestrationLeaseDto Renew(string runId, OrchestrationLeaseRenewRequest request)
    {
        lock (_gate)
        {
            var item = Required(runId);
            var lease = ActiveLease(item, request.EngineId, request.InstanceId,
                request.LeaseId, request.Fence);
            lease = lease with { ExpiresAt = _time.GetUtcNow().UtcDateTime.AddSeconds(Ttl(request.RequestedTtlSeconds)) };
            item = item with { Lease = lease };
            Save(item);
            _runs[runId] = item;
            return lease;
        }
    }

    public OrchestrationRunDto Release(string runId, ReleaseOrchestrationLeaseRequest request)
    {
        lock (_gate)
        {
            var item = Required(runId);
            _ = ActiveLease(item, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            var now = _time.GetUtcNow().UtcDateTime;
            item = item with { Lease = item.Lease! with { Status = "released" },
                Run = item.Run with { Status = "pending", UpdatedAt = now } };
            Save(item);
            _runs[runId] = item;
            return item.Run;
        }
    }

    public async Task<OrchestrationRunDto> CompleteAsync(string runId,
        CompleteOrchestrationStageRequest request, CancellationToken ct)
    {
        var gate = _completionGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await CompleteCoreAsync(runId, request, ct); }
        finally { gate.Release(); }
    }

    private async Task<OrchestrationRunDto> CompleteCoreAsync(string runId,
        CompleteOrchestrationStageRequest request, CancellationToken ct)
    {
        MonolithOrchestrationOccurrence item;
        lock (_gate)
        {
            item = Required(runId);
            if (item.CompletionKey == request.IdempotencyKey)
            {
                if (item.Completion is null || item.Completion.Stage != request.Stage
                    || item.Completion.Action != request.Action || item.Completion.OutputJson != request.OutputJson)
                    throw new InvalidOperationException("orchestration-idempotency-conflict");
                if (item.Run.Status == "completed") return item.Run;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                    throw new ArgumentException("Idempotency key is required.");
                if (_runs.Values.Any(other => other.Run.RunId != runId
                    && other.CompletionKey == request.IdempotencyKey))
                    throw new InvalidOperationException("orchestration-idempotency-conflict");
                _ = JsonDocument.Parse(request.OutputJson);
                _ = ActiveLease(item, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
                if (item.Run.CurrentStage != request.Stage)
                    throw new InvalidOperationException("orchestration-stage-mismatch");
                item = item with { CompletionKey = request.IdempotencyKey, Completion = request,
                    Run = item.Run with { Status = "applying", UpdatedAt = _time.GetUtcNow().UtcDateTime } };
                Save(item);
                _runs[runId] = item;
            }
        }
        await _applier.ApplyAsync(item, request, ct);
        lock (_gate)
        {
            item = Required(runId);
            if (item.Run.Status == "completed") return item.Run;
            var now = _time.GetUtcNow().UtcDateTime;
            var result = new OrchestrationStageResultDto(1, request.Stage, request.Action,
                request.OutputJson, now);
            item = item with { Lease = item.Lease! with { Status = "settled" },
                Run = item.Run with { Status = "completed", UpdatedAt = now,
                    CompletedAt = now, StageResults = [result] } };
            Save(item);
            _runs[runId] = item;
            return item.Run;
        }
    }

    public async Task RecoverApplyingAsync(CancellationToken ct)
    {
        MonolithOrchestrationOccurrence[] pending;
        lock (_gate) pending = _runs.Values.Where(item => item.Run.Status == "applying"
            && item.Completion is not null).ToArray();
        var failures = new List<Exception>();
        foreach (var item in pending)
        {
            try { await CompleteAsync(item.Run.RunId, item.Completion!, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException(
            "One or more orchestration decisions still need application.", failures);
    }

    private void ExpireLeases()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var item in _runs.Values.Where(value => value.Run.Status == "leased"
                     && value.Lease is { ExpiresAt: var expiry } && expiry <= now).ToArray())
        {
            var expired = item with { Lease = item.Lease! with { Status = "expired" },
                Run = item.Run with { Status = "pending", UpdatedAt = now } };
            Save(expired);
            _runs[item.Run.RunId] = expired;
        }
    }

    private MonolithOrchestrationOccurrence Required(string runId)
        => _runs.GetValueOrDefault(runId) ?? throw new KeyNotFoundException("Orchestration run was not found.");

    private OrchestrationLeaseDto ActiveLease(MonolithOrchestrationOccurrence item,
        string engineId, string instanceId, string leaseId, long fence)
    {
        var lease = item.Lease;
        if (lease is null || lease.LeaseId != leaseId || lease.Fence != fence
            || lease.EngineId != engineId || lease.InstanceId != instanceId)
            throw new InvalidOperationException("orchestration-stale-fence");
        if (item.Run.Status != "leased" || lease.Status != "active"
            || lease.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
            throw new InvalidOperationException("orchestration-lease-expired");
        return lease;
    }

    private static int Ttl(int seconds) => Math.Clamp(seconds, 10, 600);
    private static void ValidateIdentity(string engineId, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
    }

    private void Save(MonolithOrchestrationOccurrence occurrence)
    {
        if (_directory is null) return;
        var path = Path.Combine(_directory, occurrence.Run.RunId + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, occurrence, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
