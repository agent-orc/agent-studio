using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.OrchestrationDecisions;

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
    string? Profile = null,
    bool Shadow = false,
    OrchestrationDecisionReceipt? MonolithReceipt = null,
    OrchestrationDecisionReceipt? EngineReceipt = null,
    string? CancellationReason = null,
    OrchestrationDecisionReceipt? MonolithIntent = null,
    string? MonolithIntentTarget = null,
    string? ShadowResolution = null,
    long CreationSequence = 0,
    string? SupersededInState = null);

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
    private readonly OrchestrationRolloutStore? _rollout;
    private readonly Dictionary<string, MonolithOrchestrationOccurrence> _runs = new(StringComparer.Ordinal);
    private long _nextCreationSequence;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _completionGates = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;
    private readonly Dictionary<string, int> _applicationFailures = new(StringComparer.Ordinal);

    public MonolithOrchestrationStore(IConfiguration configuration, TimeProvider time,
        IMonolithOrchestrationApplier applier, OrchestrationRolloutStore rollout,
        ILogger<MonolithOrchestrationStore> logger)
        : this(string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            ? null
            : Path.Combine(configuration["TaskRepository"]!, ".orchestration-occurrences"), time, applier, rollout,
            logger) { }

    /// <param name="directory">
    /// Without a TaskRepository the store keeps occurrences in memory only, like
    /// AttemptAuthorityService, so hosts without a workspace still start.
    /// </param>
    public MonolithOrchestrationStore(string? directory, TimeProvider time, IMonolithOrchestrationApplier applier,
        OrchestrationRolloutStore? rollout = null, ILogger? logger = null)
    {
        _directory = directory;
        _time = time;
        _applier = applier;
        _rollout = rollout;
        _logger = logger;
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "orch_*.json"))
        {
            var occurrence = JsonSerializer.Deserialize<MonolithOrchestrationOccurrence>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Orchestration occurrence is empty: {file}");
            _runs.Add(occurrence.Run.RunId, occurrence);
            _nextCreationSequence = Math.Max(_nextCreationSequence, occurrence.CreationSequence);
        }
    }

    public OrchestrationRunDto Create(string projectId, string taskId,
        OrchestrationStage stage, string payloadJson, string idempotencyKey,
        string expectedSourceState, string authorityAttemptId, long authorityFence, long authorityEpoch,
        bool claimable = true, string? profile = null, bool shadow = false,
        string? monolithAction = null, string? monolithReason = null)
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
                    || existing.Run.CurrentStage != stage
                    || existing.ExpectedSourceState != expectedSourceState
                    || existing.AuthorityAttemptId != authorityAttemptId
                    || existing.Run.Status != "cancelled" &&
                       (existing.Run.PayloadJson != payloadJson
                        || existing.AuthorityFence != authorityFence
                        || existing.AuthorityEpoch != authorityEpoch))
                    throw new InvalidOperationException("orchestration-idempotency-conflict");
                if (existing.Run.Status == "cancelled")
                {
                    var resumedAt = _time.GetUtcNow().UtcDateTime;
                    existing = existing with
                    {
                        Run = existing.Run with { Status = "pending", PayloadJson = payloadJson,
                            UpdatedAt = resumedAt, CompletedAt = null },
                        AuthorityFence = authorityFence,
                        AuthorityEpoch = authorityEpoch,
                        Claimable = claimable,
                        Profile = profile,
                        Shadow = shadow,
                        MonolithReceipt = monolithAction is null ? null : OrchestrationRolloutStore.Receipt(
                            existing.Run.RunId, idempotencyKey, payloadJson, monolithAction,
                            monolithReason ?? string.Empty,
                            $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith"),
                        EngineReceipt = null,
                        CancellationReason = null,
                        SupersededInState = null,
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
                Claimable: claimable, Profile: profile, Shadow: shadow,
                CreationSequence: ++_nextCreationSequence,
                MonolithReceipt: monolithAction is null ? null : OrchestrationRolloutStore.Receipt(
                    run.RunId, idempotencyKey, payloadJson, monolithAction,
                    monolithReason ?? string.Empty,
                    $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith"));
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

    /// <summary>
    /// AGT-3060: one AI Operator occurrence per card, decider and subject. A
    /// periodic decider gets a new key per period bucket; an event-triggered
    /// one (null <paramref name="period"/>) keys on its subject alone. An active
    /// occurrence of the same decider for the card is returned instead.
    /// </summary>
    public OrchestrationRunDto CreateOperatorDecision(string projectId, string taskId, string decider,
        string payloadJson, string subjectKey, string expectedSourceState, string authorityAttemptId,
        long authorityFence, long authorityEpoch, TimeSpan? period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decider);
        if (period is { } value && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        var profile = OperatorDecisionProfile(decider);
        lock (_gate)
        {
            var active = _runs.Values.FirstOrDefault(item => item.Run.ProjectId == projectId
                && item.Run.TaskId == taskId && item.Run.CurrentStage == OrchestrationStage.OperatorDecision
                && item.Profile == profile && item.Run.Status is "pending" or "leased" or "applying");
            if (active is not null) return active.Run;
            var key = period is { } bucketPeriod
                ? $"operator:{decider}:{projectId}:{taskId}:{subjectKey}:{_time.GetUtcNow().Ticks / bucketPeriod.Ticks}"
                : $"operator:{decider}:{projectId}:{taskId}:{subjectKey}";
            return Create(projectId, taskId, OrchestrationStage.OperatorDecision, payloadJson, key,
                expectedSourceState, authorityAttemptId, authorityFence, authorityEpoch, profile: profile);
        }
    }

    public static string OperatorDecisionProfile(string decider) => "AiOperator:" + decider;

    /// <summary>One active bounded review decision per card and source subject.</summary>
    public OrchestrationRunDto CreateReviewInputDecision(string projectId, string taskId,
        string payloadJson, string subjectKey, string expectedSourceState,
        string authorityAttemptId, long authorityFence = 0, long authorityEpoch = 0)
    {
        lock (_gate)
        {
            var active = _runs.Values.FirstOrDefault(item => item.Run.ProjectId == projectId
                && item.Run.TaskId == taskId
                && item.Run.CurrentStage == OrchestrationStage.ReviewInputDecision
                && item.Run.Status is "pending" or "leased" or "applying");
            if (active is not null) return active.Run;
            return Create(projectId, taskId, OrchestrationStage.ReviewInputDecision,
                payloadJson, $"review-input:{projectId}:{taskId}:{subjectKey}",
                expectedSourceState, authorityAttemptId, authorityFence, authorityEpoch,
                claimable: false, profile: "ReviewDecision");
        }
    }

    public OrchestrationRunDto? Get(string runId)
    {
        lock (_gate) return _runs.GetValueOrDefault(runId)?.Run;
    }

    public MonolithOrchestrationOccurrence? GetOccurrence(string runId)
    {
        lock (_gate) return _runs.GetValueOrDefault(runId);
    }

    public OrchestrationRunDto? GetByIdempotencyKey(string key)
    {
        lock (_gate) return _runs.Values.FirstOrDefault(item => item.IdempotencyKey == key)?.Run;
    }

    /// <summary>
    /// Saves the monolith decision before the monolith applies it. The intent
    /// counts only once the card reached <paramref name="targetState"/>, so a
    /// process that stops after the lane move still leaves a comparable outcome.
    /// </summary>
    public void RecordMonolithIntent(string runId, string action, string reason, string targetState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetState);
        lock (_gate)
        {
            var item = Required(runId);
            if (!item.Shadow) throw new InvalidOperationException("orchestration-not-shadow");
            if (item.MonolithReceipt is not null || item.ShadowResolution is not null) return;
            item = item with { MonolithIntent = MonolithDecisionReceipt(item, action, reason),
                MonolithIntentTarget = targetState };
            Save(item);
            _runs[runId] = item;
        }
    }

    /// <summary>A monolith transition that was not applied leaves no comparable outcome.</summary>
    public void WithdrawMonolithIntent(string runId)
    {
        lock (_gate)
        {
            var item = Required(runId);
            if (item.MonolithIntent is null) return;
            item = item with { MonolithIntent = null, MonolithIntentTarget = null };
            Save(item);
            _runs[runId] = item;
        }
    }

    public void RecordMonolithDecision(string runId, string action, string reason)
    {
        lock (_gate)
        {
            var item = Required(runId);
            if (!item.Shadow) throw new InvalidOperationException("orchestration-not-shadow");
            if (item.ShadowResolution is not null) return;
            if (item.MonolithReceipt is null)
            {
                item = item with { MonolithReceipt = item.MonolithIntent ?? MonolithDecisionReceipt(item, action, reason),
                    MonolithIntent = null, MonolithIntentTarget = null };
                Save(item);
                _runs[runId] = item;
            }
            // A replay compares too: the receipt may have been saved by a call
            // that stopped before its comparison. Compare counts an occurrence once.
            CompareShadow(item);
        }
    }

    /// <summary>
    /// Compares every shadow occurrence that holds both receipts. A comparison
    /// lost between saving a receipt and comparing it is recovered here, so a
    /// mismatch always resets the switch streak.
    /// </summary>
    public void RecoverShadowComparisons()
    {
        lock (_gate)
            foreach (var item in _runs.Values.Where(value => value.Shadow)
                         .OrderBy(value => value.CreationSequence).ThenBy(value => value.Run.CreatedAt)
                         .ThenBy(value => value.Run.RunId).ToArray())
                CompareShadow(item);
    }

    /// <summary>Shadow occurrences whose monolith outcome is not recorded yet.</summary>
    public IReadOnlyList<MonolithOrchestrationOccurrence> UnrecordedShadowOutcomes()
    {
        lock (_gate) return _runs.Values.Where(item => item.Shadow && item.MonolithReceipt is null
            && item.ShadowResolution is null && item.Run.Status != "cancelled").ToArray();
    }

    /// <summary>
    /// Resolves a shadow occurrence without a monolith receipt from the card's
    /// current lane. A card in its source lane is still deciding. A saved intent
    /// whose target lane the card reached was applied and is compared. Any other
    /// occurrence, including one whose card is gone, cannot be compared and
    /// resets the switch streak, so the threshold always means consecutive
    /// verified matches.
    /// </summary>
    public void ResolveShadowOutcome(string runId, string? currentState)
    {
        lock (_gate)
        {
            var item = Required(runId);
            if (!item.Shadow || item.MonolithReceipt is not null || item.ShadowResolution is not null
                || item.Run.Status == "cancelled" || currentState == item.ExpectedSourceState)
                return;
            if (item.MonolithIntent is not null && currentState == item.MonolithIntentTarget)
            {
                item = item with { MonolithReceipt = item.MonolithIntent,
                    MonolithIntent = null, MonolithIntentTarget = null };
                Save(item);
                _runs[runId] = item;
                CompareShadow(item);
                return;
            }
            // The streak reset comes first: it is idempotent, so a stop before
            // the save repeats it instead of losing it.
            if (item.Profile is not null) _rollout?.Discount(item.Profile, runId, item.Run.CreatedAt,
                item.CreationSequence);
            item = item with { ShadowResolution = "monolith-outcome-unverified",
                MonolithIntent = null, MonolithIntentTarget = null };
            Save(item);
            _runs[runId] = item;
        }
    }

    /// <summary>
    /// Switches a rollout profile. The engine takes over only when every earlier
    /// shadow occurrence of the profile is compared or resolved; a ready streak
    /// does not account for an occurrence whose comparison is still open.
    /// </summary>
    public OrchestrationProfileStatus SwitchProfile(string profile, string target, string actor, string reason)
    {
        var rollout = _rollout ?? throw new InvalidOperationException("orchestration-rollout-unavailable");
        lock (_gate)
        {
            if (string.Equals(target, "engine", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in _runs.Values.Where(value => value.Shadow)
                             .OrderBy(value => value.CreationSequence).ThenBy(value => value.Run.CreatedAt)
                             .ThenBy(value => value.Run.RunId).ToArray())
                    CompareShadow(item);
                if (_runs.Values.Any(item => item.Shadow && item.Profile == profile
                        && item.Run.Status != "cancelled" && item.ShadowResolution is null
                        && (item.MonolithReceipt is null || item.EngineReceipt is null)))
                    throw new InvalidOperationException("orchestration-profile-shadow-unresolved");
            }
            return rollout.Switch(profile, target, actor, reason);
        }
    }

    private void CompareShadow(MonolithOrchestrationOccurrence item)
    {
        if (item.MonolithReceipt is not null && item.EngineReceipt is not null && item.Profile is not null)
            _rollout?.Compare(item.Profile, item.MonolithReceipt, item.EngineReceipt, item.Run.CreatedAt,
                item.CreationSequence);
    }

    private static OrchestrationDecisionReceipt MonolithDecisionReceipt(MonolithOrchestrationOccurrence item,
        string action, string reason)
        => OrchestrationRolloutStore.Receipt(item.Run.RunId, item.IdempotencyKey, item.Run.PayloadJson,
            action, reason, $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:monolith");

    public IReadOnlyList<OrchestrationRunDto> List(string? projectId, string? status)
    {
        lock (_gate) return _runs.Values.Select(item => item.Run)
            .Where(run => (projectId is null || run.ProjectId == projectId)
                && (status is null || run.Status == status))
            .OrderBy(run => run.CreatedAt).ThenBy(run => run.RunId).ToArray();
    }

    public IReadOnlyList<MonolithOrchestrationOccurrence> WaitingOccurrences()
    {
        lock (_gate) return _runs.Values.Where(item => item.Claimable && !item.Shadow
            && item.Run.Status is ("pending" or "leased" or "applying")).ToArray();
    }

    /// <summary>
    /// Unclaimable preparations, including cancelled ones a later settlement
    /// can still activate. A superseded occurrence's card left its lane, so it
    /// is never activated again.
    /// </summary>
    public IReadOnlyList<MonolithOrchestrationOccurrence> AwaitingActivation()
    {
        lock (_gate) return _runs.Values.Where(item => !item.Claimable
            && item.Run.Status is ("pending" or "cancelled") && !IsSuperseded(item)).ToArray();
    }

    public static bool IsSuperseded(MonolithOrchestrationOccurrence occurrence)
        => occurrence.Run.Status == "cancelled"
            && occurrence.CancellationReason?.StartsWith(EngineDecisionSupersessionCauses.Prefix,
                StringComparison.Ordinal) == true;

    /// <summary>
    /// The newest occurrence of <paramref name="profile"/> for each card. A
    /// cancelled preparation never applied a decision and is not counted.
    /// </summary>
    public IReadOnlyList<MonolithOrchestrationOccurrence> NewestPerTask(string profile)
    {
        lock (_gate) return _runs.Values
            .Where(item => item.Profile == profile && item.Run.Status != "cancelled")
            .OrderBy(item => item.CreationSequence).ThenBy(item => item.Run.CreatedAt)
            .ThenBy(item => item.Run.RunId, StringComparer.Ordinal)
            .GroupBy(item => (item.Run.ProjectId, item.Run.TaskId))
            .Select(group => group.Last())
            .ToArray();
    }

    public void Activate(string runId)
    {
        lock (_gate)
        {
            var item = Required(runId);
            if (item.Claimable) return;
            if (item.Run.Status is not ("pending" or "cancelled") || IsSuperseded(item))
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

    /// <summary>
    /// AGT-3281: settles an open occurrence that can no longer apply because
    /// its card left the expected lane. Returns null while its application
    /// runs (that application settles it) or when it is no longer open.
    /// </summary>
    public MonolithOrchestrationOccurrence? TrySupersede(string runId, string cause, string? observedState)
    {
        var gate = _completionGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return null;
        try
        {
            lock (_gate) return SupersedeCore(runId, cause, observedState);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Holds the completion gate of every open lane decision of one card, so
    /// none applies while an operator moves the card. Dispose releases them.
    /// </summary>
    public LaneDecisionHold HoldOpenLaneDecisions(string projectId, IEnumerable<string?> taskIds)
    {
        var ids = taskIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        string[] candidates;
        lock (_gate) candidates = _runs.Values.Where(item => item.Run.ProjectId == projectId
                && ids.Contains(item.Run.TaskId)
                && EngineDecisionStandingPolicy.HeldByOperatorMove(item.Run.CurrentStage, item.Run.Status, item.Shadow))
            .Select(item => item.Run.RunId).ToArray();
        var held = new List<(string RunId, SemaphoreSlim Gate)>();
        foreach (var runId in candidates)
        {
            var gate = _completionGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
            if (gate.Wait(0)) held.Add((runId, gate));
        }
        return new LaneDecisionHold(this, held);
    }

    public sealed class LaneDecisionHold : IDisposable
    {
        private readonly MonolithOrchestrationStore _store;
        private readonly List<(string RunId, SemaphoreSlim Gate)> _held;
        private bool _released;

        internal LaneDecisionHold(MonolithOrchestrationStore store, List<(string RunId, SemaphoreSlim Gate)> held)
        {
            _store = store;
            _held = held;
        }

        public IReadOnlyList<string> RunIds => _held.Select(item => item.RunId).ToArray();

        /// <summary>Settles every held occurrence that is still open; returns the superseded ones.</summary>
        public IReadOnlyList<MonolithOrchestrationOccurrence> Supersede(string cause, string observedState)
        {
            if (_released) throw new ObjectDisposedException(nameof(LaneDecisionHold));
            lock (_store._gate)
                return _held.Select(item => _store.SupersedeCore(item.RunId, cause, observedState))
                    .OfType<MonolithOrchestrationOccurrence>().ToArray();
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            foreach (var (_, gate) in _held) gate.Release();
        }
    }

    /// <summary>The caller holds the occurrence's completion gate and <see cref="_gate"/>.</summary>
    private MonolithOrchestrationOccurrence? SupersedeCore(string runId, string cause, string? observedState)
    {
        var item = Required(runId);
        if (!EngineDecisionStandingPolicy.IsOpen(item.Run.Status)) return null;
        var now = _time.GetUtcNow().UtcDateTime;
        item = item with
        {
            CancellationReason = EngineDecisionSupersessionCauses.Prefix + cause,
            SupersededInState = observedState,
            Lease = item.Lease is null ? null : item.Lease with { Status = "superseded" },
            Run = item.Run with { Status = "cancelled", UpdatedAt = now, CompletedAt = now },
        };
        Save(item);
        _runs[runId] = item;
        _logger?.LogInformation(
            "orchestration-occurrence-superseded run={RunId} task={TaskId} profile={Profile} cause={Cause} expected={Expected} observed={Observed}",
            runId, item.Run.TaskId, item.Profile, cause, item.ExpectedSourceState, observedState ?? "absent");
        return item;
    }

    public OrchestrationClaimResponse Claim(OrchestrationClaimRequest request)
    {
        ValidateIdentity(request.EngineId, request.InstanceId);
        if (request.SupportedStages.Count == 0) throw new ArgumentException("Supported stages are required.");
        lock (_gate)
        {
            ExpireLeases();
            var item = _runs.Values.Where(value => value.Run.Status == "pending" && value.Claimable
                    && (value.Shadow || _rollout?.IsRolledBack(value.Profile) != true)
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

    public OrchestrationClaimResponse ClaimRolledBack(string runId)
    {
        lock (_gate)
        {
            ExpireLeases();
            var item = Required(runId);
            if (item.Run.Status != "pending" || !item.Claimable
                || item.Shadow
                || _rollout?.IsRolledBack(item.Profile) != true)
                return new OrchestrationClaimResponse("empty");
            var now = _time.GetUtcNow().UtcDateTime;
            var lease = new OrchestrationLeaseDto($"olease_{Guid.NewGuid():N}", runId,
                "engine_rollback", "monolith", checked(item.LastFence + 1), now,
                now.AddMinutes(2), "active");
            item = item with { Lease = lease, LastFence = lease.Fence,
                Run = item.Run with { Status = "leased", UpdatedAt = now } };
            Save(item);
            _runs[runId] = item;
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
                // A superseded occurrence is settled; its decision never applies.
                if (item.Run.Status is "completed" or "cancelled") return item.Run;
                if (item.EngineReceipt is null)
                {
                    item = item with { EngineReceipt = EngineReceipt(item, request) };
                    Save(item);
                    _runs[runId] = item;
                }
            }
            else
            {
                if (!item.Shadow && _rollout?.IsRolledBack(item.Profile) == true
                    && request.EngineId != "engine_rollback")
                    throw new InvalidOperationException("orchestration-profile-rolled-back");
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
                    EngineReceipt = EngineReceipt(item, request),
                    Run = item.Run with { Status = "applying", UpdatedAt = _time.GetUtcNow().UtcDateTime } };
                Save(item);
                _runs[runId] = item;
            }
        }
        if (item.Shadow)
        {
            if (item.Profile is null || item.EngineReceipt is null)
                throw new InvalidDataException("Shadow occurrence has no engine decision receipt.");
            if (item.MonolithReceipt is not null)
                _rollout?.Compare(item.Profile, item.MonolithReceipt, item.EngineReceipt, item.Run.CreatedAt,
                    item.CreationSequence);
        }
        else
        {
            try { await _applier.ApplyAsync(item, request, ct); }
            catch (OrchestrationOccurrenceSupersededException superseded)
            {
                lock (_gate)
                    return (SupersedeCore(runId, superseded.Cause, superseded.ObservedState)
                        ?? Required(runId)).Run;
            }
        }
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

    private static OrchestrationDecisionReceipt EngineReceipt(MonolithOrchestrationOccurrence item,
        CompleteOrchestrationStageRequest request)
    {
        using var output = JsonDocument.Parse(request.OutputJson);
        var root = output.RootElement;
        var transition = root.TryGetProperty("transition", out var value)
            && value.ValueKind == JsonValueKind.Object ? value : root;
        var typed = transition.ValueKind == JsonValueKind.Object
            && (transition.TryGetProperty("action", out _)
                || transition.TryGetProperty("Action", out _))
            ? transition.Deserialize<TransitionDecision>(Json) : null;
        var action = typed is not null ? typed.Action.ToString()
            : root.TryGetProperty("decision", out var stageDecision) ? stageDecision.ToString()
            : request.Action.ToString();
        var reason = typed?.Reason ?? string.Empty;
        var decider = root.TryGetProperty("decider", out var version)
            ? version.GetString() ?? $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:engine"
            : $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}:engine";
        return OrchestrationRolloutStore.Receipt(item.Run.RunId, item.IdempotencyKey,
            item.Run.PayloadJson, action, reason, decider);
    }

    /// <summary>
    /// Applies every decision that stopped during application. A decision
    /// whose card left its lane is settled as superseded and not retried
    /// (AGT-3281). Any other failure is transient: it is logged on one line
    /// for its occurrence, with the stack on its first failure only, and
    /// retried on the next pass.
    /// </summary>
    public async Task<ApplyingRecovery> RecoverApplyingAsync(CancellationToken ct)
    {
        MonolithOrchestrationOccurrence[] pending;
        lock (_gate) pending = _runs.Values.Where(item => item.Run.Status == "applying"
            && item.Completion is not null).ToArray();
        int applied = 0, superseded = 0, retrying = 0;
        foreach (var item in pending)
        {
            var runId = item.Run.RunId;
            try
            {
                var run = await CompleteAsync(runId, item.Completion!, ct);
                if (run.Status == "completed") applied++;
                else superseded++;
                lock (_applicationFailures) _applicationFailures.Remove(runId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                retrying++;
                int failures;
                lock (_applicationFailures)
                    _applicationFailures[runId] = failures = _applicationFailures.GetValueOrDefault(runId) + 1;
                if (failures == 1)
                    _logger?.LogWarning(exception,
                        "orchestration-application-retry-pending run={RunId} task={TaskId} profile={Profile} failures={Failures} error={Error}",
                        runId, item.Run.TaskId, item.Profile, failures, exception.Message);
                else
                    _logger?.LogWarning(
                        "orchestration-application-retry-pending run={RunId} task={TaskId} profile={Profile} failures={Failures} error={Error}",
                        runId, item.Run.TaskId, item.Profile, failures, exception.Message);
            }
        }
        return new ApplyingRecovery(applied, superseded, retrying);
    }

    public sealed record ApplyingRecovery(int Applied, int Superseded, int Retrying);

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
