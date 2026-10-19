using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Chooses the outbound worker plane by observed registration health.</summary>
public sealed class ConventionalGateTransport : IRemoteGateTransport
{
    private readonly PullGateStore _store;
    private readonly IRemoteGateTransport _pull;
    private readonly IRemoteGateTransport _ssh;

    public ConventionalGateTransport(PullGateStore store, PullGateTransport pull,
        RemoteGateTransport ssh) : this(store, (IRemoteGateTransport)pull, ssh) { }

    internal ConventionalGateTransport(PullGateStore store, IRemoteGateTransport pull,
        IRemoteGateTransport ssh)
    {
        _store = store;
        _pull = pull;
        _ssh = ssh;
    }

    public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
        => (_store.HasHealthyHost ? _pull : _ssh)
            .RunAsync(request, changedFiles, profile, mode, timeout, ct);
}

/// <summary>Prepares the exact subject locally, then waits for a fenced worker result.</summary>
public sealed class PullGateTransport(PullGateStore store, TimeProvider clock,
    ILogger<PullGateTransport> logger) : IRemoteGateTransport
{
    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        var local = Path.Combine(Path.GetTempPath(), "agentstudio-pull-gate", runId);
        Directory.CreateDirectory(local);
        var infra = request.InfrastructureTimeout > TimeSpan.Zero
            ? request.InfrastructureTimeout : TimeSpan.FromMinutes(2);
        var queue = request.QueueWaitTimeout ?? timeout + infra;
        var overall = queue + timeout + infra * 4 + TimeSpan.FromMinutes(2);
        if (overall > TimeSpan.FromHours(24))
            throw new InvalidOperationException("Pull gate total budget exceeds 24 hours.");
        try
        {
            var repository = Path.Combine(local, "source.git");
            await RemoteGateProcess.RunAsync("git", ["init", "--bare", "--quiet", repository],
                local, infra, ct);
            await RemoteGateProcess.RunAsync("git",
                ["-C", repository, "fetch", "--no-tags", "--no-recurse-submodules",
                    Path.GetFullPath(request.RepositoryPath), request.ExpectedSha!],
                local, RemoteGateTransport.TransferBudget(infra, 1024L * 1024 * 1024), ct);
            await RemoteGateProcess.RunAsync("git",
                ["-C", repository, "update-ref", "refs/heads/gate-subject", request.ExpectedSha!],
                local, infra, ct);
            var bundle = Path.Combine(local, "source.bundle");
            await RemoteGateProcess.RunAsync("git", ["-C", repository, "bundle", "create", bundle, "--all"],
                local, RemoteGateTransport.TransferBudget(infra, 1024L * 1024 * 1024), ct);
            if (new FileInfo(bundle).Length > 1024L * 1024 * 1024)
                throw new InvalidDataException("Pull gate bundle exceeds 1 GiB.");
            string digest;
            await using (var input = File.OpenRead(bundle))
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            var invocation = new RemoteGateInvocation(1, runId, digest,
                request with
                {
                    RepositoryPath = "source.git", SubjectRef = "refs/heads/gate-subject",
                    WatchPath = null, JobFolderPath = null, ToolchainIdentity = null,
                    InfrastructureTimeout = infra, OnMachineGateAcquired = null,
                    OnMachineGateWaiting = null, OnMachineGateSlotAcquired = null,
                    OnMachineGateSlotWaiting = null,
                }, changedFiles, profile, mode, (int)Math.Ceiling(timeout.TotalSeconds),
                (int)Math.Ceiling(overall.TotalSeconds));
            store.Enqueue(invocation, bundle);
            var deadline = clock.GetUtcNow() + overall;
            string? lastProgress = null;
            while (clock.GetUtcNow() < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (store.Response(runId) is { } response)
                    return AcceptResponse(response, runId, digest);
                var progress = store.Progress(runId);
                if (progress != lastProgress)
                {
                    logger.LogInformation("pull_gate_progress run_id={RunId} progress={Progress}",
                        runId, progress);
                    lastProgress = progress;
                    if (progress?.StartsWith("REMOTE_GATE_ACQUIRED ", StringComparison.Ordinal) == true)
                        request.OnMachineGateAcquired?.Invoke();
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            }
            throw new TimeoutException("Pull gate result deadline expired.");
        }
        finally
        {
            store.Abandon(runId);
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }

    internal static BuildTestGateResult AcceptResponse(RemoteGateResponse response,
        string runId, string digest)
    {
        if (response.Version != 1 || response.RunId != runId || response.BundleSha256 != digest)
            throw new InvalidDataException("Pull gate result has a mismatched bundle identity.");
        return response.Result with
        {
            Executor = response.Result.Executor ?? "pull-gate",
            ServerArtifactRunId = runId,
        };
    }
}
