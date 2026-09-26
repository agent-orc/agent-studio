using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal sealed record DetachedReviewSpec(
    ReviewSubjectDto Subject,
    ReviewLeaseDto Lease,
    string ReviewWorkDir,
    IReadOnlyList<string> ReviewCredentialEnvironment,
    string? CliType = null,
    string? CodexCliBin = null,
    string? ClaudeCliBin = null,
    // The detached worker rebuilds its RunnerOptions from this spec, not from the
    // daemon's environment, so the hang detectors travel with the spec. A null
    // (spec written by an older daemon) falls back to the worker's environment.
    int? CommandSilenceWatchdogSeconds = null,
    int? ReviewNoCpuProgressSeconds = null);

internal sealed record DetachedReviewIdentity(
    int ProcessId,
    DateTime ProcessStartedAtUtc,
    string WorkspacePath,
    // AGT-2863: the worker's own answer to "which build am I". Null means the
    // record was written before this provenance existed.
    string? ReleaseId = null,
    string? BinaryPath = null);

internal sealed record DetachedReviewResult(
    ReviewExecutionEvidence? Evidence,
    string? FailureClassification,
    string? Summary,
    DateTime CompletedAtUtc);

/// <summary>
/// Runs a prepared review plan behind a runner-owned detached worker. The
/// worker writes identity, command checkpoints, and its terminal evidence to
/// durable files so a replacement daemon can continue the same fenced attempt
/// without repeating completed test work.
/// </summary>
internal sealed class DurableReviewProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    private DurableReviewProcess(
        string directory,
        int processId,
        DateTime processStartedAtUtc,
        string releaseId = ReviewWorkerProvenancePolicy.Unknown,
        string binaryPath = ReviewWorkerProvenancePolicy.Unknown)
    {
        _directory = directory;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
        ReleaseId = releaseId;
        BinaryPath = binaryPath;
    }

    public int ProcessId { get; }
    public DateTime ProcessStartedAtUtc { get; }

    /// <summary>Release of the agent-host build this worker executes.</summary>
    public string ReleaseId { get; }

    /// <summary>Resolved executable the worker runs, never the promote symlink.</summary>
    public string BinaryPath { get; }
    public string ResultPath => Path.Combine(_directory, "review-result.json");
    public string ProgressPath => Path.Combine(_directory, "review-progress.json");
    public string IdentityPath => Path.Combine(_directory, "review-worker.json");

    public static DurableReviewProcess Start(
        RunnerOptions options,
        PersistedReviewSlot slot,
        Action<string>? log = null)
    {
        var claim = slot.Claim;
        var spec = BuildSpec(options, claim);
        Directory.CreateDirectory(slot.WorkerDirectory);
        var specPath = Path.Combine(slot.WorkerDirectory, "review-spec.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException(
                             "Cannot resolve the runner executable for detached review launch.");
        var managedHost = string.Equals(
                              Path.GetFileNameWithoutExtension(executable),
                              "dotnet",
                              StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        // AGT-2866: a review worker carries the same per-slot envelope as a
        // coding worker. The shell wrapper execs, so the pid recorded below is
        // still the worker's pid and the reattachment proofs are unchanged.
        var launch = WorkerCgroup.Launch(
            managedHost ? "dotnet" : executable,
            managedHost
                ? [typeof(DurableReviewProcess).Assembly.Location, "--detached-review-worker", specPath]
                : ["--detached-review-worker", specPath],
            WorkerCgroup.TryPrepare(options, slot.WorkerDirectory, log ?? (_ => { })));
        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = slot.WorkspacePath,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        ApplyWorkerEnvironment(start.Environment);
        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Failed to start detached review worker.");
        var started = process.StartTime.ToUniversalTime();
        // The daemon launches its own executable, so its release is the worker's
        // release. Stamping it here means an attempt started by this build stays
        // attributable even if the worker's identity file is never re-read.
        var handle = new DurableReviewProcess(
            slot.WorkerDirectory,
            process.Id,
            started,
            RunnerReleaseIdentity.Current,
            RunnerReleaseIdentity.CurrentBinaryPath);
        process.Dispose();
        return handle;
    }

    /// <summary>
    /// AGT-2868: the review worker carries the same build-server fence as the
    /// coding worker. <see cref="ReviewBuildServerIsolation"/> already fences a
    /// prepared review plan's own commands, but the fence has to exist one level
    /// higher as well: the seven 1.7-day-old MSBuild nodes that blocked cgroup
    /// delegation on <c>agent-runner-review</c> on 18.09.2026 came from builds
    /// started before a plan's environment applied, and a node started with
    /// reuse enabled outlives the worker whatever the plan does afterwards.
    /// Credentials are not filtered here: a review worker resolves them from its
    /// own spec through <see cref="RunnerOptions.ReviewCredentialEnvironment"/>.
    /// </summary>
    internal static void ApplyWorkerEnvironment(IDictionary<string, string?> environment)
        => WorkerBuildServerHygiene.ApplyTo(environment);

    /// <summary>
    /// Fills in worker provenance a pre-AGT-2863 daemon never stamped, using the
    /// worker's own identity record. Purely additive: a record that already
    /// names its release is never overwritten by a re-read.
    /// </summary>
    public static PersistedReviewSlot WithWorkerProvenance(PersistedReviewSlot slot)
    {
        if (slot.WorkerReleaseId is { Length: > 0 } && slot.WorkerBinaryPath is { Length: > 0 })
            return slot;
        var identity = ReadIdentity(slot.WorkerDirectory);
        if (identity is null) return slot;
        return slot with
        {
            WorkerReleaseId = slot.WorkerReleaseId ?? identity.ReleaseId,
            WorkerBinaryPath = slot.WorkerBinaryPath ?? identity.BinaryPath,
        };
    }

    /// <summary>
    /// Provenance of the process that produced this slot's verdict, as reported
    /// alongside the verdict. An unstamped adopted record answers
    /// <see cref="ReviewWorkerProvenancePolicy.Unknown"/> rather than silently
    /// claiming the reporting daemon's release.
    /// </summary>
    public static ReviewWorkerProvenanceDto Provenance(PersistedReviewSlot slot)
        => new(
            slot.WorkerReleaseId is { Length: > 0 } release
                ? release
                : ReviewWorkerProvenancePolicy.Unknown,
            slot.WorkerBinaryPath is { Length: > 0 } binary
                ? binary
                : ReviewWorkerProvenancePolicy.Unknown,
            RunnerReleaseIdentity.Current);

    private static DetachedReviewIdentity? ReadIdentity(string workerDirectory)
    {
        var path = Path.Combine(workerDirectory, "review-worker.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DetachedReviewIdentity>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DurableReviewProcess Attach(PersistedReviewSlot slot)
        => new(
            slot.WorkerDirectory,
            slot.ProcessId ?? -1,
            slot.ProcessStartedAtUtc ?? DateTime.MinValue);

    public static bool HasCompleted(PersistedReviewSlot slot)
        => File.Exists(Path.Combine(slot.WorkerDirectory, "review-result.json"));

    public static bool TryRecoverIdentity(
        PersistedReviewSlot slot,
        out PersistedReviewSlot recovered,
        out string reason)
    {
        recovered = slot;
        if (slot.ProcessId is not null && slot.ProcessStartedAtUtc is not null)
            return VerifyLive(slot, out reason);

        var path = Path.Combine(slot.WorkerDirectory, "review-worker.json");
        if (!File.Exists(path))
        {
            reason = "review worker identity has not been recorded";
            return false;
        }

        DetachedReviewIdentity? identity;
        try
        {
            identity = JsonSerializer.Deserialize<DetachedReviewIdentity>(File.ReadAllText(path), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            reason = $"review worker identity is unreadable: {exception.Message}";
            return false;
        }
        if (identity is null)
        {
            reason = "review worker identity is empty";
            return false;
        }
        if (!PathsEqual(identity.WorkspacePath, slot.WorkspacePath))
        {
            reason = $"review worker workspace '{identity.WorkspacePath}' does not match slot workspace '{slot.WorkspacePath}'";
            return false;
        }

        recovered = slot with
        {
            ProcessId = identity.ProcessId,
            ProcessStartedAtUtc = identity.ProcessStartedAtUtc,
            WorkerReleaseId = slot.WorkerReleaseId ?? identity.ReleaseId,
            WorkerBinaryPath = slot.WorkerBinaryPath ?? identity.BinaryPath,
        };
        return VerifyLive(recovered, out reason);
    }

    public static bool VerifyLive(PersistedReviewSlot slot, out string reason)
    {
        if (slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
        {
            reason = "no persisted review process identity";
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(slot.ProcessId.Value);
            if (process.HasExited)
            {
                reason = "review process exited without durable evidence";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - slot.ProcessStartedAtUtc.Value).TotalSeconds) > 2)
            {
                reason = "review PID was reused (process start time differs)";
                return false;
            }
            if (OperatingSystem.IsLinux())
            {
                var cwdLink = new DirectoryInfo($"/proc/{slot.ProcessId.Value}/cwd");
                var target = cwdLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (string.IsNullOrWhiteSpace(target) || !PathsEqual(target, slot.WorkspacePath))
                {
                    reason = $"review process cwd '{target ?? "unavailable"}' does not match workspace '{slot.WorkspacePath}'";
                    return false;
                }
                if (DetachedWorkerTmpMountGuard.TmpMountWasTornDown(slot.ProcessId.Value, out var tmpDetail))
                {
                    reason = tmpDetail;
                    return false;
                }
            }
            reason = "live review process generation and workspace match";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or IOException)
        {
            reason = $"review process verification failed: {exception.Message}";
            return false;
        }
    }

    public DetachedReviewResult? ReadResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DetachedReviewResult>(File.ReadAllText(ResultPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ReviewExecutionCheckpoint? ReadProgress()
    {
        if (!File.Exists(ProgressPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReviewExecutionCheckpoint>(File.ReadAllText(ProgressPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// End this review worker's process tree. Authority loss is already the
    /// canonical outcome, so reaping is best effort and a refusal is not fatal.
    ///
    /// <para>AGT-2870: like its coding counterpart, <see cref="Attach"/> yields
    /// <c>-1</c> for a slot without a recorded identity, and <c>-1</c> means
    /// "every process of this uid" to <c>kill</c>. The guard refuses that pid and
    /// verifies the recorded start time before any signal is sent.</para>
    /// </summary>
    public void Kill(Action<string>? log = null)
        => ProcessSignalGuard.TryKillTree(
            ProcessId,
            $"review-worker-kill worker={Path.GetFileName(_directory)}",
            ProcessStartedAtUtc,
            log);

    private static DetachedReviewSpec BuildSpec(
        RunnerOptions options,
        ReviewClaimResponse claim)
        => new(
            claim.Subject!,
            claim.Lease!,
            Path.GetFullPath(options.ReviewWorkDir),
            options.ReviewCredentialEnvironment,
            options.CliType,
            options.CodexCliBin,
            options.ClaudeCliBin,
            options.CommandSilenceWatchdogSeconds,
            options.ReviewNoCpuProgressSeconds);

    /// <summary>
    /// Options for the detached worker. Everything the workspace needs comes
    /// from the spec; the two hang detectors previously stayed at their property
    /// defaults (600 s silence, 900 s without CPU) whatever the operator
    /// configured, which killed every quiet backend suite on 16.09.2026.
    /// </summary>
    internal static RunnerOptions WorkerOptions(DetachedReviewSpec spec)
        => new()
        {
            ServerUrl = "http://localhost",
            RunnerId = spec.Lease.ExecutorId,
            RunnerName = spec.Lease.ExecutorId,
            Hostname = spec.Lease.HostId,
            BackendName = "detached-review-worker",
            Role = "review",
            WorkDir = Path.Combine(spec.ReviewWorkDir, ".coding-not-used"),
            ReviewWorkDir = spec.ReviewWorkDir,
            ReviewCredentialEnvironment = spec.ReviewCredentialEnvironment,
            BaseBranch = "main",
            CliType = spec.CliType ?? CliSelection.ClaudeCli,
            CodexCliBin = spec.CodexCliBin ?? "codex",
            ClaudeCliBin = spec.ClaudeCliBin ?? "claude",
            CommandSilenceWatchdogSeconds = spec.CommandSilenceWatchdogSeconds
                ?? RunnerOptions.EnvInt("RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS", 600),
            ReviewNoCpuProgressSeconds = spec.ReviewNoCpuProgressSeconds
                ?? RunnerOptions.EnvIntAllowingZero("RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS", 900),
        };

    public static async Task<int> RunWorkerAsync(string specPath, bool shutdownBuildServers = false)
    {
        var spec = JsonSerializer.Deserialize<DetachedReviewSpec>(
                       await File.ReadAllTextAsync(specPath),
                       Json)
                   ?? throw new InvalidDataException($"Detached review spec is empty: {specPath}");
        var directory = Path.GetDirectoryName(specPath)!;
        var options = WorkerOptions(spec);
        var workspace = new RemoteReviewWorkspace(options, spec.Subject, spec.Lease, _ => { });
        using (var current = Process.GetCurrentProcess())
        {
            var identity = new DetachedReviewIdentity(
                current.Id,
                current.StartTime.ToUniversalTime(),
                Path.GetFullPath(workspace.RepositoryPath),
                RunnerReleaseIdentity.Current,
                RunnerReleaseIdentity.CurrentBinaryPath);
            if (!await WriteAtomicAsync(
                Path.Combine(directory, "review-worker.json"),
                JsonSerializer.Serialize(identity, Json)))
                return 0;
        }

        DetachedReviewResult result;
        try
        {
            await workspace.AdoptPreparedAsync(CancellationToken.None);
            var resume = new DurableReviewProcess(directory, -1, DateTime.MinValue).ReadProgress();
            var evidence = await workspace.ExecutePlanAsync(
                CancellationToken.None,
                resume,
                async (progress, _) =>
                {
                    if (!await WriteAtomicAsync(
                            Path.Combine(directory, "review-progress.json"),
                            JsonSerializer.Serialize(progress, Json)))
                        throw new ReviewAttemptStateRemovedException();
                });
            result = new DetachedReviewResult(
                evidence,
                null,
                null,
                DateTime.UtcNow);
        }
        catch (ReviewInfrastructureException exception)
        {
            result = new DetachedReviewResult(
                exception.Evidence,
                exception.Classification,
                exception.Message,
                DateTime.UtcNow);
        }
        catch (ReviewAttemptStateRemovedException)
        {
            // The daemon accepted the terminal report and reaped this attempt
            // while the detached process was still unwinding. There is no
            // authority or durable location left for another result.
            return 0;
        }
        catch (Exception exception)
        {
            result = new DetachedReviewResult(
                null,
                "ReviewWorkerFailed",
                $"Detached review worker failed: {exception.Message}",
                DateTime.UtcNow);
        }

        // AGT-2868: ahead of the result file, which is what lets the daemon tear
        // this worker's cgroup down. A shutdown started afterwards would race
        // that teardown and be counted as a leftover it had to kill.
        if (shutdownBuildServers) WorkerBuildServerHygiene.ShutdownBuildServers();

        if (!await WriteAtomicAsync(
            Path.Combine(directory, "review-result.json"),
            JsonSerializer.Serialize(result, Json)))
            return 0;
        return result.FailureClassification is null ? 0 : 3;
    }

    internal static async Task<bool> WriteAtomicAsync(string path, string content)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            // State deletion is the daemon's acknowledgement that this fenced
            // worker no longer owns a reportable attempt.
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class ReviewAttemptStateRemovedException : Exception;

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
