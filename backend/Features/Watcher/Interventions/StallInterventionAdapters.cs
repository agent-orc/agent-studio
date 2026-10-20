using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Clients;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Watcher;

/// <summary>Coding host admission through the identity store that the claim path reads on every poll.</summary>
public sealed class ClientIdentityAdmissionControl(ClientIdentityStore clients, HostTelemetryStore telemetry)
    : IHostAdmissionControl
{
    public HostAdmissionReading? Read(string host, double restoreFraction, DateTime nowUtc)
    {
        var identity = Find(host);
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
            last is null ? null : PerCore(last), calmSince);
    }

    public int? SetAdmission(string host, int maxParallelism)
    {
        var identity = Find(host);
        if (identity is null) return null;
        clients.SetRunnerCapacity(identity.Id, maxParallelism, null, null);
        return clients.Find(identity.Id)?.RunnerDesiredMaxParallelism;
    }

    private ClientIdentity? Find(string host) => clients.ListAll().FirstOrDefault(item =>
        item.Kind != ClientIdentityKind.Retired
        && (string.Equals(item.DisplayName, host, StringComparison.OrdinalIgnoreCase) || item.Id == host));
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
                if (!read.IsSuccessStatusCode) return new(false, $"the restart state is unreadable: {await Error(read, ct)}");
                attempt = await read.Content.ReadFromJsonAsync<OperationAttempt>(OperationsProtocol.Json, ct) ?? attempt;
            }
            var restarted = attempt.Result?.Output.ValueKind == JsonValueKind.Object
                && attempt.Result.Output.TryGetProperty("restarted", out var flag) && flag.ValueKind == JsonValueKind.True;
            return attempt.State == "succeeded" && restarted
                ? new(true, $"Operations Server attempt {attempt.Id} restarted {service} on {host}")
                : new(false, $"Operations Server attempt {attempt.Id} ended {attempt.State}"
                    + (attempt.Result is null ? string.Empty : $" ({attempt.Result.ExitClassification})"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "stall-intervention-restart-failed host={Host} service={Service}", host, service);
            return new(false, $"the Operations Server is unreachable: {exception.Message}");
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
    private readonly object _gate = new();
    private Dictionary<string, string>? _entries;

    public ProviderRouteExclusions(IConfiguration configuration) : this(() => configuration["TaskRepository"]) { }

    public ProviderRouteExclusions(Func<string?> workspaceRoot) => _root = workspaceRoot;

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
            Load()[Key(host, provider)] = interventionId;
            Save();
        }
    }

    public void Include(string host, string provider)
    {
        lock (_gate)
        {
            if (Load().Remove(Key(host, provider))) Save();
        }
    }

    private static string Key(string host, string provider) => $"{host}|{provider}";

    private Dictionary<string, string> Load()
    {
        if (_entries is not null) return _entries;
        var path = Path();
        _entries = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) ?? []
            : [];
        return _entries;
    }

    private void Save()
    {
        var path = Path() ?? throw new InvalidOperationException("TaskRepository is not configured.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_entries, Json));
        File.Move(path + ".tmp", path, overwrite: true);
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

    public bool IsRerouted(string host, string provider)
    {
        var snapshots = registry.ListCapabilitySnapshots().Where(snapshot => snapshot.HostId == host).ToArray();
        return snapshots.Length == 0
            ? exclusions.IsExcluded(host, host, provider)
            : snapshots.All(snapshot => !registry.AdmittedReviewCapabilities(snapshot.RunnerId, snapshot.InstanceId)
                .Contains("provider-auth:" + provider)) && exclusions.IsExcluded(host, host, provider);
    }

    public void Reroute(string host, string provider, string interventionId) => exclusions.Exclude(host, provider, interventionId);

    public void Restore(string host, string provider) => exclusions.Include(host, provider);
}

/// <summary>The batch gate queue through the pilot, which owns ownership markers and lane events.</summary>
public sealed class BatchGateInterventionControl(BatchGateStore store, BatchGatePilotService pilot,
    TaskScannerService scanner) : IBatchGateControl
{
    public StallGateFacts? Read(string project, DateTime nowUtc)
    {
        var pending = store.ListPending().Where(item => string.Equals(item.Subject.Project, project,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pending.Length == 0) return null;
        var jobs = scanner.ScanAllJobs();
        string? State(string card) => jobs.FirstOrDefault(job => job.Id == card || job.TaskKey == card)?.State;
        var completed = pending.Where(item => State(item.Subject.TaskKey) == TaskStates.Completed)
            .Select(item => item.Subject.TaskKey).Distinct(StringComparer.Ordinal).ToArray();
        var stale = pending.Where(item => State(item.Subject.TaskKey) is not (TaskStates.AutoReview or TaskStates.Completed))
            .Select(item => item.Subject.TaskKey).Distinct(StringComparer.Ordinal).ToArray();
        var remaining = pending.Where(item => !completed.Contains(item.Subject.TaskKey)
            && !stale.Contains(item.Subject.TaskKey)).ToArray();
        var active = store.ListManifests().Any(manifest => string.Equals(manifest.Scope.Project, project,
                StringComparison.OrdinalIgnoreCase) && HasState(manifest.BatchId)
            && store.LatestState(manifest.BatchId).Phase is BatchPhase.Formed or BatchPhase.Assembling
                or BatchPhase.Assembled or BatchPhase.Running or BatchPhase.Publishing);
        var oldest = pending.Min(item => item.EnqueuedAtUtc.UtcDateTime);
        double? remainingWait = remaining.Length == 0 || active ? null
            : (nowUtc - remaining.Min(item => item.EnqueuedAtUtc.UtcDateTime)).TotalHours;
        return new StallGateFacts(project, pending.Length, completed, stale, (nowUtc - oldest).TotalHours,
            !active, remainingWait is null ? null : $"{project}:forming", remainingWait);
    }

    public IReadOnlyList<string> Drop(string project, IReadOnlyList<string> cards, string reason)
    {
        var dropped = new List<string>();
        foreach (var card in cards)
        {
            var entries = store.ListPending().Where(item => item.Subject.TaskKey == card
                && string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length > 0 && entries.All(item => pilot.DropPendingMember(item.ReviewAttemptId, reason)))
                dropped.Add(card);
        }
        return dropped;
    }

    public bool IsPending(string project, string card) => store.ListPending().Any(item => item.Subject.TaskKey == card
        && string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase));

    public bool CloseNow(string project)
    {
        pilot.RequestCloseNow(project);
        return store.HasCloseNow(project);
    }

    private bool HasState(string batchId)
    {
        var path = Path.Combine(store.BatchDirectory(batchId), "state");
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.json").Any();
    }
}

/// <summary>Continuation bases from the authority's last real delivery.</summary>
public sealed class DeliveryContinuationControl(TaskScannerService scanner, AttemptAuthorityService authority,
    TimeProvider time) : IContinuationControl
{
    public const string Reason = "rate-limit-last-delivery";

    public IReadOnlyList<StallRequeueCandidate> RateLimitedRuns(IReadOnlyList<string> cards)
    {
        var jobs = scanner.ScanAllJobs();
        var candidates = new List<StallRequeueCandidate>();
        foreach (var card in cards.Distinct(StringComparer.Ordinal).Take(50))
        {
            var job = jobs.FirstOrDefault(item => item.Id == card || item.TaskKey == card);
            if (job is null) continue;
            var delivery = LastDelivery(job.TaskKey ?? job.Id);
            candidates.Add(new StallRequeueCandidate(job.Id, job.State, ContinuationBaseStore.Read(job.FolderPath) is null,
                delivery?.Ref, delivery?.Sha));
        }
        return candidates;
    }

    public ContinuationPin? PinLastDelivery(string card, string interventionId)
    {
        var job = Job(card);
        if (job is null || job.State != TaskStates.Ready) return null;
        var delivery = LastDelivery(job.TaskKey ?? job.Id);
        if (delivery is null) return null;
        ContinuationBaseStore.Save(job.FolderPath, new ContinuationBaseRecord(delivery.Value.Ref, delivery.Value.Sha,
            Reason, interventionId, time.GetUtcNow().UtcDateTime));
        return Read(card);
    }

    public ContinuationPin? Read(string card)
    {
        var job = Job(card);
        var record = job is null ? null : ContinuationBaseStore.Read(job.FolderPath);
        return record is null || job is null ? null : new ContinuationPin(job.Id, record.Ref, record.CommitSha, record.Reason);
    }

    public bool LeftReady(string card) => Job(card) is { } job && job.State != TaskStates.Ready;

    public bool Clear(string card, string interventionId)
    {
        var job = Job(card);
        var record = job is null ? null : ContinuationBaseStore.Read(job.FolderPath);
        if (job is null || record is null || record.Reason != Reason || record.AttemptId != interventionId) return false;
        ContinuationBaseStore.Clear(job.FolderPath);
        return true;
    }

    private TaskInfo? Job(string card) => scanner.ScanAllJobs().FirstOrDefault(item => item.Id == card || item.TaskKey == card);

    private (string Ref, string Sha)? LastDelivery(string taskKey)
    {
        var attempt = authority.GetTaskProjection(taskKey).RunAttempts
            .Where(item => !string.IsNullOrWhiteSpace(item.ResultEnvelope?.ImmutableRemoteRef)
                && !string.IsNullOrWhiteSpace(item.ResultEnvelope?.ResultSha))
            .OrderByDescending(item => item.TerminalAt ?? item.CreatedAt).FirstOrDefault();
        return attempt is null ? null : (attempt.ResultEnvelope!.ImmutableRemoteRef!, attempt.ResultEnvelope.ResultSha!);
    }
}
