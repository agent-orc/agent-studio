using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Clients;
using AgentStudio.Operations.Contracts;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Watcher;

/// <summary>
/// Coding host admission through the identity store that the claim path reads
/// on every poll. A host is addressed by its client identity id only: display
/// names are not unique and are never resolved.
/// </summary>
public sealed class ClientIdentityAdmissionControl(ClientIdentityStore clients, HostTelemetryStore telemetry)
    : IHostAdmissionControl
{
    public HostAdmissionReading? Read(string clientId, double restoreFraction, DateTime nowUtc)
    {
        var identity = Find(clientId);
        if (identity is null) return null;
        var target = identity.RunnerTargetLoadPercent ?? 80;
        var points = telemetry.Query(identity.Id, "1h", nowUtc).Points
            .Where(point => point.Timestamp <= nowUtc).OrderBy(point => point.Timestamp).ToArray();
        var last = points.LastOrDefault();
        double? PerCore(HostTelemetrySample sample) => sample.CpuCores > 0 && sample.Load1 is { } load
            ? load / sample.CpuCores : null;
        DateTime? calmSince = null;
        DateTime? previous = null;
        foreach (var point in points.Reverse())
        {
            if (PerCore(point) is not { } load || load * 100 >= target * restoreFraction
                || previous - point.Timestamp > TimeSpan.FromMinutes(5)) break;
            calmSince = point.Timestamp;
            previous = point.Timestamp;
        }
        double? memory = last?.MemoryTotalBytes is > 0
            ? 100.0 * last.MemoryUsedBytes.GetValueOrDefault() / last.MemoryTotalBytes.Value : null;
        return new HostAdmissionReading(identity.Id, identity.DisplayName,
            identity.RunnerDesiredMaxParallelism ?? identity.RunnerEffectiveMaxParallelism,
            last?.CpuCores ?? 0, last?.Load1, last?.CpuPercent, memory, target, last?.ActiveSlots ?? 0,
            last is null ? null : PerCore(last), calmSince, last?.Timestamp,
            last is null ? null : (last.SwapInBytesPerSecond ?? 0) + (last.SwapOutBytesPerSecond ?? 0));
    }

    public int? SetAdmission(string clientId, int maxParallelism)
    {
        var identity = Find(clientId);
        if (identity is null) return null;
        clients.SetRunnerCapacity(identity.Id, maxParallelism, null, null);
        return clients.Find(identity.Id)?.RunnerDesiredMaxParallelism;
    }

    public bool TrySetAdmission(string clientId, int expected, int replacement)
        => Find(clientId) is { } identity
            && clients.TrySetRunnerCapacity(identity.Id, expected, replacement);

    private ClientIdentity? Find(string clientId)
        => clients.Find(clientId) is { } identity && identity.Kind != ClientIdentityKind.Retired ? identity : null;
}

/// <summary>
/// What a review daemon or the remote gate worker runs now. A review host's
/// busy slots come from its last telemetry and its claimable items are the
/// pending review attempts it can claim with its admitted capabilities. The
/// gate worker claims nothing itself (batch members wait for the batch plan);
/// it runs work while this Studio holds a gate slot, a batch is active or a
/// per-task gate is in flight.
/// </summary>
public sealed class ServiceWorkloadReader(V1ReviewExecutorRegistry registry, AttemptAuthorityService attempts,
    BatchGateStore gates, GateSlotUsageTracker gateSlots, PullGateStore pullGates, IConfiguration configuration) : IServiceWorkload
{
    /// <summary>How long the gate host's own slot count, reported to a waiting run, stays current.</summary>
    public static readonly TimeSpan RemoteSlotsFresh = TimeSpan.FromMinutes(10);

    public IDisposable? TryFenceClaims(string host, string role)
    {
        if (role == "review") return attempts.TryFenceReviewHost(host);
        if (role != "gate") return null;
        var pull = pullGates.TryFenceHostClaims(host);
        if (pull is null) return null;
        if (!string.Equals(configuration["RemoteGate:SshHost"], host, StringComparison.OrdinalIgnoreCase))
            return pull;
        var ssh = gateSlots.TryFenceDispatch(requireFreshRemote: true);
        if (ssh is not null) return new CombinedRestartFence((IRestartClaimFence)pull, ssh);
        pull.Dispose();
        return null;
    }

    private sealed class CombinedRestartFence(IRestartClaimFence pull, IRestartClaimFence ssh) : IRestartClaimFence
    {
        public void Retain() { pull.Retain(); ssh.Retain(); }
        public void ConfirmTerminal() { pull.ConfirmTerminal(); ssh.ConfirmTerminal(); }
        public void Dispose() { ssh.Dispose(); pull.Dispose(); }
    }

    public bool HasRestartFence(string host, string role)
        => role == "review" ? attempts.HasReviewRestartFence(host)
            : role == "gate" && pullGates.HasRestartFence(host);

    public void ReleaseRestartFence(string host, string role)
    {
        if (role == "review") attempts.ReleaseReviewRestartFence(host);
        else if (role == "gate")
        {
            pullGates.ReleaseRestartFence(host);
            if (!pullGates.HasRestartFence(host)) gateSlots.ReleaseRestartFence();
        }
    }

    public ServiceWorkloadReading? Read(string host, string role, DateTime nowUtc)
    {
        switch (role)
        {
            case "review":
                var snapshots = registry.ListCapabilitySnapshots().Where(item => item.HostId == host).ToArray();
                if (snapshots.Length == 0) return null;
                int? busy = snapshots.All(item => item.Telemetry is { } sample
                    && sample.ObservedAt <= nowUtc
                    && nowUtc - sample.ObservedAt <= TimeSpan.FromMinutes(10))
                    ? snapshots.Sum(item => item.Telemetry!.ActiveSlots) : null;
                // The slot advertisement can say zero after an attempt acquired a lease.
                // Authority is the current owner of running review work.
                var leased = attempts.ListActiveReviewAttempts().Any(attempt =>
                    attempt.Lease?.HostId == host);
                var claimable = attempts.ListPendingReviewAttempts()
                    .Count(attempt => snapshots.Any(snapshot => OperationalStallProbe.CanClaim(registry, snapshot, attempt)));
                return new ServiceWorkloadReading(busy, leased, claimable);
            case "gate":
                if (pullGates.Workload(host) is { } pull)
                {
                    var active = BatchGateInterventionControl.ActiveBatchProjects(gates).Count > 0
                        || gates.ListPending().Any(item => item.Subject.ActivePerTaskGate);
                    var sshDispatch = string.Equals(configuration["RemoteGate:SshHost"], host,
                        StringComparison.OrdinalIgnoreCase) ? gateSlots.Dispatching : 0;
                    var sshRemote = string.Equals(configuration["RemoteGate:SshHost"], host,
                        StringComparison.OrdinalIgnoreCase)
                        ? gateSlots.RemoteSnapshot(RemoteSlotsFresh) : null;
                    int? gateBusy = sshRemote is null && string.Equals(configuration["RemoteGate:SshHost"], host,
                        StringComparison.OrdinalIgnoreCase) ? null
                        : Math.Max(pull.Busy + sshDispatch, sshRemote?.Busy ?? 0);
                    return new ServiceWorkloadReading(gateBusy, active, pull.Queued,
                        new StallGateSlotFacts(pullGates.Hosts().First(item => item.Id == host).Slots,
                            pull.Busy + sshDispatch, sshRemote?.Busy, active));
                }
                var gateHost = configuration["RemoteGate:SshHost"]?.Trim();
                if (string.IsNullOrWhiteSpace(gateHost) || !string.Equals(gateHost, host, StringComparison.OrdinalIgnoreCase))
                    return null;
                var own = gateSlots.Snapshot();
                var running = BatchGateInterventionControl.ActiveBatchProjects(gates).Count > 0
                    || gates.ListPending().Any(item => item.Subject.ActivePerTaskGate);
                var remote = gateSlots.RemoteSnapshot(RemoteSlotsFresh);
                return new ServiceWorkloadReading(own.Busy, running, 0,
                    new StallGateSlotFacts(remote?.Total ?? own.Total, own.Busy, remote?.Busy, running));
            default:
                return null;
        }
    }
}

/// <summary>
/// Restarts through the Operations Server (<c>service.restart</c>). Configuration:
/// <c>Operations:Url</c>, <c>Operations:ServiceTokenFile</c> (a principal with the
/// <c>service.restart</c>, <c>maintenance.execute</c> and <c>operations.read</c> scopes),
/// <c>Operations:Agents:&lt;host&gt;</c> (the host's agent id) and optionally
/// <c>Operations:RestartableServices:&lt;host&gt;</c> (comma-separated allowlist mirror).
/// </summary>
public sealed class OperationsServerRestartControl(
    IHttpClientFactory http,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<OperationsServerRestartControl> logger) : IServiceRestartControl
{
    public const string HttpClientName = "operations-server";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    public async Task<bool?> IsTerminalAsync(string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["Operations:Url"])
            || string.IsNullOrWhiteSpace(configuration["Operations:ServiceTokenFile"])) return false;
        try
        {
            using var client = Client();
            var key = OperationsProtocol.Digest(idempotencyKey)[..48];
            using var response = await client.GetAsync($"api/operations/v1/attempts/by-key/{key}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return false;
            var attempt = await response.Content.ReadFromJsonAsync<OperationAttempt>(OperationsProtocol.Json, ct);
            return attempt?.State is "succeeded" or "failed" or "cancelled" or "timed-out";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or UriFormatException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "stall-intervention-restart-recovery-read-failed");
            return false;
        }
    }

    public bool CanRestart(string host, string service)
    {
        if (string.IsNullOrWhiteSpace(configuration["Operations:Url"])
            || string.IsNullOrWhiteSpace(configuration["Operations:ServiceTokenFile"])
            || string.IsNullOrWhiteSpace(configuration[$"Operations:Agents:{host}"])
            || !OperationCatalogue.ValidServiceName(service)) return false;
        var allowlist = configuration[$"Operations:RestartableServices:{host}"];
        return string.IsNullOrWhiteSpace(allowlist)
            || allowlist.Split(',', StringSplitOptions.TrimEntries).Contains(service, StringComparer.Ordinal);
    }

    public async Task<ServiceRestartOutcome> RestartAsync(string host, string service, string idempotencyKey,
        string reason, CancellationToken ct)
    {
        if (!CanRestart(host, service)) return new(false, $"no Operations Agent may restart {service} on {host}");
        try
        {
            using var client = Client();
            var input = JsonSerializer.SerializeToElement(new { service }, OperationsProtocol.Json);
            var deadline = time.GetUtcNow().AddMinutes(4);
            var command = new OperationCommand(OperationsProtocol.Digest(idempotencyKey)[..48], "ai-operator",
                configuration[$"Operations:Agents:{host}"]!, OperationCatalogue.ServiceRestart.Id,
                OperationCatalogue.ServiceRestart.Version, input, OperationsProtocol.Digest(input), deadline,
                OperationsProtocol.Digest(reason)[..32]);
            using var submitted = await client.PostAsJsonAsync("api/operations/v1/commands", command, OperationsProtocol.Json, ct);
            if (!submitted.IsSuccessStatusCode)
                return new(false, $"the Operations Server refused the restart: {await Error(submitted, ct)}");
            var attempt = await submitted.Content.ReadFromJsonAsync<OperationAttempt>(OperationsProtocol.Json, ct)
                ?? throw new InvalidDataException("The Operations Server returned no attempt.");
            while (attempt.State is "queued" or "running" or "cancelling" && time.GetUtcNow() < deadline)
            {
                await Task.Delay(PollInterval, time, ct);
                using var read = await client.GetAsync($"api/operations/v1/attempts/{attempt.Id}", ct);
                if (!read.IsSuccessStatusCode) return new(false, $"the restart state is unreadable: {await Error(read, ct)}", false);
                attempt = await read.Content.ReadFromJsonAsync<OperationAttempt>(OperationsProtocol.Json, ct) ?? attempt;
            }
            var restarted = attempt.Result?.Output.ValueKind == JsonValueKind.Object
                && attempt.Result.Output.TryGetProperty("restarted", out var flag) && flag.ValueKind == JsonValueKind.True;
            return attempt.State == "succeeded" && restarted
                ? new(true, $"Operations Server attempt {attempt.Id} restarted {service} on {host}")
                : new(false, $"Operations Server attempt {attempt.Id} ended {attempt.State}"
                    + (attempt.Result is null ? string.Empty : $" ({attempt.Result.ExitClassification})"),
                    attempt.State is "succeeded" or "failed" or "cancelled" or "timed-out");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "stall-intervention-restart-failed host={Host} service={Service}", host, service);
            return new(false, $"the Operations Server is unreachable: {exception.Message}", false);
        }
    }

    private HttpClient Client()
    {
        var client = http.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(configuration["Operations:Url"]!.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            File.ReadAllText(configuration["Operations:ServiceTokenFile"]!).Trim());
        return client;
    }

    private static async Task<string> Error(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return $"{(int)response.StatusCode} {(body.Length <= 200 ? body : body[..200])}";
    }
}

/// <summary>
/// Durable per-host provider exclusions (<c>.watcher/provider-route-exclusions.json</c>).
/// The review registry drops an excluded provider's <c>provider-auth:</c> capability,
/// so attempts that need the provider go to the other hosts.
/// </summary>
public sealed class ProviderRouteExclusions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string?> _root;
    private readonly ILogger<ProviderRouteExclusions>? _logger;
    private readonly object _gate = new();
    private Dictionary<string, string>? _entries;

    public ProviderRouteExclusions(IConfiguration configuration, ILogger<ProviderRouteExclusions> logger)
        : this(() => configuration["TaskRepository"], logger) { }

    public ProviderRouteExclusions(Func<string?> workspaceRoot, ILogger<ProviderRouteExclusions>? logger = null)
    {
        _root = workspaceRoot;
        _logger = logger;
    }

    public bool IsExcluded(string? hostId, string runnerId, string provider)
    {
        lock (_gate)
        {
            var entries = Load();
            return entries.Count > 0 && (hostId is not null && entries.ContainsKey(Key(hostId, provider))
                || entries.ContainsKey(Key(runnerId, provider)));
        }
    }

    public void Exclude(string host, string provider, string interventionId)
    {
        lock (_gate)
        {
            var entries = new Dictionary<string, string>(Load(), StringComparer.Ordinal)
            {
                [Key(host, provider)] = interventionId,
            };
            Save(entries);
        }
    }

    public void Include(string host, string provider)
    {
        lock (_gate)
        {
            var entries = new Dictionary<string, string>(Load(), StringComparer.Ordinal);
            if (entries.Remove(Key(host, provider))) Save(entries);
        }
    }

    private static string Key(string host, string provider) => $"{host}|{provider}";

    /// <summary>
    /// The review registry calls this inside its capability path, so an
    /// unreadable or torn file must never throw there: it is logged and read
    /// as empty, and read again on the next call.
    /// </summary>
    private IReadOnlyDictionary<string, string> Load()
    {
        if (_entries is not null) return _entries;
        var path = Path();
        if (path is null || !File.Exists(path)) return _entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json);
            return _entries = new Dictionary<string, string>(read ?? [], StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "provider-route-exclusions-unreadable path={Path}", path);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        var path = Path() ?? throw new InvalidOperationException("TaskRepository is not configured.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        StallInterventionStore.WriteDurably(path, JsonSerializer.Serialize(entries, Json));
        _entries = entries;
    }

    private string? Path()
    {
        var root = _root();
        return string.IsNullOrWhiteSpace(root) ? null
            : System.IO.Path.Combine(root, ".watcher", "provider-route-exclusions.json");
    }
}

/// <summary>Provider routing over the review registry; the effect is read back from the admitted capabilities.</summary>
public sealed class ReviewProviderRouteControl(V1ReviewExecutorRegistry registry, ProviderRouteExclusions exclusions,
    TimeProvider time) : IProviderRouteControl
{
    public IReadOnlyList<string> HealthyHosts(string provider, string exceptHost)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return registry.ListCapabilitySnapshots()
            .Where(snapshot => snapshot.HostId != exceptHost && now - snapshot.LastSeenAt <= TimeSpan.FromMinutes(10)
                && registry.AdmittedReviewCapabilities(snapshot.RunnerId, snapshot.InstanceId)
                    .Contains("provider-auth:" + provider))
            .Select(snapshot => snapshot.HostId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public bool IsReviewHost(string host) => registry.ListCapabilitySnapshots().Any(snapshot => snapshot.HostId == host);

    /// <summary>A host without a review registration is never rerouted: nothing routes its attempts by provider.</summary>
    public bool IsRerouted(string host, string provider)
    {
        var snapshots = registry.ListCapabilitySnapshots().Where(snapshot => snapshot.HostId == host).ToArray();
        return snapshots.Length > 0 && exclusions.IsExcluded(host, host, provider)
            && snapshots.All(snapshot => !registry.AdmittedReviewCapabilities(snapshot.RunnerId, snapshot.InstanceId)
                .Contains("provider-auth:" + provider));
    }

    public bool IsExcluded(string host, string provider) => exclusions.IsExcluded(host, host, provider);

    public void Reroute(string host, string provider, string interventionId) => exclusions.Exclude(host, provider, interventionId);

    public void Restore(string host, string provider) => exclusions.Include(host, provider);
}

/// <summary>
/// The batch gate queue through the pilot, which owns ownership markers and
/// lane events. A member's card is resolved by the key the attempt authority
/// files it under (<see cref="TaskIdentity.AuthorityKey(TaskInfo)"/>); a member
/// whose card is not found has state null and is never dropped.
/// </summary>
public sealed class BatchGateInterventionControl(BatchGateStore store, BatchGatePilotService pilot,
    TaskScannerService scanner, GateSlotUsageTracker gateSlots) : IBatchGateControl
{
    public StallGateFacts? Read(string project, DateTime nowUtc)
    {
        var pending = store.ListPending().Where(item => string.Equals(item.Subject.Project, project,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pending.Length == 0) return null;
        var jobs = scanner.ScanAllJobsWithArchive();
        var members = pending.Select(item => new StallGateMember(item.Subject.TaskKey, item.ReviewAttemptId,
            StateOf(jobs, item.Subject.TaskKey))).ToArray();
        var active = ActiveBatchProjects(store).Contains(project);
        var reviewing = pending.Where(item => StateOf(jobs, item.Subject.TaskKey) == TaskStates.AutoReview).ToArray();
        var oldest = pending.Min(item => item.EnqueuedAtUtc.UtcDateTime);
        double? remainingWait = reviewing.Length == 0 || active ? null
            : (nowUtc - reviewing.Min(item => item.EnqueuedAtUtc.UtcDateTime)).TotalHours;
        var own = gateSlots.Snapshot();
        var remote = gateSlots.RemoteSnapshot(ServiceWorkloadReader.RemoteSlotsFresh);
        var running = active || store.ListPending().Any(item => item.Subject.ActivePerTaskGate);
        return new StallGateFacts(project, pending.Length, members, (nowUtc - oldest).TotalHours,
            !active, remainingWait is null ? null : $"{project}:forming", remainingWait,
            new StallGateSlotFacts(remote?.Total ?? own.Total, own.Busy, remote?.Busy, running));
    }

    /// <summary>Drops only the frozen attempts whose card is still positively completed or stale now.</summary>
    public IReadOnlyList<string> Drop(string project, IReadOnlyList<string> reviewAttemptIds, string reason)
    {
        var jobs = scanner.ScanAllJobsWithArchive();
        var dropped = new List<string>();
        foreach (var attemptId in reviewAttemptIds.Distinct(StringComparer.Ordinal))
        {
            var pending = store.ListPending().FirstOrDefault(item => item.ReviewAttemptId == attemptId
                && string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase));
            if (pending is null || !StallGateMembers.Droppable(StateOf(jobs, pending.Subject.TaskKey))) continue;
            if (pilot.DropPendingMember(attemptId, reason)) dropped.Add(attemptId);
        }
        return dropped;
    }

    public bool IsPending(string project, string reviewAttemptId) => store.ListPending().Any(item =>
        item.ReviewAttemptId == reviewAttemptId
        && string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase));

    public bool CloseNow(string project)
    {
        pilot.RequestCloseNow(project);
        return store.HasCloseNow(project);
    }

    /// <summary>Projects with a formed, assembling, running or publishing batch.</summary>
    public static IReadOnlySet<string> ActiveBatchProjects(BatchGateStore store)
        => store.ListManifests().Where(manifest => HasState(store, manifest.BatchId)
                && store.LatestState(manifest.BatchId).Phase is BatchPhase.Formed or BatchPhase.Assembling
                    or BatchPhase.Assembled or BatchPhase.Running or BatchPhase.Publishing)
            .Select(manifest => manifest.Scope.Project).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The state of exactly one card that the authority key (or the legacy folder id) names; null otherwise.</summary>
    private static string? StateOf(IReadOnlyList<TaskInfo> jobs, string key)
    {
        var matches = jobs.Where(job => string.Equals(TaskIdentity.AuthorityKey(job), key, StringComparison.Ordinal)
            || string.Equals(job.Id, key, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].State : null;
    }

    private static bool HasState(BatchGateStore store, string batchId)
    {
        var path = Path.Combine(store.BatchDirectory(batchId), "state");
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.json").Any();
    }
}

/// <summary>
/// Continuation bases from the authority's last completed, reviewed delivery.
/// The authority projection is read by <see cref="TaskIdentity.AuthorityKey(TaskInfo)"/>,
/// so keyed cards resolve. A pinned base is an empty salvage when its commit is
/// only the start of one of the card's attempts and never a result with work.
/// </summary>
public sealed class DeliveryContinuationControl(TaskScannerService scanner, AttemptAuthorityService authority,
    TimeProvider time, Action? beforePin = null, LaneMutexRegistry? laneMutex = null,
    Action? beforeClear = null) : IContinuationControl
{
    public const string Reason = "rate-limit-last-delivery";

    public IReadOnlyList<StallRequeueCandidate> RateLimitedRuns(IReadOnlyList<string> cards)
    {
        var jobs = scanner.ScanAllJobs();
        var candidates = new List<StallRequeueCandidate>();
        foreach (var card in cards.Distinct(StringComparer.Ordinal).Take(50))
        {
            var job = Find(jobs, card);
            if (job is not null) candidates.Add(Candidate(card, job));
        }
        return candidates;
    }

    public ContinuationPin? PinLastDelivery(string card, string interventionId)
    {
        var job = Job(card);
        if (job is null) return null;
        var expectedBase = ContinuationBaseStore.Read(job.FolderPath);
        var candidate = Candidate(card, job);
        if (candidate.State != TaskStates.Ready || !candidate.LastRunRateLimited
            || candidate.BaseKind is not (StallContinuationBases.Missing or StallContinuationBases.EmptySalvage)
            || candidate.LastDeliveryRef is null || candidate.LastDeliverySha is null) return null;
        beforePin?.Invoke();
        using var lane = (laneMutex ?? LaneMutexRegistry.NullSingleton).TryAcquireStrict(job.WatchPath);
        if (lane is null) return null;
        if (!ContinuationBaseStore.TrySaveWhen(job.FolderPath, expectedBase,
                new ContinuationBaseRecord(candidate.LastDeliveryRef, candidate.LastDeliverySha,
                    Reason, interventionId, time.GetUtcNow().UtcDateTime),
                () => Directory.Exists(job.FolderPath)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(job.FolderPath)),
                        TaskStates.Ready, StringComparison.OrdinalIgnoreCase))) return null;
        return Read(card);
    }

    public ContinuationPin? Read(string card)
    {
        var job = Job(card);
        var record = job is null ? null : ContinuationBaseStore.Read(job.FolderPath);
        return record is null || job is null ? null : new ContinuationPin(card, record.Ref, record.CommitSha, record.Reason);
    }

    public bool LeftReady(string card) => Job(card) is { } job && job.State != TaskStates.Ready;

    public bool Clear(string card, string interventionId)
    {
        var job = Job(card);
        var record = job is null ? null : ContinuationBaseStore.Read(job.FolderPath);
        if (job is null || record is null || record.Reason != Reason || record.AttemptId != interventionId) return false;
        beforeClear?.Invoke();
        return ContinuationBaseStore.TryClearWhen(job.FolderPath, record);
    }

    private StallRequeueCandidate Candidate(string card, TaskInfo job)
    {
        var projection = authority.GetTaskProjection(TaskIdentity.AuthorityKey(job));
        var delivery = LastReviewedDelivery(projection);
        var pinned = ContinuationBaseStore.Read(job.FolderPath);
        var kind = pinned is null ? StallContinuationBases.Missing
            : string.Equals(pinned.CommitSha, delivery?.ResultEnvelope?.ResultSha, StringComparison.OrdinalIgnoreCase)
                ? StallContinuationBases.Delivery
            : EmptySalvage(pinned.CommitSha, projection) ? StallContinuationBases.EmptySalvage
            : StallContinuationBases.Other;
        var last = projection.RunAttempts.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        var rateLimited = last is { State: AttemptLifecycleState.Failed or AttemptLifecycleState.Cancelled }
            && Contract.ProviderAccessClassifier.Classify(1, last.TerminalReason, last.TerminalOutcome).Kind
                == Contract.ProviderAccessEvidenceKind.RateLimited;
        return new StallRequeueCandidate(card, job.State, kind, rateLimited,
            delivery?.ResultEnvelope?.ImmutableRemoteRef, delivery?.ResultEnvelope?.ResultSha, pinned?.CommitSha);
    }

    /// <summary>The last completed run with a result beyond its base whose review settled.</summary>
    private static RunAttemptDto? LastReviewedDelivery(AttemptAuthorityProjection projection)
        => projection.RunAttempts
            .Where(run => run.State == AttemptLifecycleState.Completed
                && !string.IsNullOrWhiteSpace(run.ResultEnvelope?.ImmutableRemoteRef)
                && !string.IsNullOrWhiteSpace(run.ResultEnvelope?.ResultSha)
                && !string.Equals(run.ResultEnvelope.ResultSha, run.ResultEnvelope.BaseSha, StringComparison.OrdinalIgnoreCase)
                && projection.ReviewAttempts.Any(review => review.SourceRunAttemptId == run.AttemptId
                    && review.State == AttemptLifecycleState.Completed && review.Outcome is not null))
            .OrderByDescending(run => run.TerminalAt ?? run.CreatedAt).FirstOrDefault();

    private static bool EmptySalvage(string commitSha, AttemptAuthorityProjection projection)
        => projection.RunAttempts.Any(run => string.Equals(run.ResultEnvelope?.BaseSha, commitSha, StringComparison.OrdinalIgnoreCase))
           && !projection.RunAttempts.Any(run => run.ResultEnvelope is { } envelope
               && string.Equals(envelope.ResultSha, commitSha, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(envelope.ResultSha, envelope.BaseSha, StringComparison.OrdinalIgnoreCase));

    private TaskInfo? Job(string card) => Find(scanner.ScanAllJobs(), card);

    private static TaskInfo? Find(IReadOnlyList<TaskInfo> jobs, string card)
        => jobs.FirstOrDefault(item => string.Equals(TaskIdentity.AuthorityKey(item), card, StringComparison.Ordinal))
           ?? jobs.FirstOrDefault(item => item.Id == card || item.Key == card || item.TaskKey == card);
}
