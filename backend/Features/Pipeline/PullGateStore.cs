using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed record PullGateHost(string Id, string Name, int Slots, int ProcessorCount,
    DateTimeOffset LastHeartbeat, string? CurrentRun)
{
    public int Busy { get; init; }
}
public sealed record PullGateClaim(string RunId, string Lease, DateTimeOffset ExpiresAt);
internal sealed record PullGateJob(string RunId, string BundleSha256, string State,
    string? HostId, string? Lease, DateTimeOffset ExpiresAt, string? Progress,
    DateTimeOffset? EnqueuedAt = null);

/// <summary>Durable bundle and result authority. Every mutation is serialized and lease fenced.</summary>
public sealed class PullGateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HealthyTime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly IConfiguration? _configuration;
    private readonly Dictionary<string, PullGateJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PullGateHost> _hosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _restartFences = new(StringComparer.Ordinal);

    public PullGateStore(string root, TimeProvider clock, IConfiguration? configuration = null)
    {
        _root = root;
        _clock = clock;
        _configuration = configuration;
        Directory.CreateDirectory(root);
        LoadRestartFences();
        foreach (var file in Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
        {
            var job = JsonSerializer.Deserialize<PullGateJob>(File.ReadAllText(file), Json);
            if (job is not null) _jobs[job.RunId] = job;
        }
    }

    private string Folder(string id) => Path.Combine(_root, id);
    private DateTimeOffset Now => _clock.GetUtcNow();
    public IReadOnlyList<PullGateHost> Hosts()
    {
        lock (_gate) return _hosts.Values.Select(SnapshotHost).OrderBy(host => host.Name).ToArray();
    }
    public bool HasHealthyHost
    {
        get { lock (_gate) return _hosts.Values.Any(host => Now - host.LastHeartbeat < HealthyTime); }
    }
    public IReadOnlyList<PullGateHost> HealthyHosts()
    {
        lock (_gate) return _hosts.Values.Where(host => Now - host.LastHeartbeat < HealthyTime)
            .Select(SnapshotHost).ToArray();
    }

    /// <summary>Only queued jobs can be claimed by an idle pull gate worker.</summary>
    public (int Busy, int Queued)? Workload(string hostId)
    {
        lock (_gate)
        {
            if (!_hosts.ContainsKey(hostId)) return null;
            // An expired lease is still potentially executing on the host.
            return (_jobs.Values.Count(job => job.HostId == hostId && job.State == "claimed"),
                _jobs.Values.Count(job => job.State == "queued"));
        }
    }

    public DateTime? OldestQueuedAtUtc()
    {
        lock (_gate) return _jobs.Values.Where(job => job.State == "queued" && job.EnqueuedAt is not null)
            .Select(job => (DateTime?)job.EnqueuedAt!.Value.UtcDateTime).OrderBy(value => value).FirstOrDefault();
    }

    public IDisposable? TryFenceHostClaims(string hostId)
    {
        lock (_gate)
        {
            if (_restartFences.Contains("*") || _restartFences.Contains(hostId) || Workload(hostId) is not { Busy: 0 })
                return null;
            _restartFences.Add(hostId);
            try { SaveRestartFences(); }
            catch (IOException)
            {
                _restartFences.Remove(hostId);
                return null;
            }
            return new GateRestartFence(this, hostId);
        }
    }

    public bool HasRestartFence(string hostId)
    {
        lock (_gate) return _restartFences.Contains(hostId);
    }

    public void ReleaseRestartFence(string hostId)
    {
        lock (_gate)
        {
            if (!_restartFences.Remove(hostId)) return;
            try { SaveRestartFences(); }
            catch (IOException) { _restartFences.Add(hostId); }
        }
    }

    private sealed class GateRestartFence(PullGateStore owner, string hostId) : IRestartClaimFence
    {
        private bool _retained;
        public void Retain() => _retained = true;
        public void ConfirmTerminal() => _retained = false;
        public void Dispose()
        {
            if (_retained) return;
            lock (owner._gate)
            {
                owner._restartFences.Remove(hostId);
                try { owner.SaveRestartFences(); }
                catch (IOException) { owner._restartFences.Add(hostId); }
            }
        }
    }

    private string RestartFencePath => Path.Combine(_root, "restart-fences.json");
    private void LoadRestartFences()
    {
        if (!File.Exists(RestartFencePath)) return;
        try
        {
            foreach (var host in JsonSerializer.Deserialize<string[]>(File.ReadAllText(RestartFencePath)) ?? [])
                if (!string.IsNullOrWhiteSpace(host)) _restartFences.Add(host);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _restartFences.Add("*");
        }
    }

    private void SaveRestartFences()
    {
        var temporary = RestartFencePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _restartFences.OrderBy(host => host).ToArray());
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, RestartFencePath, overwrite: true);
    }

    private PullGateHost SnapshotHost(PullGateHost host)
    {
        var active = _jobs.Values.Where(job => job.HostId == host.Id && job.State == "claimed"
            && job.ExpiresAt > Now).ToArray();
        return host with { Busy = active.Length, CurrentRun = active.FirstOrDefault()?.RunId };
    }

    public PullGateHost Register(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Gate host identity is invalid.");
        lock (_gate)
        {
            // Host capacity is an operator decision held by the server. The
            // worker cannot raise it by advertising more local slots.
            var slots = _configuration?.GetValue<int?>($"RemoteGate:HostCapacity:{id}:Slots")
                ?? _configuration?.GetValue<int?>("RemoteGate:GateSlots") ?? 1;
            var processors = _configuration?.GetValue<int?>($"RemoteGate:HostCapacity:{id}:ProcessorCount")
                ?? _configuration?.GetValue<int?>("RemoteGate:ProcessorCount") ?? 2;
            if (slots is < 1 or > 64 || processors is < 1 or > 64)
                throw new ArgumentException("Server gate host capacity is invalid.");
            var active = _jobs.Values.FirstOrDefault(job => job.HostId == id && job.State == "claimed"
                && job.ExpiresAt > Now);
            var host = new PullGateHost(id, name, slots, processors, Now, active?.RunId);
            _hosts[id] = host;
            return host;
        }
    }

    public string Enqueue(RemoteGateInvocation invocation, string bundlePath)
    {
        RemoteGateWorker.Validate(invocation, invocation.RunId);
        var folder = Folder(invocation.RunId);
        Directory.CreateDirectory(folder);
        File.Copy(bundlePath, Path.Combine(folder, "source.bundle"));
        File.WriteAllText(Path.Combine(folder, "request.json"),
            JsonSerializer.Serialize(invocation, Json));
        lock (_gate)
        {
            var job = new PullGateJob(invocation.RunId, invocation.BundleSha256, "queued",
                null, null, default, null, Now);
            Save(job);
            return job.RunId;
        }
    }

    public PullGateClaim? Claim(string hostId)
    {
        lock (_gate)
        {
            if (_restartFences.Contains("*") || _restartFences.Contains(hostId)) return null;
            if (!_hosts.TryGetValue(hostId, out var host) || Now - host.LastHeartbeat >= HealthyTime)
                return null;
            var used = _jobs.Values.Count(job => job.HostId == hostId && job.State == "claimed"
                && job.ExpiresAt > Now);
            if (used >= host.Slots) return null;
            var job = _jobs.Values.OrderBy(item => item.RunId).FirstOrDefault(item =>
                item.State == "queued" || item.State == "claimed" && item.ExpiresAt <= Now);
            if (job is null) return null;
            var staleArtifacts = Path.Combine(Folder(job.RunId), "artifacts");
            if (Directory.Exists(staleArtifacts)) Directory.Delete(staleArtifacts, recursive: true);
            var claimed = job with { State = "claimed", HostId = hostId,
                Lease = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                ExpiresAt = Now.Add(LeaseTime), Progress = "claimed" };
            var requestPath = Path.Combine(Folder(job.RunId), "request.json");
            var invocation = JsonSerializer.Deserialize<RemoteGateInvocation>(
                File.ReadAllText(requestPath), Json)!;
            File.WriteAllText(requestPath, JsonSerializer.Serialize(invocation with
            {
                GateSlots = host.Slots, ProcessorCount = host.ProcessorCount,
            }, Json));
            Save(claimed);
            _hosts[hostId] = host with { CurrentRun = job.RunId };
            return new PullGateClaim(job.RunId, claimed.Lease!, claimed.ExpiresAt);
        }
    }

    public DateTimeOffset Heartbeat(string hostId, string runId, string lease, string? progress)
    {
        lock (_gate)
        {
            var job = RequireLease(hostId, runId, lease);
            var expiry = Now.Add(LeaseTime);
            Save(job with { ExpiresAt = expiry, Progress = progress ?? job.Progress });
            var host = _hosts[hostId];
            _hosts[hostId] = host with { LastHeartbeat = Now, CurrentRun = runId };
            return expiry;
        }
    }

    public void ReportProgress(string hostId, string runId, string lease, string progress)
    {
        if (progress.Length > 2048) throw new ArgumentException("Gate progress line is too long.");
        lock (_gate)
        {
            var job = RequireLease(hostId, runId, lease);
            Save(job with { Progress = progress });
        }
    }

    public string FileForLease(string hostId, string runId, string lease, string name)
    {
        lock (_gate)
        {
            RequireLease(hostId, runId, lease);
            return Path.Combine(Folder(runId), name);
        }
    }

    public void UploadArtifact(string hostId, string runId, string lease, string name, Stream input)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)
            || name is "." or ".." || name.Length > 128)
            throw new ArgumentException("Invalid artifact name.");
        string target;
        lock (_gate)
        {
            RequireLease(hostId, runId, lease);
            var folder = Path.Combine(Folder(runId), "artifacts");
            Directory.CreateDirectory(folder);
            target = Path.Combine(folder, name);
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > 512L * 1024 * 1024)
                        throw new InvalidDataException("Gate artifact exceeds 512 MiB.");
                    output.Write(buffer, 0, read);
                }
            }
            lock (_gate)
            {
                RequireLease(hostId, runId, lease);
                File.Move(temporary, target, true);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string? Artifact(string runId, string name)
    {
        if (name != Path.GetFileName(name) || name is "." or "..") return null;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(runId, out var job) || job.State != "completed") return null;
            var path = Path.Combine(Folder(runId), "artifacts", name);
            return File.Exists(path) ? path : null;
        }
    }

    public IReadOnlyList<string> Artifacts(string runId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(runId, out var job) || job.State != "completed") return [];
            var folder = Path.Combine(Folder(runId), "artifacts");
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name => name is not null)
                    .Select(name => name!).OrderBy(name => name).ToArray()
                : [];
        }
    }

    public void Complete(string hostId, string runId, string lease, RemoteGateResponse response)
    {
        lock (_gate)
        {
            var job = RequireLease(hostId, runId, lease);
            if (response.Version != 1 || response.RunId != runId
                || response.BundleSha256 != job.BundleSha256
                || response.Result.ExpectedSha is null)
                throw new InvalidDataException("Gate result does not match the claimed bundle.");
            File.WriteAllText(Path.Combine(Folder(runId), "response.json"),
                JsonSerializer.Serialize(response, Json));
            Save(job with { State = "completed", Lease = null, Progress = "completed" });
            _hosts[hostId] = _hosts[hostId] with { LastHeartbeat = Now, CurrentRun = null };
        }
    }

    public RemoteGateResponse? Response(string runId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(runId, out var job) || job.State != "completed") return null;
            return JsonSerializer.Deserialize<RemoteGateResponse>(
                File.ReadAllText(Path.Combine(Folder(runId), "response.json")), Json);
        }
    }

    public string? Progress(string runId)
    {
        lock (_gate) return _jobs.GetValueOrDefault(runId)?.Progress;
    }

    public void Abandon(string runId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(runId, out var job) || job.State == "completed") return;
            Save(job with { State = "abandoned", Lease = null });
        }
    }

    private PullGateJob RequireLease(string hostId, string runId, string lease)
    {
        if (!_jobs.TryGetValue(runId, out var job) || job.State != "claimed"
            || job.HostId != hostId || job.Lease != lease || job.ExpiresAt <= Now)
            throw new InvalidOperationException("Gate lease expired or was replaced.");
        return job;
    }

    private void Save(PullGateJob job)
    {
        var path = Path.Combine(Folder(job.RunId), "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job, Json));
        File.Move(path + ".tmp", path, true);
        _jobs[job.RunId] = job;
    }
}
