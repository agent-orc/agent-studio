using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Bounded emergency transport for exact integration/promotion candidates.
/// This does not mint Task Server run authority or publish a repository ref.
/// </summary>
public sealed class RemoteGateTransport : IRemoteGateTransport
{
    internal static readonly EventId OrphanedRunCancelledEvent =
        new(1, "remote gate run orphaned and cancelled");
    /// <summary>
    /// Age after which a GUID run folder under the local transport root is
    /// treated as abandoned. The folder is last written at request.json; before
    /// response.json can be the next write, at most 2 x InfrastructureTimeout
    /// (remote mkdir, request scp), the bundle scp (at most
    /// max(InfrastructureTimeout, 1,024 s) under the 1 GiB limit), the worker
    /// SSH wait (at most 86,400 s + 150 s) and one more InfrastructureTimeout for
    /// the response scp itself can pass. With Stable's 480 s that is about
    /// 24.7 h. 26 h stays safe while InfrastructureTimeout is at most about
    /// 29 minutes.
    /// </summary>
    internal static readonly TimeSpan StaleRunFolderAge = TimeSpan.FromHours(26);

    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private const long NeverSwept = long.MinValue;

    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteGateTransport> _logger;
    private readonly TimeProvider _time;
    private readonly string _localRoot;
    private readonly RemoteGateRunRegistry _runs;
    private readonly Action<string> _deleteDirectory;
    private readonly GateSlotUsageTracker _slotUsage;
    private static readonly ConcurrentDictionary<string, byte> LiveRuns = new(StringComparer.Ordinal);
    private readonly Func<RemoteGateRunRecord, bool> _ownerIsAlive;
    private readonly OrchestratorLog? _activity;
    private readonly AgentStudio.Tasks.TaskScannerService? _scanner;
    private readonly Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> _runProcess;
    private readonly string _section;
    private long _lastSweepTicks = NeverSwept;

    public RemoteGateTransport(IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        TimeProvider? timeProvider = null, GateSlotUsageTracker? slotUsage = null,
        OrchestratorLog? activity = null, AgentStudio.Tasks.TaskScannerService? scanner = null)
        : this(configuration, logger, timeProvider ?? TimeProvider.System,
            Path.Combine(Path.GetTempPath(), "agentstudio-gate-transport"),
            WorktreeDirectoryCleanup.DeleteDirectoryWithoutFollowingReparsePoints, slotUsage,
            runRegistry: CreateDefaultRegistrySafe(timeProvider ?? TimeProvider.System, configuration, logger),
            activity: activity, scanner: scanner)
    {
    }

    /// <summary>
    /// A transport to an additional shard host (AGT-3232): its options come
    /// from <paramref name="section"/>, with unset values taken from
    /// <c>RemoteGate</c>.
    /// </summary>
    internal RemoteGateTransport(IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        string section, GateSlotUsageTracker slotUsage, TimeProvider? timeProvider = null,
        OrchestratorLog? activity = null, AgentStudio.Tasks.TaskScannerService? scanner = null)
        : this(configuration, logger, timeProvider ?? TimeProvider.System,
            Path.Combine(Path.GetTempPath(), "agentstudio-gate-transport"),
            WorktreeDirectoryCleanup.DeleteDirectoryWithoutFollowingReparsePoints, slotUsage,
            runRegistry: CreateDefaultRegistrySafe(timeProvider ?? TimeProvider.System, configuration, logger),
            activity: activity, scanner: scanner)
    {
        _section = section;
    }

    internal RemoteGateTransport(IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        TimeProvider timeProvider, string localRoot, Action<string> deleteDirectory,
        GateSlotUsageTracker? slotUsage = null,
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task>? runProcess = null,
        RemoteGateRunRegistry? runRegistry = null,
        Func<RemoteGateRunRecord, bool>? ownerIsAlive = null,
        OrchestratorLog? activity = null, AgentStudio.Tasks.TaskScannerService? scanner = null,
        string section = RemoteGateOptions.PrimarySection)
    {
        _section = section;
        _configuration = configuration;
        _logger = logger;
        _time = timeProvider;
        _localRoot = localRoot;
        _runs = runRegistry ?? new RemoteGateRunRegistry(localRoot + ".run-records", timeProvider);
        if (!Regex.IsMatch(_runs.InstallationId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
            throw new InvalidDataException("Remote gate installation id is invalid.");
        _deleteDirectory = deleteDirectory;
        _slotUsage = slotUsage ?? new GateSlotUsageTracker(clock: timeProvider);
        _runProcess = runProcess ?? RemoteGateProcess.RunAsync;
        _ownerIsAlive = ownerIsAlive ?? IsOwnerProcessAlive;
        _activity = activity;
        _scanner = scanner;
    }

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var options = RemoteGateOptions.Read(_configuration, _section);
        _slotUsage.Configure(options.GateSlots);
        var runId = Guid.NewGuid().ToString("N");
        var local = Path.Combine(_localRoot, runId);
        var remote = options.Root + "/" + runId;
        var infra = request.InfrastructureTimeout > TimeSpan.Zero
            ? request.InfrastructureTimeout : TimeSpan.FromMinutes(2);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.Project ?? Path.GetFullPath(request.RepositoryPath)))).ToLowerInvariant();
        var cache = options.Root + "/cache/" + cacheKey;
        var queue = request.QueueWaitTimeout ?? timeout + infra;
        var overallSeconds = checked((int)Math.Ceiling((queue + timeout + infra * 3 + TimeSpan.FromMinutes(2)).TotalSeconds));
        if (overallSeconds is < 1 or > 86400)
            throw new InvalidOperationException("Remote gate total budget must be at most 24 hours.");
        TrySweepStaleRunFolders();
        Directory.CreateDirectory(local);
        var remoteCreationAttempted = false;
        try
        {
            long sourcePackKiB = 0;
            await Git(local, ["-C", Path.GetFullPath(request.RepositoryPath), "count-objects", "-v"], infra, ct,
                line =>
                {
                    if (line.StartsWith("size-pack: ", StringComparison.Ordinal)
                        && long.TryParse(line[11..], out var value)) sourcePackKiB = value;
                });
            var preparationBudget = TransferBudget(infra, checked(sourcePackKiB * 1024));
            var repository = Path.Combine(local, "source.git");
            await Git(local, ["init", "--bare", "--quiet", repository], infra, ct);
            // Only this private bare repository receives a ref. The operator's
            // checkout and the integration remote remain untouched.
            await GitPrepared(local, ["-C", repository, "fetch", "--no-tags", "--no-recurse-submodules",
                Path.GetFullPath(request.RepositoryPath), request.ExpectedSha!], preparationBudget,
                sourcePackKiB * 1024, "fetch", ct);
            await Git(local, ["-C", repository, "update-ref", "refs/heads/gate-subject", request.ExpectedSha!], infra, ct);
            var knownTips = new List<string>();
            await Ssh(options, local, "umask 077; mkdir -p -- " + Quote(options.Root + "/cache")
                + "; git init --bare --quiet -- " + Quote(cache)
                + "; git -C " + Quote(cache) + " for-each-ref --sort=-committerdate --count=16 --format='%(objectname)' refs/heads/gate-cache",
                infra, ct, line => { if (Regex.IsMatch(line, "^[0-9a-f]{40}([0-9a-f]{24})?$")) knownTips.Add(line); });
            var includedTips = new List<string>();
            // Only exclude cache tips that are ancestors of this exact subject.
            foreach (var tip in knownTips)
            {
                if (!string.Equals(tip, request.ExpectedSha, StringComparison.OrdinalIgnoreCase)
                    && await GitIsAncestor(local, repository, tip, request.ExpectedSha!, preparationBudget, ct))
                {
                    includedTips.Add(tip);
                    break;
                }
            }
            await GitPrepared(local, BundleArguments(repository, Path.Combine(local, "source.bundle"), includedTips),
                preparationBudget, sourcePackKiB * 1024, "bundle", ct);
            var bundle = Path.Combine(local, "source.bundle");
            var bundleBytes = new FileInfo(bundle).Length;
            if (bundleBytes > 1024L * 1024 * 1024)
                throw new InvalidOperationException("Remote gate source bundle exceeds the 1 GiB transport limit.");
            var transferBudget = TransferBudget(infra, bundleBytes);
            var workerInfrastructureBudget = preparationBudget > transferBudget ? preparationBudget : transferBudget;
            overallSeconds = checked((int)Math.Ceiling((queue + timeout + workerInfrastructureBudget * 3
                + transferBudget + TimeSpan.FromMinutes(2)).TotalSeconds));
            if (overallSeconds > 86400)
                throw new InvalidOperationException("Remote gate total budget must be at most 24 hours.");
            string digest;
            await using (var input = File.OpenRead(bundle))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            var invocation = new RemoteGateInvocation(1, runId, digest,
                request with
                {
                    RepositoryPath = "source.git",
                    WatchPath = null,
                    JobFolderPath = null,
                    SubjectRef = "refs/heads/gate-subject",
                    ToolchainIdentity = null,
                    InfrastructureTimeout = workerInfrastructureBudget,
                    OnMachineGateAcquired = null,
                    OnMachineGateWaiting = null,
                    OnMachineGateSlotAcquired = null,
                    OnMachineGateSlotWaiting = null,
                }, changedFiles, profile, mode, checked((int)Math.Ceiling(timeout.TotalSeconds)), overallSeconds)
            { CachePath = cache, GateSlots = options.GateSlots, ProcessorCount = options.ProcessorCount };
            invocation = invocation with { InstallationId = _runs.InstallationId };
            await File.WriteAllTextAsync(Path.Combine(local, "request.json"),
                JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json), ct);
            TrackLiveRun(runId);
            _runs.Record(runId, options.Host, options.Root, request.Project, request.WatchPath);
            // SSH can fail after mkdir succeeds on the host. From this point,
            // cleanup must prove the directory is gone before dropping its record.
            remoteCreationAttempted = true;
            await Ssh(options, local,
                "umask 077; mkdir -p -- " + Quote(options.Root) + "; mkdir -- " + Quote(remote), infra, ct);
            var transfer = Stopwatch.StartNew();
            try
            {
                await Scp(options, local, "source.bundle", options.Host + ":" + remote + "/source.bundle", transferBudget, ct);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                throw new IOException(TransferFailure(bundleBytes, transfer.Elapsed, transferBudget, exception.Message), exception);
            }
            transfer.Stop();
            _logger.LogInformation("remote_gate_bundle_transferred run_id={RunId} bundle_bytes={BundleBytes} transfer_ms={TransferMs} budget_seconds={BudgetSeconds}",
                runId, bundleBytes, transfer.ElapsedMilliseconds, transferBudget.TotalSeconds);
            await Scp(options, local, "request.json", options.Host + ":" + remote + "/request.json", infra, ct);
            await Ssh(options, local, "touch -- " + Quote(remote + "/heartbeat"), infra, ct);
            _logger.LogInformation("remote_gate_dispatched run_id={RunId} host={Host} expected_sha={ExpectedSha} bundle_sha256={Digest} bundle_bytes={BundleBytes} transfer_ms={TransferMs}",
                runId, options.Host, request.ExpectedSha, digest, bundleBytes, transfer.ElapsedMilliseconds);
            var command = "cd " + Quote(remote) + " && exec flock " + Quote(remote + "/worker.lock")
                + " timeout --signal=TERM --kill-after=120s "
                + overallSeconds + "s env DOTNET_PROCESSOR_COUNT=" + options.ProcessorCount
                + " dotnet " + Quote(options.WorkerPath)
                + " --remote-build-test-gate-worker " + Quote(remote + "/request.json");
            using (var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var heartbeat = RefreshHeartbeatAsync(options, local, remote, infra, heartbeatStop.Token);
                try
                {
                    await Ssh(options, local, command, TimeSpan.FromSeconds(overallSeconds + 150), ct,
                        line => ReportWorkerLine(line, runId, options.Host, request));
                }
                finally
                {
                    heartbeatStop.Cancel();
                    try { await heartbeat; }
                    catch (OperationCanceledException exception) when (heartbeatStop.IsCancellationRequested)
                    {
                        _logger.LogDebug(exception, "remote_gate_heartbeat_stopped run_id={RunId}", runId);
                    }
                }
            }
            await Scp(options, local, options.Host + ":" + remote + "/response.json", "response.json", infra, ct);
            var responsePath = Path.Combine(local, "response.json");
            if (new FileInfo(responsePath).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Remote gate response exceeds the evidence limit.");
            var response = JsonSerializer.Deserialize<RemoteGateResponse>(
                await File.ReadAllTextAsync(responsePath, ct), RemoteBuildTestGateRunner.Json)
                ?? throw new InvalidDataException("Remote gate response is empty.");
            return AcceptResponse(response, runId, digest, options.Host, request.Executor,
                bundleBytes, transfer.Elapsed);
        }
        finally
        {
            _slotUsage.Released(runId);
            try
            {
                if (remoteCreationAttempted)
                {
                    // Cancellation is a worker-owned marker, not an arbitrary PID
                    // kill. The worker observes it while queued or executing. An
                    // independent host timeout still bounds a broken SSH link.
                    var cleanup = CleanupCommand(remote);
                    try
                    {
                        await Ssh(options, local, cleanup, TimeSpan.FromSeconds(50), CancellationToken.None);
                        await Ssh(options, local, "test ! -e " + Quote(remote), TimeSpan.FromSeconds(10), CancellationToken.None);
                        _runs.Remove(runId);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "remote_gate_cleanup_pending host={Host} run_id={RunId}", options.Host, runId);
                    }
                }
                else if (_runs.ReadAll().Any(record => record.RunId == runId))
                    _runs.Remove(runId);
            }
            finally
            {
                LiveRuns.TryRemove(_runs.ProcessInstance + ":" + runId, out _);
                DeleteLocalRunFolder(local, runId);
            }
        }
    }

    internal void TrackLiveRun(string runId) => LiveRuns.TryAdd(_runs.ProcessInstance + ":" + runId, 0);

    private async Task RefreshHeartbeatAsync(RemoteGateOptions options, string cwd, string remote,
        TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await Ssh(options, cwd, "touch -- " + Quote(remote + "/heartbeat"), timeout, ct); }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                _logger.LogWarning(exception, "remote_gate_heartbeat_failed host={Host} run_id={RunId}", options.Host,
                    Path.GetFileName(remote));
            }
        }
    }

    internal async Task CancelOrphanedRunsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_configuration[$"{_section}:SshHost"])) return;
        var records = _runs.ReadAll().Where(record => record.InstallationId == _runs.InstallationId
            && (record.OwnerProcessInstance == _runs.ProcessInstance
                ? !LiveRuns.ContainsKey(record.OwnerProcessInstance + ":" + record.RunId)
                : !_ownerIsAlive(record))).ToList();
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var age = _time.GetUtcNow() - record.StartedAtUtc;
            var options = RemoteGateOptions.Read(_configuration, _section);
            if (!string.Equals(options.Host, record.Host, StringComparison.Ordinal)
                || !string.Equals(options.Root, record.Root, StringComparison.Ordinal)) continue;
            var remote = record.Root + "/" + record.RunId;
            try
            {
                var cleanup = CleanupCommand(remote);
                await Ssh(options, Path.GetTempPath(), cleanup, TimeSpan.FromSeconds(50), ct);
                await Ssh(options, Path.GetTempPath(), "test ! -e " + Quote(remote), TimeSpan.FromSeconds(10), ct);
                _runs.Remove(record.RunId);
                _slotUsage.Released(record.RunId);
                _logger.LogWarning(OrphanedRunCancelledEvent,
                    "remote gate run orphaned and cancelled run_id={RunId} host={Host} age_seconds={AgeSeconds}",
                    record.RunId, record.Host, Math.Max(0, age.TotalSeconds));
                var watchPath = record.WatchPath ?? _scanner?.GetWatchPaths()
                    .FirstOrDefault(entry => string.Equals(entry.Name, record.Project, StringComparison.Ordinal))?.Path;
                if (watchPath is not null)
                    _activity?.Append(watchPath, new OrchestratorLogEntry
                    {
                        Ts = _time.GetUtcNow().UtcDateTime,
                        Kind = OrchestratorLogKinds.Action,
                        Topic = "remote-gate-orphan",
                        Summary = "Remote gate run orphaned and cancelled",
                        Reasoning = $"Run {record.RunId} on {record.Host}; age {Math.Max(0, age.TotalSeconds):F0} seconds.",
                    });
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                _logger.LogWarning(exception, "remote_gate_orphan_cancel_pending run_id={RunId} host={Host}",
                    record.RunId, record.Host);
            }
        }
    }

    internal static RemoteGateRunRegistry CreateDefaultRegistrySafe(TimeProvider time,
        IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        string? installationDirectory = null, string? dataDirectory = null)
    {
        try { return CreateDefaultRegistry(time, configuration, installationDirectory, dataDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or InvalidDataException)
        {
            logger.LogWarning(exception, "remote_gate_registry_unavailable");
            return RemoteGateRunRegistry.Disabled(time);
        }
    }

    internal static RemoteGateRunRegistry CreateDefaultRegistry(TimeProvider time, IConfiguration configuration,
        string? installationDirectory = null, string? dataDirectory = null)
    {
        // The service working directory belongs to the installation. Prebuilt
        // releases live below changing versioned directories, so BaseDirectory
        // would strand the previous release's run records after an upgrade.
        var configuredRoot = configuration["RemoteGate:InstallationRoot"];
        var installation = Path.GetFullPath(installationDirectory
            ?? (string.IsNullOrWhiteSpace(configuredRoot) ? Directory.GetCurrentDirectory() : configuredRoot));
        var data = dataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(data) ? installation : data;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation))).ToLowerInvariant();
        var installationId = configuration["RemoteGate:InstallationId"]?.Trim();
        if (string.IsNullOrEmpty(installationId)
            && !string.IsNullOrWhiteSpace(configuration["Connectivity:ManifestPath"]))
        {
            var manifestPath = Path.GetFullPath(configuration["Connectivity:ManifestPath"]!);
            if (File.Exists(manifestPath))
                installationId = AgentStudio.Management.InstallationConnectivityManifest
                    .Parse(File.ReadAllText(manifestPath)).InstallationId;
        }
        if (installationId is not null
            && !Regex.IsMatch(installationId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
            throw new InvalidDataException("Remote gate installation id is invalid.");
        return new RemoteGateRunRegistry(Path.Combine(root, "agentstudio", "remote-gate-runs", scope), time,
            installationId);
    }

    private static bool IsOwnerProcessAlive(RemoteGateRunRecord record)
    {
        if (record.OwnerProcessId <= 0 || record.OwnerProcessStartTicks <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(record.OwnerProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.OwnerProcessStartTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate owner process inspection");
            return false;
        }
    }

    private static string CleanupCommand(string remote)
    {
        var path = Quote(remote);
        var lockPath = Quote(remote + "/worker.lock");
        return "if [ -d " + path + " ]; then touch -- " + Quote(remote + "/cancel")
            + "; for i in $(seq 1 45); do [ -d " + path + " ] || exit 0; "
            + "if flock -n " + lockPath + " sh -c " + Quote("rm -rf -- " + path)
            + "; then break; fi; sleep 1; done; fi; test ! -e " + path;
    }

    internal static BuildTestGateResult AcceptResponse(RemoteGateResponse response,
        string runId, string digest, string host, string executor, long bundleBytes,
        TimeSpan transfer)
    {
        if (response.Version != 1 || response.RunId != runId || response.BundleSha256 != digest)
            throw new InvalidDataException("Remote gate response does not match this invocation and source bundle.");
        return response.Result with
        {
            Executor = "ssh:" + host + "/" + executor,
            Reason = response.Result.Reason
                + $" Source bundle {bundleBytes} bytes transferred in {transfer.TotalSeconds:F1}s."
        };
    }

    internal void ReportWorkerLine(string line, string runId, string host, BuildTestGateRequest request)
    {
        if (line.StartsWith("REMOTE_GATE_CAPACITY_WARNING ", StringComparison.Ordinal))
            _logger.LogWarning("remote_gate_capacity_warning host={Host} {Detail}", host, line);
        if (line == "REMOTE_GATE_ACQUIRED " + runId)
            request.OnMachineGateAcquired?.Invoke();
        if (line.StartsWith("REMOTE_GATE_SLOT " + runId + " ", StringComparison.Ordinal))
        {
            var usage = line[("REMOTE_GATE_SLOT " + runId + " ").Length..].Split('/');
            if (usage.Length == 2 && int.TryParse(usage[0], out var slot)
                && int.TryParse(usage[1], out var total) && slot >= 0 && total > slot)
            {
                _slotUsage.Acquired(runId, total);
                request.OnMachineGateSlotAcquired?.Invoke(slot, total);
            }
        }
        if (line.StartsWith("REMOTE_GATE_WAITING " + runId + " ", StringComparison.Ordinal))
        {
            var usage = line[("REMOTE_GATE_WAITING " + runId + " ").Length..].Split('/');
            if (usage.Length == 2 && int.TryParse(usage[0], out var busy)
                && int.TryParse(usage[1], out var total) && busy >= 0 && total > 0 && busy <= total)
            {
                request.OnMachineGateWaiting?.Invoke();
                request.OnMachineGateSlotWaiting?.Invoke(busy, total);
            }
        }
    }

    /// <summary>
    /// Removes this run's local folder, including the read-only pack files git
    /// writes, without following reparse points. A failure is logged and never
    /// changes the gate result.
    /// </summary>
    internal void DeleteLocalRunFolder(string local, string runId)
    {
        // The only recursively removed local path was constructed from our
        // fixed temp namespace and a generated GUID, never a request path.
        try { _deleteDirectory(local); }
        catch (Exception exception) { _logger.LogWarning(exception, "remote_gate_local_transport_cleanup_failed run_id={RunId}", runId); }
    }

    /// <summary>
    /// Sweeps abandoned run folders at most once per <see cref="SweepInterval"/>
    /// per process. Parallel gates race on the last-sweep ticks, so exactly one
    /// caller sweeps; the others continue immediately.
    /// </summary>
    internal bool TrySweepStaleRunFolders()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (last != NeverSwept && now.Ticks - last < SweepInterval.Ticks) return false;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last) return false;
        try { SweepStaleRunFolders(now); }
        catch (Exception exception) { _logger.LogWarning(exception, "remote_gate_local_transport_sweep_failed"); }
        return true;
    }

    /// <summary>
    /// Deletes direct child directories of the transport root whose name has
    /// the <c>Guid.ToString("N")</c> shape and whose last write is older than
    /// <see cref="StaleRunFolderAge"/>. Dev and Stable share this root, so the
    /// name shape and the age are the only ownership proof; files, other names
    /// and top-level reparse points are never touched.
    /// </summary>
    private void SweepStaleRunFolders(DateTime utcNow)
    {
        if (!Directory.Exists(_localRoot)) return;
        var deadline = utcNow - StaleRunFolderAge;
        var removed = 0;
        var failed = 0;
        foreach (var candidate in Directory.EnumerateDirectories(_localRoot).ToList())
        {
            if (!IsAbandonedRunFolder(candidate, deadline)) continue;
            try
            {
                _deleteDirectory(candidate);
                removed++;
            }
            catch (Exception exception)
            {
                // The other instance sharing this root may have swept it first.
                if (!Directory.Exists(candidate)) removed++;
                else
                {
                    failed++;
                    AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate stale run folder sweep");
                }
            }
        }
        _logger.Log(failed > 0 ? LogLevel.Warning : LogLevel.Information,
            "remote_gate_local_transport_sweep removed={Removed} failed={Failed}", removed, failed);
    }

    private static bool IsAbandonedRunFolder(string candidate, DateTime deadline)
    {
        try
        {
            var info = new DirectoryInfo(candidate);
            return IsRunFolderName(info.Name)
                && (info.Attributes & FileAttributes.ReparsePoint) == 0
                && info.LastWriteTimeUtc <= deadline;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder vanished or cannot be inspected; it is not a proven
            // abandoned run folder, so it is left alone.
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate stale run folder inspection");
            return false;
        }
    }

    internal static bool IsRunFolderName(string name)
        => name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Task Git(string cwd, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string>? output = null)
        => RemoteGateProcess.RunAsync("git", args, cwd, timeout, ct, output);

    private static async Task GitPrepared(string cwd, IReadOnlyList<string> args, TimeSpan budget,
        long sourceBytes, string step, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        try { await Git(cwd, args, budget, ct); }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            throw new IOException(TransportFailure("git " + step, sourceBytes, elapsed.Elapsed,
                budget, exception.Message), exception);
        }
    }

    internal static TimeSpan TransferBudget(TimeSpan configured, long bytes)
    {
        var payloadSeconds = Math.Min(Math.Ceiling(bytes / (1024d * 1024d)), 3600);
        return TimeSpan.FromSeconds(Math.Max(configured.TotalSeconds, payloadSeconds));
    }

    internal static string TransferFailure(long bytes, TimeSpan elapsed, TimeSpan budget, string cause)
        => TransportFailure("source.bundle transfer", bytes, elapsed, budget, cause);

    private static string TransportFailure(string step, long bytes, TimeSpan elapsed, TimeSpan budget, string cause)
        => FormattableString.Invariant(
            $"Remote gate {step} failed after {elapsed.TotalSeconds:F1}s ({bytes} bytes, budget {budget.TotalSeconds:F0}s): {cause}");

    internal static IReadOnlyList<string> BundleArguments(string repository, string bundle,
        IReadOnlyList<string> knownTips)
    {
        var args = new List<string> { "-C", repository, "-c", "pack.threads=2", "bundle", "create",
            bundle, "refs/heads/gate-subject" };
        foreach (var tip in knownTips)
        {
            args.Add("--not");
            args.Add(tip);
        }
        return args;
    }

    private static async Task<bool> GitIsAncestor(string cwd, string repository, string tip,
        string expected, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Git(cwd, ["-C", repository, "merge-base", "--is-ancestor", tip, expected], timeout, ct);
            return true;
        }
        catch (IOException) { return false; }
    }

    internal Task Ssh(RemoteGateOptions options, string cwd, string command,
        TimeSpan timeout, CancellationToken ct, Action<string>? output = null)
        => RunTransportProcess("ssh", ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            options.Host, command], cwd, timeout, ct, output);

    internal Task Scp(RemoteGateOptions options, string cwd, string source, string target,
        TimeSpan timeout, CancellationToken ct)
        => RunTransportProcess("scp", ["-B", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=15", source, target], cwd, timeout, ct);

    private async Task RunTransportProcess(string fileName, IReadOnlyList<string> args, string cwd,
        TimeSpan timeout, CancellationToken ct, Action<string>? output = null)
    {
        try
        {
            await _runProcess(fileName, args, cwd, timeout, ct, output);
            _slotUsage.Reached();
        }
        catch
        {
            _slotUsage.Failed();
            throw;
        }
    }

    internal static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
}

internal sealed record RemoteGateOptions(string Host, string WorkerPath, string Root, int ProcessorCount, int GateSlots)
{
    internal const string PrimarySection = "RemoteGate";

    /// <summary>
    /// Reads the gate host of <paramref name="section"/>. A shard host section
    /// (<c>RemoteGate:ShardHosts:N</c>) names its own <c>SshHost</c> and takes
    /// every other unset value from <c>RemoteGate</c>.
    /// </summary>
    internal static RemoteGateOptions Read(IConfiguration configuration, string section = PrimarySection)
    {
        string? Value(string key) => configuration[$"{section}:{key}"] ?? configuration[$"{PrimarySection}:{key}"];
        // An unparsable count is out of range, so it fails closed below.
        int Count(string key, int fallback) => Value(key) is { } raw
            ? int.TryParse(raw, out var value) ? value : -1
            : fallback;
        var host = configuration[$"{section}:SshHost"]?.Trim();
        var worker = Value("WorkerPath")?.Trim();
        var root = (Value("Root") ?? "/var/tmp/agentstudio-remote-gates").TrimEnd('/');
        var processors = Count("ProcessorCount", 2);
        var slots = Count("GateSlots", 1);
        if (host is null || !Regex.IsMatch(host, "^[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,254}$")
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !SafeRemotePath(worker) || !SafeRemotePath(root)
            || root.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2
            || processors is < 1 or > 64 || slots is < 1 or > 64)
            throw new InvalidOperationException(
                "Remote gate transport requires RemoteGate:SshHost and an absolute RemoteGate:WorkerPath on an approved remote host; local execution is disabled.");
        return new(host, worker!, root, processors, slots);
    }

    private static bool SafeRemotePath(string? value) => value is not null
        && Regex.IsMatch(value, "^/[a-zA-Z0-9_./-]+$")
        && !value.Split('/').Any(part => part is "." or "..");
}

/// <summary>Small bounded transport processes; never used to run a local build or test.</summary>
internal static class RemoteGateProcess
{
    internal static async Task RunAsync(string fileName, IReadOnlyList<string> args, string cwd,
        TimeSpan timeout, CancellationToken ct, Action<string>? output = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new IOException(fileName + " did not start.");
        var diagnostics = new Queue<string>();
        var diagnosticLock = new object();
        async Task Drain(StreamReader reader, bool stdout)
        {
            while (await reader.ReadLineAsync(bounded.Token) is { } line)
            {
                if (stdout) output?.Invoke(line);
                lock (diagnosticLock)
                {
                    diagnostics.Enqueue(line.Length > 2048 ? line[..2048] : line);
                    while (diagnostics.Count > 32) diagnostics.Dequeue();
                }
            }
        }
        var stdout = Drain(process.StandardOutput, true);
        var stderr = Drain(process.StandardError, false);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(bounded.Token);
            if (process.ExitCode != 0)
                throw new IOException(fileName + " exited " + process.ExitCode + ": " + string.Join("\n", diagnostics));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) { AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate transport child cleanup"); }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception exception) { AgentStudio.Diagnostics.SilentCatch.Note(exception, "remote gate transport pipe drain"); }
            if (!ct.IsCancellationRequested && bounded.IsCancellationRequested)
                throw new TimeoutException(fileName + " exceeded its remote gate transport budget.");
            throw;
        }
    }
}
