using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Explicit Linux-only gate CLI. It exits before the API host and its background
/// services are created. No listener or task-workspace authority is started.
/// </summary>
public static class RemoteGateWorker
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(35);
    internal static readonly TimeSpan HeartbeatPollInterval = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(string requestPath,
        Action<string>? progress = null, CancellationToken shutdown = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("The remote gate worker requires Linux; local backend execution is disabled.");
            return 2;
        }
        requestPath = Path.GetFullPath(requestPath);
        var directory = Path.GetDirectoryName(requestPath)!;
        if (Path.GetFileName(requestPath) != "request.json"
            || !Regex.IsMatch(Path.GetFileName(directory), "^[0-9a-f]{32}$")
            || new FileInfo(requestPath).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Remote gate request path or size is invalid.");
        var invocation = JsonSerializer.Deserialize<RemoteGateInvocation>(
            await File.ReadAllTextAsync(requestPath), RemoteBuildTestGateRunner.Json)
            ?? throw new InvalidDataException("Remote gate invocation is empty.");
        Validate(invocation, Path.GetFileName(directory));
        WarnIfOversubscribed(invocation.GateSlots, invocation.ProcessorCount);
        var repository = Path.Combine(directory, "source.git");
        var bundle = Path.Combine(directory, "source.bundle");
        var artifactDirectory = Path.Combine(directory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var slotHeld = 0;
        var request = invocation.Request with
        {
            RepositoryPath = repository,
            SubjectRef = "refs/heads/gate-subject",
            JobFolderPath = null,
            WatchPath = null,
            ToolchainIdentity = null,
            OnMachineGateWaiting = null,
            OnMachineGateSlotWaiting = (busy, total) =>
                Report($"REMOTE_GATE_WAITING {invocation.RunId} {busy}/{total}"),
            OnMachineGateSlotAcquired = (slot, total) =>
            {
                Interlocked.Exchange(ref slotHeld, 1);
                Report($"REMOTE_GATE_SLOT {invocation.RunId} {slot}/{total}");
            },
            OnMachineGateAcquired = () => Report("REMOTE_GATE_ACQUIRED " + invocation.RunId),
            GateSlots = invocation.GateSlots,
        };
        void Report(string line)
        {
            Console.WriteLine(line);
            progress?.Invoke(line);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        lifetime.CancelAfter(TimeSpan.FromSeconds(invocation.OverallTimeoutSeconds));
        using var cancelWatcher = new CancellationTokenSource();
        var shutdownRequested = 0;
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Interlocked.Exchange(ref shutdownRequested, 1);
            lifetime.Cancel();
        });
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            Interlocked.Exchange(ref shutdownRequested, 1);
            lifetime.Cancel();
        });
        var watch = WatchCancellationAsync(directory, invocation.InstallationId, lifetime, cancelWatcher.Token,
            TimeProvider.System);
        using var logging = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = logging.CreateLogger<RemoteGateWorkerLog>();
        BuildTestGateResult result;
        var sessionLost = false;
        var cleanupFailed = false;
        try
        {
            await using (var source = File.OpenRead(bundle))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(source, lifetime.Token)).ToLowerInvariant();
                if (!string.Equals(actual, invocation.BundleSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Remote gate source bundle digest mismatch.");
            }
            // The clone is private to this GUID invocation. Its only advertised
            // branch was created from the requested exact commit, never a live
            // integration ref. BuildTestGateRunner independently proves HEAD.
            if (invocation.CachePath is { } cache && Directory.Exists(cache))
            {
                await RemoteGateProcess.RunAsync("git", ["clone", "--bare", "--quiet", cache, repository],
                    directory, request.InfrastructureTimeout, lifetime.Token);
                await RemoteGateProcess.RunAsync("git", ["-C", repository, "fetch", "--no-tags", bundle,
                    "refs/heads/gate-subject:refs/heads/gate-subject"],
                    directory, request.InfrastructureTimeout, lifetime.Token);
                await RemoteGateProcess.RunAsync("git", ["-C", repository, "symbolic-ref", "HEAD", "refs/heads/gate-subject"],
                    directory, request.InfrastructureTimeout, lifetime.Token);
                try
                {
                    await RemoteGateProcess.RunAsync("git", ["-C", cache, "fetch", "--no-tags", repository,
                        "refs/heads/gate-subject:refs/heads/gate-cache/" + request.ExpectedSha],
                        directory, request.InfrastructureTimeout, lifetime.Token);
                }
                catch (Exception exception) when (!lifetime.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "remote_gate_cache_update_failed run_id={RunId}", invocation.RunId);
                }
            }
            else
            {
                await RemoteGateProcess.RunAsync("git", ["clone", "--bare", "--quiet", bundle, repository],
                    directory, request.InfrastructureTimeout, lifetime.Token);
            }
            var runner = new BuildTestGateRunner(logging.CreateLogger<BuildTestGateRunner>());
            var previousArtifactDirectory = GateArtifactOutput.Current;
            GateArtifactOutput.Current = artifactDirectory;
            try
            {
                result = await runner.RunAsync(request, invocation.ChangedFiles, invocation.Profile,
                    invocation.Mode, TimeSpan.FromSeconds(invocation.TimeoutSeconds), lifetime.Token);
            }
            finally { GateArtifactOutput.Current = previousArtifactDirectory; }
        }
        catch (OperationCanceledException)
        {
            result = RemoteBuildTestGateRunner.Failure(request, BuildTestGateFailureKind.Cancellation,
                "Remote gate worker stopped after cancellation or its overall deadline.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "remote_gate_worker_failed run_id={RunId}", invocation.RunId);
            result = RemoteBuildTestGateRunner.Failure(request, BuildTestGateFailureKind.Environment,
                "Remote gate worker failed: " + exception.Message);
        }
        finally
        {
            if (Volatile.Read(ref slotHeld) != 0 && lifetime.IsCancellationRequested)
                Report($"REMOTE_GATE_SLOT {invocation.RunId} release");
            cancelWatcher.Cancel();
            sessionLost = await watch;
            try
            {
                if (Directory.Exists(repository)) Directory.Delete(repository, recursive: true);
                if (File.Exists(bundle)) File.Delete(bundle);
            }
            catch (Exception exception)
            {
                cleanupFailed = true;
                logger.LogWarning(exception, "remote_gate_worker_cleanup_failed run_id={RunId}", invocation.RunId);
            }
        }
        if (cleanupFailed)
            result = result with
            {
                Verdict = BuildTestGateVerdict.Fail,
                FailureKind = BuildTestGateFailureKind.Environment,
                Reason = "Remote gate source cleanup failed; gate remains blocked. " + result.Reason,
            };
        result = AttachCancellationSource(result, Volatile.Read(ref shutdownRequested) != 0);
        result = result with { Executor = "remote-worker:" + Environment.MachineName };
        var response = new RemoteGateResponse(1, invocation.RunId, invocation.BundleSha256, result);
        var responsePath = Path.Combine(directory, "response.json");
        await File.WriteAllTextAsync(responsePath + ".tmp", JsonSerializer.Serialize(response, RemoteBuildTestGateRunner.Json));
        File.Move(responsePath + ".tmp", responsePath, overwrite: true);
        if (sessionLost)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Remote gate session-loss cleanup failed: " + exception.Message);
            }
        }
        return 0;
    }

    internal static BuildTestGateResult AttachCancellationSource(
        BuildTestGateResult result, bool shutdownRequested)
        => result.FailureKind == BuildTestGateFailureKind.Cancellation && shutdownRequested
            && result.CancellationReason is null
            ? result with { CancellationReason = GateCancellationReasons.Shutdown }
            : result;

    internal static void Validate(RemoteGateInvocation invocation, string directoryName)
    {
        if (invocation.Version != 1 || invocation.RunId != directoryName
            || !Regex.IsMatch(invocation.RunId, "^[0-9a-f]{32}$")
            || (invocation.InstallationId.Length > 0
                && !Regex.IsMatch(invocation.InstallationId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
            || !Regex.IsMatch(invocation.BundleSha256, "^[0-9a-f]{64}$")
            || invocation.Request is null || !invocation.Request.RequireExactSubject
            || !RemoteBuildTestGateRunner.ValidSha(invocation.Request.ExpectedSha)
            || invocation.TimeoutSeconds is < 1 or > 43200
            || invocation.OverallTimeoutSeconds < invocation.TimeoutSeconds
            || invocation.OverallTimeoutSeconds > 86400
            || invocation.Request.InfrastructureTimeout <= TimeSpan.Zero
            || invocation.GateSlots is < 1 or > 64
            || invocation.ProcessorCount is < 1 or > 64
            || (invocation.CachePath is { } cachePath
                && (!Regex.IsMatch(cachePath, "^/[a-zA-Z0-9_./-]+/cache/[0-9a-f]{64}$")
                    || cachePath.Split('/').Any(part => part is "." or "..")))
            || invocation.Mode == PostStepMode.Off
            || invocation.Request.Sharding is { } sharding
                && (sharding.Part is { IsValid: false } || sharding.Timings is { IsWithinBounds: false }))
            throw new InvalidDataException("Remote gate invocation identity, subject, or budget is invalid.");
    }

    private static void WarnIfOversubscribed(int slots, int processors)
    {
        var hostCores = GateHostCapacity.OnlineCores(exception =>
            Console.Error.WriteLine("Could not read online host cores: " + exception.Message));
        if (IsOversubscribed(slots, processors, hostCores))
            Console.WriteLine($"REMOTE_GATE_CAPACITY_WARNING slots={slots} processors={processors} host_cores={hostCores}");
    }

    internal static bool IsOversubscribed(int slots, int processors, int hostCores)
        => (long)slots * processors > hostCores;

    internal static bool IsHeartbeatStale(DateTimeOffset now, DateTimeOffset lastWriteUtc)
        => now - lastWriteUtc > HeartbeatTimeout;

    internal static async Task<bool> WatchCancellationAsync(string directory, string installationId,
        CancellationTokenSource execution, CancellationToken ct, TimeProvider time)
    {
        using var timer = new PeriodicTimer(HeartbeatPollInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (installationId.Length > 0)
                {
                    var heartbeat = Path.Combine(directory, "heartbeat");
                    if (!File.Exists(heartbeat))
                    {
                        execution.Cancel();
                        return true;
                    }
                    DateTimeOffset lastWrite;
                    try { lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(heartbeat), TimeSpan.Zero); }
                    catch (IOException) { execution.Cancel(); return true; }
                    if (IsHeartbeatStale(time.GetUtcNow(), lastWrite))
                    {
                        execution.Cancel();
                        return true;
                    }
                }
                if (File.Exists(Path.Combine(directory, "cancel")))
                {
                    execution.Cancel();
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The owner stops this watcher after gate cleanup.
            return false;
        }
        return false;
    }

    private sealed class RemoteGateWorkerLog { }
}

internal static class GateArtifactOutput
{
    private static readonly AsyncLocal<string?> Directory = new();
    internal static string? Current
    {
        get => Directory.Value;
        set => Directory.Value = value;
    }
}
