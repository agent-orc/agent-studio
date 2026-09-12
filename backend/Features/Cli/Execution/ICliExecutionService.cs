
namespace AgentStudio.Cli;

/// <summary>
/// Common surface every CLI backend exposes (Claude Code, Codex, Gemini).
/// All implementations are wrapped by <see cref="CliRouter"/> so callers
/// never need to know which CLI executes a given job.
/// </summary>
public interface ICliExecutionService
{
    /// <summary>One of <see cref="CliTypes"/>.</summary>
    string CliType { get; }

    string GetCliPath();
    bool IsAvailable();
    (bool Available, string? Version, string Path) TestCliPath(string? path = null);

    Task<(CliExecution? Execution, string? Error)> StartAsync(
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
        string? executionEngine = null,
        CancellationToken ct = default);

    /// <summary>
    /// Whether this adapter can isolate its persistent state for a
    /// <see cref="CliContextModes.Clean"/> run by relocating
    /// the CLI's config home to a stable, task-isolated directory (T1b /
    /// ASS-1742). Claude and Codex can; Gemini exposes no such redirect and is
    /// shared-only.
    /// Defaults to false so a stub / shared-only backend opts out cleanly; the
    /// runner falls back to a shared run when this is false even if clean was
    /// requested. Must agree with
    /// <see cref="CliContextModes.SupportsClean"/>.
    /// </summary>
    bool SupportsCleanContext => false;

    /// <summary>
    /// Acquire a task-isolated config home seeded with only auth + base config
    /// on first use. Later attempts adopt the same marker-validated home so CLI
    /// rollout state remains resumable. The base spawn flow injects the returned
    /// env override into the child; bounded retention owns deletion after the
    /// task is inactive. Returns null when the backend is shared-only or the
    /// home could not be acquired. Default no-op keeps stubs and shared-only
    /// backends compilable.
    /// </summary>
    CleanContextPreparation? PrepareCleanContext(string jobKey, string workingDirectory) => null;

    /// <summary>
    /// The task's still-live clean-context home (the isolated
    /// CLAUDE_CONFIG_DIR / CODEX_HOME all attempts of this task share), or null
    /// when none exists or its task marker is invalid. Attempts and recoveries
    /// of the same task reuse one home so CLI session state stays resumable
    /// across them, including after a backend restart (MKT-8 / WEB-14
    /// "Codex rollout state loss"); the runner consults this before planning a
    /// resume so a clean-context Codex session whose rollout lives in this home
    /// is actually resumed instead of being discarded into full-context
    /// recovery. Default null keeps stubs and shared-only backends compilable.
    /// </summary>
    string? GetPersistentCleanContextHome(string jobKey) => null;

    /// <summary>
    /// Terminate the live process for <paramref name="taskKey"/>. The
    /// <paramref name="reason"/> flows into <see cref="AgentStudio.Shared.RunStatusClassifier"/>
    /// so user pauses, follow-up pause-and-send, and watchdog kills are
    /// reported as <c>status = "stopped"</c> rather than the misleading
    /// <c>status = "failed", exitCode = -1</c> the legacy implementation
    /// produced. Returns false when no process is tracked under that key.
    /// </summary>
    bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop);
    bool SendInput(string jobKey, string input);

    List<CliOutputLine> GetOutput(string jobKey);

    /// <summary>
    /// Called by the runner once the per-run JSONL has been merged into the
    /// job's durable <c>logs/cli-output.log</c>. The CLI backend should drop
    /// its runtime JSONL so that, after the in-memory buffer is evicted, the
    /// disk-fallback path in <see cref="GetOutput"/> doesn't double up lines
    /// already present in the consolidated log. No-op if the backend doesn't
    /// keep a runtime JSONL.
    /// </summary>
    void DiscardPersistedOutput(string jobKey);

    /// <summary>
    /// Release runtime output handles without deleting the persisted fallback.
    /// The runner calls this before task-folder lane transitions so Windows
    /// directory moves are not blocked by a completed run's retained log store.
    /// </summary>
    void ReleaseOutputResources(string jobKey);

    CliExecution? GetExecution(string jobKey);
    SessionUsage? GetLastUsage(string jobKey);

    /// <summary>Captured CLI-native session id for a run (from its init/thread frame), or null. Default null keeps stubs compilable; real backends provide it.</summary>
    string? GetCapturedSessionId(string jobKey) => null;

    /// <summary>Most recent parsed per-turn usage snapshot (+ observed-at + run start), or null. The runner mirrors it onto the agent message bus.</summary>
    (ParsedTurnUsage Usage, DateTime ObservedAt, DateTime StartedAt)? GetLastParsedTurnUsage(string jobKey) => null;

    /// <summary>Whether this CLI emits a session id on every run. When true, a missing captured id is a capture-loss the runner routes to Recovery. A stub that never does stays false.</summary>
    bool EmitsSessionId => false;

    /// <summary>Whether the runner should attempt post-hoc usage reconstruction when a run finished without a usage footer (Claude reads its session JSONL).</summary>
    bool NeedsPostHocUsageReconstruction => false;

    bool IsRunningForProject(string rootPath);

    /// <summary>
    /// Currently-tracked live runs (OS process alive and the run still marked
    /// <c>running</c>) as <c>(jobKey, execution)</c> pairs. Used by the runner's
    /// post-restart slot reconcile (ASS-1753) to re-book runs this CLI still
    /// owns into the in-memory slot registry, whose contents a restart cleared.
    /// The default is empty so test stubs and backends that do not track live
    /// runs stay compilable and contribute nothing to the reconcile.
    /// </summary>
    IReadOnlyList<(string JobKey, CliExecution Execution)> RunningExecutions()
        => Array.Empty<(string, CliExecution)>();

    /// <summary>
    /// Describe the context sources this CLI loaded for the live (or
    /// just-finished, still-tracked) run under <paramref name="jobKey"/> -
    /// memory / session paths, the instruction-file chain, global config, MCP
    /// servers, plus model / effective permission mode / cwd. This is a
    /// <b>read-only observability</b> surface (ASS-1739 / T1a): producing it
    /// never changes what the CLI loads. For Claude the scalar header and MCP
    /// list come from the stream-json init frame the CLI already emits; for
    /// Codex / Gemini they are derived from the adapter invocation
    /// plus each CLI's documented config-path conventions. The runner calls
    /// this at run finish (while the per-run process info is still alive) and
    /// persists the result onto the run's <see cref="AgentStudio.Shared.SessionEvent"/>.
    /// Returns null when the run is unknown or no context could be derived;
    /// the default no-op keeps test stubs implementing this interface directly
    /// compilable.
    /// </summary>
    AgentStudio.Shared.CliExecutionContext? DescribeContextSources(string jobKey) => null;

    /// <summary>
    /// UTC timestamp of the last <b>real</b> streamed line from this run
    /// (not synthetic taskboard / orchestrator / watchdog markers), or
    /// null if the run is unknown / has finished. The watchdog uses this
    /// to compute silence duration. Should equal
    /// <see cref="CliExecution.StartedAt"/> on a brand-new run before
    /// the first frame arrives.
    /// </summary>
    DateTime? GetLastStreamedAt(string jobKey);

    /// <summary>
    /// Reset the silence clock for a live run by stamping its last-streamed
    /// timestamp to now, without any output having actually arrived. The runner
    /// calls this on the tick after an OS suspend/resume is detected: the wall
    /// clock jumped forward by the nap duration, so the run looks silent for the
    /// whole sleep even though the agent never went quiet. Resetting here keeps
    /// the watchdog from killing a healthy run on the resume tick. No-op when
    /// the run is unknown / finished, and a default no-op for backends and test
    /// stubs that don't track a silence clock.
    /// </summary>
    void ResetSilenceClock(string jobKey) { }

    /// <summary>
    /// Read / write the watchdog state previously announced for this run.
    /// Used by the runner's per-tick announcer to suppress same-state
    /// repeats. Defaults to <see cref="AgentStudio.Shared.WatchdogState.Healthy"/>
    /// for unknown runs.
    /// </summary>
    AgentStudio.Shared.WatchdogState GetWatchdogState(string jobKey);
    void SetWatchdogState(string jobKey, AgentStudio.Shared.WatchdogState state);

    void ReattachOnStartup();

    /// <summary>
    /// Returns true when startup recovery can prove that this exact local run
    /// still has either a live durable worker or an atomic terminal result.
    /// Recovery services use this before the project runner has initialized,
    /// so a restart bridge is not mistaken for a dead execution.
    /// </summary>
    bool CanReattach(string jobKey) => false;

    /// <summary>
    /// Periodic orphan sweep, run on a timer <b>while the backend is up</b>
    /// (unlike <see cref="ReattachOnStartup"/>, which only fires once at boot).
    /// Closes the days-long accumulation gap: when the backend stays up for
    /// days, a finished or crashed run can leave its CLI process tree
    /// (codex / node) alive and holding job-folder handles, wedging the next
    /// lane move. This reaps only process trees the backend no longer tracks
    /// as a live run; a genuinely in-flight run is never touched. Default
    /// no-op so implementations without their own active-process tracking
    /// (and test stubs) opt out cleanly.
    /// </summary>
    void ReapStaleOrphans() { }

    /// <summary>Returns the set of models the user can select for this CLI.</summary>
    Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>
    /// Returns true if <paramref name="sessionName"/> looks like a session
    /// identifier this CLI can resume. Cross-CLI session names (e.g. a slug
    /// fed to Claude's <c>-r</c>) used to make the new CLI hang silently;
    /// callers should drop the recorded name and start fresh when this returns
    /// false.
    /// </summary>
    bool IsCompatibleSessionName(string? sessionName);

    event Action<string, CliOutputLine>? OnOutput;
    event Action<string, CliExecution>? OnStarted;
    event Action<string, CliExecution>? OnFinished;

    /// <summary>
    /// Typed lifecycle events (ADR-0013). Subclasses with an adapter
    /// raise these alongside <see cref="OnOutput"/>; subclasses without
    /// one only fire <see cref="CliRunEvent.RunStarted"/> and
    /// <see cref="CliRunEvent.ProcessExited"/> (both come from the
    /// base spawn / monitor flow). The runner uses these to drive the
    /// phase-aware watchdog.
    /// </summary>
    event Action<string, CliRunEvent>? OnRunEvent;
}
