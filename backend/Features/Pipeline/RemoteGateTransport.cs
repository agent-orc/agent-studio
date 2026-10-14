using System.Diagnostics;
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
    private readonly Action<string> _deleteDirectory;
    private readonly GateSlotUsageTracker _slotUsage;
    private long _lastSweepTicks = NeverSwept;

    public RemoteGateTransport(IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        TimeProvider? timeProvider = null, GateSlotUsageTracker? slotUsage = null)
        : this(configuration, logger, timeProvider ?? TimeProvider.System,
            Path.Combine(Path.GetTempPath(), "agentstudio-gate-transport"),
            WorktreeDirectoryCleanup.DeleteDirectoryWithoutFollowingReparsePoints, slotUsage)
    {
    }

    internal RemoteGateTransport(IConfiguration configuration, ILogger<RemoteGateTransport> logger,
        TimeProvider timeProvider, string localRoot, Action<string> deleteDirectory,
        GateSlotUsageTracker? slotUsage = null)
    {
        _configuration = configuration;
        _logger = logger;
        _time = timeProvider;
        _localRoot = localRoot;
        _deleteDirectory = deleteDirectory;
        _slotUsage = slotUsage ?? new GateSlotUsageTracker();
    }

    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile,
        PostStepMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var options = RemoteGateOptions.Read(_configuration);
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
        var remoteCreated = false;
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
            await File.WriteAllTextAsync(Path.Combine(local, "request.json"),
                JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json), ct);
            await Ssh(options, local,
                "umask 077; mkdir -p -- " + Quote(options.Root) + "; mkdir -- " + Quote(remote), infra, ct);
            remoteCreated = true;
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
            _logger.LogInformation("remote_gate_dispatched run_id={RunId} host={Host} expected_sha={ExpectedSha} bundle_sha256={Digest} bundle_bytes={BundleBytes} transfer_ms={TransferMs}",
                runId, options.Host, request.ExpectedSha, digest, bundleBytes, transfer.ElapsedMilliseconds);
            var command = "cd " + Quote(remote) + " && exec timeout --signal=TERM --kill-after=120s "
                + overallSeconds + "s env DOTNET_PROCESSOR_COUNT=" + options.ProcessorCount
                + " dotnet " + Quote(options.WorkerPath)
                + " --remote-build-test-gate-worker " + Quote(remote + "/request.json");
            await Ssh(options, local, command, TimeSpan.FromSeconds(overallSeconds + 150), ct,
                line => ReportWorkerLine(line, runId, options.Host, request));
            await Scp(options, local, options.Host + ":" + remote + "/response.json", "response.json", infra, ct);
            var responsePath = Path.Combine(local, "response.json");
            if (new FileInfo(responsePath).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Remote gate response exceeds the evidence limit.");
            var response = JsonSerializer.Deserialize<RemoteGateResponse>(
                await File.ReadAllTextAsync(responsePath, ct), RemoteBuildTestGateRunner.Json)
                ?? throw new InvalidDataException("Remote gate response is empty.");
            if (response.Version != 1 || response.RunId != runId || response.BundleSha256 != digest)
                throw new InvalidDataException("Remote gate response does not match this invocation and source bundle.");
            return response.Result with
            {
                Executor = "ssh:" + options.Host + "/" + request.Executor,
                Reason = response.Result.Reason + $" Source bundle {bundleBytes} bytes transferred in {transfer.Elapsed.TotalSeconds:F1}s."
            };
        }
        finally
        {
            _slotUsage.Released(runId);
            if (remoteCreated)
            {
                // Cancellation is a worker-owned marker, not an arbitrary PID
                // kill. The worker observes it while queued or executing. An
                // independent host timeout still bounds a broken SSH link.
                var cleanup = "if [ -f " + Quote(remote + "/response.json") + " ]; then rm -rf -- "
                    + Quote(remote) + "; else touch -- " + Quote(remote + "/cancel") + "; fi";
                try { await Ssh(options, local, cleanup, TimeSpan.FromSeconds(20), CancellationToken.None); }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "remote_gate_cleanup_pending host={Host} run_id={RunId}", options.Host, runId);
                }
            }
            DeleteLocalRunFolder(local, runId);
        }
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

    private static Task Ssh(RemoteGateOptions options, string cwd, string command,
        TimeSpan timeout, CancellationToken ct, Action<string>? output = null)
        => RemoteGateProcess.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            options.Host, command], cwd, timeout, ct, output);

    private static Task Scp(RemoteGateOptions options, string cwd, string source, string target,
        TimeSpan timeout, CancellationToken ct)
        => RemoteGateProcess.RunAsync("scp", ["-B", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=15", source, target], cwd, timeout, ct);

    internal static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
}

internal sealed record RemoteGateOptions(string Host, string WorkerPath, string Root, int ProcessorCount, int GateSlots)
{
    internal static RemoteGateOptions Read(IConfiguration configuration)
    {
        var host = configuration["RemoteGate:SshHost"]?.Trim();
        var worker = configuration["RemoteGate:WorkerPath"]?.Trim();
        var root = (configuration["RemoteGate:Root"] ?? "/var/tmp/agentstudio-remote-gates").TrimEnd('/');
        var processors = configuration.GetValue("RemoteGate:ProcessorCount", 2);
        var slots = configuration.GetValue("RemoteGate:GateSlots", 1);
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
