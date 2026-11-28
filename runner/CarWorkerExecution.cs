using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;
// The runner's own wire model also declares a CliOutputLine; the CAR event
// payload type must resolve to the library's.
using CarOutputLine = CodingAgentRunner.Model.CliOutputLine;

namespace AgentRunner;

/// <summary>
/// The CAR execution engine inside the detached worker. The worker process
/// stays the durability boundary: <see cref="DurableAgentProcess"/> owns spawn,
/// reattach, <c>output.jsonl</c> and <c>result.json</c>. This class drives the CLI through
/// <see cref="ICliDriver"/> (descriptor-built argv, structured events, typed
/// outcome). No raw coding-CLI spawn exists in the Runner.
///
/// <para>Host integration choices retained from the migration:</para>
/// <list type="bullet">
///   <item><b>stream-json</b> replaces plaintext output (P5 divergence is pinned
///   in the parity fixtures, not discovered in production).</item>
///   <item><b>Permission injection</b>: the card's <c>permissionMode</c> becomes a
///   CLI flag; absent means YOLO (the zielbild default), where the host config used
///   to decide silently.</item>
///   <item><b>Clean context</b>: the card's <c>contextMode</c> selects a stable,
///   task-isolated config home from the shared host store. The credential file
///   is linked so the host's OAuth refresh writes through.</item>
///   <item><b>Prompt on stdin</b> is preserved via CAR-A
///   (<see cref="ClaudePromptTransport.Stdin"/>) — no argv-length or
///   <c>/proc/&lt;pid&gt;/cmdline</c> regression.</item>
///   <item><b>Delegation off, git guard off</b>: remote agents author their own
///   commits (the host owns push + salvage), and T1 changes exactly the two
///   behaviours above — a runner-materialized subagent set or a newly blocking
///   git wrapper would each be a third, undecided jump.</item>
/// </list>
/// </summary>
internal static class CarWorkerExecution
{
    /// <summary>Grace the worker allows CAR to classify and report after a stop request.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Drive one CLI run for <paramref name="spec"/> through CAR and return the
    /// same shape the legacy engine produced, so <c>result.json</c> and the
    /// classification stack downstream stay byte-compatible.
    /// <paramref name="optionsCustomizer"/> is a test seam: it receives the
    /// production-built options and may swap the binary paths / inject a fixture
    /// spawner - so the parity tests exercise exactly the production settings
    /// (stdin transport, delegation off, guard off) minus the real binary.
    /// <paramref name="stopToken"/> is a caller deadline: when it fires, the CLI
    /// is stopped exactly like the watchdog stops it, so the run does not outlive
    /// a caller that has already returned.
    /// </summary>
    public static async Task<(ProcessResult Result, bool TimedOut, bool LaunchFailed)> RunAsync(
        DetachedJobSpec spec,
        string workerDirectory,
        Action<string, string> append,
        Func<CliOptions, CliOptions>? optionsCustomizer = null,
        string? cleanContextRoot = null,
        IReadOnlyList<string>? extraCliArguments = null,
        CancellationToken stopToken = default,
        string? cleanContextUserHome = null)
    {
        var runId = string.IsNullOrWhiteSpace(spec.RunId)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(workerDirectory))
            : spec.RunId!;
        var cliType = CliSelection.NormalizeCliType(spec.CliType) ?? CliSelection.ClaudeCli;

        using var trace = CarEventTrace.Open(workerDirectory);
        using var logger = new CarWorkerLogger(Path.Combine(workerDirectory, "car.log"), append);
        var protocolNovelty = new CliProtocolNoveltyTracker(cliType);
        using var signalRecorder = OperatingSystem.IsLinux()
            ? new SignalRecordingCliSpawner(extraCliArguments)
            : null;
        var options = BuildCliOptions(spec, cliType) with { Spawner = signalRecorder };
        if (optionsCustomizer is not null) options = optionsCustomizer(options);
        var recordsSignals = ReferenceEquals(options.Spawner, signalRecorder);
        options = options with
        {
            Spawner = new CodingWorkerSpawner(cliType, options.Spawner,
                optionsCustomizer is null ? null : options.EnvironmentOverrides),
        };
        var runner = new CliRunner(options, logger, new WorkerRunLogPathProvider(workerDirectory));
        var driver = runner.Get(cliType);

        TaskCleanContextLease? cleanContext = null;
        if (CliContextModes.Normalize(spec.ContextMode) == CliContextModes.Clean)
        {
            try
            {
                cleanContext = TaskCleanContextStore.Acquire(
                    cliType,
                    string.IsNullOrWhiteSpace(spec.CleanContextKey) ? runId : spec.CleanContextKey!,
                    userHome: cleanContextUserHome, rootOverride: cleanContextRoot, runnerManaged: true);
                append(
                    "system",
                    $"[runner] clean-context {(cleanContext.Reused ? "reused" : "seeded")} " +
                    $"task home '{cleanContext.HomePath}'");
            }
            catch (Exception ex)
            {
                append("system", $"[runner] stable clean-context acquisition failed: {ex.Message}");
                return (new ProcessResult(125, string.Empty, ex.Message), false, true);
            }
        }

        // Same tail budgets as the legacy engine: the terminal sentinel and the
        // final reply arrive last, so a bounded tail keeps classification intact
        // while a runaway run cannot grow the worker heap without bound.
        var stdout = new BoundedOutputBuffer(2 * 1024 * 1024);
        var stderr = new BoundedOutputBuffer(256 * 1024);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processStarted = 0;
        long observedTokens = 0;
        long cumulativeBaseline = spec.TokenBaseline ?? -1;
        var tokenCeilingHit = 0;

        void OnOutput(string id, CarOutputLine line)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (line.Stream == "stdout") stdout.Append(line.Text);
            else if (line.Stream == "stderr") stderr.Append(line.Text);
            append(line.Stream, line.Text);
            if (line.Stream == "stdout" && spec.TokenCeiling is { } ceiling
                && MechanicalTokenUsage.TryRead(line.Text, out var tokens, out var cumulative))
            {
                long total;
                if (cumulative)
                {
                    Interlocked.CompareExchange(ref cumulativeBaseline, tokens, -1);
                    total = Math.Max(Interlocked.Read(ref observedTokens),
                        tokens - Interlocked.Read(ref cumulativeBaseline));
                    Interlocked.Exchange(ref observedTokens, total);
                }
                else total = Interlocked.Add(ref observedTokens, tokens);
                if (total >= ceiling && Interlocked.Exchange(ref tokenCeilingHit, 1) == 0)
                {
                    append("system", $"[runner] resumed mechanical round reached token ceiling {ceiling}; stopping CLI");
                    driver.Stop(runId, RunStopReason.Watchdog);
                }
            }
            if (line.Stream == "stdout"
                && protocolNovelty.TryObserveFrame(line.Text, out var novelty))
                append("system", novelty.ToMarker());
        }

        void OnRunEvent(string id, CliRunEvent evt)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (cliType == CliSelection.CodexCli)
                evt = RunnerCodexEventAdapter.MapKnownError(evt);
            if (evt is CliRunEvent.RunStarted)
                Volatile.Write(ref processStarted, 1);
            trace.Write(evt);
        }

        void OnFinished(string id, CliRunInfo info)
        {
            if (string.Equals(id, runId, StringComparison.Ordinal)) finished.TrySetResult(info);
        }

        driver.OnOutput += OnOutput;
        driver.OnRunEvent += OnRunEvent;
        driver.OnFinished += OnFinished;
        try
        {
            var extraEnvironment = new Dictionary<string, string>
            {
                ["JOB_RESULTS_DIR"] = spec.ResultsDirectory,
            };
            if (cleanContext is not null)
            {
                foreach (var pair in cleanContext.Environment)
                    extraEnvironment[pair.Key] = pair.Value;
            }
            // The agent's own build/test/lint must resolve against the caches
            // repository preparation restored into for this run (TE-52).
            PreparationCacheEnvironment.Apply(extraEnvironment, spec.Environment?
                .Where(pair => WorkerEdgeCredentialBoundary.IsCodingRuntimeName(pair.Key, cliType))
                .ToDictionary(pair => pair.Key, pair => pair.Value));
            ApplyThinkingEnvironment(extraEnvironment, cliType, spec.Model, spec.ThinkingLevel);
            WorkerEdgeCredentialBoundary.RemoveFrom(extraEnvironment);
            // The candidate token must win over the clean home's inherited environment.
            // Only this sign-in's async flow sees it; other CAR children keep the accepted token.
            if (ProviderAuthEnvironment.TryGetForCli(cliType, out var authName, out var authValue))
                extraEnvironment[authName] = authValue;

            var request = new CliRunRequest
            {
                RunId = runId,
                Prompt = spec.Prompt,
                WorkingDirectory = spec.WorkingDirectory,
                Model = spec.Model,
                ThinkingLevel = spec.ThinkingLevel,
                ResumeSessionId = cleanContext is { Reused: false } ? null : spec.ResumeSessionId,
                // Null normalizes to YOLO — documented T1 behaviour jump: remote
                // runs used to inject no flag and let the host config decide.
                PermissionMode = spec.PermissionMode,
                // Null normalizes to clean — the second documented jump; safe
                // since CAR-B links the credential seed instead of copying it.
                // The host owns a task-stable lease and injects its home. CAR
                // must not cut a second process-scoped temp home around it.
                ContextMode = cleanContext is null
                    ? CliContextModes.Normalize(spec.ContextMode)
                    : CliContextModes.Shared,
                ExtraEnvironment = extraEnvironment,
            };

            var (run, error) = await driver.StartAsync(request, CancellationToken.None);
            if (run is null)
            {
                if (cleanContext is { Reused: false })
                {
                    try { cleanContext.Delete(); }
                    catch (Exception ex)
                    {
                        append("system", $"[runner] failed-start clean-context cleanup failed: {ex.Message}");
                    }
                }
                append("system", $"[runner] car engine failed to start {cliType}: {error}");
                return (new ProcessResult(125, string.Empty, error ?? "CAR start failed"), false, true);
            }

            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds));
            var deadline = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)
                .ContinueWith(_ => { }, TaskScheduler.Default);
            var completed = await Task.WhenAny(finished.Task, deadline);
            if (!ReferenceEquals(completed, finished.Task))
            {
                timedOut = true;
                append("system", stopToken.IsCancellationRequested
                    ? "[runner] caller deadline expired; stopping CLI"
                    : $"[runner] run exceeded {spec.TimeoutSeconds}s timeout");
                driver.Stop(runId, RunStopReason.Watchdog);
                await Task.WhenAny(finished.Task, Task.Delay(StopGrace));
            }

            if (timedOut)
            {
                // Byte parity with the legacy timeout result: classification input
                // stays "Runner timeout" / exit 124, not a partial transcript.
                return (new ProcessResult(124, string.Empty,
                    Volatile.Read(ref tokenCeilingHit) != 0
                        ? "Resumed mechanical token ceiling reached" : "Runner timeout"), true, false);
            }

            var info = await finished.Task;
            var exitCode = info.ExitCode ?? 125;
            if (Volatile.Read(ref tokenCeilingHit) != 0)
                return (new ProcessResult(124, stdout.ToString(), "Resumed mechanical token ceiling reached"), true, false);
            return (
                new ProcessResult(
                    exitCode,
                    stdout.ToString(),
                    stderr.ToString(),
                    recordsSignals ? signalRecorder?.ReadRecordedSignal(exitCode) : null),
                false,
                LaunchFailed: Volatile.Read(ref processStarted) == 0 || info.ProcessId <= 0);
        }
        finally
        {
            driver.OnOutput -= OnOutput;
            driver.OnRunEvent -= OnRunEvent;
            driver.OnFinished -= OnFinished;
            try { driver.Forget(runId); }
            finally
            {
                try { cleanContext?.Dispose(); }
                catch (Exception ex)
                {
                    append("system", $"[runner] clean-context last-use update failed: {ex.Message}");
                }
            }
        }
    }

    // CAR 0.3.1 does not know Fable 5.2's ladder. Use Claude Code's native
    // effort override so worker and read-only turns retain an explicit card level.
    internal static void ApplyThinkingEnvironment(
        Dictionary<string, string> environment, string cli, string? model, string? thinking)
    {
        if (cli != CliSelection.ClaudeCli || model is null
            || !ExecutionModelIdentity.Equivalent(model, "claude-fable-5-2")) return;
        var level = thinking?.Trim().ToLowerInvariant();
        if (level is "low" or "medium" or "high" or "xhigh" or "max")
            environment["CLAUDE_CODE_EFFORT_LEVEL"] = level;
    }

    /// <summary>The production CAR options for one worker spec.</summary>
    private static CliOptions BuildCliOptions(DetachedJobSpec spec, string cliType) => new()
    {
        ClaudePath = cliType == CliSelection.ClaudeCli ? spec.FileName : null,
        CodexPath = cliType == CliSelection.CodexCli ? spec.FileName : null,
        // CAR-A: keep the remote prompt transport exactly what it always was —
        // stdin — instead of inheriting the library's argv default.
        ClaudePromptTransport = ClaudePromptTransport.Stdin,
        // Remote agents author their own commits; the host owns push, salvage and
        // the verified delivery. Turning CAR's git guard on here would block the
        // established remote contribution model — that decision belongs to a
        // later tranche, not to a transport migration.
        AllowAgentGitMutation = true,
        // A runner-materialized subagent set plus a prompt block would be a third,
        // undecided behaviour change; T1 ships exactly two.
        Delegation = new DelegationOptions { Enabled = false },
    };

    /// <summary>
    /// CAR's own per-stream run log, kept inside the worker directory next to
    /// <c>output.jsonl</c> so nothing accumulates outside the slot that
    /// teardown already deletes (CAR-D later removes the double write).
    /// </summary>
    private sealed class WorkerRunLogPathProvider(string workerDirectory) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => Path.Combine(workerDirectory, "car-log");
        public string GetActiveJobsFile() => Path.Combine(workerDirectory, "car-active-runs.json");
    }

    /// <summary>
    /// Library diagnostics: everything to <c>car.log</c> in the worker directory,
    /// warnings and errors additionally as <c>system</c> lines into the shipped
    /// output log so an operator sees anomalies without shelling into the host.
    /// </summary>
    private sealed class CarWorkerLogger : ILogger, IDisposable
    {
        private readonly StreamWriter _file;
        private readonly Action<string, string> _append;
        private readonly object _gate = new();

        public CarWorkerLogger(string path, Action<string, string> append)
        {
            _file = new StreamWriter(new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { AutoFlush = true };
            _append = append;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception) + (exception is null ? "" : $" :: {exception.Message}");
            lock (_gate) _file.WriteLine($"{DateTime.UtcNow:O} [{logLevel}] {text}");
            if (logLevel >= LogLevel.Warning) _append("system", $"[car] {text}");
        }

        public void Dispose() => _file.Dispose();
    }
    /// <summary>
    /// CAR inherits the daemon environment even when its request overlay is clean.
    /// Enforce the same policy as the detached worker at the final process boundary.
    /// Explicit fixture overrides from the trusted test-only options customizer
    /// remain available to replay binaries. Production requests cannot supply them.
    /// </summary>
    internal sealed class CodingWorkerSpawner(
        string cliType,
        ICliProcessSpawner? inner = null,
        IReadOnlyDictionary<string, string>? testEnvironmentOverrides = null) : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            WorkerEdgeCredentialBoundary.ApplyCodingEnvironment(startInfo.Environment, cliType);
            if (testEnvironmentOverrides is not null)
                foreach (var pair in testEnvironmentOverrides) startInfo.Environment[pair.Key] = pair.Value;
            if (inner is not null) return inner.Spawn(startInfo);
            if (OperatingSystem.IsWindows())
            {
                // Retain CAR's curated Windows handle inheritance as well as its pipes.
                return SpawnWindows(startInfo);
            }
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process,
                startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
                process.StandardOutput, process.StandardError);
        }

        // CAR 0.7.0 keeps its native adapter internal. Bind the pinned contract
        // instead of falling back to broader Windows handle inheritance. Missing
        // members fail the launch closed; the regression test checks this shape.
        internal static MethodInfo WindowsSpawnMethod => typeof(CliRunner).Assembly
            .GetType("CodingAgentRunner.Execution.Win.WindowsHandleScrubSpawner", throwOnError: true)!
            .GetMethod("Spawn", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("CAR Windows spawn contract is unavailable.");

        private static CliSpawn SpawnWindows(ProcessStartInfo startInfo)
        {
            var method = WindowsSpawnMethod;
            var result = method.Invoke(null,
            [
                startInfo.FileName, startInfo.ArgumentList.ToArray(), startInfo.WorkingDirectory,
                startInfo.Environment.Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value!),
                startInfo.RedirectStandardInput, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ]) ?? throw new InvalidOperationException("CAR Windows spawn returned no process.");
            object? Read(string name) => method.ReturnType.GetProperty(name)!.GetValue(result);
            return new CliSpawn((Process)Read("Process")!, (Stream?)Read("Stdin") ?? Stream.Null,
                new StreamReader((Stream)Read("Stdout")!), new StreamReader((Stream)Read("Stderr")!),
                _ => ((Action)Read("KillTree")!)());
        }

    }
}

/// <summary>
/// The typed event trace (<c>events.jsonl</c> in the worker directory). It
/// records the live <see cref="CliRunEvent"/> stream emitted by CAR.
/// </summary>
internal sealed class CarEventTrace : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private long _sequence;

    private CarEventTrace(StreamWriter writer) => _writer = writer;

    public static CarEventTrace Open(string workerDirectory)
        => new(new StreamWriter(new FileStream(
                Path.Combine(workerDirectory, "events.jsonl"),
                FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { AutoFlush = true });

    public void Write(CliRunEvent evt)
    {
        try
        {
            var entry = new
            {
                Sequence = Interlocked.Increment(ref _sequence),
                Timestamp = DateTime.UtcNow,
                Type = evt.GetType().Name,
                Event = (object)evt,
            };
            var line = JsonSerializer.Serialize(entry, Json);
            lock (_gate) _writer.WriteLine(line);
        }
        catch
        {
            // The trace is observability, never a run failure.
        }
    }

    public void Dispose() => _writer.Dispose();
}
