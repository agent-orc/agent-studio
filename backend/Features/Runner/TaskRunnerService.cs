using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>
/// DI-singleton background service that owns one <see cref="ProjectRunner"/>
/// per watched workspace, ticks them on a 5 s loop for auto-pickup, and
/// fans out the public start / stop / continue / status API used by the
/// HTTP endpoints. The per-project lifecycle and the per-run decision tree
/// live in <see cref="AgentStudio.Runner"/>; this class is
/// intentionally kept to routing + cross-project orchestration so the three
/// concerns can be read independently.
/// </summary>
public class TaskRunnerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskRunnerService> _logger;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly TaskSessionLog _sessions;
    private readonly CliRouter _router;
    private readonly ContextUsageParser _contextUsageParser;
    private readonly SummaryGenerationService _summaryService;
    private readonly RuntimePromptService _prompts;
    private readonly TaskTransitionService _transitions;
    private readonly ProjectSettingsService _projectSettings;
    private readonly QuotaService _quotaService;
    private readonly CliQuotaCapsService _quotaCaps;
    private readonly CliQuotaWaitPolicyService? _quotaWaitPolicy;
    private readonly ProviderLimitRegistry _providerLimits;
    private readonly CliQuotaFallbackService? _quotaFallback;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly OrchestratorRunner _orchestratorRunner;
    private readonly OrchestratorSessionStore _orchestratorSessions;
    private readonly GlobalOrchestratorBootstrap _globalOrchestrator;
    private readonly PickupFailureLog _pickupFailures;
    private readonly CrossSlugInfraCircuitBreaker _infraBreaker;
    // Forwarded to each ProjectRunner. DI injects the registered singleton even
    // into this optional slot; null only when a test fixture builds the service
    // directly, in which case ProjectRunner builds its own workspace-less fallback.
    private readonly HumanReviewEscalation? _humanReviewEscalation;
    private readonly AgentMessageBusBridge? _bus;
    private readonly AgentStudio.TaskAccess.ITaskAccess _taskAccess;
    private readonly PickupLockFile? _pickupLock;
    private readonly IntegrationLeaseService? _integrationLeases;
    private readonly TimelineLog? _timeline;
    private readonly AgentStudio.Pipeline.PipelineExecutionLog? _pipelineLog;
    private readonly AgentStudio.Pipeline.ModelQualificationService? _modelQualification;
    private readonly AgentStudio.Pipeline.IntegrationPushQueue? _integrationPushQueue;
    private readonly AgentStudio.Pipeline.IConceptWorkbenchPublisher? _conceptWorkbenchPublisher;
    private readonly PromptEnrichmentService? _promptEnrichment;
    private readonly DossierMaintenanceService? _dossierMaintenance;
    private readonly VisualQaService? _visualQa;
    // Forwarded to each ProjectRunner. DI injects the registered singleton; a
    // project can disable the default-on step through its pipeline convention.
    // Null only when a test fixture builds the service directly.
    private readonly AgentStudio.Runner.PostAbortReviewStepService? _postAbortReview;
    // Forwarded to each ProjectRunner for post-hoc Claude token reconstruction
    // from session transcripts. DI injects the registered singleton; null only
    // when a test fixture builds the service directly.
    private readonly AgentStudio.Cli.ClaudeSessionInspector? _sessionInspector;
    // ASS-1729: holds a Windows system power-request while >=1 run is active so
    // the host does not sleep mid-run. Optional: DI injects the singleton; null
    // when a test fixture builds the service directly (keep-awake then off).
    private readonly SystemKeepAwake? _keepAwake;
    // Optional: null only when a test fixture builds the service directly.
    // Consulted at boot so a RootPath set through the registry (onboarding
    // dialog / project settings) takes effect without anyone having to hand-
    // edit the gitignored appsettings.Local.json WatchPaths entry (ASS -
    // "Agent Studio" mode-toggle 404, 2026-07-05).
    private readonly AgentStudio.Registry.ProjectRegistry? _projectRegistry;
    // AGT-1812: two-tier orchestrator resolver (project -> workspace default),
    // handed to each ProjectRunner so its model-override reads pick up a
    // workspace default. Optional: null when a test fixture builds the service
    // directly, in which case runners fall back to the project-only value.
    private readonly AgentStudio.Registry.OrchestratorDefaultsProvider? _orchestratorDefaults;
    // AGT-2003: the server-authoritative run-lease authority + this backend's own
    // runner identity, used only to project the active lease owner onto a task's
    // read-time DTO (ResolveRunnerBadge). Both are DI singletons; null only when a
    // test fixture builds the service directly, in which case no runner badge is
    // surfaced (the card falls back to the plain local-run presentation).
    private readonly RunLeaseService? _runLeases;
    private readonly RunnerIdentity? _runnerIdentity;
    private readonly ILoadThrottleGate? _loadThrottle;
    private readonly AgentStudio.Clients.ClientIdentityStore? _clients;
    private readonly StartupExecutionAdmission? _executionAdmission;
    private readonly ModelMigrationService? _modelMigrations;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    /// <summary>
    /// Process-wide pickup role (ADR-0044). Resolved once at construction
    /// from <c>Runner:Role</c> with a fallback to <c>Environment:IsDev</c>.
    /// Surfaced so endpoints can echo it back in the runner status.
    /// </summary>
    public RunnerRole Role { get; }

    /// <summary>
    /// Logical name of this backend instance for cross-process lock attribution.
    /// Resolved from <c>Runner:BackendName</c> with a fallback derived from
    /// <c>Environment:IsDev</c> ("dev" vs "stable") so the two checkouts under
    /// <c>agent-taskboard-devspace/</c> produce distinct lock owners even when
    /// the operator forgets to set the explicit key.
    /// </summary>
    public string BackendName { get; }

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskMutationService mutations,
        TaskSessionLog sessions,
        CliRouter router,
        ContextUsageParser contextUsageParser,
        SummaryGenerationService summaryService,
        RuntimePromptService prompts,
        TaskTransitionService transitions,
        ProjectSettingsService projectSettings,
        QuotaService quotaService,
        CliQuotaCapsService quotaCaps,
        OrchestratorChatLog chatLog,
        OrchestratorLog orchestratorLog,
        OrchestratorRunner orchestratorRunner,
        OrchestratorSessionStore orchestratorSessions,
        GlobalOrchestratorBootstrap globalOrchestrator,
        GitService git,
        PickupFailureLog pickupFailures,
        CrossSlugInfraCircuitBreaker infraBreaker,
        AgentStudio.TaskAccess.ITaskAccess taskAccess,
        AgentMessageBusBridge? bus = null,
        PickupLockFile? pickupLock = null,
        IntegrationLeaseService? integrationLeases = null,
        TimelineLog? timeline = null,
        AgentStudio.Pipeline.PipelineExecutionLog? pipelineLog = null,
        HumanReviewEscalation? humanReviewEscalation = null,
        AgentStudio.Runner.PostAbortReviewStepService? postAbortReview = null,
        AgentStudio.Cli.ClaudeSessionInspector? sessionInspector = null,
        SystemKeepAwake? keepAwake = null,
        AgentStudio.Registry.ProjectRegistry? projectRegistry = null,
        AgentStudio.Registry.OrchestratorDefaultsProvider? orchestratorDefaults = null,
        RunLeaseService? runLeases = null,
        RunnerIdentity? runnerIdentity = null,
        CliQuotaFallbackService? quotaFallback = null,
        ILoadThrottleGate? loadThrottle = null,
        AgentStudio.Pipeline.ModelQualificationService? modelQualification = null,
        AgentStudio.Pipeline.IntegrationPushQueue? integrationPushQueue = null,
        AgentStudio.Clients.ClientIdentityStore? clients = null,
        CliQuotaWaitPolicyService? quotaWaitPolicy = null,
        AgentStudio.Pipeline.IConceptWorkbenchPublisher? conceptWorkbenchPublisher = null,
        PromptEnrichmentService? promptEnrichment = null,
        DossierMaintenanceService? dossierMaintenance = null,
        VisualQaService? visualQa = null,
        StartupExecutionAdmission? executionAdmission = null,
        ProviderLimitRegistry? providerLimits = null,
        ModelMigrationService? modelMigrations = null)
    {
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _sessions = sessions;
        _router = router;
        _contextUsageParser = contextUsageParser;
        _summaryService = summaryService;
        _prompts = prompts;
        _transitions = transitions;
        _projectSettings = projectSettings;
        _quotaService = quotaService;
        _quotaCaps = quotaCaps;
        _quotaFallback = quotaFallback;
        _loadThrottle = loadThrottle;
        _chatLog = chatLog;
        _orchestratorLog = orchestratorLog;
        _orchestratorRunner = orchestratorRunner;
        _orchestratorSessions = orchestratorSessions;
        _globalOrchestrator = globalOrchestrator;
        _git = git;
        _pickupFailures = pickupFailures;
        _infraBreaker = infraBreaker;
        _humanReviewEscalation = humanReviewEscalation;
        _taskAccess = taskAccess;
        _bus = bus;
        _pickupLock = pickupLock;
        _integrationLeases = integrationLeases;
        _timeline = timeline;
        _pipelineLog = pipelineLog;
        _modelQualification = modelQualification;
        _integrationPushQueue = integrationPushQueue;
        _conceptWorkbenchPublisher = conceptWorkbenchPublisher;
        _promptEnrichment = promptEnrichment;
        _dossierMaintenance = dossierMaintenance;
        _visualQa = visualQa;
        _postAbortReview = postAbortReview;
        _sessionInspector = sessionInspector;
        _keepAwake = keepAwake;
        _projectRegistry = projectRegistry;
        _orchestratorDefaults = orchestratorDefaults;
        _runLeases = runLeases;
        _runnerIdentity = runnerIdentity;
        _clients = clients;
        _quotaWaitPolicy = quotaWaitPolicy;
        _providerLimits = providerLimits ?? new ProviderLimitRegistry();
        _executionAdmission = executionAdmission;
        _modelMigrations = modelMigrations;

        Role = RunnerRoles.ResolveFromConfig(_config);
        BackendName = ResolveBackendName(_config);
        _logger.LogInformation(
            "TaskRunnerService booting with role={Role} backend={Backend} (Runner:Role={Configured}, Environment:IsDev={IsDev})",
            RunnerRoles.Format(Role),
            BackendName,
            _config["Runner:Role"] ?? "<unset>",
            _config.GetValue<bool>("Environment:IsDev"));
        if (Role == RunnerRole.TestSubject)
        {
            _logger.LogWarning(
                "[runner-role] Auto-pickup is structurally DISABLED for this backend (role=test-subject). " +
                "Explicit /api/tasks/{{id}}/start calls still execute (Playwright fixtures, manual debugging). " +
                "See ADR-0044 and AGENTS.md 'Dev backend lifecycle'.");
        }
    }

    /// <summary>
    /// Picks the backend identity stamped onto pickup-lock files. Explicit
    /// <c>Runner:BackendName</c> wins; when unset, dev/stable is inferred from
    /// <c>Environment:IsDev</c> so the two checkouts produce distinct owners.
    /// </summary>
    private static string ResolveBackendName(IConfiguration cfg)
    {
        var explicitName = cfg["Runner:BackendName"];
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();
        var isDev = cfg.GetValue<bool>("Environment:IsDev");
        return isDev ? "dev" : "stable";
    }

    /// <summary>
    /// Stamps a fresh <see cref="PickupLockOwner"/> for the given project,
    /// carrying pid, hostname, role, and backend name so a foreign lock writer
    /// can be identified later. The owner is per-project but the rest of the
    /// fields are process-wide.
    /// </summary>
    private PickupLockOwner BuildPickupLockOwner(string projectName) => new()
    {
        Pid = System.Environment.ProcessId,
        Hostname = System.Environment.MachineName,
        Role = RunnerRoles.Format(Role),
        BackendName = BackendName,
        BackendPort = ResolveBackendPort(_config),
        ProjectName = projectName
    };

    private static int ResolveBackendPort(IConfiguration cfg)
    {
        var raw = cfg["Urls"] ?? cfg["ASPNETCORE_URLS"] ?? System.Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        foreach (var token in raw.Split([';', ' '], System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var p)) return p;
            var colon = token.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(token[(colon + 1)..].TrimEnd('/'), out var p2)) return p2;
        }
        return 0;
    }

    private readonly GitService _git;

    public SummaryGenerationService SummaryService => _summaryService;

    public CliRouter Router => _router;

    /// <summary>
    /// The active <see cref="StuckLoopBudget"/> read from configuration at
    /// startup. Surfaced so endpoints can label the auto-loop snapshot
    /// they return with the actual ceilings the runner is enforcing.
    /// </summary>
    public StuckLoopBudget StuckLoopBudget => _stuckLoopBudget;
    private StuckLoopBudget _stuckLoopBudget = StuckLoopBudget.Default;

    /// <summary>
    /// AGT-2003 — project the runner holding this task's active run lease onto a
    /// card-renderable <see cref="AgentStudio.Shared.TaskRunnerInfo"/>, or null when
    /// no lease is held (the common local-run case: the in-process runner uses the
    /// disk pickup-lock, not the run lease). O(1) in-memory peek, so it is safe to
    /// call once per job in the read overlay; the caller gates it on the Progress
    /// lane. <see cref="AgentStudio.Shared.TaskRunnerInfo.IsRemote"/> compares the
    /// lease owner against this backend's own runner id, so a lease taken by the
    /// local backend (should one ever acquire it) still reads as a local run.
    /// </summary>
    public AgentStudio.Shared.TaskRunnerInfo? ResolveRunnerBadge(string taskKey)
    {
        if (_runLeases == null || string.IsNullOrWhiteSpace(taskKey)) return null;
        var peek = _runLeases.Peek(taskKey);
        return ProjectRunnerBadge(peek.Lease, _runnerIdentity?.RunnerId);
    }

    /// <summary>
    /// Pure projection of a peeked run-lease record + this backend's own runner id
    /// into a card-renderable <see cref="AgentStudio.Shared.TaskRunnerInfo"/>. Null
    /// when no lease is held. <c>IsRemote</c> is true when the lease owner differs
    /// from <paramref name="localRunnerId"/> (a remote host executes it); a blank
    /// local id is treated as "cannot prove local" and reads as remote so a real
    /// remote lease is never hidden. Extracted from <see cref="ResolveRunnerBadge"/>
    /// so the lokal-vs-remote decision is unit-testable without the runner's DI graph.
    /// </summary>
    public static AgentStudio.Shared.TaskRunnerInfo? ProjectRunnerBadge(
        AgentStudio.Shared.RunLeaseInfoDto? lease, string? localRunnerId)
    {
        if (lease is null) return null;
        var isRemote = string.IsNullOrWhiteSpace(localRunnerId)
            || !string.Equals(lease.RunnerId, localRunnerId, StringComparison.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(lease.RunnerName) ? lease.RunnerId : lease.RunnerName;
        return new AgentStudio.Shared.TaskRunnerInfo
        {
            RunnerId = lease.RunnerId,
            RunnerName = name,
            Hostname = lease.Hostname,
            BackendName = lease.BackendName,
            IsRemote = isRemote,
            LeaseId = lease.LeaseId,
            FencingToken = lease.FencingToken,
            AttemptId = lease.AttemptId,
            AuthorityEpoch = lease.AuthorityEpoch,
            AcquiredAt = lease.AcquiredAt
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Start);

        // Initialize runners for each watch path
        var entries = _scanner.GetWatchPaths();
        foreach (var rawEntry in entries)
        {
            // The registry (set via the onboarding dialog or project settings)
            // is the source of truth once a record exists (ADR-0042); static
            // WatchPaths fields are bootstrap-era fallbacks. RepositoryPath is
            // independent of task storage and also supplies the working directory
            // when no narrower RootPath is configured.
            var registryProject = _projectRegistry?.FindByStorageLocation(rawEntry.Path);
            var entry = ResolveRunnerEntry(rawEntry, registryProject);

            if (string.IsNullOrEmpty(entry.RootPath))
            {
                _logger.LogWarning("WatchPath '{Name}' has no RootPath configured, skipping runner", entry.Name);
                continue;
            }

            if (!Directory.Exists(entry.RootPath))
            {
                _logger.LogWarning("RootPath '{RootPath}' for '{Name}' does not exist, skipping runner", entry.RootPath, entry.Name);
                continue;
            }

            var runner = new ProjectRunner(
                entry.Name, entry, _logger, _scanner, _states, _sessions, _router, _summaryService,
                _prompts, _transitions, _chatLog, _mutations, _orchestratorLog, _orchestratorRunner,
                _orchestratorSessions, _projectSettings, _quotaService, _quotaCaps, _git,
                _pickupFailures, _infraBreaker, _taskAccess, _bus,
                role: Role,
                pickupLock: _pickupLock,
                pickupLockOwner: BuildPickupLockOwner(entry.Name),
                integrationLeases: _integrationLeases,
                timeline: _timeline,
                pipelineLog: _pipelineLog,
                humanReviewEscalation: _humanReviewEscalation,
                postAbortReview: _postAbortReview,
                sessionInspector: _sessionInspector,
                orchestratorDefaults: _orchestratorDefaults,
                quotaFallback: _quotaFallback,
                quotaWaitPolicy: _quotaWaitPolicy,
                loadThrottle: _loadThrottle,
                modelQualification: _modelQualification,
                integrationPushQueue: _integrationPushQueue,
                conceptWorkbenchPublisher: _conceptWorkbenchPublisher,
                promptEnrichment: _promptEnrichment,
                dossierMaintenance: _dossierMaintenance,
                visualQa: _visualQa,
                providerLimits: _providerLimits,
                modelMigrations: _modelMigrations);
            runner.ConfigureWatchdog(LoadWatchdogConfig(_config), PhaseBudgetTable.FromConfig(_config));
            runner.ConfigureCircuitBreaker(RunnerCircuitBreakerOptions.FromConfig(_config));
            _stuckLoopBudget = LoadStuckLoopBudget(_config);
            runner.ConfigureStuckLoopBudget(_stuckLoopBudget);
            runner.OnStatusChanged += status =>
            {
                // A throwing subscriber here (typically the SignalR fan-out in
                // Program.cs raising synchronously when the hub system is in
                // a transitional state) used to bubble back up through
                // ProjectRunner.NotifyStatus into the pickup tick, escape
                // ExecuteAsync, and stop the host (BackgroundServiceExceptionBehavior
                // defaults to StopHost). Catch and log instead.
                try { OnRunnerStatusChanged?.Invoke(entry.Name, status); }
                catch (Exception ex) { _logger.LogWarning(ex, "OnRunnerStatusChanged subscriber threw for {Project}", entry.Name); }
            };
            // Persist every mode change so the auto-pickup toggle survives
            // backend restarts. Includes implicit transitions like "auto-single
            // → manual" after a job completes. The source is threaded so a
            // system-driven flip (update-quiesce, circuit-breaker) updates the
            // live RunnerMode mirror without clobbering the operator's durable
            // DesiredRunnerMode intent (ASS-1753).
            runner.OnModePersist += (mode, source) => _projectSettings.SetRunnerMode(entry.Name, mode, source);
            _runners[entry.Name] = runner;

            // Restore last saved mode (if any) after wiring the persist hook so
            // the restore itself is idempotent and doesn't double-write. Prefer
            // the operator's durable DesiredRunnerMode over the live RunnerMode
            // mirror so a restart that happened while a transient system-manual
            // was in effect (e.g. mid-update) still comes back up in the mode the
            // operator actually asked for (ASS-1753). Legacy records that predate
            // DesiredRunnerMode fall back to RunnerMode.
            var savedSettings = _projectSettings.Get(entry.Name);
            var savedMode = string.IsNullOrWhiteSpace(savedSettings.DesiredRunnerMode)
                ? savedSettings.RunnerMode
                : savedSettings.DesiredRunnerMode;
            if (!string.IsNullOrWhiteSpace(savedMode) && savedMode != "manual")
            {
                runner.RestoreMode(savedMode!);
                _logger.LogInformation("Restored runner mode '{Mode}' for project '{Name}'", savedMode, entry.Name);
            }
            _logger.LogInformation("Initialized runner for project '{Name}' (Root: {RootPath})", entry.Name, entry.RootPath);
        }

        // Check CLI availability (default backend = Claude)
        if (!_router.Get(CliTypes.Claude).IsAvailable())
        {
            _logger.LogWarning("Claude CLI not available - runners will be in manual/board-only mode");
        }

        // Boot the orchestrator's long-lived Claude session per project so
        // it has warm context (project README, recent activity, lane
        // counts) ready for auto-mode decisions. Cheap when a session is
        // already on disk - we only re-boot if the persisted file is
        // missing. Fire-and-forget per project so a slow boot doesn't
        // block app startup; a project whose boot fails is logged and
        // the orchestrator falls back to one-shot calls on first use.
        foreach (var runner in _runners.Values)
        {
            var snapshot = runner;
            _ = Task.Run(async () =>
            {
                try { await snapshot.BootOrchestratorSessionAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Orchestrator boot failed for {Project}", snapshot.ProjectName); }
            }, stoppingToken);
        }

        // Boot the global orchestrator. Sits above the per-project ones; one
        // session for cross-project decisions, persisted under TaskRepository
        // so it survives restarts. Fire-and-forget for the same reason as
        // the per-project boots: a slow boot must not block app startup.
        _ = Task.Run(async () =>
        {
            try { await _globalOrchestrator.BootAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Global orchestrator boot failed"); }
        }, stoppingToken);

        // ASS-1729: OS suspend/resume detector. The wall clock keeps advancing
        // while the host sleeps but this stopwatch (QPC) freezes, so comparing
        // their deltas across a tick reveals how long we were suspended. The
        // first Observe() only primes the baseline.
        var monoClock = System.Diagnostics.Stopwatch.StartNew();
        var sleepDetector = new SleepDetector();
        sleepDetector.Observe(DateTime.UtcNow, monoClock.Elapsed);

        // Run the loop - poll every 5 seconds for auto-mode runners.
        // Per-tick try/catch is the load-bearing safety net: an unhandled
        // exception escaping ExecuteAsync stops the host
        // (BackgroundServiceExceptionBehavior defaults to StopHost). One bad
        // tick must not take down the API for every other project.
        while (!stoppingToken.IsCancellationRequested)
        {
            // ASS-1729: before any watchdog evaluation this iteration, check
            // whether the host just woke from sleep. If so, reset the silence
            // clocks of every active run across all projects - the wall-clock
            // jump is sleep, not agent silence, and must not trigger a kill.
            try
            {
                var slept = sleepDetector.Observe(DateTime.UtcNow, monoClock.Elapsed);
                if (slept is { } sleptSeconds)
                {
                    var resetRuns = 0;
                    foreach (var runner in _runners.Values)
                    {
                        try { resetRuns += runner.AbsorbSleep(sleptSeconds); }
                        catch (Exception ex) { _logger.LogWarning(ex, "AbsorbSleep failed for {Project}", runner.ProjectName); }
                    }
                    _logger.LogInformation(
                        "[power] system slept ~{Minutes:F0} min; reset silence clocks for {Runs} run(s)",
                        sleptSeconds / 60.0, resetRuns);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Sleep detection tick failed; continuing"); }

            foreach (var runner in _runners.Values)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    // Quota cap watchdog: if the active job's CLI has gone past
                    // the configured cap since it started (e.g. usage rolled
                    // over after a probe refresh), terminate it. We do this
                    // before the pickup tick so the slot frees up immediately.
                    var capStop = runner.EnforceQuotaCapsOnActiveJob(RunStopReason.QuotaCapExceeded);
                    if (capStop.Blocked)
                    {
                        _logger.LogInformation(
                            "[taskboard] cap watchdog stopped active job for {Project}: {Reason}",
                            runner.ProjectName, capStop.DescribeReason());
                    }

                    await runner.TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pickup tick failed for project {Project}; continuing", runner.ProjectName);
                }
            }

            // ASS-1729: reconcile the keep-awake power request with the total
            // number of in-flight runs across all projects. Idempotent: holds
            // the request while >=1 run is active, releases it at 0. Done after
            // the ticks so newly-started/finished runs this iteration count.
            try
            {
                var activeRuns = 0;
                foreach (var runner in _runners.Values) activeRuns += runner.ActiveRunCount;
                _keepAwake?.Update(activeRuns);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Keep-awake refresh failed; continuing"); }

            try { await Task.Delay(5000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Loop exited (shutdown): drop any held keep-awake request so the host
        // is not pinned awake after the backend stops ticking.
        try { _keepAwake?.Update(0); }
        catch (Exception ex) { _logger.LogWarning(ex, "Keep-awake release on shutdown failed"); }
    }

    public RunnerStatus GetStatus()
    {
        var projects = new Dictionary<string, ProjectRunnerStatus>();
        foreach (var (name, runner) in _runners)
        {
            projects[name] = runner.GetStatus();
        }
        return new RunnerStatus { Projects = projects };
    }

    public bool SetMode(string projectName, string mode, string? reason = null)
    {
        var result = RequestModeChange(projectName, mode, reason);
        return result != null && result.Outcome != ModeChangeOutcome.Invalid;
    }

    /// <summary>
    /// Typed mode-change entry point (ADR-0044). Returns the structured
    /// outcome so <c>PUT /api/runner/{project}/mode</c> can answer with
    /// <c>{applied|deferred, mode, pendingMode, willApplyAfter}</c> instead
    /// of an opaque 200/400. Null means "unknown project" (the endpoint
    /// returns 404 in that case). <see cref="ModeChangeOutcome.Invalid"/>
    /// means the mode string was not one of the four allowed values; the
    /// endpoint converts that to 400.
    /// </summary>
    /// <remarks>
    /// The deferred branch (manual / paused requested while a job is
    /// running) is the load-bearing change from the original
    /// <c>SetMode</c>: previously the call returned 200 unconditionally and
    /// the operator had no way to tell that an active job was still going
    /// to run to completion before the mode flip "took". The deferred slot
    /// surfaces via <see cref="ProjectRunnerStatus.PendingMode"/> and the
    /// API response so the lane pill can render "MANUAL (after current)"
    /// while the runner finishes the job in flight.
    /// </remarks>
    public ModeChangeResult? RequestModeChange(string projectName, string mode, string? reason = null)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Start);
        if (!_runners.TryGetValue(projectName, out var runner)) return null;
        var validModes = new[] { "manual", "auto-single", "auto-continuous", "paused" };
        if (!validModes.Contains(mode))
            return new ModeChangeResult(ModeChangeOutcome.Invalid, runner.GetStatus().Mode, null, null);
        // Cross-slug infra breaker reset: when the operator flips back to
        // an auto mode, treat that as "infra is fixed, run again" and clear
        // the distinct-slug counter across all CLIs for this project. The
        // breaker drives the auto -> manual transition itself, but it never
        // sets an auto-* mode, so this hook reliably distinguishes operator
        // intent from runner-internal mode flips.
        if (mode is "auto-single" or "auto-continuous")
        {
            try { _infraBreaker.OnOperatorResumeAuto(projectName); }
            catch (Exception ex) { _logger.LogWarning(ex, "CrossSlugInfraCircuitBreaker reset failed for {Project}", projectName); }
        }
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "api: PUT /api/runner/{project}/mode" : reason;
        return runner.RequestModeChange(mode, effectiveReason);
    }

    /// <summary>
    /// Continuous-decision read surface (ADR-0027): returns the live
    /// unresolved interruptive sentinel(s) the named project's running job
    /// has emitted, or null when the project is not configured. Empty list
    /// means "no decision is currently pending".
    /// </summary>
    public IReadOnlyList<PendingDecisionEntry>? GetPendingDecisions(string projectName)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return null;
        return runner.GetPendingDecisions();
    }

    public async Task<ContinueJobResponse> StartJobAsync(string jobId, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, string? thinkingLevelOverride = null, CancellationToken ct = default)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Start);
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new TaskOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new TaskOperationException($"No runner configured for project '{info.ProjectName}' - check RootPath in WatchPaths config", 400);

        // Persist CLI type override before validity check so the next iteration picks it up.
        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        // Persist override on the job so subsequent runs reuse it
        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        if (thinkingLevelOverride is not null && thinkingLevelOverride != info.ThinkingLevel)
        {
            _mutations.SetJobThinkingLevel(jobId, thinkingLevelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        // Admission before spawn: a start that the lane, the delivery state, or
        // the configured execution location forbids is queued, not spawned. A
        // manual start carries no prompt, so nothing is persisted - the promotion
        // to the top of 2-ready IS the intent. Evaluated before the local CLI
        // check: a card routed to a remote runner must not be refused because
        // this host lacks the CLI that will never run it.
        var admission = FollowUpAdmissionPolicy.Decide(BuildFollowUpAdmissionFacts(info));
        if (admission.Action == FollowUpAdmissionAction.Queue)
            return QueueFollowUp(info, jobId, watchPath, ContinueModes.Continue, prompt: string.Empty, admission);

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new TaskOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        var startedFrom = info.State;
        var outcome = await runner.StartJobManualAsync(jobId, ct);
        var response = ShapeOutcome(outcome, info, jobId, watchPath, mode: ContinueModes.Continue, prompt: string.Empty);
        return await ConfirmStartedRunAsync(response, info, jobId, watchPath, startedFrom, ct);
    }

    /// <summary>
    /// Resumes a job's CLI session and feeds it a follow-up prompt. Recovery
    /// fallback: when no session is recorded (or the recorded id is incompatible
    /// with the current CLI), <see cref="ProjectRunner.ContinueJobAsync"/>
    /// switches to a fresh-session run with a recovery prompt that instructs
    /// the agent to reconstruct context from the job folder. The user gets the
    /// continuation they asked for instead of a 400 - at the cost of conversation
    /// memory that wasn't already on disk.
    /// </summary>
    public async Task<ContinueJobResponse> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, string? thinkingLevelOverride = null, string? mode = null, CancellationToken ct = default)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Continue);
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new TaskOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new TaskOperationException($"No runner configured for project '{info.ProjectName}'", 400);

        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        if (thinkingLevelOverride is not null && thinkingLevelOverride != info.ThinkingLevel)
        {
            _mutations.SetJobThinkingLevel(jobId, thinkingLevelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var normalizedMode = ContinueModes.Normalize(mode);

        // Admission before spawn (AGT-2747). Evaluated on the card, not on the
        // runner's slot state: a lane that cannot hold a run, a delivery under
        // review, or a remote execution location all mean "queue this", because
        // a process started here would be killed by lane reconciliation within
        // seconds while the caller had already been told "started". It also runs
        // before the local CLI check, so a follow-up destined for a remote runner
        // is never refused because this host lacks the CLI.
        var admission = FollowUpAdmissionPolicy.Decide(BuildFollowUpAdmissionFacts(info));

        // The follow-up joins the task's written record either way; only the
        // question "does a process start here" depends on the admission.
        await RecordUserFollowUpAsync(info, jobId, followupPrompt, normalizedMode, watchPath, ct);

        if (admission.Action == FollowUpAdmissionAction.Queue)
            return QueueFollowUp(info, jobId, watchPath, normalizedMode, followupPrompt, admission);

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new TaskOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        var startedFrom = info.State;
        var outcome = await runner.ContinueJobAsync(jobId, followupPrompt, normalizedMode, ct);
        var response = ShapeOutcome(outcome, info, jobId, watchPath, normalizedMode, followupPrompt);
        return await ConfirmStartedRunAsync(response, info, jobId, watchPath, startedFrom, ct);
    }

    /// <summary>
    /// Writes the user's follow-up into the task's durable record before any
    /// run decision: the extend-mode prompt history, the continuation note on
    /// <c>prompt.md</c> (which is also what a remote runner reads when it later
    /// claims the card), the <c>[user]</c> line the activity log polls, and the
    /// typed bus projection.
    /// </summary>
    private async Task RecordUserFollowUpAsync(
        TaskInfo info, string jobId, string followupPrompt, string mode, string? watchPath, CancellationToken ct)
    {
        // Extend mode: persist the new prompt as its own prompt-N.md so the
        // task description grows blog-style. The agent still receives just
        // the new prompt (the runner handles framing); the historical files
        // are evidence and a renderable timeline for the UI.
        if (mode == ContinueModes.Extend)
        {
            try
            {
                var nextIndex = NextPromptHistoryIndex(info.FolderPath);
                var promptFile = Path.Combine(info.FolderPath, $"prompt-{nextIndex}.md");
                await File.WriteAllTextAsync(promptFile, followupPrompt.TrimEnd() + Environment.NewLine, System.Text.Encoding.UTF8, ct);
                _logger.LogInformation("Extend mode: wrote {File} for job {JobId}", Path.GetFileName(promptFile), jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write extend prompt history for {JobId}", jobId);
            }
        }

        _mutations.AppendContinuationNote(jobId, followupPrompt, watchPath);
        AppendUserPromptToCliLog(info, followupPrompt);

        // Mirror the user follow-up onto the bus. cli-output.log remains the
        // canonical transcript (the activity-log parser reads it); the bus
        // event is the typed projection that downstream messages chain onto
        // via correlationId.
        try { _ = _bus?.EmitUserPromptAsync(info, followupPrompt, mode); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of user prompt failed for {JobId}", jobId); }
    }

    /// <summary>
    /// Translates a <see cref="RunOutcome"/> from <see cref="ProjectRunner"/>
    /// into a <see cref="ContinueJobResponse"/> for the HTTP layer. The
    /// busy-project case is the load-bearing branch: the user's intent gets
    /// saved as a draft on the target job, the job is promoted to the top of
    /// <c>2-ready</c>, the chat receives an orchestrator <c>[queued]</c>
    /// meta line, and the response is shaped as <c>status: "queued"</c>
    /// (the endpoint returns 202). Other rejection reasons surface as
    /// <see cref="TaskOperationException"/>.
    /// </summary>
    private ContinueJobResponse ShapeOutcome(
        RunOutcome outcome,
        TaskInfo info,
        string jobId,
        string? watchPath,
        string mode,
        string prompt)
    {
        if (outcome.Execution != null)
        {
            return new ContinueJobResponse { Status = "started", Execution = outcome.Execution };
        }

        var rej = outcome.Rejection ?? new RunRejection(RunRejectReason.None, "unknown");

        if (rej.Reason == RunRejectReason.ProjectBusy)
        {
            // Save user intent + promote the target job to top of 2-ready,
            // then post the orchestrator meta line into the chat. From the
            // user's seat, the modal disappears and the chat reflects the
            // queued state.
            var savedIntent = _mutations.SavePendingIntent(
                jobId, mode, prompt,
                reason: FollowUpQueueReasons.ProjectBusy,
                activeJobId: rej.BusyJobId,
                watchPath: watchPath);

            var fromState = info.State;
            // A user follow-up queued behind the busy project: the lane change is
            // the operator's, so its cause is the operator cause of the lane pair.
            var position = _states.PromoteToReadyTop(
                jobId, watchPath,
                transitionCause: LaneChangeCauses.ForOperatorMove(fromState, TaskStates.Ready),
                transitionDetail: "follow-up-queued-project-busy");

            try
            {
                var refreshed = _scanner.FindJob(jobId, watchPath) ?? info;
                var summary = $"Saved follow-up for \"{refreshed.Title}\"; project busy with \"{rej.BusyJobTitle ?? rej.BusyJobId ?? "another task"}\". Promoted from {fromState} to 2-ready (position {position}).";
                _chatLog.Append(refreshed, OrchestratorMessageKind.Decision,
                    $"[queued] Saved your follow-up. Project busy with \"{rej.BusyJobTitle ?? rej.BusyJobId ?? "another task"}\"; this task moved from {fromState} to 2-ready (position {position}). Will run on next auto-pickup.");

                _orchestratorLog.Append(refreshed.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Decision,
                    Topic = OrchestratorLogTopics.TaskQueued,
                    JobId = jobId,
                    Summary = summary,
                    Reasoning = $"User sent a {mode} follow-up while the project was running another job. " +
                                $"Saved the prompt as pending-intent.json on the target task and promoted the task to top of 2-ready " +
                                $"so the auto-pickup loop runs it on the next tick. Active job at the time: {rej.BusyJobId}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write [queued] meta for {JobId}", jobId);
            }

            return new ContinueJobResponse
            {
                Status = "queued",
                Queued = new ContinueJobQueuedInfo
                {
                    Reason = FollowUpQueueReasons.ProjectBusy,
                    ActiveJobId = rej.BusyJobId,
                    ActiveJobTitle = rej.BusyJobTitle,
                    Position = position > 0 ? position : 1,
                    PromotedFromState = fromState
                }
            };
        }

        if (rej.Reason == RunRejectReason.TaskNotFound)
            throw new TaskOperationException(rej.Message ?? "Job not found", 404);

        if (rej.Reason == RunRejectReason.QuotaCapExceeded)
            throw new TaskOperationException(rej.Message ?? "Quota cap exceeded", 429);

        // CliUnavailable, None, or anything else: 400 with the message.
        throw new TaskOperationException(rej.Message ?? "Cannot start job", 400);
    }

    /// <summary>
    /// Collects the plain facts <see cref="FollowUpAdmissionPolicy"/> needs:
    /// the card's lane and lifecycle phase, plus the project's resolved
    /// execution location measured against this backend's own runner identity.
    /// </summary>
    private FollowUpAdmissionFacts BuildFollowUpAdmissionFacts(TaskInfo info)
    {
        var settings = _projectSettings.Get(info.ProjectName);
        return new FollowUpAdmissionFacts(
            Lane: info.State,
            Phase: info.Phase,
            ExecutionLocation: ProjectExecutionPolicy.ResolveExecutionLocation(settings),
            LocalRunnerId: _runnerIdentity?.RunnerId,
            LocalRunnerName: _runnerIdentity?.RunnerName);
    }

    /// <summary>
    /// Queues a follow-up the admission refused to start locally: persist the
    /// prompt as <c>pending-intent.json</c>, promote the card to the top of
    /// <c>2-ready</c> so the next local pickup or remote claim consumes it, and
    /// shape the <c>202 {"status":"queued"}</c> answer.
    ///
    /// <para>
    /// A manual start carries no prompt. Nothing is persisted then - saving an
    /// empty intent would leave a badge on the card that no run would ever
    /// consume - and the promotion alone carries the operator's intent.
    /// </para>
    /// </summary>
    private ContinueJobResponse QueueFollowUp(
        TaskInfo info,
        string jobId,
        string? watchPath,
        string mode,
        string prompt,
        FollowUpAdmissionDecision decision)
    {
        var reason = decision.QueueReason ?? FollowUpQueueReasons.LaneNotRunnable;
        var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
        if (hasPrompt)
        {
            _mutations.SavePendingIntent(
                jobId, mode, prompt,
                reason: reason,
                activeJobId: null,
                watchPath: watchPath);
        }

        var fromState = info.State;
        var position = _states.PromoteToReadyTop(
            jobId, watchPath,
            transitionCause: LaneChangeCauses.ForOperatorMove(fromState, TaskStates.Ready),
            transitionDetail: $"follow-up-queued-{reason}");

        var refreshed = _scanner.FindJob(jobId, watchPath) ?? info;
        _logger.LogInformation(
            "follow-up-queued job={JobId} project={Project} reason={Reason} mode={Mode} from={From} position={Position} detail={Detail}",
            jobId, info.ProjectName, reason, mode, fromState, position, decision.Detail);

        try
        {
            _timeline?.Append(
                refreshed.FolderPath,
                TimelineEventKinds.FollowUpQueued,
                TimelineActors.System,
                summary: hasPrompt
                    ? $"Follow-up queued ({reason}); it waits for the next run."
                    : $"Start queued ({reason}); the card waits for the next run.",
                details: new Dictionary<string, string>
                {
                    ["reason"] = reason,
                    ["mode"] = mode,
                    ["fromLane"] = fromState,
                    ["detail"] = decision.Detail,
                });

            var queuedLine = hasPrompt
                ? $"[queued] Saved your follow-up: {decision.Detail}. This task moved from {fromState} to 2-ready (position {position}); the next run consumes it."
                : $"[queued] {decision.Detail}. This task moved from {fromState} to 2-ready (position {position}).";
            _chatLog.Append(refreshed, OrchestratorMessageKind.Decision, queuedLine);

            _orchestratorLog.Append(refreshed.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Decision,
                Topic = OrchestratorLogTopics.TaskQueued,
                JobId = jobId,
                Summary = $"Queued a {mode} follow-up for \"{refreshed.Title}\" ({reason}); promoted from {fromState} to 2-ready (position {position}).",
                Reasoning = $"Follow-up admission refused a local start: {decision.Detail}. " +
                            (hasPrompt
                                ? "The prompt is persisted as pending-intent.json on the task; the next run (local pickup or remote claim) consumes it."
                                : "No prompt accompanied the start, so the promotion alone carries the intent.")
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write [queued] meta for {JobId}", jobId);
        }

        return new ContinueJobResponse
        {
            Status = "queued",
            Queued = new ContinueJobQueuedInfo
            {
                Reason = reason,
                Position = position > 0 ? position : 1,
                PromotedFromState = fromState
            }
        };
    }

    /// <summary>
    /// Honest <c>started</c>: a run that a stop path killed inside the start
    /// window is reported as <c>409</c>, not as a successful start.
    ///
    /// <para>
    /// The detector is the preservation half of this feature: every stop path
    /// that kills a run carrying an unconsumed follow-up writes the follow-up
    /// back to <c>pending-intent.json</c> first
    /// (<c>ProjectRunner.PreserveUnconsumedFollowUp</c>). A <em>fresh</em>
    /// <c>run-stopped:</c> intent on the job we just started IS the proof that
    /// the run did not survive - no guessing from process status, and no false
    /// alarm for a run that legitimately finished in under a second. Freshness
    /// matters because a card can already carry an older, still-unconsumed
    /// intent when the operator continues it by hand.
    /// </para>
    ///
    /// <para>
    /// Only runs that had to change lanes are watched. A continue on a card
    /// already in <c>3-progress</c> performs no lane move, so lane
    /// reconciliation cannot fire against it and the caller keeps the fast path.
    /// </para>
    /// </summary>
    private async Task<ContinueJobResponse> ConfirmStartedRunAsync(
        ContinueJobResponse response,
        TaskInfo info,
        string jobId,
        string? watchPath,
        string startedFromLane,
        CancellationToken ct)
    {
        if (response.Status != "started") return response;
        if (startedFromLane == TaskStates.Progress) return response;

        var window = ResolveStartWindow(_config);
        if (window <= TimeSpan.Zero) return response;

        // A card can already carry an unconsumed intent when the operator
        // continues it by hand; only an intent this window did not start with
        // counts as evidence that the run was killed.
        var intentBefore = info.PendingIntent;

        var deadline = DateTime.UtcNow + window;
        var poll = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try { await Task.Delay(remaining < poll ? remaining : poll, ct); }
            catch (OperationCanceledException) { return response; }

            var preserved = _scanner.FindJob(jobId, watchPath)?.PendingIntent;
            if (!FollowUpAdmissionPolicy.IsStartWindowLoss(intentBefore, preserved)) continue;

            _logger.LogWarning(
                "follow-up-start-lost job={JobId} project={Project} from={From} savedReason={SavedReason}",
                jobId, info.ProjectName, startedFromLane, preserved.SavedReason);
            throw new TaskOperationException(
                $"The run was stopped inside the start window ({preserved.SavedReason}). " +
                "Your follow-up stays saved on the task and the next run consumes it.",
                409);
        }

        return response;
    }

    /// <summary>
    /// How long <see cref="ConfirmStartedRunAsync"/> watches a freshly started
    /// run before answering <c>started</c>. <c>Runner:FollowUpStartWindowMs</c>
    /// overrides it; <c>0</c> disables the check (tests, and operators who
    /// prefer the older optimistic answer).
    /// </summary>
    internal static TimeSpan ResolveStartWindow(IConfiguration cfg)
    {
        var ms = cfg.GetValue("Runner:FollowUpStartWindowMs", 1200);
        return ms <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(ms, 10_000));
    }

    /// <summary>
    /// Loads watchdog thresholds from <c>Watchdog:*</c> in configuration.
    /// Falls back to <see cref="WatchdogConfig.Default"/> when nothing is
    /// set. Per-CLI overrides are not yet read here; we apply one config
    /// per project runner today.
    /// </summary>
    /// <summary>
    /// Loads <see cref="StuckLoopBudget"/> from <c>StuckLoop:*</c> configuration.
    /// Falls back to <see cref="StuckLoopBudget.Default"/> when nothing is set.
    /// Defaults are deliberately generous - the user can tighten via
    /// appsettings if their CLI quota is small.
    /// </summary>
    private static StuckLoopBudget LoadStuckLoopBudget(IConfiguration cfg)
    {
        var section = cfg.GetSection("StuckLoop");
        var d = StuckLoopBudget.Default;
        if (!section.Exists()) return d;
        return new StuckLoopBudget(
            MaxIterations:          section.GetValue("MaxIterations", d.MaxIterations),
            MaxOrchestratorTokens:  section.GetValue<long>("MaxOrchestratorTokens", d.MaxOrchestratorTokens));
    }

    /// <summary>
    /// Snapshot of the auto-loop state for one job (null when no loop is in
    /// flight). The caller passes the project name directly so the lookup
    /// is O(1) - the previous variant accepted only <c>watchPath</c> and
    /// resolved the project via <see cref="TaskScannerService.FindJob"/>,
    /// which performs a full disk rescan per call. With the runtime
    /// overlay applied to every TaskInfo, that turned <c>/api/tasks</c> and
    /// <c>/api/tasks/grouped</c> into O(N^2) disk reads (~7-15 s on a
    /// 150-job board) and froze the polling UI. Locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>.
    /// </summary>
    public StuckLoopState? GetStuckLoopStateForJob(string jobId, string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return null;
        return _runners.TryGetValue(projectName, out var runner)
            ? runner.GetStuckLoopState(jobId)
            : null;
    }

    /// <summary>
    /// In-memory run facts for one Progress-lane task (ASS-1751), resolved O(1)
    /// via the project name exactly like <see cref="GetStuckLoopStateForJob"/>.
    /// Returns default facts (no slot / no backoff / zero failures) when the
    /// project has no live runner - which is also the orphan case after a
    /// backend restart. The endpoint overlay classifies these into a
    /// <see cref="TaskRunActivity"/>.
    /// </summary>
    public RunActivityFacts GetRunActivityForJob(string jobId, string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return default;
        return _runners.TryGetValue(projectName, out var runner)
            ? runner.GetRunActivity(jobId)
            : default;
    }

    /// <summary>Build the canonical execution location from runtime ownership facts.</summary>
    public AgentStudio.Shared.TaskExecutionLocation ResolveExecutionLocation(
        AgentStudio.Shared.TaskInfo job,
        AgentStudio.Shared.CliExecution? execution,
        AgentStudio.Shared.TaskRunActivity? activity)
    {
        var inspection = _runLeases?.Inspect(job.TaskKey) ?? new RunLeaseInspection("none", null);
        var configured = _projectSettings.Get(job.ProjectName).ExecutionRunner;
        var client = inspection.Lease is null ? null : _clients?.Find(inspection.Lease.ClientId ?? inspection.Lease.RunnerId);
        return ProjectExecutionLocation(
            job, execution, activity, inspection, configured, _runnerIdentity,
            client?.LastSeenAt, DateTime.UtcNow);
    }

    internal static AgentStudio.Shared.TaskExecutionLocation ProjectExecutionLocation(
        AgentStudio.Shared.TaskInfo job,
        AgentStudio.Shared.CliExecution? execution,
        AgentStudio.Shared.TaskRunActivity? activity,
        RunLeaseInspection inspection,
        string? configuredRunnerId,
        RunnerIdentity? localIdentity,
        DateTime? clientLastSeenAt,
        DateTime now)
    {
        var lease = inspection.Lease;
        var branch = string.IsNullOrWhiteSpace(job.Provenance?.Branch) ? null : job.Provenance.Branch;
        var baseProjection = new AgentStudio.Shared.TaskExecutionLocation
        {
            ConfiguredRunnerId = string.IsNullOrWhiteSpace(configuredRunnerId) ? null : configuredRunnerId,
            SessionId = string.IsNullOrWhiteSpace(job.SessionName) ? null : job.SessionName,
            Branch = branch,
            WorktreePath = string.IsNullOrWhiteSpace(job.FolderPath) ? null : job.FolderPath,
            LastActivityAt = job.LastActivity,
            LastRejection = job.State == AgentStudio.Shared.TaskStates.Ready
                            && job.RemoteDispatchRejection is { } rejection
                            && rejection.RejectedAtUtc >= job.EnteredLaneAt.ToUniversalTime()
                ? rejection
                : null,
        };

        if (job.State == AgentStudio.Shared.TaskStates.Progress && lease is not null)
        {
            var remote = localIdentity is null
                || !string.Equals(lease.RunnerId, localIdentity.RunnerId, StringComparison.OrdinalIgnoreCase);
            var heartbeat = lease.LastHeartbeatAt;
            var stale = inspection.State != "active" || now - heartbeat > TimeSpan.FromSeconds(75);
            return baseProjection with
            {
                State = remote
                    ? stale ? AgentStudio.Shared.TaskExecutionStates.RemoteDisconnected : AgentStudio.Shared.TaskExecutionStates.RemoteRunning
                    : AgentStudio.Shared.TaskExecutionStates.LocalRunning,
                ExecutionKind = remote ? "remote" : "local",
                RunnerId = lease.RunnerId,
                ClientId = lease.ClientId ?? lease.RunnerId,
                HostDisplayName = string.IsNullOrWhiteSpace(lease.RunnerName) ? lease.Hostname : lease.RunnerName,
                StartedAt = lease.AcquiredAt,
                LastHeartbeat = heartbeat,
                LastActivityAt = Max(heartbeat, clientLastSeenAt, job.LastActivity),
                ProcessId = lease.Pid > 0 ? lease.Pid : null,
                ConnectionState = stale ? "disconnected" : "connected",
                LeaseState = inspection.State,
                TrustReason = stale
                    ? "The last fenced run lease owner is retained, but its heartbeat is stale or the lease expired."
                    : "The task server currently holds a fenced run lease for this runner and has a recent heartbeat.",
            };
        }

        var locallyRunning = job.State == AgentStudio.Shared.TaskStates.Progress
            && (string.Equals(execution?.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(activity?.Kind, AgentStudio.Shared.TaskRunActivityKinds.Active, StringComparison.OrdinalIgnoreCase));
        if (locallyRunning)
        {
            return baseProjection with
            {
                State = AgentStudio.Shared.TaskExecutionStates.LocalRunning,
                ExecutionKind = "local",
                RunnerId = localIdentity?.RunnerId,
                ClientId = localIdentity?.RunnerId,
                HostDisplayName = localIdentity?.RunnerName ?? localIdentity?.Hostname ?? "Local",
                StartedAt = execution?.StartedAt,
                LastActivityAt = Max(job.LastActivity, execution?.StartedAt),
                ProcessId = execution is { ProcessId: > 0 } ? execution.ProcessId : activity?.ProcessId,
                ConnectionState = "connected",
                LeaseState = "local-process",
                TrustReason = "The local CLI execution registry reports a live process for this task.",
            };
        }

        if (job.State == AgentStudio.Shared.TaskStates.Ready && !string.IsNullOrWhiteSpace(configuredRunnerId))
        {
            return baseProjection with
            {
                State = AgentStudio.Shared.TaskExecutionStates.QueuedRemote,
                ExecutionKind = "remote",
                RunnerId = configuredRunnerId,
                ClientId = configuredRunnerId,
                HostDisplayName = configuredRunnerId,
                ConnectionState = "queued",
                LeaseState = "none",
                TrustReason = "No execution is active. Project routing queues this task for the configured remote runner.",
            };
        }

        if (job.State == AgentStudio.Shared.TaskStates.Progress)
        {
            // Neither a live local process nor a fenced run lease currently owns
            // this in-progress task. The usual cause is a task-server restart
            // that dropped the in-memory run-lease record while the run itself
            // kept going. Do NOT stick on a permanent "Recovering" badge: heal
            // from the freshest signal available - the job folder's last-activity
            // stamp, which every runner push / log write advances (see
            // TaskScannerService.GetLastActivityTime). LastActivity is a local
            // file-write stamp, so normalise it to UTC before comparing to now.
            var lastActivityUtc = job.LastActivity == default
                ? (DateTime?)null
                : job.LastActivity.ToUniversalTime();
            var freshest = Max(lastActivityUtc, clientLastSeenAt);
            var recentActivity = freshest.HasValue
                && now - freshest.Value <= TimeSpan.FromMinutes(3);
            var remoteRouted = !string.IsNullOrWhiteSpace(configuredRunnerId)
                && (localIdentity is null
                    || !string.Equals(configuredRunnerId, localIdentity.RunnerId, StringComparison.OrdinalIgnoreCase));

            if (remoteRouted)
            {
                // A remote run recovers by replaying the job folder / runner
                // pushes - that is the normal path, never a fault. Present the
                // configured remote runner as the owner (neutral), connected
                // while activity is fresh and quietly reconnecting once it goes
                // idle; never a "recovering / session-lost" warning.
                return baseProjection with
                {
                    State = AgentStudio.Shared.TaskExecutionStates.RemoteRunning,
                    ExecutionKind = "remote",
                    RunnerId = configuredRunnerId,
                    ClientId = configuredRunnerId,
                    HostDisplayName = configuredRunnerId,
                    LastActivityAt = freshest ?? baseProjection.LastActivityAt,
                    ConnectionState = recentActivity ? "connected" : "reconnecting",
                    LeaseState = "none",
                    TrustReason = recentActivity
                        ? "No run lease is held (e.g. after a task-server restart), but the configured remote runner is still replaying fresh activity from the job folder."
                        : "No run lease is held; waiting for the configured remote runner to resume replaying the job folder.",
                };
            }

            if (recentActivity)
            {
                // A local run whose process registration was lost but which is
                // still producing fresh output. Treat it as running so the badge
                // self-heals instead of accusing a live run of being orphaned.
                return baseProjection with
                {
                    State = AgentStudio.Shared.TaskExecutionStates.LocalRunning,
                    ExecutionKind = "local",
                    RunnerId = localIdentity?.RunnerId,
                    ClientId = localIdentity?.RunnerId,
                    HostDisplayName = localIdentity?.RunnerName ?? localIdentity?.Hostname ?? "Local",
                    LastActivityAt = freshest ?? baseProjection.LastActivityAt,
                    ConnectionState = "connected",
                    LeaseState = "none",
                    TrustReason = "No run lease is held, but this in-progress task is still producing fresh activity.",
                };
            }

            return baseProjection with
            {
                State = AgentStudio.Shared.TaskExecutionStates.Recovering,
                ConnectionState = "recovering",
                TrustReason = "The task is in progress, but no live process or fenced run lease currently owns it and no fresh activity has arrived.",
            };
        }

        if (lease is not null)
        {
            var remote = localIdentity is null
                || !string.Equals(lease.RunnerId, localIdentity.RunnerId, StringComparison.OrdinalIgnoreCase);
            return baseProjection with
            {
                State = remote
                    ? AgentStudio.Shared.TaskExecutionStates.RemoteRunning
                    : AgentStudio.Shared.TaskExecutionStates.LocalRunning,
                ExecutionKind = remote ? "remote" : "local",
                RunnerId = lease.RunnerId,
                ClientId = lease.ClientId ?? lease.RunnerId,
                HostDisplayName = string.IsNullOrWhiteSpace(lease.RunnerName) ? lease.Hostname : lease.RunnerName,
                StartedAt = lease.AcquiredAt,
                LastHeartbeat = lease.LastHeartbeatAt,
                LastActivityAt = Max(lease.LastHeartbeatAt, job.LastActivity),
                ProcessId = lease.Pid > 0 ? lease.Pid : null,
                ConnectionState = "historical",
                LeaseState = inspection.State,
                Historical = true,
                TrustReason = "The task server retained the last fenced run lease owner after execution ended.",
            };
        }

        return baseProjection;
    }

    private static DateTime? Max(params DateTime?[] values)
        => values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Max() is var max && max != default ? max : null;

    public QuotaFallbackStatus? GetQuotaFallbackForJob(string jobId, string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return null;
        return _runners.TryGetValue(projectName, out var runner)
            ? runner.GetQuotaFallback(jobId)
            : null;
    }

    private static WatchdogConfig LoadWatchdogConfig(IConfiguration cfg)
    {
        var section = cfg.GetSection("Watchdog");
        if (!section.Exists()) return WatchdogConfig.Default;
        var d = WatchdogConfig.Default;
        return new WatchdogConfig(
            Enabled:              section.GetValue("Enabled", d.Enabled),
            WarmUpGraceSeconds:   section.GetValue("WarmUpGraceSeconds", d.WarmUpGraceSeconds),
            QuietSeconds:         section.GetValue("QuietSeconds", d.QuietSeconds),
            SuspiciousSeconds:    section.GetValue("SuspiciousSeconds", d.SuspiciousSeconds),
            HungSeconds:          section.GetValue("HungSeconds", d.HungSeconds),
            TickIntervalSeconds:  section.GetValue("TickIntervalSeconds", d.TickIntervalSeconds));
    }

    /// <summary>
    /// Counts existing <c>prompt-N.md</c> files in the job folder and returns
    /// the next free index (1-based). The original task lives in
    /// <c>prompt.md</c>; extensions land as <c>prompt-1.md</c>,
    /// <c>prompt-2.md</c>, ... so the timeline is obvious from a directory
    /// listing alone.
    /// </summary>
    private static int NextPromptHistoryIndex(string jobFolder)
    {
        if (!Directory.Exists(jobFolder)) return 1;
        var max = 0;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "prompt-*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // name = "prompt-3"
            var dash = name.IndexOf('-');
            if (dash < 0 || dash >= name.Length - 1) continue;
            if (int.TryParse(name[(dash + 1)..], out var n) && n > max) max = n;
        }
        return max + 1;
    }

    /// <summary>
    /// Persist the user's follow-up as a <c>[user]</c>-stream line in
    /// <c>logs/cli-output.log</c>. The activity log polls this file, so writing
    /// the line synchronously before the CLI starts means the user sees their
    /// own message in the conversation immediately - no silent gap between
    /// click and the agent's first reply.
    /// </summary>
    private void AppendUserPromptToCliLog(TaskInfo info, string prompt)
    {
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var oneLine = prompt.Replace("\r", " ").Replace("\n", " ").TrimEnd();
            var line = $"[{ts}] [user] {oneLine}";
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record user follow-up in CLI log for {JobId}", info.Id);
        }
    }

    public bool StopJob(string jobId, string? watchPath = null, RunStopReason reason = RunStopReason.UserStop)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var stopped = _router.Get(info.CliType).Stop(info.TaskKey, reason);
        if (stopped)
        {
            // Lifecycle event: stop requested. The matching RunFinished message
            // is emitted by ProjectRunner.OnCliFinishedAsync after the process
            // exits; this records the trigger separately so the timeline shows
            // both the request and the actual termination.
            try { _ = _bus?.EmitRunStopRequestedAsync(info, reason, source: "taskboard"); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of stop-requested failed for {JobId}", jobId); }
        }
        return stopped;
    }

    /// <summary>
    /// True only when a CLI process is currently executing this job.
    /// The "3-progress" folder alone is not enough - a job can sit there
    /// after a stop / crash / restart without a live process.
    /// </summary>
    public bool IsJobLive(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var execution = _router.Get(info.CliType).GetExecution(info.TaskKey);
        return execution?.Status == "running";
    }

    public List<CliOutputLine> GetJobOutput(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];

        var logPath = TaskPaths.CliOutputLog(info.FolderPath);
        var liveOutput = _router.Get(info.CliType).GetOutput(info.TaskKey);

        if (liveOutput.Count > 0)
        {
            // Prepend the accumulated historical log so continuations show the full conversation.
            var historical = CliOutputLogParser.ParseFile(logPath);
            return historical.Count > 0 ? [.. historical, .. liveOutput] : liveOutput;
        }

        return CliOutputLogParser.ParseFile(logPath);
    }

    /// <summary>Looks up the CliExecution for a job from the right CLI backend.</summary>
    public CliExecution? GetExecutionForJob(TaskInfo info)
        => _router.Get(info.CliType).GetExecution(info.TaskKey);

    public Task<(ContextUsageSnapshot? Snapshot, string? Error)> RefreshContextUsageAsync(string jobId, string? watchPath = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return Task.FromResult<(ContextUsageSnapshot?, string?)>((null, "Job not found"));
        // The interactive /context usage refresh was a Copilot-only feature; the
        // Copilot CLI backend has been removed. No remaining CLI exposes an
        // on-demand context-usage probe, so this is always a no-op now.
        return Task.FromResult<(ContextUsageSnapshot?, string?)>(
            (null, $"{CliTypes.Normalize(info.CliType)} CLI does not support /context usage refresh."));
    }

    /// <summary>
    /// Releases the in-memory active-job latch on the matching project's
    /// runner when an external mutation (the move endpoint, the boot-time
    /// stuck-folder sweep, a manual folder rearrangement) takes the active
    /// job out of <c>3-progress</c>. Wired off
    /// <see cref="TaskTransitionService.OnJobMoved"/> in <c>Program.cs</c> so
    /// the clear is atomic with the move from the API caller's perspective:
    /// a successful <c>POST /api/tasks/{id}/move</c> guarantees the next
    /// pickup tick sees <c>active=null</c>.
    /// </summary>
    public bool ClearActiveJobForProject(string projectName, string jobId, string reason)
    {
        if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(jobId)) return false;
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        return runner.ClearActiveJobIfMatches(jobId, reason);
    }

    /// <summary>
    /// Reconciles only the project whose watch tree emitted the change. The
    /// runner inspects its captured active task folder directly, avoiding the
    /// global task-index lookup that used to run synchronously on every watcher
    /// event.
    /// </summary>
    public void ReconcileRunnerForPath(string changedPath)
    {
        foreach (var runner in _runners.Values)
        {
            if (!PathIsUnder(runner.Entry.Path, changedPath)) continue;
            try { runner.ReconcileActiveJobAgainstDisk(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reconcile failed for runner {Project}", runner.ProjectName); }
            return;
        }
    }

    private static bool PathIsUnder(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative != "."
                   && !Path.IsPathRooted(relative)
                   && !relative.Split(
                           [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                           StringSplitOptions.RemoveEmptyEntries)
                       .Any(part => part == "..");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Activates a runner for a project created after host startup. This is
    /// intentionally idempotent so POST retries cannot duplicate pickup loops.
    /// </summary>
    public bool EnsureRunner(WatchPathEntry rawEntry)
    {
        if (_runners.ContainsKey(rawEntry.Name)) return true;

        var registryProject = _projectRegistry?.FindByStorageLocation(rawEntry.Path);
        var entry = ResolveRunnerEntry(rawEntry, registryProject);
        if (string.IsNullOrWhiteSpace(entry.RootPath) || !Directory.Exists(entry.RootPath))
        {
            _logger.LogInformation(
                "runner-activation-skipped project={Project} reason=root-path-unavailable root={RootPath}",
                entry.Name, entry.RootPath);
            return false;
        }

        var runner = new ProjectRunner(
            entry.Name, entry, _logger, _scanner, _states, _sessions, _router, _summaryService,
            _prompts, _transitions, _chatLog, _mutations, _orchestratorLog, _orchestratorRunner,
            _orchestratorSessions, _projectSettings, _quotaService, _quotaCaps, _git,
            _pickupFailures, _infraBreaker, _taskAccess, _bus,
            role: Role,
            pickupLock: _pickupLock,
            pickupLockOwner: BuildPickupLockOwner(entry.Name),
            integrationLeases: _integrationLeases,
            timeline: _timeline,
            pipelineLog: _pipelineLog,
            humanReviewEscalation: _humanReviewEscalation,
            postAbortReview: _postAbortReview,
            sessionInspector: _sessionInspector,
            orchestratorDefaults: _orchestratorDefaults,
            quotaFallback: _quotaFallback,
            quotaWaitPolicy: _quotaWaitPolicy,
            loadThrottle: _loadThrottle,
            conceptWorkbenchPublisher: _conceptWorkbenchPublisher,
            promptEnrichment: _promptEnrichment,
            dossierMaintenance: _dossierMaintenance,
            visualQa: _visualQa,
            providerLimits: _providerLimits,
            modelMigrations: _modelMigrations);
        runner.ConfigureWatchdog(LoadWatchdogConfig(_config), PhaseBudgetTable.FromConfig(_config));
        runner.ConfigureCircuitBreaker(RunnerCircuitBreakerOptions.FromConfig(_config));
        runner.ConfigureStuckLoopBudget(LoadStuckLoopBudget(_config));
        runner.OnStatusChanged += status =>
        {
            try { OnRunnerStatusChanged?.Invoke(entry.Name, status); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnRunnerStatusChanged subscriber threw for {Project}", entry.Name); }
        };
        runner.OnModePersist += (mode, source) => _projectSettings.SetRunnerMode(entry.Name, mode, source);

        if (!_runners.TryAdd(entry.Name, runner)) return true;
        var saved = _projectSettings.Get(entry.Name);
        var savedMode = string.IsNullOrWhiteSpace(saved.DesiredRunnerMode) ? saved.RunnerMode : saved.DesiredRunnerMode;
        if (!string.IsNullOrWhiteSpace(savedMode) && savedMode != "manual") runner.RestoreMode(savedMode!);
        _logger.LogInformation("runner-activated project={Project} root={RootPath}", entry.Name, entry.RootPath);
        _ = Task.Run(async () =>
        {
            try { await runner.BootOrchestratorSessionAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Orchestrator boot failed for {Project}", entry.Name); }
        });
        return true;
    }

    /// <summary>
    /// Registry repository authority is independent of task storage. A project
    /// with a repository but no separate working-directory setting runs from
    /// the repository path; a configured RootPath remains the desired CLI
    /// subfolder and is later mapped into each task worktree.
    /// </summary>
    internal static WatchPathEntry ResolveRunnerEntry(
        WatchPathEntry rawEntry,
        ProjectRecord? registryProject)
    {
        var repositoryPath = !string.IsNullOrWhiteSpace(registryProject?.RepositoryPath)
            ? registryProject.RepositoryPath!
            : rawEntry.RepositoryPath;
        var configuredWorkingDirectory = !string.IsNullOrWhiteSpace(registryProject?.RootPath)
            ? registryProject.RootPath!
            : rawEntry.RootPath;
        var effectiveWorkingDirectory = !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
            ? configuredWorkingDirectory
            : repositoryPath;

        return rawEntry with
        {
            RootPath = effectiveWorkingDirectory ?? string.Empty,
            RepositoryPath = repositoryPath ?? string.Empty,
        };
    }

    public bool StartRunner(string projectName)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Start);
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        // Starting is always immediate: auto-* never defers.
        runner.SetMode("auto-single", "api: POST /api/runner/{project}/start");
        return true;
    }

    public bool StopRunner(string projectName)
    {
        // Stop routes through the deferred-aware request so a Stop fired
        // while a job is running queues the pause behind the active job
        // (the runner does not kill the in-flight CLI). Matches the
        // semantics applied by PUT /mode (ADR-0044).
        var result = RequestModeChange(projectName, "paused", "api: POST /api/runner/{project}/stop");
        return result != null && result.Outcome != ModeChangeOutcome.Invalid;
    }
}
