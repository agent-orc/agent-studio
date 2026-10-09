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
    public static async Task<int> RunAsync(string requestPath)
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
        var repository = Path.Combine(directory, "source.git");
        var bundle = Path.Combine(directory, "source.bundle");
        var request = invocation.Request with
        {
            RepositoryPath = repository,
            SubjectRef = "refs/heads/gate-subject",
            JobFolderPath = null,
            WatchPath = null,
            ToolchainIdentity = null,
            OnMachineGateWaiting = null,
            OnMachineGateAcquired = () => Console.WriteLine("REMOTE_GATE_ACQUIRED " + invocation.RunId),
        };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(invocation.OverallTimeoutSeconds));
        using var cancelWatcher = new CancellationTokenSource();
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            lifetime.Cancel();
        });
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            lifetime.Cancel();
        });
        var watch = WatchCancellationAsync(directory, lifetime, cancelWatcher.Token);
        using var logging = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = logging.CreateLogger<RemoteGateWorkerLog>();
        BuildTestGateResult result;
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
            result = await runner.RunAsync(request, invocation.ChangedFiles, invocation.Profile,
                invocation.Mode, TimeSpan.FromSeconds(invocation.TimeoutSeconds), lifetime.Token);
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
            cancelWatcher.Cancel();
            await watch;
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
        result = result with { Executor = "remote-worker:" + Environment.MachineName };
        var response = new RemoteGateResponse(1, invocation.RunId, invocation.BundleSha256, result);
        var responsePath = Path.Combine(directory, "response.json");
        await File.WriteAllTextAsync(responsePath + ".tmp", JsonSerializer.Serialize(response, RemoteBuildTestGateRunner.Json));
        File.Move(responsePath + ".tmp", responsePath, overwrite: true);
        return 0;
    }

    internal static void Validate(RemoteGateInvocation invocation, string directoryName)
    {
        if (invocation.Version != 1 || invocation.RunId != directoryName
            || !Regex.IsMatch(invocation.RunId, "^[0-9a-f]{32}$")
            || !Regex.IsMatch(invocation.BundleSha256, "^[0-9a-f]{64}$")
            || invocation.Request is null || !invocation.Request.RequireExactSubject
            || !RemoteBuildTestGateRunner.ValidSha(invocation.Request.ExpectedSha)
            || invocation.TimeoutSeconds is < 1 or > 43200
            || invocation.OverallTimeoutSeconds < invocation.TimeoutSeconds
            || invocation.OverallTimeoutSeconds > 86400
            || invocation.Request.InfrastructureTimeout <= TimeSpan.Zero
            || (invocation.CachePath is { } cachePath
                && (!Regex.IsMatch(cachePath, "^/[a-zA-Z0-9_./-]+/cache/[0-9a-f]{64}$")
                    || cachePath.Split('/').Any(part => part is "." or "..")))
            || invocation.Mode == PostStepMode.Off)
            throw new InvalidDataException("Remote gate invocation identity, subject, or budget is invalid.");
    }

    private static async Task WatchCancellationAsync(string directory,
        CancellationTokenSource execution, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!File.Exists(Path.Combine(directory, "cancel"))) continue;
                execution.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The owner stops this watcher after gate cleanup.
            return;
        }
    }

    private sealed class RemoteGateWorkerLog { }
}
