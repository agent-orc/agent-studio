using System.Text.Json;
using AgentStudio.CliHosting;
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
/// T1 (AGT-2370): the CAR execution engine inside the detached worker. The worker
/// process stays the durability boundary — <see cref="DurableAgentProcess"/> owns
/// spawn, reattach, <c>output.jsonl</c> and <c>result.json</c> — and this class
/// only replaces how the worker drives the coding CLI: through
/// <see cref="ICliDriver"/> (descriptor-built argv, structured events, typed
/// outcome) instead of a raw <see cref="ProcessRunner"/> spawn.
///
/// <para>Deliberate behaviour vs. the legacy engine, per the migration plan
/// (docs/operations/car-migration-plan.md §3 T1):</para>
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
    /// </summary>
    public static async Task<(ProcessResult Result, bool TimedOut, bool LaunchFailed)> RunAsync(
        DetachedJobSpec spec,
        string workerDirectory,
        Action<string, string> append,
        Func<CliOptions, CliOptions>? optionsCustomizer = null,
        string? cleanContextRoot = null)
    {
        var runId = string.IsNullOrWhiteSpace(spec.RunId)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(workerDirectory))
            : spec.RunId!;
        var cliType = AgentCliProcess.NormalizeCliType(spec.CliType) ?? AgentCliProcess.ClaudeCli;

        using var trace = CarEventTrace.Open(workerDirectory);
        using var logger = new CarWorkerLogger(Path.Combine(workerDirectory, "car.log"), append);
        var protocolNovelty = new CliProtocolNoveltyTracker(cliType);
        var options = BuildCliOptions(spec, cliType);
        if (optionsCustomizer is not null) options = optionsCustomizer(options);
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
                    rootOverride: cleanContextRoot);
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

        void OnOutput(string id, CarOutputLine line)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (line.Stream == "stdout") stdout.Append(line.Text);
            else if (line.Stream == "stderr") stderr.Append(line.Text);
            append(line.Stream, line.Text);
            if (line.Stream == "stdout"
                && protocolNovelty.TryObserveFrame(line.Text, out var novelty))
                append("system", novelty.ToMarker());
        }

        void OnRunEvent(string id, CliRunEvent evt)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (cliType == AgentCliProcess.CodexCli)
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
            // Clean context redirects the provider's config home. Keep the
            // service-provisioned headless credential independent of that home
            // by explicitly admitting only Claude's supported environment token.
            if (ProviderAuthEnvironment.TryGetForCli(cliType, out var authName, out var authValue))
                extraEnvironment[authName] = authValue;
            if (cleanContext is not null)
            {
                foreach (var pair in cleanContext.Environment)
                    extraEnvironment[pair.Key] = pair.Value;
            }

            var request = new CliRunRequest
            {
                RunId = runId,
                Prompt = spec.Prompt,
                WorkingDirectory = spec.WorkingDirectory,
                Model = spec.Model,
                ThinkingLevel = spec.ThinkingLevel,
                ResumeSessionId = spec.ResumeSessionId,
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
            var deadline = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)
                .ContinueWith(_ => { }, TaskScheduler.Default);
            var completed = await Task.WhenAny(finished.Task, deadline);
            if (!ReferenceEquals(completed, finished.Task))
            {
                timedOut = true;
                append("system", $"[runner] run exceeded {spec.TimeoutSeconds}s timeout");
                driver.Stop(runId, RunStopReason.Watchdog);
                await Task.WhenAny(finished.Task, Task.Delay(StopGrace));
            }

            if (timedOut)
            {
                // Byte parity with the legacy timeout result: classification input
                // stays "Runner timeout" / exit 124, not a partial transcript.
                return (new ProcessResult(124, string.Empty, "Runner timeout"), true, false);
            }

            var info = await finished.Task;
            var exitCode = info.ExitCode ?? 125;
            return (
                new ProcessResult(exitCode, stdout.ToString(), stderr.ToString()),
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

    /// <summary>The production CAR options for one worker spec.</summary>
    private static CliOptions BuildCliOptions(DetachedJobSpec spec, string cliType) => new()
    {
        ClaudePath = cliType == AgentCliProcess.ClaudeCli ? spec.FileName : null,
        CodexPath = cliType == AgentCliProcess.CodexCli ? spec.FileName : null,
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
}

/// <summary>
/// The typed event trace (<c>events.jsonl</c> in the worker directory) — plan §3
/// T1 step 3. On the CAR engine it records the live <see cref="CliRunEvent"/>s;
/// on the legacy engine the same trace is produced in shadow mode by applying the
/// CAR adapters to the raw lines, which is what proves event parity before the
/// process start switches (plan §4, Schattenbetrieb).
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

    /// <summary>Legacy-engine shadow mode: map one raw output line through the CAR adapters.</summary>
    public void WriteFromRawLine(
        string? cliType,
        string runId,
        string stream,
        string text,
        Action<string>? onUnclassifiedFrame = null)
    {
        IEnumerable<CliRunEvent> events;
        try
        {
            var kind = stream == "stderr" ? CliStreamKind.Stderr : CliStreamKind.Stdout;
            events = AgentCliProcess.NormalizeCliType(cliType) == AgentCliProcess.CodexCli
                ? CodexEventAdapter.Map(text, runId, kind)
                : kind == CliStreamKind.Stdout
                    ? ClaudeEventAdapter.Map(text, runId)
                    : Array.Empty<CliRunEvent>();
        }
        catch
        {
            // Adapter failures remain non-fatal in legacy shadow mode, but a
            // structured frame that triggered one must still become visible.
            onUnclassifiedFrame?.Invoke(text);
            return;
        }

        foreach (var evt in events)
        {
            var normalized = AgentCliProcess.NormalizeCliType(cliType) == AgentCliProcess.CodexCli
                ? RunnerCodexEventAdapter.MapKnownError(evt)
                : evt;
            Write(normalized);
            if (normalized is CliRunEvent.Unknown unknown)
                onUnclassifiedFrame?.Invoke(unknown.RawDetail ?? unknown.Sample ?? string.Empty);
        }
    }

    public void Dispose() => _writer.Dispose();
}
