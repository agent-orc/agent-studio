using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>The Task Server operations the host post adapter uses (AGT-3171 DP-S8).</summary>
public interface IPostWorkAuthorityPort
{
    Task<long> AllocationVersionAsync(CancellationToken ct);
    Task<PostWorkClaimResponse> ClaimAsync(PostWorkClaimRequest request, CancellationToken ct);
    Task<PostWorkLease> RenewAsync(string attemptId, PostWorkRenewRequest request, CancellationToken ct);
    Task<PostWorkSubject> ReportAsync(string attemptId, SubmitPostWorkReportRequest request, CancellationToken ct);
}

/// <summary>Task Server client adapter for the post adapter.</summary>
public sealed class TaskServerPostWorkAuthority(TaskServerClient client, string hostId) : IPostWorkAuthorityPort
{
    public Task<long> AllocationVersionAsync(CancellationToken ct) => client.GetHostAllocationVersionAsync(hostId, ct);
    public Task<PostWorkClaimResponse> ClaimAsync(PostWorkClaimRequest request, CancellationToken ct)
        => client.ClaimPostWorkAsync(request, ct);
    public Task<PostWorkLease> RenewAsync(string attemptId, PostWorkRenewRequest request, CancellationToken ct)
        => client.RenewPostWorkAsync(attemptId, request, ct);
    public Task<PostWorkSubject> ReportAsync(string attemptId, SubmitPostWorkReportRequest request, CancellationToken ct)
        => client.ReportPostWorkAsync(attemptId, request, ct);
}

/// <summary>Runs one bounded post-work step for one fenced attempt.</summary>
public interface IPostWorkStepExecutor
{
    string Kind { get; }
    Task<PostWorkStepEvidence> RunAsync(PostWorkLease lease, PostWorkStep step, string workDirectory, CancellationToken ct);
    Task<bool> CleanupAsync(PostWorkLease lease, string workDirectory, CancellationToken ct);
}

/// <summary>Host post adapter settings. Secrets never travel through these values.</summary>
public sealed record PostWorkHostOptions(
    string Executor,
    string ContainerRuntime,
    string? Image,
    string Network,
    int LeaseSeconds)
{
    public static PostWorkHostOptions From(Func<string, string?> env, bool windows)
    {
        var executor = env("RUNNER_POST_WORK_EXECUTOR") is { Length: > 0 } configured
            ? configured.Trim().ToLowerInvariant()
            : windows ? PostWorkExecutorKinds.Process : PostWorkExecutorKinds.Container;
        if (executor is not (PostWorkExecutorKinds.Container or PostWorkExecutorKinds.Process))
            throw new ArgumentException("RUNNER_POST_WORK_EXECUTOR must be 'container' or 'process'.");
        if (windows && executor == PostWorkExecutorKinds.Container)
            throw new ArgumentException("Windows post work uses the process executor; Docker stays optional.");
        var image = env("RUNNER_POST_WORK_IMAGE");
        if (executor == PostWorkExecutorKinds.Container
            && (string.IsNullOrWhiteSpace(image) || !image.Contains("@sha256:", StringComparison.Ordinal)))
            throw new ArgumentException("RUNNER_POST_WORK_IMAGE must pin a role image digest (name@sha256:...).");
        var runtime = env("RUNNER_POST_WORK_CONTAINER_RUNTIME") is { Length: > 0 } value ? value : "podman";
        var network = env("RUNNER_POST_WORK_NETWORK") is { Length: > 0 } net ? net : "none";
        var lease = int.TryParse(env("RUNNER_POST_WORK_LEASE_SECONDS"), out var seconds) ? seconds : 120;
        return new PostWorkHostOptions(executor, runtime, image, network, Math.Clamp(lease, 30, 900));
    }
}

/// <summary>
/// The host agent's post adapter. It claims eligible post-work subjects with
/// the host's current allocation version, runs the bounded steps in a fresh
/// container (Linux) or an isolated process (Windows), renews its lease while
/// running and reports evidence and a proposed outcome. A lost lease stops
/// the steps and suppresses the report; lifecycle effects stay server owned.
/// </summary>
public sealed class PostWorkDaemon
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IPostWorkAuthorityPort _authority;
    private readonly IPostWorkStepExecutor _executor;
    private readonly string _executorId;
    private readonly Func<string> _instanceId;
    private readonly string _stateDir;
    private readonly int _leaseSeconds;
    private readonly TimeSpan _poll;
    private readonly Action<string> _log;

    public PostWorkDaemon(IPostWorkAuthorityPort authority, IPostWorkStepExecutor executor, string executorId,
        Func<string> instanceId, string stateDir, int leaseSeconds, TimeSpan poll, Action<string> log)
    {
        _authority = authority;
        _executor = executor;
        _executorId = executorId;
        _instanceId = instanceId;
        _stateDir = stateDir;
        _leaseSeconds = leaseSeconds;
        _poll = poll;
        _log = log;
    }

    public static string ClaimPath(string stateDir) => Path.Combine(stateDir, "post-work-claim.json");

    public static PostWorkLease? ReadPersistedClaim(string stateDir)
        => File.Exists(ClaimPath(stateDir))
            ? JsonSerializer.Deserialize<PostWorkLease>(File.ReadAllText(ClaimPath(stateDir)), Json)
            : null;

    public async Task RunAsync(CancellationToken shutdown)
    {
        await RecoverPersistedClaimAsync(shutdown);
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                // A host drain is a claim barrier: no new claim, then acknowledge zero.
                if (HostRoleDrainControl.ReadRequest(_stateDir, "post") is { } drain)
                {
                    HostRoleDrainControl.Acknowledge(_stateDir, "post", drain, 0);
                    await Task.Delay(_poll, shutdown);
                    continue;
                }
                var version = await _authority.AllocationVersionAsync(shutdown);
                var claim = await _authority.ClaimAsync(
                    new PostWorkClaimRequest(_executorId, _instanceId(), _leaseSeconds, version), shutdown);
                if (claim is { Status: "claimed", Subject: { } subject, Lease: { } lease })
                    await ExecuteAsync(subject, lease, shutdown);
                else
                    await Task.Delay(_poll, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"post-work poll failed: {exception.GetType().Name}: {exception.Message}");
                try { await Task.Delay(_poll, shutdown); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>
    /// Runs one claimed subject. Returns the accepted report, or null when the
    /// lease was lost and the report was suppressed.
    /// </summary>
    internal async Task<PostWorkReport?> ExecuteAsync(PostWorkSubject subject, PostWorkLease lease, CancellationToken shutdown)
    {
        Persist(lease);
        var workDirectory = Path.Combine(_stateDir, "post-work", lease.AttemptId + "-" + lease.Fence);
        Directory.CreateDirectory(workDirectory);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "subject.json"),
            JsonSerializer.Serialize(new { subject.SubjectId, subject.TaskId, subject.Artifacts, subject.PolicyDigest }, Json),
            CancellationToken.None);
        var authority = new PostWorkAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence,
            lease.AuthorityEpoch);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = RenewUntilDoneAsync(lease, authority, lost);
        var evidence = new List<PostWorkStepEvidence>();
        try
        {
            foreach (var step in subject.Steps)
            {
                var result = await _executor.RunAsync(lease, step, workDirectory, lost.Token);
                evidence.Add(result);
                if (result.Status != "passed") break;
            }
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested)
        {
            _log($"post-work attempt={lease.AttemptId} step cancelled after lease loss or shutdown");
        }
        var leaseLost = lost.IsCancellationRequested && !shutdown.IsCancellationRequested;
        await lost.CancelAsync();
        await heartbeat;
        var clean = await _executor.CleanupAsync(lease, workDirectory, CancellationToken.None);
        if (shutdown.IsCancellationRequested && !leaseLost)
        {
            // Restart recovery: the lease expires on the server and the subject is reclaimable.
            _log($"post-work attempt={lease.AttemptId} interrupted by shutdown; lease left to expire");
            return null;
        }
        if (leaseLost || _renewalRefused)
        {
            _log($"post-work attempt={lease.AttemptId} fence={lease.Fence} lost its lease; report suppressed");
            ClearPersisted();
            return null;
        }
        var report = PostWorkProposalPolicy.Propose(evidence, subject.Steps.Count, _executor.Kind, clean, DateTime.UtcNow);
        try
        {
            await _authority.ReportAsync(lease.AttemptId, new SubmitPostWorkReportRequest(authority, report), shutdown);
            _log($"post-work attempt={lease.AttemptId} reported outcome={report.ProposedOutcome} reason={report.Reason}");
        }
        catch (TaskServerException exception) when (exception.ErrorCode is "post-work-lease-lost" or "stale-fence")
        {
            _log($"post-work attempt={lease.AttemptId} report refused ({exception.ErrorCode}); late report is inert");
            report = null;
        }
        ClearPersisted();
        return report;
    }

    private bool _renewalRefused;

    /// <summary>Test seam for the renewal cadence; null in production.</summary>
    internal TimeSpan? RenewIntervalOverride { get; set; }

    private async Task RenewUntilDoneAsync(PostWorkLease lease, PostWorkAuthority authority, CancellationTokenSource lost)
    {
        _renewalRefused = false;
        var interval = RenewIntervalOverride ?? TimeSpan.FromSeconds(Math.Max(5, _leaseSeconds / 3));
        while (!lost.IsCancellationRequested)
        {
            try { await Task.Delay(interval, lost.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                await _authority.RenewAsync(lease.AttemptId, new PostWorkRenewRequest(authority, _leaseSeconds), lost.Token);
            }
            catch (TaskServerException exception) when (exception.ErrorCode is "post-work-lease-lost" or "stale-fence")
            {
                _renewalRefused = true;
                await lost.CancelAsync();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                // Transient: keep working until the server refuses or the lease runs out.
                _log($"post-work renew failed attempt={lease.AttemptId}: {exception.GetType().Name}");
            }
        }
    }

    private async Task RecoverPersistedClaimAsync(CancellationToken ct)
    {
        if (ReadPersistedClaim(_stateDir) is not { } lease) return;
        // The earlier process cannot prove its steps finished; clean its
        // container or process tree and let the server lease expire. The
        // subject is then reclaimed under a newer fence.
        var workDirectory = Path.Combine(_stateDir, "post-work", lease.AttemptId + "-" + lease.Fence);
        await _executor.CleanupAsync(lease, workDirectory, ct);
        _log($"post-work recovered attempt={lease.AttemptId} fence={lease.Fence}; cleaned and left to the authority");
        ClearPersisted();
    }

    private void Persist(PostWorkLease lease)
    {
        Directory.CreateDirectory(_stateDir);
        var temporary = ClaimPath(_stateDir) + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(lease, Json));
        File.Move(temporary, ClaimPath(_stateDir), overwrite: true);
    }

    private void ClearPersisted() => File.Delete(ClaimPath(_stateDir));
}

/// <summary>
/// Pure mapping from step evidence to the host's proposed outcome. The host
/// never decides pass, rework or human review; the server applies its own
/// decision. A step failure is evidence for that decision, a timeout or an
/// unusable executor asks for a later retry.
/// </summary>
public static class PostWorkProposalPolicy
{
    public static PostWorkReport Propose(
        IReadOnlyList<PostWorkStepEvidence> evidence, int plannedSteps, string executorKind, bool clean, DateTime now)
    {
        var cleanup = clean ? "clean" : "dirty";
        var timedOut = evidence.FirstOrDefault(item => item.Status == "timed-out");
        if (timedOut is not null)
            return new(PostWorkOutcomes.Deferred, "step-timed-out:" + timedOut.StepId, now.AddMinutes(2),
                executorKind, evidence, cleanup);
        var unavailable = evidence.FirstOrDefault(item => item.Status == "executor-unavailable");
        if (unavailable is not null)
            return new(PostWorkOutcomes.Blocked, "executor-unavailable:" + unavailable.StepId, null,
                executorKind, evidence, cleanup);
        var failed = evidence.FirstOrDefault(item => item.Status == "failed");
        if (failed is not null)
            return new(PostWorkOutcomes.Decided, "step-failed:" + failed.StepId, null, executorKind, evidence, cleanup);
        return evidence.Count == plannedSteps
            ? new(PostWorkOutcomes.Decided, plannedSteps == 0 ? "no-steps" : "steps-passed", null,
                executorKind, evidence, cleanup)
            : new(PostWorkOutcomes.Deferred, "steps-incomplete", now.AddMinutes(1), executorKind, evidence, cleanup);
    }
}

/// <summary>Pure argv for one fresh, bounded work container per attempt step.</summary>
public static class PostWorkContainerCommand
{
    public static string Name(PostWorkLease lease, PostWorkStep step)
        => $"pw-{lease.AttemptId}-{lease.Fence}-{Sanitize(step.StepId)}";

    public static IReadOnlyList<string> Run(
        PostWorkLease lease, PostWorkStep step, string image, string network, string workDirectory)
    {
        // The AGT-3019 grant is in half cores; memory follows the profile floor of 1 GiB per half core.
        var halfCores = Math.Max(1, lease.GrantHalfCores);
        return
        [
            "run", "--rm", "--name", Name(lease, step),
            "--label", "agent-studio.post-work.attempt=" + lease.AttemptId,
            "--label", "agent-studio.post-work.fence=" + lease.Fence,
            "--network", network,
            "--read-only", "--user", "65532:65532", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", "256",
            "--cpus", (halfCores / 2.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            "--memory", halfCores + "g",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m",
            "--mount", $"type=bind,src={workDirectory},dst=/work",
            "--workdir", "/work",
            image, step.FileName, .. step.Arguments,
        ];
    }

    /// <summary>Removes every container of this attempt, including orphans of a crashed run.</summary>
    public static IReadOnlyList<string> RemoveByAttempt(PostWorkLease lease)
        => ["ps", "-aq", "--filter", "label=agent-studio.post-work.attempt=" + lease.AttemptId];

    private static string Sanitize(string value)
        => new(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').Take(40).ToArray());
}

/// <summary>Linux executor: one fresh rootless container per step, removed after the attempt.</summary>
public sealed class ContainerPostWorkStepExecutor(PostWorkHostOptions options, Action<string> log) : IPostWorkStepExecutor
{
    public string Kind => PostWorkExecutorKinds.Container;

    public async Task<PostWorkStepEvidence> RunAsync(
        PostWorkLease lease, PostWorkStep step, string workDirectory, CancellationToken ct)
    {
        var name = PostWorkContainerCommand.Name(lease, step);
        var result = await PostWorkProcess.RunAsync(options.ContainerRuntime,
            PostWorkContainerCommand.Run(lease, step, options.Image!, options.Network, workDirectory),
            workDirectory, TimeSpan.FromSeconds(step.TimeoutSeconds), ct,
            onTimeout: () => PostWorkProcess.RunQuietAsync(options.ContainerRuntime, ["kill", name]));
        return result.ToEvidence(step.StepId);
    }

    public async Task<bool> CleanupAsync(PostWorkLease lease, string workDirectory, CancellationToken ct)
    {
        var listed = await PostWorkProcess.RunAsync(options.ContainerRuntime,
            PostWorkContainerCommand.RemoveByAttempt(lease), Path.GetTempPath(), TimeSpan.FromSeconds(30), ct);
        foreach (var id in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await PostWorkProcess.RunQuietAsync(options.ContainerRuntime, ["rm", "-f", id]);
        var remaining = await PostWorkProcess.RunAsync(options.ContainerRuntime,
            PostWorkContainerCommand.RemoveByAttempt(lease), Path.GetTempPath(), TimeSpan.FromSeconds(30), ct);
        var directoryRemoved = PostWorkProcess.TryDeleteDirectory(workDirectory, log);
        return remaining.ExitCode == 0 && remaining.Output.Trim().Length == 0 && directoryRemoved;
    }
}

/// <summary>Windows executor: an isolated process tree in the attempt directory, killed on timeout.</summary>
public sealed class ProcessPostWorkStepExecutor(Action<string> log) : IPostWorkStepExecutor
{
    public string Kind => PostWorkExecutorKinds.Process;

    public async Task<PostWorkStepEvidence> RunAsync(
        PostWorkLease lease, PostWorkStep step, string workDirectory, CancellationToken ct)
        => (await PostWorkProcess.RunAsync(step.FileName, step.Arguments, workDirectory,
            TimeSpan.FromSeconds(step.TimeoutSeconds), ct)).ToEvidence(step.StepId);

    public Task<bool> CleanupAsync(PostWorkLease lease, string workDirectory, CancellationToken ct)
        => Task.FromResult(PostWorkProcess.TryDeleteDirectory(workDirectory, log));
}

internal sealed record PostWorkProcessResult(string Status, int? ExitCode, long DurationMs, string Output)
{
    public PostWorkStepEvidence ToEvidence(string stepId)
        => new(stepId, Status, ExitCode, DurationMs,
            "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Output))).ToLowerInvariant(),
            Output.Length <= 4000 ? Output : Output[^4000..]);
}

internal static class PostWorkProcess
{
    public static async Task<PostWorkProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, string workDirectory, TimeSpan timeout,
        CancellationToken ct, Func<Task>? onTimeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var watch = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start."); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new PostWorkProcessResult("executor-unavailable", null, 0, exception.Message);
        }
        using (process)
        {
            var output = new StringBuilder();
            process.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (output) output.AppendLine(line.Data); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (output) output.AppendLine(line.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                if (onTimeout is not null && !ct.IsCancellationRequested) await onTimeout();
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException exception)
                {
                    Trace.TraceInformation("post-work process exited before timeout cleanup: {0}", exception.Message);
                }
                await process.WaitForExitAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                lock (output) return new PostWorkProcessResult("timed-out", null, watch.ElapsedMilliseconds, output.ToString());
            }
            process.WaitForExit();
            lock (output)
                return new PostWorkProcessResult(process.ExitCode == 0 ? "passed" : "failed", process.ExitCode,
                    watch.ElapsedMilliseconds, output.ToString());
        }
    }

    public static async Task RunQuietAsync(string fileName, IEnumerable<string> arguments)
    {
        try { await RunAsync(fileName, arguments, Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (Exception exception)
        {
            Trace.TraceWarning("post-work container cleanup command failed: {0}", exception.Message);
        }
    }

    public static bool TryDeleteDirectory(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"post-work cleanup could not remove {path}: {exception.GetType().Name}");
            return false;
        }
    }
}
