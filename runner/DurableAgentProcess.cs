using System.Diagnostics;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// What the detached worker executes. The worker only ever needs
/// <see cref="FileName"/> / <see cref="Arguments"/>; the trailing T0b fields
/// record <b>why</b> those arguments look the way they do, so a reattaching
/// daemon and a post-mortem can both read the card's execution spec out of the
/// same file the run started from.
///
/// <para>
/// The T0b fields are optional on purpose: a <c>spec.json</c> written before T0b
/// deserialises with nulls and runs exactly as it did, which is what keeps a
/// runner upgrade safe mid-wave (<c>KillMode=process</c> leaves live workers
/// behind).
/// </para>
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
    // T1 (AGT-2370) — CAR execution-engine fields, additive like the T0b block
    // above: a pre-T1 spec.json deserialises with nulls and Engine=null selects
    // the legacy raw-spawn branch, which is what keeps a mid-wave runner deploy
    // legal (KillMode=process leaves live workers on their old binary anyway;
    // this covers the file contract for any tooling that re-reads a spec).
    string? Engine = null,
    string? RunId = null,
    string? ResumeSessionId = null,
    string? CleanContextKey = null);

internal sealed record DetachedJobLogLine(long Sequence, DateTime Timestamp, string Stream, string Text);

internal sealed record DetachedJobResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    DateTime CompletedAtUtc,
    bool LaunchFailed = false);

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
        IReadOnlyList<string>? argsOverride = null,
        RunSpecDto? runSpec = null,
        string? runId = null,
        string? resumeSessionId = null,
        string? cleanContextKey = null)
    {
        Directory.CreateDirectory(workerDirectory);
        var specPath = Path.Combine(workerDirectory, "spec.json");
        var spec = BuildSpec(
            options,
            repoPath,
            prompt,
            resultsDirectory,
            argsOverride,
            runSpec,
            runId,
            resumeSessionId,
            cleanContextKey);
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the runner executable for detached job launch.");
        var managedHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = managedHost ? "dotnet" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        if (managedHost) start.ArgumentList.Add(typeof(DurableAgentProcess).Assembly.Location);
        start.ArgumentList.Add("--detached-worker");
        start.ArgumentList.Add(specPath);
        // The daemon needs all provider credentials for capability probes, but
        // a detached worker receives only the credential for its selected CLI.
        // In particular, Codex workers must not inherit Claude's setup token.
        if (ProviderAuthEnvironment.TryGetForCli(spec.CliType, out var authName, out var authValue))
            start.Environment[authName] = authValue;
        else
            start.Environment.Remove(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the detached runner worker.");
        var started = process.StartTime.ToUniversalTime();
        var handle = new DurableAgentProcess(workerDirectory, process.Id, started);
        process.Dispose();
        return handle;
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
        IReadOnlyList<string>? argsOverride = null,
        RunSpecDto? runSpec = null,
        string? runId = null,
        string? resumeSessionId = null,
        string? cleanContextKey = null)
    {
        // One resolution truth for both engines: which CLI runs (card wish vs.
        // host binaries, foreign-CLI fallback drops the model pins) comes from
        // AgentCliProcess.Resolve. The legacy engine additionally uses its argv;
        // the CAR engine ignores the argv and lets the descriptor build it from
        // the typed fields below.
        var invocation = AgentCliProcess.Resolve(options, runSpec, argsOverride);
        return new DetachedJobSpec(
            invocation.FileName,
            invocation.Arguments,
            Path.GetFullPath(repoPath),
            prompt,
            Path.GetFullPath(resultsDirectory),
            options.RunTimeoutSeconds,
            invocation.CliType,
            invocation.Model,
            invocation.ThinkingLevel,
            runSpec?.PermissionMode,
            runSpec?.ContextMode,
            Engine: options.ExecEngine,
            RunId: runId,
            ResumeSessionId: resumeSessionId,
            CleanContextKey: cleanContextKey);
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

    public void Kill()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* lease loss/cancellation is already the authoritative outcome */ }
    }

    public static async Task<int> RunWorkerAsync(string specPath)
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
        var timedOut = false;
        var launchFailed = false;
        if (string.Equals(spec.Engine, RunnerOptions.ExecEngineCar, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                (processResult, timedOut, launchFailed) = await CarWorkerExecution.RunAsync(spec, directory, Append);
            }
            catch (Exception ex)
            {
                Append("system", $"[runner] detached worker failed: {ex.Message}");
                processResult = new ProcessResult(125, string.Empty, ex.ToString());
                launchFailed = true;
            }
        }
        else
        {
            // Legacy raw spawn — behind RUNNER_EXEC_ENGINE=legacy since AGT-2370,
            // deleted in AGT-2373. The CAR adapters run in shadow mode on the raw
            // lines so the typed events.jsonl trace exists on both engines and
            // event parity is provable before the process start switches.
            var runId = string.IsNullOrWhiteSpace(spec.RunId)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))
                : spec.RunId!;
            using var trace = CarEventTrace.Open(directory);
            var protocolNovelty = new CliProtocolNoveltyTracker(
                AgentCliProcess.NormalizeCliType(spec.CliType) ?? AgentCliProcess.ClaudeCli);
            void ObserveUnclassifiedFrame(string rawDetail)
            {
                if (protocolNovelty.TryObserveFrame(rawDetail, out var novelty))
                    Append("system", novelty.ToMarker());
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
            try
            {
                var environment = new Dictionary<string, string?>
                {
                    ["JOB_RESULTS_DIR"] = spec.ResultsDirectory,
                };
                if (ProviderAuthEnvironment.TryGetForCli(spec.CliType, out var authName, out var authValue))
                    environment[authName] = authValue;
                processResult = await ProcessRunner.RunAsync(
                    spec.FileName,
                    spec.Arguments,
                    spec.WorkingDirectory,
                    spec.Prompt,
                    line =>
                    {
                        Append("stdout", line);
                        ObserveUnclassifiedFrame(line);
                        trace.WriteFromRawLine(
                            spec.CliType,
                            runId,
                            "stdout",
                            line);
                    },
                    line =>
                    {
                        Append("stderr", line);
                        trace.WriteFromRawLine(
                            spec.CliType,
                            runId,
                            "stderr",
                            line);
                    },
                    environment,
                    clearEnvironment: false,
                    ct: timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                timedOut = true;
                Append("system", $"[runner] run exceeded {spec.TimeoutSeconds}s timeout");
                processResult = new ProcessResult(124, string.Empty, "Runner timeout");
            }
            catch (Exception ex)
            {
                Append("system", $"[runner] detached worker failed: {ex.Message}");
                processResult = new ProcessResult(125, string.Empty, ex.ToString());
                launchFailed = ex is System.ComponentModel.Win32Exception
                               || ex.Message.Contains("Failed to start process", StringComparison.OrdinalIgnoreCase);
            }
        }

        var result = new DetachedJobResult(
            processResult.ExitCode,
            processResult.StdOut,
            processResult.StdErr,
            timedOut,
            DateTime.UtcNow,
            launchFailed);
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
