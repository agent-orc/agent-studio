using System.Diagnostics;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Typed CodingAgentRunner request persisted for a detached worker. The empty
/// <see cref="Arguments"/> field remains only for reading pre-AGT-2373 JSON;
/// CAR owns argv construction for every new worker.
/// </summary>
internal sealed record DetachedJobSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Prompt,
    string ResultsDirectory,
    int TimeoutSeconds,
    string? CliType = null,
    string? Model = null,
    string? ThinkingLevel = null,
    string? PermissionMode = null,
    string? ContextMode = null,
    string? RunId = null,
    string? ResumeSessionId = null,
    string? CleanContextKey = null,
    // TE-52 — the coding run's dependency cache binding: the per-run
    // NUGET_PACKAGES / NPM_CONFIG_CACHE / PLAYWRIGHT_BROWSERS_PATH locations
    // repository preparation restored into. Additive like the blocks above; a
    // pre-TE-52 spec.json deserialises with null and simply binds nothing.
    IReadOnlyDictionary<string, string>? Environment = null);

internal sealed record DetachedJobLogLine(long Sequence, DateTime Timestamp, string Stream, string Text);

internal sealed record DetachedJobResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    DateTime CompletedAtUtc,
    bool LaunchFailed = false,
    int? Signal = null);

internal sealed record DetachedJobProcessObservation(
    bool IsLive,
    DetachedJobResult? Result,
    string Detail);

internal sealed record DetachedWorkerIdentity(
    int ProcessId,
    DateTime ProcessStartedAtUtc,
    string WorktreePath);

internal sealed class DetachedWorkerLostException(string message) : Exception(message);

/// <summary>
/// Starts an agent behind a tiny runner-owned worker process. systemd may stop
/// the daemon main PID while this worker continues. Output and the terminal
/// result live in files, so the replacement daemon can follow and finish the
/// same attempt without inheriting an anonymous pipe.
/// </summary>
internal sealed class DurableAgentProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    private DurableAgentProcess(string directory, int processId, DateTime processStartedAtUtc)
    {
        _directory = directory;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
    }

    public int ProcessId { get; }
    public DateTime ProcessStartedAtUtc { get; }
    public string LogPath => Path.Combine(_directory, "output.jsonl");
    public string ResultPath => Path.Combine(_directory, "result.json");
    public string IdentityPath => Path.Combine(_directory, "worker.json");

    public static DurableAgentProcess Start(
        RunnerOptions options,
        string workerDirectory,
        string repoPath,
        string prompt,
        string resultsDirectory,
        RunSpecDto? runSpec = null,
        string? runId = null,
        string? resumeSessionId = null,
        string? cleanContextKey = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? log = null)
    {
        Directory.CreateDirectory(workerDirectory);
        var specPath = Path.Combine(workerDirectory, "spec.json");
        var spec = BuildSpec(
            options,
            repoPath,
            prompt,
            resultsDirectory,
            runSpec,
            runId,
            resumeSessionId,
            cleanContextKey,
            environment);
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the runner executable for detached job launch.");
        var managedHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var launch = WorkerCgroup.Launch(
            managedHost ? "dotnet" : executable,
            managedHost
                ? [typeof(DurableAgentProcess).Assembly.Location, "--detached-worker", specPath]
                : ["--detached-worker", specPath],
            // AGT-2866: the worker joins its own cgroup and then execs, so this
            // pid is still the worker's pid and every reattachment proof holds.
            WorkerCgroup.TryPrepare(options, workerDirectory, log ?? (_ => { })));
        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        ApplyWorkerEnvironment(start.Environment, spec.CliType);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the detached runner worker.");
        var started = process.StartTime.ToUniversalTime();
        var handle = new DurableAgentProcess(workerDirectory, process.Id, started);
        process.Dispose();
        return handle;
    }

    /// <summary>
    /// The environment differences between the daemon and the coding worker it
    /// starts. Everything else is inherited, which is what the run's caches and
    /// the provider configuration rely on.
    ///
    /// <para>Credentials: the daemon needs all provider credentials for its
    /// capability probes, but a detached worker receives only the credential for
    /// its selected CLI. In particular, Codex workers must not inherit Claude's
    /// setup token.</para>
    ///
    /// <para>Build servers (AGT-2820, AGT-2868): a reused MSBuild node survives
    /// the build that started it, is reparented to init the moment this worker
    /// exits, and then accumulates in the unit cgroup across daemon restarts
    /// until cgroup delegation itself fails. Disabling node reuse and the
    /// MSBuild server here reaches every build the agent starts, which is the
    /// only place the runner can fence it: the agent writes its own
    /// <c>dotnet</c> command lines.</para>
    /// </summary>
    internal static void ApplyWorkerEnvironment(
        IDictionary<string, string?> environment,
        string? cliType)
    {
        if (ProviderAuthEnvironment.TryGetForCli(cliType, out var authName, out var authValue))
            environment[authName] = authValue;
        else
            environment.Remove(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        WorkerBuildServerHygiene.ApplyTo(environment);
    }

    /// <summary>
    /// T0b — the pure part of <see cref="Start"/>: turn the card's execution spec
    /// plus the host configuration into the exact worker specification, without
    /// touching the process table. Kept separate so the spec that lands on disk
    /// can be asserted directly.
    /// </summary>
    internal static DetachedJobSpec BuildSpec(
        RunnerOptions options,
        string repoPath,
        string prompt,
        string resultsDirectory,
        RunSpecDto? runSpec = null,
        string? runId = null,
        string? resumeSessionId = null,
        string? cleanContextKey = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var invocation = CliSelection.Resolve(options, runSpec);
        return new DetachedJobSpec(
            invocation.FileName,
            [],
            Path.GetFullPath(repoPath),
            prompt,
            Path.GetFullPath(resultsDirectory),
            options.RunTimeoutSeconds,
            invocation.CliType,
            invocation.Model,
            invocation.ThinkingLevel,
            runSpec?.PermissionMode,
            runSpec?.ContextMode,
            RunId: runId,
            ResumeSessionId: resumeSessionId,
            CleanContextKey: cleanContextKey,
            Environment: environment);
    }

    /// <summary>
    /// The preparation cache binding a worker was started with, or null when the
    /// specification is missing, unreadable, or predates TE-52. Used by a resumed
    /// attempt, which must keep the binding of the preparation that ran once for
    /// the whole run.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? TryReadEnvironment(string workerDirectory)
    {
        try
        {
            var path = Path.Combine(workerDirectory, "spec.json");
            return File.Exists(path) ? ReadSpec(path).Environment : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Read a worker specification back, including one written before the T0b fields existed.</summary>
    internal static DetachedJobSpec ReadSpec(string specPath)
        => JsonSerializer.Deserialize<DetachedJobSpec>(File.ReadAllText(specPath), Json)
           ?? throw new InvalidDataException($"Detached job spec is empty: {specPath}");

    public static DurableAgentProcess Attach(PersistedRunnerSlot slot)
        => new(
            slot.WorkerDirectory,
            slot.ProcessId ?? -1,
            slot.ProcessStartedAtUtc ?? DateTime.MinValue);

    /// <summary>
    /// Recover the worker identity written by the worker itself. This closes the
    /// Process.Start-to-slot-save handoff window: if the daemon exits after the
    /// child exists but before its own slot write, the replacement can still
    /// prove and persist the exact PID generation before renewing the lease.
    /// </summary>
    public static bool TryRecoverIdentity(
        PersistedRunnerSlot slot,
        out PersistedRunnerSlot recovered,
        out string reason)
    {
        recovered = slot;
        if (slot.ProcessId is not null && slot.ProcessStartedAtUtc is not null)
        {
            reason = "process identity already persisted";
            return true;
        }

        var path = Path.Combine(slot.WorkerDirectory, "worker.json");
        if (!File.Exists(path))
        {
            reason = "worker identity has not been recorded";
            return false;
        }

        DetachedWorkerIdentity? identity;
        try
        {
            identity = JsonSerializer.Deserialize<DetachedWorkerIdentity>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            reason = $"worker identity is unreadable: {ex.Message}";
            return false;
        }

        if (identity is null)
        {
            reason = "worker identity is empty";
            return false;
        }
        if (!PathsEqual(identity.WorktreePath, slot.WorktreePath))
        {
            reason = $"worker identity worktree '{identity.WorktreePath}' does not match slot worktree '{slot.WorktreePath}'";
            return false;
        }

        recovered = slot with
        {
            ProcessId = identity.ProcessId,
            ProcessStartedAtUtc = identity.ProcessStartedAtUtc,
        };
        return VerifyLive(recovered, out reason);
    }

    /// <summary>
    /// Resolves the only two facts that make a persisted worker reattachable:
    /// a live, positively identified process or its atomically persisted
    /// terminal result. The second result read closes the worker-exit race
    /// where the result appears after the first read but before PID liveness is
    /// checked.
    /// </summary>
    public static DetachedJobProcessObservation InspectForReattach(PersistedRunnerSlot slot)
    {
        var process = Attach(slot);
        return InspectForReattach(
            process.ReadResult,
            () =>
            {
                var isLive = VerifyLive(slot, out var detail);
                return (isLive, detail);
            });
    }

    internal static DetachedJobProcessObservation InspectForReattach(
        Func<DetachedJobResult?> readResult,
        Func<(bool IsLive, string Detail)> verifyLive)
    {
        var result = readResult();
        if (result is not null)
            return new DetachedJobProcessObservation(false, result, "durable result ready");

        var (isLive, detail) = verifyLive();
        if (isLive)
            return new DetachedJobProcessObservation(true, null, detail);

        result = readResult();
        return result is not null
            ? new DetachedJobProcessObservation(false, result, "durable result ready")
            : new DetachedJobProcessObservation(false, null, detail);
    }

    public static bool VerifyLive(PersistedRunnerSlot slot, out string reason)
    {
        if (slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
        {
            reason = "no persisted process identity";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(slot.ProcessId.Value);
            if (process.HasExited)
            {
                reason = "process has exited without a durable result";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - slot.ProcessStartedAtUtc.Value).TotalSeconds) > 2)
            {
                reason = "PID was reused (process start time differs)";
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                var cwdLink = new DirectoryInfo($"/proc/{slot.ProcessId.Value}/cwd");
                var target = cwdLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (string.IsNullOrWhiteSpace(target) || !PathsEqual(target, slot.WorktreePath))
                {
                    reason = $"process cwd '{target ?? "unavailable"}' does not match worktree '{slot.WorktreePath}'";
                    return false;
                }
                if (DetachedWorkerTmpMountGuard.TmpMountWasTornDown(slot.ProcessId.Value, out var tmpDetail))
                {
                    reason = tmpDetail;
                    return false;
                }
            }

            reason = "live process and worktree match";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            reason = $"process verification failed: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<DetachedJobLogLine> ReadAfter(long sequence)
    {
        if (!File.Exists(LogPath)) return [];
        var lines = new List<DetachedJobLogLine>();
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            try
            {
                var line = JsonSerializer.Deserialize<DetachedJobLogLine>(raw, Json);
                if (line is not null && line.Sequence > sequence) lines.Add(line);
            }
            catch (JsonException)
            {
                // The worker may currently be appending this final line. It will
                // be complete and parseable on the next poll.
            }
        }
        return lines.OrderBy(x => x.Sequence).ToList();
    }

    public DetachedJobResult? ReadResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try { return JsonSerializer.Deserialize<DetachedJobResult>(File.ReadAllText(ResultPath), Json); }
        catch (JsonException) { return null; } // atomic rename normally makes this unreachable
    }

    /// <summary>
    /// End this worker's process tree.
    ///
    /// <para>AGT-2870: <see cref="Attach"/> substitutes <c>-1</c> for a slot that
    /// never recorded a worker identity, and on Linux .NET that sentinel passes
    /// every gate in the runtime down to <c>kill(-1, SIGKILL)</c>, which is every
    /// process of this uid. The pid therefore goes through
    /// <see cref="ProcessSignalGuard"/>, which refuses it below the pid floor and
    /// proves <see cref="ProcessStartedAtUtc"/> against the live process before
    /// signalling, so a recycled pid number is not killed in the worker's
    /// place.</para>
    /// </summary>
    public void Kill(Action<string>? log = null)
        => ProcessSignalGuard.TryKillTree(
            ProcessId,
            $"worker-kill worker={Path.GetFileName(_directory)}",
            ProcessStartedAtUtc,
            log);

    public static async Task<int> RunWorkerAsync(string specPath, bool shutdownBuildServers = false)
    {
        var spec = JsonSerializer.Deserialize<DetachedJobSpec>(await File.ReadAllTextAsync(specPath), Json)
            ?? throw new InvalidDataException($"Detached job spec is empty: {specPath}");
        var directory = Path.GetDirectoryName(specPath)!;
        using (var current = Process.GetCurrentProcess())
        {
            var identity = new DetachedWorkerIdentity(
                current.Id,
                current.StartTime.ToUniversalTime(),
                Path.GetFullPath(spec.WorkingDirectory));
            await WriteAtomicAsync(
                Path.Combine(directory, "worker.json"),
                JsonSerializer.Serialize(identity, Json));
        }
        var logPath = Path.Combine(directory, "output.jsonl");
        var resultPath = Path.Combine(directory, "result.json");
        Directory.CreateDirectory(spec.ResultsDirectory);
        long sequence = 0;
        var logGate = new object();
        using var logStream = new FileStream(
            logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        void Append(string stream, string text)
        {
            var entry = new DetachedJobLogLine(Interlocked.Increment(ref sequence), DateTime.UtcNow, stream, text);
            var json = JsonSerializer.Serialize(entry, Json);
            lock (logGate) logWriter.WriteLine(json);
        }

        ProcessResult processResult;
        bool timedOut;
        bool launchFailed;
        try
        {
            (processResult, timedOut, launchFailed) = await CarWorkerExecution.RunAsync(spec, directory, Append);
        }
        catch (Exception ex)
        {
            Append("system", $"[runner] detached worker failed: {ex.Message}");
            processResult = new ProcessResult(125, string.Empty, ex.ToString());
            timedOut = false;
            launchFailed = true;
        }

        // AGT-2868: before the result file appears, because the result file is
        // what makes the daemon tear this worker's cgroup down. Doing it after
        // would race the teardown and be counted as a leftover it killed.
        if (shutdownBuildServers)
            WorkerBuildServerHygiene.ShutdownBuildServers(message => Append("system", message));

        var result = new DetachedJobResult(
            processResult.ExitCode,
            processResult.StdOut,
            processResult.StdErr,
            timedOut,
            DateTime.UtcNow,
            launchFailed,
            processResult.Signal);
        await WriteAtomicAsync(resultPath, JsonSerializer.Serialize(result, Json));
        return processResult.ExitCode;
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var temp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
