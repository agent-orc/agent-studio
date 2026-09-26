using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentStudio.CliHosting;
using LibOutcome = CodingAgentRunner.Model.RunOutcome;

namespace AgentStudio.Cli;

/// <summary>
/// Studio's shared CAR host adapter for Claude Code, Codex, and Antigravity.
/// CAR owns spawning and protocol execution; this adapter owns output
/// projection, persistence, durability, and host policy. Per-CLI behavior is supplied as a
/// <see cref="CliBehavior"/> (delegates + data) rather than via subclass
/// overrides; the per-CLI behaviors live in <see cref="BuiltInCliBehaviors"/>
/// and are wired into a concrete engine instance via the
/// <c>ForClaude</c> / <c>ForCodex</c> / <c>ForAntigravity</c> factory helpers.
/// </summary>
public partial class GenericCliExecutionService : ICliExecutionService
{
    protected readonly ILogger _logger;
    protected readonly IConfiguration _configuration;
    internal readonly ConcurrentDictionary<string, ProcInfo> _processes = new();
    private readonly CliBehavior _behavior;

    /// <summary>
    /// Per-task clean-context homes (jobKey → live preparation). Session-state
    /// stability contract (MKT-8 / WEB-14 "Codex rollout state loss"): all
    /// attempts/recoveries of the same task reuse ONE isolated home, so the
    /// CLI's own session state (Codex <c>sessions/rollout-*.jsonl</c>, Claude
    /// per-cwd transcripts) survives a mid-run restart and a stored session id
    /// stays resumable. A fresh home is cut only on the task's first start or
    /// after retention removed its inactive home. The in-memory registry avoids
    /// repeated acquisition during one backend lifetime; the marker-validated
    /// filesystem store remains authoritative across process restarts.
    /// </summary>
    private readonly ConcurrentDictionary<string, CleanContextPreparation> _cleanContextsByJob = new();

    /// <summary>
    /// Mutable per-instance CLI path override (set via <see cref="SetCliPath"/>).
    /// Generic to all CLIs, so it lives on the engine; behaviors read it through
    /// <see cref="CliPathOverride"/>.
    /// </summary>
    private string? _cliPathOverride;

    public string CliType => _behavior.CliType;

    // ── Engine-context accessors for behaviors (same assembly) ──────────
    internal ILogger Logger => _logger;
    internal IConfiguration Configuration => _configuration;
    internal string? CliPathOverride => _cliPathOverride;
    internal bool TryGetProc(string jobKey, out ProcInfo info) => _processes.TryGetValue(jobKey, out info!);

    /// <summary>
    /// Set the per-instance CLI path override (generic across all CLIs). The
    /// per-CLI <see cref="CliBehavior.GetCliPath"/> reads
    /// <see cref="CliPathOverride"/> first.
    /// </summary>
    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("{Cli} CLI path set to: {Path}", CliType, GetCliPath());
    }

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;

    /// <summary>
    /// Typed lifecycle events from the CLI (ADR-0013). Subclasses with an
    /// adapter (Claude / Codex / Gemini) raise these alongside
    /// the legacy <see cref="OnOutput"/> stream so consumers can migrate
    /// incrementally. Subclasses without an adapter emit nothing here -
    /// the runner falls back to the silence-only watchdog in that case.
    /// </summary>
    public event Action<string, CliRunEvent>? OnRunEvent;

    /// <summary>
    /// Engine entry point for emitting typed events. Wraps the public
    /// invocation with a per-subscriber try/catch so a buggy listener
    /// cannot crash the read loop. Internal so behaviors can raise events.
    /// </summary>
    internal void RaiseRunEvent(string jobKey, CliRunEvent evt)
    {
        if (_processes.TryGetValue(jobKey, out var info))
        {
            if (evt is CliRunEvent.TurnFailed failed
                && !string.IsNullOrWhiteSpace(failed.Reason))
            {
                info.LastTurnFailureReason = failed.Reason;
            }
            else if (evt is CliRunEvent.TurnCompleted)
            {
                // A later successful turn resolves an earlier turn failure in
                // the same process. Do not reuse stale diagnostic evidence if
                // the process subsequently fails for a different reason.
                info.LastTurnFailureReason = null;
            }
        }
        try { OnRunEvent?.Invoke(jobKey, evt); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnRunEvent subscriber threw for {JobId}", jobKey); }
    }

    internal GenericCliExecutionService(CliBehavior behavior, ILogger logger, IConfiguration configuration)
    {
        _behavior = behavior;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Quality-first Claude Opus default: the newest available member of the
    /// claude-opus family (AGT-2716). Lives on the engine (the old
    /// <c>ClaudeCliService.DefaultOpusModel</c> home was deleted with the shim).
    /// </summary>
    public static string DefaultOpusModel => ModelFamilyResolver.Resolve(ModelFamilies.ClaudeOpus);

    // ── Built-in CLI factory helpers ────────────────────────────────────
    //
    // The thin per-CLI shim classes were deleted; production DI (Program.cs)
    // and the test fixtures build a concrete engine per CLI through these
    // factories. Each wires the per-CLI CliBehavior from BuiltInCliBehaviors.

    /// <summary>Build a Claude-Code engine from the per-CLI dependencies.</summary>
    internal static GenericCliExecutionService ForClaude(
        ILogger logger,
        IConfiguration configuration,
        CliUsageParserRegistry? usageParsers = null,
        ICliModelRegistry? modelRegistry = null,
        ClaudeModelDiscovery? modelDiscovery = null)
        => new GenericCliExecutionService(
            BuiltInCliBehaviors.Claude(usageParsers, modelRegistry ?? new CliModelRegistry(), modelDiscovery),
            logger, configuration);

    /// <summary>Build a Codex engine from the per-CLI dependencies.</summary>
    internal static GenericCliExecutionService ForCodex(
        ILogger logger,
        IConfiguration configuration,
        CodexModelDiscovery modelDiscovery,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry)
        => new GenericCliExecutionService(
            BuiltInCliBehaviors.Codex(modelDiscovery, usageParsers, modelRegistry),
            logger, configuration);

    /// <summary>Build an Antigravity/Gemini engine (no extra dependencies).</summary>
    internal static GenericCliExecutionService ForAntigravity(
        ILogger logger,
        IConfiguration configuration)
        => new GenericCliExecutionService(BuiltInCliBehaviors.Antigravity(), logger, configuration);

    public string GetCliPath() => _behavior.GetCliPath(this);

    /// <summary>
    /// Default: accept any non-empty session name. Behaviors with strict
    /// session-id formats (Claude requires UUIDs) supply a delegate that
    /// rejects names that came from a different CLI's session store.
    /// </summary>
    public bool IsCompatibleSessionName(string? sessionName)
        => _behavior.IsCompatibleSessionName?.Invoke(this, sessionName)
           ?? !string.IsNullOrWhiteSpace(sessionName);

    public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
        => _behavior.TestCliPath?.Invoke(this, path) ?? DefaultTestCliPath(path);

    internal (bool Available, string? Version, string Path) DefaultTestCliPath(string? path = null)
    {
        var testPath = ResolveExecutable(path?.Trim() ?? GetCliPath());
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = testPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var rawVersion = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            // Keep only the first non-empty line — some CLIs print update hints on line 2+
            var version = rawVersion.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return (proc.ExitCode == 0, version, testPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI not available at path '{Path}'", testPath);
            return (false, null, testPath);
        }
    }

    public bool IsAvailable() => TestCliPath().Available;

    /// <summary>
    /// On Windows, npm-installed Node CLIs ship as a Bash shim (no extension) plus
    /// a <c>.cmd</c> launcher. <see cref="Process.Start"/> can only execute the
    /// <c>.cmd</c>/<c>.exe</c>, so we resolve bare names to their PATHEXT match.
    /// On non-Windows the input is returned unchanged.
    /// </summary>
    public static string ResolveExecutable(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return nameOrPath;
        if (!OperatingSystem.IsWindows()) return nameOrPath;
        // Already absolute or has an extension — trust the caller.
        if (Path.IsPathRooted(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;
        if (Path.HasExtension(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        // If a path was given (rooted or relative with extension), keep it.
        if (Path.IsPathRooted(nameOrPath))
        {
            foreach (var ext in exts)
            {
                var candidate = nameOrPath + ext;
                if (File.Exists(candidate)) return candidate;
            }
            return nameOrPath;
        }

        foreach (var dir in dirs)
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, nameOrPath + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return nameOrPath;
    }

    /// <summary>
    /// Normalize or replace a persisted job model before it reaches argv,
    /// telemetry, or the synthetic started line. Default: trim / null-if-blank.
    /// Drivers with CLI-specific model namespaces supply a delegate to prevent a
    /// stale model from another CLI being passed through after the job's
    /// <c>cliType</c> changes.
    /// </summary>
    public string? NormalizeModelForInvocation(string? model)
        => _behavior.NormalizeModelForInvocation?.Invoke(this, model)
           ?? (string.IsNullOrWhiteSpace(model) ? null : model.Trim());

    /// <summary>Try to extract session metadata from a fresh output line (behavior hook; default no-op).</summary>
    internal void OnOutputLine(ProcInfo info, CliOutputLine line)
        => _behavior.OnOutputLine?.Invoke(this, info, line);

    /// <summary>
    /// Map one raw stdout/stderr line to zero or more <see cref="CliRunEvent"/>
    /// instances. Default: yield nothing (CLIs without an adapter stay on the
    /// silence-only watchdog). Behaviors with an adapter (Claude / Codex /
    /// Gemini) supply a delegate to the per-CLI mapping function.
    ///
    /// <para>
    /// The engine fires <see cref="OnRunEvent"/> for every event returned here,
    /// in order, on the same read-loop thread. Adapters must be pure functions
    /// and not throw - exceptions are swallowed so a malformed frame cannot
    /// crash the read loop.
    /// </para>
    /// </summary>
    internal IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
        => _behavior.MapLineToRunEvents?.Invoke(this, jobKey, line) ?? Array.Empty<CliRunEvent>();

    internal void CaptureRawLine(string jobKey, CliOutputLine line)
        => _behavior.CaptureRawLine?.Invoke(this, jobKey, line);

    /// <summary>
    /// Arm a side-channel liveness watcher for a freshly spawned run. Default:
    /// no-op. Behaviors that have a stdout-independent activity signal (Claude
    /// watches <c>~/.claude/projects/&lt;cwd&gt;/&lt;uuid&gt;.jsonl</c> mtime) supply a
    /// delegate that constructs a watcher and stores it on
    /// <see cref="ProcInfo.SessionLiveness"/>; the engine disposes it in
    /// <see cref="MonitorProcessAsync"/> when the process exits.
    ///
    /// <para>
    /// The watcher should reset the watchdog silence clock by raising a
    /// <see cref="CliRunEvent.Heartbeat"/> via <see cref="RaiseRunEvent"/>
    /// (Heartbeat is an activity signal in
    /// <see cref="RunPhaseTransitions.IsActivitySignal"/>). For a resume
    /// (<paramref name="resumeSession"/> true with a known
    /// <paramref name="sessionName"/>) the session id is available at spawn,
    /// so the watcher can arm immediately - the case that matters most for
    /// SessionInitializing, where there is no stdout for the whole window.
    /// For a fresh run the behavior typically arms once it captures the
    /// CLI-assigned session id from the first stdout frame.
    /// </para>
    /// </summary>
    internal void StartSessionLiveness(string jobKey, ProcInfo info, bool resumeSession, string? sessionName)
        => _behavior.StartSessionLiveness?.Invoke(this, info, resumeSession, sessionName);

    /// <summary>
    /// Translate a single raw line read from the CLI's stdout or stderr into
    /// one or more user-visible buffer lines. Default: pass through unchanged.
    /// Used by Claude / Codex / Gemini behaviors to expand stream-json NDJSON
    /// frames into the marker-line convention the frontend's activity log parser
    /// already understands.
    /// </summary>
    public IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
        => _behavior.TransformReadLine?.Invoke(this, raw) ?? new[] { raw };

    public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
        => _behavior.GetModelCatalog?.Invoke(this, forceRefresh, ct) ?? DefaultModelCatalogAsync();

    internal Task<CliModelCatalog> DefaultModelCatalogAsync()
    {
        return Task.FromResult(new CliModelCatalog
        {
            Models = [],
            Source = "default-only",
            FetchedAt = DateTime.UtcNow
        });
    }

    public Task<(CliExecution? Execution, string? Error)> StartAsync(
        string jobId,
        string jobKey,
        string prompt,
        string workingDirectory,
        string? sessionName = null,
        bool resumeSession = false,
        string? model = null,
        string? thinkingLevel = null,
        string? jobFolderPath = null,
        string? permissionMode = null,
        string? contextMode = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        return StartCarAsync(
            jobId, jobKey, prompt, workingDirectory, sessionName,
            resumeSession, model, thinkingLevel, jobFolderPath,
            permissionMode, contextMode, environment, ct);
    }

    internal static string BuildStartedLineText(
        string cliType,
        int processId,
        string? model,
        string? thinkingLevel,
        string? sessionName,
        bool resumeSession)
        => $"[taskboard] Started {cliType} CLI (PID {processId})"
           + (string.IsNullOrWhiteSpace(model) ? "" : $", model={model}")
           + (string.IsNullOrWhiteSpace(thinkingLevel) ? "" : $", thinkingLevel={thinkingLevel}")
           + (string.IsNullOrWhiteSpace(sessionName) ? "" : $", session={sessionName}")
           + (resumeSession ? " (resume)" : "");

    /// <summary>
    public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        try
        {
            if (IsLive(info))
            {
                // Record the intent BEFORE Kill so MonitorProcessAsync's
                // classifier can tell the deliberate kill apart from a real
                // crash - even if Kill races the natural exit by a tick, the
                // marker is set and the classifier does the right thing.
                info.StopReason = reason;
                if (info.DurableWorker != null && info.CarDriver == null)
                {
                    info.DurableWorker.Kill();
                    _logger.LogInformation(
                        "Stopped reattached durable {Cli} worker for job {JobId} (reason={Reason})",
                        CliType,
                        jobKey,
                        reason);
                    return true;
                }
                if (info.CarDriver != null)
                {
                    var stopped = info.CarDriver.Stop(jobKey, reason);
                    if (stopped)
                        _logger.LogInformation("Stopped {Cli} CAR run for job {JobId} (reason={Reason})", CliType, jobKey, reason);
                    return stopped;
                }
                if (info.ProcessReaper is not null)
                    info.ProcessReaper.Terminate();
                else
                    KillProcessTree(info.Process, jobKey);
                _logger.LogInformation("Killed {Cli} process for job {JobId} (reason={Reason})", CliType, jobKey, reason);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill {Cli} process for job {JobId}", CliType, jobKey);
            return false;
        }
    }

    public bool SendInput(string jobKey, string input)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        if (!IsLive(info)) return false;
        if (info.CarDriver != null) return info.CarDriver.SendInput(jobKey, input);
        try
        {
            info.Process.StandardInput.WriteLine(input);
            return true;
        }
        catch { return false; }
    }

    public List<CliOutputLine> GetOutput(string jobKey)
    {
        if (_processes.TryGetValue(jobKey, out var info))
            return info.OutputBuffer.ToList();

        // No live process. Either the backend was restarted while a CLI run
        // was in flight, or the post-exit retention window elapsed. Recover
        // from the persisted per-stream files (merged by timestamp) so the
        // Activity Log isn't blank — this is the durability guarantee callers
        // depend on. ReadMerged also falls back to the legacy single-file layout.
        return RunLogStore.ReadMerged(GetOutputLogDir(jobKey));
    }

    public void DiscardPersistedOutput(string jobKey)
    {
        // If the process is still tracked, drop the open writer first so the
        // Windows file handle is released before delete.
        ReleaseOutputResources(jobKey);

        try { RunLogStore.DeleteRun(GetOutputLogDir(jobKey)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete persisted CLI log dir for {JobKey}", jobKey); }
    }

    public void ReleaseOutputResources(string jobKey)
    {
        if (_processes.TryGetValue(jobKey, out var info))
        {
            try { info.OutputLog.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase: already disposed"); /* already disposed */ }
        }
    }

    public CliExecution? GetExecution(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.Execution : null;

    public string? GetWorkingDirectory(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.WorkingDirectory : null;

    public bool ConfirmRecoveredExecution(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info) || !info.RecoveredAfterStartup)
            return false;
        return Interlocked.CompareExchange(ref info.RecoveryDisposition, 1, 0) == 0;
    }

    public bool RejectRecoveredExecution(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info) || !info.RecoveredAfterStartup)
            return false;
        Interlocked.Exchange(ref info.RecoveryDisposition, 2);
        return true;
    }

    public SessionUsage? GetLastUsage(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastUsage : null;

    /// <summary>The CLI-native session id captured for a run (from its init/thread frame), or null. Lifted to the base so the runner reads it without knowing which CLI ran.</summary>
    public string? GetCapturedSessionId(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;

    /// <summary>The most recent parsed per-turn usage snapshot for a run (+ when observed + run start), or null. Read-only over the run's tracking entry.</summary>
    public (ParsedTurnUsage Usage, DateTime ObservedAt, DateTime StartedAt)? GetLastParsedTurnUsage(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return null;
        if (info.LastParsedUsage == null || info.LastParsedUsageAt == null) return null;
        return (info.LastParsedUsage, info.LastParsedUsageAt.Value, info.Execution.StartedAt);
    }

    public (IReadOnlyList<ParsedTurnUsage> Usages, DateTime ObservedAt, DateTime StartedAt)? GetLastParsedTurnUsages(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return null;
        if (info.LastParsedUsages is not { Count: > 0 } usages || info.LastParsedUsageAt == null) return null;
        return (usages, info.LastParsedUsageAt.Value, info.Execution.StartedAt);
    }

    /// <summary>
    /// Claude: latest <c>rate_limit_event</c> snapshot parsed from the
    /// stream-json output, or null. Read-only over the run's tracking entry;
    /// surfaced via <c>GET /api/tasks/{id}/claude/session-info</c>.
    /// </summary>
    public ClaudeRateLimitSnapshot? GetLastRateLimit(string jobKey)
        => TryGetProc(jobKey, out var info) ? info.LastRateLimit : null;

    /// <summary>
    /// Codex: inputs the runner's per-tick silent-completion check needs.
    /// Returns <c>null</c> when no <c>command_execution</c> <c>item.completed</c>
    /// has been observed yet for this run. Pure read on top of the per-CLI
    /// capture done inside the behavior's <c>MapLineToRunEvents</c>.
    /// </summary>
    public CodexLastCommandSnapshot? GetLastCommandExecution(string jobKey)
    {
        if (!TryGetProc(jobKey, out var info)) return null;
        if (info.LastCommandObservedAt is null) return null;
        return new CodexLastCommandSnapshot(
            ExitCode: info.LastCommandExitCode,
            Command: info.LastCommandLine,
            OutputTail: info.LastCommandOutputTail,
            ObservedAt: info.LastCommandObservedAt.Value);
    }

    /// <summary>Codex: true once the per-tick silent-completion detector tripped for this run.</summary>
    public bool IsSilentCompletionTripped(string jobKey)
        => TryGetProc(jobKey, out var info) && info.SilentCompletionTripped;

    /// <summary>Real CLIs emit a session id on every run; a behavior that does not sets this false.</summary>
    public bool EmitsSessionId => _behavior.EmitsSessionId;

    /// <summary>Whether the runner should reconstruct usage post-hoc when a run finished without a usage footer (Claude reads its session JSONL). Default false.</summary>
    public bool NeedsPostHocUsageReconstruction => _behavior.NeedsPostHocUsageReconstruction;

    public bool IsRunningForProject(string rootPath) =>
        _processes.Values.Any(p => p.WorkingDirectory == rootPath && IsLive(p));

    public IReadOnlyList<(string JobKey, CliExecution Execution)> RunningExecutions()
    {
        var result = new List<(string, CliExecution)>();
        foreach (var kv in _processes)
        {
            var info = kv.Value;
            if (!IsLive(info) && !info.RecoveredTerminalPending) continue;
            var exec = info.Execution;
            if (exec == null) continue;
            if (!string.Equals(exec.Status, "running", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add((kv.Key, exec));
        }
        return result;
    }

    private static bool IsLive(ProcInfo info)
    {
        if (info.DurableWorker != null)
            return info.DurableWorker.Inspect(info.WorkingDirectory).IsLive;
        return !SafeHasExited(info.Process);
    }

    /// <summary>
    /// Default convention-based execution context (ASS-1739 / T1a): scalar
    /// header from the run's <see cref="ProcInfo"/> plus the per-CLI
    /// convention sources from <see cref="CliContextConventions"/>. CLIs with a
    /// richer self-report (Claude's init frame) override this and merge.
    /// Returns null when the run is unknown.
    /// </summary>
    public AgentStudio.Shared.CliExecutionContext? DescribeContextSources(string jobKey)
        => _behavior.DescribeContextSources?.Invoke(this, jobKey) ?? DefaultDescribeContextSources(jobKey);

    internal AgentStudio.Shared.CliExecutionContext? DefaultDescribeContextSources(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? BuildConventionContext(info) : null;

    /// <summary>
    /// T1b (ASS-1742): shared-only by default. Claude / Codex behaviors set this
    /// true and provide a real <see cref="CliBehavior.PrepareCleanContext"/>.
    /// Re-declared here (not just inherited as a default interface member) so the
    /// engine <c>StartAsync</c> can read it through <c>this</c>.
    /// </summary>
    public bool SupportsCleanContext => _behavior.SupportsCleanContext;

    /// <inheritdoc cref="ICliExecutionService.PrepareCleanContext" />
    public CleanContextPreparation? PrepareCleanContext(string jobKey, string workingDirectory)
        => _behavior.PrepareCleanContext?.Invoke(this, jobKey, workingDirectory);

    /// <summary>
    /// Acquire the clean-context home for one attempt of a task: reuse the
    /// task's registered home when it is still on disk (session-state
    /// stability across attempts/recoveries of the same run — MKT-8 / WEB-14),
    /// otherwise cut a fresh one and register it. Returns
    /// <c>(preparation, reused)</c>; <c>(null, false)</c> when preparation
    /// failed and the caller should fall back to a shared run.
    /// </summary>
    internal (CleanContextPreparation? Preparation, bool Reused) AcquireCleanContext(string jobKey, string workingDirectory)
    {
        if (_cleanContextsByJob.TryGetValue(jobKey, out var existing))
        {
            if (Directory.Exists(existing.TempHome))
                return (existing, true);
            // The home vanished underneath us: the registration is stale; drop
            // it and let the durable store create the deterministic path again.
            _cleanContextsByJob.TryRemove(new KeyValuePair<string, CleanContextPreparation>(jobKey, existing));
        }

        var prepared = PrepareCleanContext(jobKey, workingDirectory);
        if (prepared != null) _cleanContextsByJob[jobKey] = prepared;
        return (prepared, prepared?.Reused ?? false);
    }

    /// <inheritdoc cref="ICliExecutionService.GetPersistentCleanContextHome" />
    public string? GetPersistentCleanContextHome(string jobKey)
    {
        if (_cleanContextsByJob.TryGetValue(jobKey, out var prep) && Directory.Exists(prep.TempHome))
            return prep.TempHome;

        return CleanContextPreparer.TryGetExistingHome(
            CliType,
            ResolveUserHome(),
            jobKey,
            out var home,
            CleanContextRetentionHostedService.ResolveRootOverride(_configuration))
            ? home
            : null;
    }

    /// <summary>
    /// Build the convention-only context for a tracked run. Shared by the engine
    /// <see cref="DefaultDescribeContextSources"/> and the Claude behavior (which
    /// adds init-frame data on top). The scalar permission mode is the
    /// platform mode the runner resolved, surfaced via its display name.
    /// Internal so behaviors can call it.
    /// </summary>
    internal AgentStudio.Shared.CliExecutionContext BuildConventionContext(ProcInfo info)
    {
        var clean = info.CleanContext;
        // Under clean the home-rooted convention probes (~/.claude, ~/.codex)
        // no longer reflect what the run loaded. The CLI read the task home
        // instead. Skip them (home=null) and surface the relocated paths from the
        // preparation so the panel shows the isolated home, not the operator's.
        var home = clean != null ? null : ResolveUserHome();
        var sources = CliContextConventions.For(CliType, info.WorkingDirectory, home);
        if (clean != null) sources.AddRange(clean.Sources);
        return new()
        {
            Cli = CliType,
            Model = info.Execution.Model,
            PermissionMode = info.PermissionMode is { } m ? CliPermissionModes.DisplayName(m) : null,
            Cwd = info.WorkingDirectory,
            ContextMode = info.ContextMode,
            CapturedAt = DateTime.UtcNow,
            Source = "convention",
            Sources = sources,
        };
    }

    /// <summary>
    /// The user-profile home used to root the convention probes
    /// (<c>~/.claude</c>, <c>~/.codex</c>, ...). Matches the resolution the
    /// session inspectors use so the probed paths line up with what the CLIs
    /// actually read.
    /// </summary>
    internal static string? ResolveUserHome()
        => Environment.GetEnvironmentVariable("USERPROFILE")
           ?? Environment.GetEnvironmentVariable("HOME");

    public DateTime? GetLastStreamedAt(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastStreamedAt : null;

    public void ResetSilenceClock(string jobKey)
    {
        if (_processes.TryGetValue(jobKey, out var info))
        {
            info.LastStreamedAt = DateTime.UtcNow;
        }
    }

    public WatchdogState GetWatchdogState(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastWatchdogState : WatchdogState.Healthy;

    public void SetWatchdogState(string jobKey, WatchdogState state)
    {
        if (_processes.TryGetValue(jobKey, out var info)) info.LastWatchdogState = state;
    }

    /// <summary>
    /// Runner-side hook for the Codex silent-completion detector. Writes
    /// the synthetic <c>[codex-silent-completion]</c> marker line into the
    /// run's output buffer + persisted log (so <see cref="AgentOutcomeAnalyzer"/>
    /// recognises it on the post-run analysis), latches the
    /// <see cref="ProcInfo.SilentCompletionTripped"/> flag so the per-tick
    /// detector cannot fire again, and asks the CLI service to stop the
    /// process with <see cref="RunStopReason.SilentCompletion"/>. Returns
    /// <c>true</c> when the trip happened, <c>false</c> when the latch was
    /// already set (idempotent for callers that race).
    ///
    /// <para>
    /// Lives on the base class so the wiring matches
    /// <c>CheckEnvironmentBlocker</c>: same marker shape, same kill semantics,
    /// same buffer + log append discipline. Today only the runner's Codex
    /// path uses it (the detector itself is Codex-only); other CLIs would
    /// hook in identically if their own silent-completion shape is later
    /// recognised.
    /// </para>
    /// </summary>
    public bool TripSilentCompletion(string jobKey, string diagnosis)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        if (info.SilentCompletionTripped) return false;

        info.SilentCompletionTripped = true;

        var synthetic = new CliOutputLine
        {
            Timestamp = DateTime.UtcNow,
            Stream = "system",
            Text = $"[codex-silent-completion] {diagnosis}"
        };
        info.OutputBuffer.Add(synthetic);
        try
        {
            if (!info.OutputLog.Append(synthetic))
                _logger.LogWarning("Failed to persist codex-silent-completion marker for {TaskKey}", jobKey);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Persisting codex-silent-completion marker failed for {TaskKey}", jobKey); }
        try { OnOutput?.Invoke(jobKey, synthetic); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase:703"); }

        _logger.LogWarning(
            "Codex silent-completion tripped for {Cli} job {TaskKey}: {Diagnosis}",
            CliType, jobKey, diagnosis);

        try { Stop(jobKey, RunStopReason.SilentCompletion); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stop after codex-silent-completion failed for {TaskKey}", jobKey); }
        return true;
    }

    /// <summary>
    /// Startup hook. Default behaviour for base-class CLIs (Claude / Codex /
    /// Gemini) is to <b>reap</b> orphaned processes — kill any CLI process that
    /// outlived a previous backend run. We deliberately do not re-attach: the
    /// stdout pipe is unrecoverable, so an orphan would keep mutating the repo
    /// while the user's UI is blind. Killing on startup eliminates the
    /// double-execution risk and lets the resume-prompt logic in
    /// <see cref="ProjectRunner"/> drive a clean fresh continuation.
    /// <para>
    /// Subclasses that genuinely want re-attach semantics can override this.
    /// </para>
    /// </summary>
    public void ReattachOnStartup() => ReattachDurableWorkersAndReapLegacyOrphans();

    /// <summary>
    /// Runs the canonical <see cref="AgentEnvironmentDetector"/> against a
    /// freshly read line. When a recognised OS-level / sandbox blocker
    /// fires often enough (or once for an unambiguous pattern), writes a
    /// synthetic <c>[environment-blocker]</c> system line so
    /// <see cref="Runner.AgentOutcomeAnalyzer"/> can pick it up post-run,
    /// then terminates the child via <see cref="Stop(string, RunStopReason)"/>.
    /// First-trip latches on <see cref="ProcInfo.EnvironmentBlockerTripped"/>
    /// so a flurry of repeated stderr lines produces exactly one outcome.
    /// </summary>
    private void CheckEnvironmentBlocker(string jobKey, ProcInfo info, CliOutputLine rawLine)
    {
        if (info.EnvironmentBlockerTripped) return;
        if (AgentEnvironmentDetector.IsRecoverySignal(rawLine.Text))
        {
            info.EnvironmentBlockerHitCount = 0;
            return;
        }

        AgentEnvironmentDetector.EnvironmentBlockerPattern? match;
        // MatchRuntimeBlocker (not Match) so an agent that greps/reads blocker
        // strings into its own command_execution / tool-result output cannot
        // self-terminate the run. See AgentEnvironmentDetector.IsAgentToolEcho.
        try { match = AgentEnvironmentDetector.MatchRuntimeBlocker(rawLine.Text); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Environment-blocker detector threw for {TaskKey}", jobKey);
            return;
        }
        if (match == null) return;

        info.EnvironmentBlockerHitCount++;
        var threshold = match.ImmediateTerminate ? 1 : AgentEnvironmentDetector.HitThreshold;
        if (info.EnvironmentBlockerHitCount < threshold) return;

        info.EnvironmentBlockerTripped = true;
        var diagnosis = AgentEnvironmentDetector.Diagnose(match, CliType);

        var synthetic = new CliOutputLine
        {
            Timestamp = DateTime.UtcNow,
            Stream = "system",
            Text = $"[environment-blocker] {diagnosis}"
        };
        info.OutputBuffer.Add(synthetic);
        try
        {
            if (!info.OutputLog.Append(synthetic))
                _logger.LogWarning("Failed to persist environment-blocker marker for {TaskKey}", jobKey);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Persisting environment-blocker marker failed for {TaskKey}", jobKey); }
        try { OnOutput?.Invoke(jobKey, synthetic); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase:781"); }

        _logger.LogWarning(
            "Environment blocker '{Pattern}' detected for {Cli} job {TaskKey} after {Hits} hit(s); terminating run",
            match.Id, CliType, jobKey, info.EnvironmentBlockerHitCount);

        try { Stop(jobKey, RunStopReason.EnvironmentBlocker); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stop after environment-blocker failed for {TaskKey}", jobKey); }
    }

    /// <summary>
    /// Rate-limited accounting for a failed CLI-output persist. The read loop
    /// runs once per streamed line, so logging every failure turned an
    /// unwritable output target mid-stream into hundreds of identical warnings
    /// per second - a log + I/O flood that helped take the host down. Instead
    /// we count the drops and emit one warning on the first failure, then at
    /// most one every <see cref="PersistWarnInterval"/> while the condition
    /// persists, carrying the captured cause and the running drop count.
    /// </summary>
    private void NotePersistFailure(string jobKey, ProcInfo info)
    {
        var count = ++info.PersistFailureCount;
        var now = DateTime.UtcNow;
        if (count == 1 || now - info.LastPersistWarnAtUtc >= PersistWarnInterval)
        {
            info.LastPersistWarnAtUtc = now;
            _logger.LogWarning(
                "Failed to persist CLI output line for {JobId}: {Reason} ({Count} line(s) dropped so far; suppressing identical warnings for {Window}s)",
                jobKey, info.OutputLog.LastAppendError ?? "unknown I/O error", count, (int)PersistWarnInterval.TotalSeconds);
        }
    }

    private static readonly TimeSpan PersistWarnInterval = TimeSpan.FromSeconds(30);


    /// <summary>
    /// Retain a finished run for the UI/durable-log handoff, then evict it with
    /// an identity guard. Both execution engines share this host-owned lifetime.
    /// </summary>
    private void ScheduleEviction(string jobKey, ProcInfo info)
    {
        _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(_ =>
        {
            // Remove only this run. A later continuation can replace the same
            // key before the timer fires and owns its own state and clean home.
            if (!_processes.TryRemove(new KeyValuePair<string, ProcInfo>(jobKey, info)))
                return;

            info.OutputLog.Dispose();
            try { info.SessionLiveness?.Dispose(); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase: retention watcher dispose"); }

            if (_cleanContextsByJob.TryRemove(jobKey, out var cleanAtBoundary))
            {
                try { cleanAtBoundary.Dispose(); }
                catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase: clean-context dispose"); }
            }
            try { info.CleanContext?.Dispose(); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase: clean-context dispose"); }

            try { info.ProcessReaper?.Dispose(); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase: process-reaper dispose"); }
        });
    }

    // ── Output log persistence ───────────────────────────────────────────

    /// <summary>
    /// Resolve the per-run output directory (<c>.runtime/cli-output/&lt;cli&gt;-&lt;taskKey&gt;/</c>)
    /// that holds one append-only file per stream. Public so the runner can
    /// recover the Activity Log from disk after a backend restart, when no
    /// <see cref="ProcInfo"/> exists in memory anymore.
    /// </summary>
    public string GetOutputLogDir(string jobKey)
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime", "cli-output")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "cli-output");
        Directory.CreateDirectory(baseDir);
        var safe = SanitizeForFile($"{CliType}-{jobKey}");
        return Path.Combine(baseDir, safe);
    }

    /// <summary>
    /// Legacy single-file path (pre-5b layout: <c>&lt;cli&gt;-&lt;taskKey&gt;.jsonl</c>).
    /// Retained only so backward-compatible reads can find output from a run
    /// that started before per-stream files existed.
    /// </summary>
    public string GetOutputLogPath(string jobKey) => GetOutputLogDir(jobKey) + ".jsonl";

    private static string SanitizeForFile(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length > 180 ? name[^180..] : name;
    }

    // ── Active-job tracking + orphan reaper ──────────────────────────────
    //
    // Why this exists: a CLI run is a child process of the backend. On a
    // backend crash / `dotnet watch` rebuild / IDE stop, that child can
    // outlive its parent — silently editing files, calling APIs, burning
    // quota with no UI to watch it. The next backend start therefore reaps:
    // reads the persisted PIDs, kills any that are still alive (with a
    // PID-recycling check via process name + start time), and clears the
    // file. Cheaper and less risky than re-attaching, which would need a
    // working stdout pipe we can't get back.

    private record ActiveJob
    {
        public string TaskKey { get; init; } = "";
        public string JobId { get; init; } = "";
        public int ProcessId { get; init; }
        public string? ProcessName { get; init; }
        public DateTime? ProcessStartTimeUtc { get; init; }
        public DateTime StartedAt { get; init; }
        public string? WorkerDirectory { get; init; }
        public string? WorkingDirectory { get; init; }
        public string? JobFolderPath { get; init; }
        public string? Model { get; init; }
        public string? ThinkingLevel { get; init; }
        public string? PermissionMode { get; init; }
        public string? ContextMode { get; init; }
        public string? SessionName { get; init; }
    }

    private readonly object _activeJobsLock = new();
    private static readonly JsonSerializerOptions ActiveJobsJsonOpts = new() { WriteIndented = true };

    private string GetActiveJobsPath()
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, $"active-jobs-{CliType}.json");
    }

    private string GetDurableWorkerDirectory(string jobKey)
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime", "local-cli-workers")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "local-cli-workers");
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, SanitizeForFile($"{CliType}-{jobKey}"));
    }

    private List<ActiveJob> ReadActiveJobs()
    {
        var path = GetActiveJobsPath();
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ActiveJob>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read active-jobs file at {Path}", path);
            return [];
        }
    }

    private void WriteActiveJobs(List<ActiveJob> list)
    {
        try
        {
            var path = GetActiveJobsPath();
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(list, ActiveJobsJsonOpts),
                new UTF8Encoding(false));
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                stream.Flush(flushToDisk: true);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write active-jobs file");
        }
    }

    private void UpsertActiveJob(ActiveJob entry)
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            list.RemoveAll(e => e.TaskKey == entry.TaskKey);
            list.Add(entry);
            WriteActiveJobs(list);
        }
    }

    private void RemoveActiveJob(string jobKey)
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            var removed = list.RemoveAll(e => e.TaskKey == jobKey);
            if (removed > 0) WriteActiveJobs(list);
        }
    }

    private static string? SafeProcessName(Process p)
    {
        try { return p.ProcessName; } catch { return null; }
    }

    private static DateTime? SafeProcessStartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    /// <summary>
    /// Reads the persisted active-jobs file and kills any process that is
    /// still alive (orphan from a previous backend run). PID recycling is
    /// guarded by matching <see cref="Process.ProcessName"/> and
    /// <see cref="Process.StartTime"/> against the persisted values — a
    /// 5-second tolerance accounts for clock skew between the recorded UTC
    /// time and what Windows reports back. The file is always cleared at
    /// the end so a half-clean run never leaves partial state behind.
    /// </summary>
    private void ReattachDurableWorkersAndReapLegacyOrphans()
    {
        lock (_activeJobsLock)
        {
            var entries = ReadActiveJobs();
            if (entries.Count == 0) return;
            var retained = new List<ActiveJob>();

            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.WorkerDirectory)
                    && !string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                {
                    try
                    {
                        var worker = DurableLocalCliProcess.Attach(
                            entry.WorkerDirectory!,
                            entry.ProcessId,
                            entry.ProcessStartTimeUtc ?? DateTime.MinValue);
                        AdoptDurableWorker(entry, worker, worker.Inspect(entry.WorkingDirectory!));
                        retained.Add(entry);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Could not adopt durable {Cli} worker for {JobId}; reporting it as lost",
                            CliType,
                            entry.JobId);
                        ReportDurableWorkerLost(entry, ex.Message);
                        continue;
                    }
                }

                ReapLegacyEntry(entry);
            }

            WriteActiveJobs(retained);
        }
    }

    private void ReapLegacyEntry(ActiveJob entry)
    {
        Process? process = null;
        try { process = Process.GetProcessById(entry.ProcessId); }
        catch (ArgumentException ex) { _ = ex; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy orphan lookup failed for PID {Pid} ({Cli})", entry.ProcessId, CliType);
            return;
        }
        if (process == null) return;
        try
        {
            if (!process.HasExited && MatchesRecordedIdentity(process, entry))
            {
                SafeKillReap(process, entry);
                _logger.LogWarning(
                    "Reaped pre-durability orphan {Cli} process for {JobId} (PID {Pid})",
                    CliType,
                    entry.JobId,
                    entry.ProcessId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reap pre-durability PID {Pid} ({Cli})", entry.ProcessId, CliType);
        }
        finally
        {
            process.Dispose();
        }
    }

    protected void ReapOrphans()
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            if (list.Count == 0) return;

            foreach (var entry in list)
            {
                Process? proc = null;
                try { proc = Process.GetProcessById(entry.ProcessId); }
                catch (ArgumentException) { proc = null; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetProcessById failed for {Pid} ({Cli})", entry.ProcessId, CliType);
                    continue;
                }

                if (proc == null) continue;

                try
                {
                    if (proc.HasExited) continue;

                    // PID-recycling guard: if the running process clearly isn't
                    // the one we recorded, leave it alone.
                    if (!MatchesRecordedIdentity(proc, entry)) continue;

                    SafeKillReap(proc, entry);
                    _logger.LogWarning("Reaped orphan {Cli} CLI for job {Job} (PID {Pid}) left over from a previous backend run",
                        CliType, entry.JobId, entry.ProcessId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reap PID {Pid} ({Cli})", entry.ProcessId, CliType);
                }
                finally
                {
                    try { proc.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase:1194"); }
                }
            }

            // Always wipe the file: any process that legitimately survives
            // (PID-recycling skip) was not ours to track anyway. New runs
            // will repopulate via UpsertActiveJob.
            WriteActiveJobs([]);
        }
    }

    /// <summary>
    /// PID-recycling guard shared by the startup reaper and the periodic
    /// stale-orphan sweep: the live process at <paramref name="entry"/>'s
    /// recorded PID must still match the recorded process name and start time
    /// (5s tolerance for UTC/clock skew). Returns false when the PID has been
    /// recycled by an unrelated process, so callers never kill a stranger.
    /// </summary>
    private bool MatchesRecordedIdentity(Process proc, ActiveJob entry)
    {
        var liveName = SafeProcessName(proc);
        var liveStart = SafeProcessStartTime(proc);

        // Name comparison, when both sides are available. A definite mismatch
        // means the PID was recycled by an unrelated process - never kill it.
        var nameConfirmed = false;
        if (!string.IsNullOrEmpty(entry.ProcessName) && !string.IsNullOrEmpty(liveName))
        {
            if (!string.Equals(liveName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping reap of PID {Pid}: name '{Live}' != recorded '{Recorded}'",
                    entry.ProcessId, liveName, entry.ProcessName);
                return false;
            }
            nameConfirmed = true;
        }

        // Start-time comparison (5s tolerance for UTC/clock skew), when both
        // sides are available. A definite mismatch is again a recycled PID.
        var startConfirmed = false;
        if (entry.ProcessStartTimeUtc.HasValue && liveStart.HasValue)
        {
            if (Math.Abs((liveStart.Value - entry.ProcessStartTimeUtc.Value).TotalSeconds) > 5)
            {
                _logger.LogDebug("Skipping reap of PID {Pid}: start time mismatch ({Live} vs {Recorded})",
                    entry.ProcessId, liveStart, entry.ProcessStartTimeUtc);
                return false;
            }
            startConfirmed = true;
        }

        // Never kill a stranger. Require at least one attribute to POSITIVELY
        // confirm this live process is the one we recorded. When neither the
        // name nor the start time can be compared - the recorded value was null
        // (a CLI that exited before its identity could be read leaves exactly
        // that), or the live process refuses to report it - a recycled PID is
        // indistinguishable from ours, so we must not Process.Kill it. On Linux
        // a recycled PID can be any process on the box, including the host that
        // spawned us; a blind tree-kill there is a silent-death vector.
        if (!nameConfirmed && !startConfirmed)
        {
            _logger.LogDebug(
                "Skipping reap of PID {Pid}: identity unverifiable (recorded name='{Name}', startUtc={Start}); refusing to kill a possibly-recycled PID",
                entry.ProcessId, entry.ProcessName, entry.ProcessStartTimeUtc);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Periodic counterpart to <see cref="ReapOrphans"/>, safe to call on a
    /// timer while the backend is up. Walks the persisted active-jobs file and
    /// reaps the recorded process tree only for entries the backend no longer
    /// tracks as a live run — the run finished or its
    /// <see cref="MonitorProcessAsync"/> died without
    /// <see cref="RemoveActiveJob"/> firing, yet the CLI process (codex / node)
    /// is still alive and holding job-folder handles. This is the
    /// accumulation the bug observed: a backend left up for days collects
    /// orphan codex processes from earlier runs, and their open handles wedge
    /// the next lane move with "file in use by another process".
    ///
    /// <para>Safety: an entry whose run is genuinely in flight (a live,
    /// non-exited <see cref="ProcInfo"/> in <see cref="_processes"/>) is kept
    /// untouched, so the timer can never kill an active run. The same
    /// <see cref="MatchesRecordedIdentity"/> PID-recycling guard the startup
    /// reaper uses protects against killing an unrelated process that inherited
    /// a recycled PID. Stale entries whose process is already gone are simply
    /// pruned from the file.</para>
    /// </summary>
    public void ReapStaleOrphans()
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            if (list.Count == 0) return;

            var survivors = new List<ActiveJob>();
            var reaped = 0;
            foreach (var entry in list)
            {
                // Keep entries whose run is still genuinely in flight. The
                // startup spawn path sets _processes BEFORE writing the file,
                // so a live run always has a tracked ProcInfo here.
                if (_processes.TryGetValue(entry.TaskKey, out var liveInfo)
                    && !SafeHasExited(liveInfo.Process))
                {
                    survivors.Add(entry);
                    continue;
                }

                Process? proc = null;
                try { proc = Process.GetProcessById(entry.ProcessId); }
                catch (ArgumentException) { proc = null; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "stale-orphan GetProcessById failed for PID {Pid} ({Cli})", entry.ProcessId, CliType);
                    survivors.Add(entry); // can't decide safely; keep for next sweep
                    continue;
                }

                if (proc == null) continue; // process gone: drop the stale entry

                try
                {
                    if (proc.HasExited) continue; // gone: drop
                    if (!MatchesRecordedIdentity(proc, entry))
                    {
                        // PID recycled by a stranger; don't kill, but the run is
                        // no longer ours to track, so let the entry drop.
                        continue;
                    }

                    SafeKillReap(proc, entry);
                    reaped++;
                    _logger.LogWarning(
                        "Reaped stale-orphan {Cli} CLI tree for job {Job} (PID {Pid}): run no longer tracked but process survived and held job handles",
                        CliType, entry.JobId, entry.ProcessId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reap stale-orphan PID {Pid} ({Cli})", entry.ProcessId, CliType);
                    survivors.Add(entry); // retry next sweep
                }
                finally
                {
                    try { proc.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliExecutionServiceBase:1315"); }
                }
            }

            if (survivors.Count != list.Count) WriteActiveJobs(survivors);
            if (reaped > 0)
                _logger.LogInformation("stale-orphan-sweep {Cli} reaped={Reaped} remaining={Remaining}", CliType, reaped, survivors.Count);
        }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    /// <summary>
    /// Reap an orphan CLI process, falling back to a single-process kill
    /// when the whole-tree kill is refused by the OS.
    ///
    /// <para>
    /// Background. <see cref="Process.Kill(bool)"/> with
    /// <c>entireProcessTree: true</c> can throw
    /// <see cref="InvalidOperationException"/> with the message
    /// "Cannot be used to terminate a process tree containing the calling
    /// process." This happens on Windows when the child CLI ended up in
    /// the same Win32 job object as the backend host (most often: the
    /// backend was launched from a developer-tool console whose job
    /// object also captures grandchildren of the child CLI, so the tree
    /// the kernel computes loops back through us). The whole-tree kill
    /// is then refused atomically — no descendants are killed either.
    /// Without a fallback the orphan keeps running and, after the
    /// backend restarts and respawns the same job, two CLI processes
    /// race for the same <c>logs/cli-output.log</c> handle.
    /// </para>
    ///
    /// <para>
    /// The fallback kills only the direct child. Any grandchildren that
    /// existed are left to the operating system to reap when their root
    /// exits (npm-shim launchers terminate cleanly when their parent
    /// stream closes; the worst case is a brief grand-orphan window
    /// that has the same lifetime as the direct kill anyway). Other
    /// failure modes (<see cref="UnauthorizedAccessException"/>,
    /// process already exited) propagate to the outer catch so the
    /// reaper logs them once and moves on.
    /// </para>
    /// </summary>
    private void SafeKillReap(Process proc, ActiveJob entry)
    {
        AgentStudio.Diagnostics.CliKillAudit.Trace(proc, $"SafeKillReap job={entry.JobId} cli={CliType}");
        try
        {
            proc.Kill(entireProcessTree: true);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("calling process", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Whole-tree kill refused for PID {Pid} ({Cli}): backend is inside the tree. Falling back to OS tree-kill (taskkill /T) to reap descendants.",
                entry.ProcessId, CliType);
            // The plain proc.Kill() below kills ONLY the direct child, leaving the
            // real CLI grandchild (e.g. claude) orphaned and holding the job's
            // logs/cli-output.log handle — the next run for that job then fails with
            // "file in use by another process", which trips the auto-failure
            // circuit-breaker and halts the whole runner. taskkill /T has no
            // calling-process restriction and DOES reap the descendants.
            if (OperatingSystem.IsWindows() && TryOsTreeKill(entry.ProcessId)) return;
        }
        proc.Kill();
    }

    private void KillProcessTree(Process process, string jobKey)
    {
        AgentStudio.Diagnostics.CliKillAudit.Trace(process, $"KillProcessTree job={jobKey} cli={CliType}");
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("calling process", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Whole-tree kill refused for {Cli} job {JobId}; falling back to OS tree-kill.",
                CliType, jobKey);
            if (OperatingSystem.IsWindows() && TryOsTreeKill(process.Id)) return;
            process.Kill();
        }
        catch (Exception ex) when (OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                ex,
                "Managed whole-tree kill failed for {Cli} job {JobId}; trying OS tree-kill.",
                CliType, jobKey);
            if (!TryOsTreeKill(process.Id)) throw;
        }
    }

    /// <summary>
    /// Windows fallback when the managed whole-tree kill is refused: ask the OS
    /// to terminate the process and all descendants via <c>taskkill /F /T</c>.
    /// Unlike <see cref="Process.Kill(bool)"/> this has no "calling process in
    /// the tree" restriction, so it reaps the orphaned CLI grandchild that would
    /// otherwise keep <c>logs/cli-output.log</c> locked.
    /// </summary>
    private bool TryOsTreeKill(int pid)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /T /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "taskkill tree-kill for PID {Pid} failed; falling back to single-process kill", pid);
            return false;
        }
    }

    /// <summary>Per-process bookkeeping. Internal so behavior factories in the same assembly can read/write its fields.</summary>
    internal sealed class ProcInfo
    {
        public Process Process { get; }
        public CliExecution Execution { get; set; }
        public string WorkingDirectory { get; }
        public List<CliOutputLine> OutputBuffer { get; } = [];
        public SessionUsage? LastUsage { get; set; }
        public string? OutputLogPath { get; init; }
        public RunLogStore OutputLog { get; init; } = null!;
        public string? SessionName { get; set; }
        /// <summary>For Codex: the UUID extracted from the first <c>session_meta</c> JSON line.</summary>
        public string? CapturedSessionId { get; set; }

        /// <summary>
        /// Latest <see cref="ParsedTurnUsage"/> captured from a CLI "turn
        /// finished" frame (Codex: <c>turn.completed</c>; Claude: <c>result</c>).
        /// Set by the per-CLI <see cref="OnOutputLine"/> hook when the adapter
        /// recognises the frame; consumed by the runner to mirror the usage
        /// onto the agent message bus as a <c>kind:token-usage</c> message so
        /// the workspace timeline and the token aggregation cache see the
        /// coding agent's own per-turn spend.
        /// </summary>
        public ParsedTurnUsage? LastParsedUsage { get; set; }

        /// <summary>Every model-attributed row from the latest provider usage frame.</summary>
        public IReadOnlyList<ParsedTurnUsage>? LastParsedUsages { get; set; }

        /// <summary>UTC timestamp the most recent <see cref="LastParsedUsage"/> frame was observed.</summary>
        public DateTime? LastParsedUsageAt { get; set; }

        /// <summary>For Claude: the latest <c>rate_limit_event</c> frame parsed
        /// from the stream-json output. Null until the first event arrives.</summary>
        public ClaudeRateLimitSnapshot? LastRateLimit { get; set; }

        /// <summary>
        /// Most recent unresolved typed turn-failure detail. A later
        /// <see cref="CliRunEvent.TurnCompleted"/> clears it. Both execution
        /// engines use it for the terminal event before falling back to the
        /// host outcome classifier's coarser process-level diagnosis.
        /// </summary>
        public string? LastTurnFailureReason { get; set; }

        /// <summary>
        /// Resolved platform permission mode the runner handed this run
        /// (<c>CliPermissionModes</c>). Captured at spawn so the read-only
        /// execution-context surface (ASS-1739 / T1a) can report the effective
        /// posture for CLIs that have no init frame of their own. Null when the
        /// runner injected no explicit mode (defer to the CLI's global config).
        /// </summary>
        public string? PermissionMode { get; set; }

        /// <summary>
        /// Resolved context mode for this run (T1b / ASS-1742): <c>clean</c> or
        /// <c>shared</c>. Captured at spawn so <c>DescribeContextSources</c> can
        /// report it on the execution-context panel.
        /// </summary>
        public string? ContextMode { get; set; }

        /// <summary>
        /// The run's isolated clean-context home (T1b), when CLEAN was resolved
        /// and this CLI supports it. The lease is released on ProcInfo eviction,
        /// which refreshes last use without deleting the task-stable directory;
        /// the bounded retention sweep owns deletion. Null for shared runs and
        /// shared-only CLIs.
        /// </summary>
        public CleanContextPreparation? CleanContext { get; init; }

        /// <summary>
        /// Process group holding this run's CLI process and every process it
        /// spawns — including helpers that detach from the PID tree (the
        /// agent's Playwright capture server, a stray <c>node serve.cjs</c>).
        /// Terminated at run-finish so those detached holders die BEFORE the
        /// worktree cleanup, otherwise they wedge <c>git worktree remove</c>
        /// "Device or resource busy" and orphan the worktree (AGT-1791). Null
        /// on unsupported hosts or when the OS refused the assignment (best-effort;
        /// the tree-kill fallback still applies).
        /// </summary>
        internal TaskProcessReaper? ProcessReaper { get; init; }

        /// <summary>
        /// For Claude: the parsed stream-json init frame (model, cwd,
        /// permission mode, MCP servers, ...). Populated by
        /// the Claude behavior's output hook the moment the frame arrives;
        /// consumed by <c>DescribeContextSources</c> so the execution-context
        /// panel shows what the CLI itself reported it loaded. Null for other
        /// CLIs and before the init frame is seen.
        /// </summary>
        public ClaudeInitContext? ClaudeInit { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent <b>real</b> streamed line - lines
        /// that came off the CLI's stdout/stderr, not synthetic taskboard /
        /// orchestrator / watchdog markers we emitted ourselves. Drives
        /// <see cref="Watchdog"/> silence-clock decisions. Initialized to
        /// <see cref="CliExecution.StartedAt"/> on spawn so the watchdog
        /// starts measuring from run start, not from the synthetic Started
        /// line we add immediately afterward.
        /// </summary>
        public DateTime LastStreamedAt { get; set; }

        /// <summary>
        /// Last <see cref="WatchdogState"/> the runner observed for this
        /// process. Used by the runner's per-tick announcer so identical
        /// states do not produce duplicate chat meta lines.
        /// </summary>
        public WatchdogState LastWatchdogState { get; set; } = WatchdogState.Healthy;

        /// <summary>
        /// Set by <see cref="Stop(string, RunStopReason)"/> immediately
        /// before the kill so <see cref="MonitorProcessAsync"/> can
        /// classify the resulting exit as a deliberate stop instead of a
        /// crash. Stays at <see cref="RunStopReason.None"/> for natural
        /// exits - that is the signal the classifier uses to fall back to
        /// the exit-code-based completed/failed mapping.
        /// </summary>
        public RunStopReason StopReason { get; set; } = RunStopReason.None;

        /// <summary>
        /// CAR driver that owns this process. It is null only for a durable run
        /// recovered after a backend restart, where the detached worker owns it.
        /// </summary>
        public CodingAgentRunner.Execution.ICliDriver? CarDriver { get; init; }

        /// <summary>
        /// Durable worker ownership for a CAR run. It remains available after
        /// startup adoption when the original CAR driver and anonymous pipes
        /// no longer exist.
        /// </summary>
        internal DurableLocalCliProcess? DurableWorker { get; init; }

        /// <summary>
        /// Keeps a result that was written while Studio was down visible as an
        /// occupied recovered slot until ProjectRunner has rebooked it and the
        /// delayed terminal callback can run.
        /// </summary>
        internal bool RecoveredTerminalPending { get; set; }

        /// <summary>True only for a worker discovered from the startup ledger.</summary>
        internal bool RecoveredAfterStartup { get; init; }

        /// <summary>0 pending, 1 authority confirmed, 2 rejected.</summary>
        internal int RecoveryDisposition;

        /// <summary>
        /// Number of <see cref="AgentEnvironmentDetector"/> hits observed
        /// in this run's raw output. The base class read loop increments
        /// this per matching line; the threshold check decides whether
        /// to trip the blocker.
        /// </summary>
        public int EnvironmentBlockerHitCount { get; set; }

        /// <summary>
        /// Latch set once the run has been killed for an environment
        /// blocker. Stops subsequent matching lines from re-tripping the
        /// detector or producing duplicate synthetic markers.
        /// </summary>
        public bool EnvironmentBlockerTripped { get; set; }

        /// <summary>
        /// Codex silent-completion capture. Mirrors the trigger shape of the
        /// <see cref="CodexSilentCompletionDetector"/>: the last
        /// <c>command_execution</c> <c>item.completed</c> the run emitted,
        /// with its reported exit code, the command string, a tail of the
        /// aggregated output, and the UTC observation timestamp. The runner's
        /// per-tick silent-completion check reads these to build its
        /// detection inputs without parsing the buffer again.
        /// </summary>
        public int? LastCommandExitCode { get; set; }
        public string? LastCommandLine { get; set; }
        public string? LastCommandOutputTail { get; set; }
        public DateTime? LastCommandObservedAt { get; set; }

        /// <summary>
        /// Latch set once the run has been killed for a Codex silent
        /// completion. Mirrors <see cref="EnvironmentBlockerTripped"/>:
        /// stops the per-tick detector from re-firing while the stop is in
        /// flight and the second-to-last frame ages further.
        /// </summary>
        public bool SilentCompletionTripped { get; set; }

        /// <summary>
        /// Read-loop tasks captured at spawn time. <see cref="MonitorProcessAsync"/>
        /// awaits them (with a short timeout) before writing the synthetic
        /// "CLI exited" line, so bursts of stdout that finish just before
        /// process exit reach <see cref="OutputBuffer"/> in their natural
        /// order rather than after the exit marker.
        /// </summary>
        public Task? StdoutReadTask { get; set; }
        public Task? StderrReadTask { get; set; }

        /// <summary>
        /// Optional side-channel liveness watcher armed by
        /// <see cref="StartSessionLiveness"/> (Claude's per-session JSONL
        /// mtime watcher). Disposed by <see cref="MonitorProcessAsync"/> on
        /// process exit so the FileSystemWatcher handle is released promptly.
        /// Null for CLIs without a stdout-independent activity signal.
        /// </summary>
        public IDisposable? SessionLiveness { get; set; }

        /// <summary>
        /// Running count of streamed lines this run failed to persist to the
        /// on-disk log. Drives the rate-limited persist-failure warning in
        /// <see cref="NotePersistFailure"/>; reset to 0 once a later line
        /// persists successfully so a recovery is logged exactly once.
        /// </summary>
        public long PersistFailureCount { get; set; }

        /// <summary>UTC timestamp of the most recent emitted persist-failure warning.</summary>
        public DateTime LastPersistWarnAtUtc { get; set; }

        /// <summary>Identity guard for competing durable terminal observations.</summary>
        internal int FinalizationStarted;

        public ProcInfo(Process process, CliExecution execution, string workingDirectory)
        {
            Process = process;
            Execution = execution;
            WorkingDirectory = workingDirectory;
        }
    }
}
