

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Serilog.Events;

// Static Serilog logger so DI-less / static contexts (TryReadEnteredLaneAt,
// path + parser helpers, the SilentCatch standard) have a real logger before -
// and independently of - the DI container. CreateBootstrapLogger publishes it
// to Log.Logger immediately and is swapped in place by the fully configured
// logger once the host is built (UseSerilog below). Console sink only for now.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTaskServerPlaneProxy(builder.Configuration);
var orchestrationExecutionMode = OrchestrationExecutionModeParser.Parse(
    builder.Configuration["Orchestration:ExecutionMode"]);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});
builder.WebHost.ConfigureKestrel(options =>
{
    // A public-demo visitor never has a legitimate reason to send a large
    // body - every mutation is denied at the edge before it would be read.
    // The tighter default only bounds the cost of an oversized request
    // reaching Kestrel in the first place; it is not the mutation boundary.
    var publicDemoBodyCap = SecurityProfiles.IsPublicDemo(builder.Configuration);
    options.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long>(
        publicDemoBodyCap ? "Security:PublicDemoMaxRequestBodyBytes" : "Security:MaxRequestBodyBytes",
        publicDemoBodyCap ? 16 * 1024 : 25 * 1024 * 1024);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

// Default BackgroundServiceExceptionBehavior is StopHost: an unhandled
// exception escaping any HostedService.ExecuteAsync stops the entire host.
// That turned a single faulted runner tick or hosted-service iteration into
// a silent API-down event. Switch to Ignore so the offending service stops
// in isolation and the rest of the API keeps serving while the per-tick
// try/catch (TaskRunnerService) and the UnhandledException recorder above
// surface the cause.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// WebApplicationFactory runs the real entry point in the xunit process. Keep
// this signal alongside the Test environment check because a small number of
// opt-in integration fixtures intentionally exercise the default environment.
var underTestHost = Array.Exists(
    AppDomain.CurrentDomain.GetAssemblies(),
    a => a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);

// Local-only override file (gitignored) - sets per-checkout flags such as
// Environment:IsDev. Loaded after appsettings.Development.json so a developer
// can flip the dev banner / dev PWA icon on for their checkout without
// committing the toggle. Test hosts must not inherit any configuration from
// this machine-specific file. In-memory test fixtures are added later by
// WebApplicationFactory and remain available without merging with local array
// entries such as WatchPaths:1..n.
if (!builder.Environment.IsEnvironment("Test") && !underTestHost)
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

// Test-isolation guard (prevention). An integration test that boots
// WebApplicationFactory<Program> must never touch the production task
// workspace or registry. A loaded xunit assembly is the reliable in-process
// test-host signal; if we detect it and TaskRepository still resolves to a
// real (non-temp) workspace, redirect it to an isolated per-run temp dir.
// Everything storage-related (the flat tasks/ tree, the id/ index, and the
// <TaskRepository>/.metadata/projects.json registry) is derived from
// TaskRepository, so this single redirect prevents a test from creating,
// renaming or deleting anything in the live board — the root cause of both
// the atp-orphan-delete-api-tests registry junk and the shared-workspace
// migration corruption. Never fires in production (xunit is not loaded).
if (underTestHost)
{
    var configuredRepo = builder.Configuration["TaskRepository"];
    var tempRoot = Path.GetTempPath().Replace('\\', '/');
    var repoIsIsolated = string.IsNullOrWhiteSpace(configuredRepo)
        || configuredRepo.Replace('\\', '/').Contains(tempRoot, StringComparison.OrdinalIgnoreCase);
    if (!repoIsIsolated)
    {
        var iso = Path.Combine(Path.GetTempPath(), "atp-test-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(iso);
        builder.Configuration["TaskRepository"] = iso;
    }
}

var startupSecurityProfile = SecurityProfiles.ActiveProfile(builder.Configuration);
var publicDemoExecutionProfile = string.Equals(
    startupSecurityProfile,
    SecurityProfiles.PublicDemo,
    StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new StartupExecutionAdmission(startupSecurityProfile));
builder.Services.AddSingleton<PublicDemoViewerSessionStore>();

// Rolling backend file logger + crash marker (see Services/Diagnostics).
// Built before WebApplication so the process-wide crash handlers below
// can capture the very first throw, even if it lands during DI build.
builder.Services.Configure<BackendFileLoggerOptions>(
    builder.Configuration.GetSection(BackendFileLoggerOptions.SectionName));
var fileLoggerOptions = new BackendFileLoggerOptions();
builder.Configuration.GetSection(BackendFileLoggerOptions.SectionName).Bind(fileLoggerOptions);
if (underTestHost
    && string.IsNullOrWhiteSpace(builder.Configuration[$"{BackendFileLoggerOptions.SectionName}:LogDirectory"]))
{
    fileLoggerOptions.LogDirectory = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-test-host-logs",
        $"{Environment.ProcessId}-{Guid.NewGuid():N}");
}
var fileLogSink = new BackendFileLogSink(fileLoggerOptions);
var crashRecorder = new CrashRecorder(fileLoggerOptions, fileLogSink);
builder.Services.AddSingleton(fileLogSink);
builder.Services.AddSingleton(crashRecorder);
// Drop the default MEL providers (Console/Debug/EventSource) so the operator
// sees a single Serilog-formatted console stream rather than two. The rolling
// backend file logger is re-added explicitly and kept alive via Serilog's
// writeToProviders below.
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new BackendFileLoggerProvider(fileLogSink));

// Route Microsoft.Extensions.Logging through Serilog with a Console sink.
// writeToProviders:true keeps every registered MEL provider (the rolling
// BackendFileLoggerProvider above) receiving events, so this is purely
// additive: Serilog console PLUS the existing file log, no instrumentation
// dropped and no behaviour change beyond the console format. The fully
// configured logger also replaces the bootstrap Log.Logger in place, so the
// static SilentCatch standard and other DI-less callers share this config.
// preserveStaticLogger under a test host: parallel WebApplicationFactory
// boots re-run these top-level statements concurrently, and two hosts racing
// to swap-and-freeze the one static bootstrap Log.Logger throw "The logger
// is already frozen" (flaky full-suite failures). Production keeps the
// in-place swap so DI-less callers (SilentCatch) share the configured logger.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(),
    preserveStaticLogger: underTestHost,
    writeToProviders: true);

// Last-resort safety nets: an uncaught exception in a fire-and-forget Task
// (e.g. CLI output streaming, SignalR fan-out) used to take down the whole API
// silently with an empty stderr. Log them and persist a crash marker so the
// next operator (or Layer 3 review) can find the cause without re-attaching.
// Held in named delegates (not inline lambdas) so a test host can detach them
// on shutdown - WebApplicationFactory<Program> re-runs this entry point on
// every boot, and re-subscribing without ever unsubscribing leaked one handler
// per boot, each pinned to that boot's since-deleted temp log directory. Every
// body is wrapped: UnobservedTaskException is raised on the finalizer thread,
// where an escaping exception is fatal, and even the fallback stderr write can
// throw once the captured pipe is closed at teardown.
EventHandler<UnobservedTaskExceptionEventArgs> onUnobservedTaskException = (_, e) =>
{
    try
    {
        crashRecorder.Record("UnobservedTaskException", e.Exception, isTerminating: false);
        DiagnosticsConsole.Error($"[UnobservedTaskException] {e.Exception}");
    }
    catch (Exception ex) { DiagnosticsConsole.Error($"[UnobservedTaskException] handler failed: {ex.Message}"); }
    // SetObserved only flips a flag on the args and never throws; it stays in a
    // finally so the exception is marked observed even if Record faulted above.
    finally { e.SetObserved(); }
};
UnhandledExceptionEventHandler onUnhandledException = (_, e) =>
{
    try
    {
        if (e.ExceptionObject is Exception ex)
        {
            crashRecorder.Record("UnhandledException", ex, isTerminating: e.IsTerminating);
        }
        DiagnosticsConsole.Error($"[UnhandledException] terminating={e.IsTerminating} {e.ExceptionObject}");
    }
    catch (Exception ex) { DiagnosticsConsole.Error($"[UnhandledException] handler failed: {ex.Message}"); }
};

// ProcessExit fires for any normal CLR teardown - Ctrl+C, parent shell exit,
// SIGTERM, OS-initiated polite shutdown. It does NOT fire for hard kills
// (Process.Kill from outside, TerminateProcess, native crash, OOM-killer).
// By writing a small "shutdown marker" here we close the diagnostic gap:
//   - marker present + last-crash.json absent  -> graceful shutdown
//   - marker absent + last-crash.json present  -> managed exception killed it
//   - both absent                              -> native / external kill (the
//                                                 silent-disappearance case
//                                                 we hit three times on
//                                                 2026-05-15; this is the
//                                                 signal that points us at
//                                                 OS-level or Process.Kill-
//                                                 from-parent investigations
//                                                 rather than wasted time
//                                                 grepping for managed throws).
// The marker file lives next to last-crash.json so a future operator finds
// both in one glance. Failure to write is intentionally swallowed - we are
// already in the host's last-gasp window.
EventHandler onProcessExit = (_, _) =>
{
    try
    {
        var dir = Path.GetFullPath(fileLoggerOptions.LogDirectory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "last-shutdown.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAt = DateTime.UtcNow.ToString("O"),
            pid = Environment.ProcessId,
            reason = "ProcessExit",
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    // Last-gasp teardown: log via stderr rather than Serilog, whose console
    // sink may already be flushing/disposed during ProcessExit. DiagnosticsConsole
    // swallows a broken pipe so the exit handler can never throw.
    catch (Exception ex) { DiagnosticsConsole.Error($"[ProcessExit] shutdown-marker write failed: {ex}"); }
    try { fileLogSink.WriteRaw($"{DateTime.UtcNow:O} INFO  Program ProcessExit fired (pid={Environment.ProcessId})"); }
    catch (Exception ex) { DiagnosticsConsole.Error($"[ProcessExit] shutdown-marker log failed: {ex}"); }
};
// Permanent, never-detached safety net: guarantees every unobserved task
// exception is marked observed so the finalizer thread can never rethrow it
// (fatal when DOTNET_ThrowUnobservedTaskExceptions=1, which dev/CI hosts set).
// The per-run handler below still records the exception; it is detached when a
// test host stops, so this closes the window where no subscriber would call
// SetObserved and a finalizing faulted task would abort the whole run.
ProcessGlobalTaskSafety.EnsureUnobservedTaskExceptionsAreObserved();
TaskScheduler.UnobservedTaskException += onUnobservedTaskException;
AppDomain.CurrentDomain.UnhandledException += onUnhandledException;
AppDomain.CurrentDomain.ProcessExit += onProcessExit;

// Boot-time silent-death detector. The handlers above can only witness a
// *managed* death; a StackOverflowException, an OS OOM-kill, or a native PTY
// crash terminates the process before any of them run and leaves the api-log
// to simply stop. By diffing the previous run's startup marker against the
// shutdown / crash markers this names that silent class at the next boot (and
// drops a last-silent-kill.json) instead of leaving the operator staring at a
// log that ends mid-line. Also arms a fresh startup.json for the next boot.
// Runs before DI build so it captures the prior run regardless of what boot
// does next; fully swallowed so diagnostics can never block boot.
// Skip entirely under a test host. WebApplicationFactory<Program> re-runs this
// entry point dozens of times in one process, all sharing/reusing a marker
// directory, and an in-process boot never writes a shutdown marker on dispose
// (ProcessExit only fires at real process teardown). So the classifier reports
// a spurious SilentKill for the prior boot and prints "[startup] previous
// backend run died silently ...". That false line has surfaced as the *reason*
// in test-host crash reports and misdirected triage toward a phantom backend
// death. The detector is a production operational feature - naming how the one
// long-lived backend died between OS-level restarts - and has no meaning across
// ephemeral in-process test boots.
if (!underTestHost)
{
    try
    {
        var previousRun = crashRecorder.ClassifyPreviousRunAndArm();
        if (previousRun.Verdict == PreviousRunVerdict.SilentKill)
            Console.Error.WriteLine(
                $"[startup] previous backend run died silently (pid={previousRun.PreviousPid}, " +
                $"started={previousRun.PreviousStartedAt:O}) — see last-silent-kill.json");
    }
    catch (Exception ex)
    {
        // Boot diagnostics must never block boot; record without rethrowing.
        Log.ForContext("SourceContext", "Program").Warning(ex, "Boot silent-death detector failed");
    }
}

builder.Services.AddSingleton<ClientIdentityStore>();
builder.Services.AddSingleton<AccessSecurityStore>();
builder.Services.AddSingleton<ManagementService>();
builder.Services.AddSingleton<IProviderAuthProvisioner, SshProviderAuthProvisioner>();
builder.Services.AddSingleton<ITunnelKeeperManager, WindowsTunnelKeeperManager>();
builder.Services.AddSingleton<RemoteRunnerLinkService>();
builder.Services.AddSingleton<MigrationStateStore>();
builder.Services.AddSingleton<HostTelemetryStore>();
builder.Services.AddSingleton<AgentStudio.Persistence.IAtomicJsonFileWriter, AgentStudio.Persistence.AtomicJsonFileWriter>();
builder.Services.AddSingleton<OrchestratorConfigService>();
builder.Services.AddSingleton<WorkspaceManagementService>();
builder.Services.AddSingleton<TaskScannerService>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
builder.Services.AddSingleton<ArtifactThumbnailService>();
builder.Services.AddSingleton<AgentStudio.Shared.ITaskScanner>(sp => sp.GetRequiredService<TaskScannerService>());
builder.Services.AddSingleton<CliOutputLogMaintenanceService>();
// F45a: workspace / project registries + jobKey resolver. Additive layer;
// not yet load-bearing for the existing lane-folder code paths (F45c).
builder.Services.AddSingleton<AgentStudio.Registry.WorkspaceRegistry>();
builder.Services.AddSingleton<AgentStudio.Registry.ProjectRegistry>();
builder.Services.AddSingleton<AgentStudio.Registry.ComponentRoutingService>();
// AGT-1812: per-workspace default settings store + the two-tier orchestrator
// resolver (project override -> workspace default -> platform constant default).
builder.Services.AddSingleton<AgentStudio.Registry.WorkspaceSettingsService>();
builder.Services.AddSingleton<AgentStudio.Registry.OrchestratorDefaultsProvider>();
// Project URLs: read-only repo scan for suggestions + minimal dev-server spawn.
builder.Services.AddSingleton<AgentStudio.Registry.ProjectUrlDetectionService>();
builder.Services.AddSingleton<AgentStudio.Registry.IProjectUrlPortInspector, AgentStudio.Registry.ProjectUrlPortInspector>();
builder.Services.AddSingleton<AgentStudio.Registry.ProjectUrlProcessService>();
builder.Services.AddSingleton<AgentStudio.Registry.ProjectUrlReadinessService>();
// AGT-2180: bounded HTTP client shared by the readiness probe and the URL
// diagnostics (an unconfigured named client would wait 100 s per probe).
builder.Services.AddHttpClient("project-url-readiness", client =>
{
    client.Timeout = TimeSpan.FromSeconds(3);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentStudio-URLPreview/1.0");
});
builder.Services.AddSingleton<AgentStudio.Tasks.TaskKeyResolver>();
builder.Services.AddSingleton<ScreenshotIndexService>();
// F21: per-project write mutex for the lane tree. Must be registered
// before TaskStateMachine / TaskMutationService / TaskAccessService so
// every lane-mutating service can take it as a dependency. See
// docs/system/architecture/runner-lanes/progress-lane-writers.md.
builder.Services.AddSingleton<LaneMutexRegistry>();
// SignalR fanout for fine-grained job mutation events (jobCreated /
// jobUpdated / jobMoved / jobDeleted / jobsReordered). Registered before
// the mutation services so each takes it as a constructor dependency.
// See backend/Services/Jobs/TaskChangeNotifier.cs.
builder.Services.AddSingleton<TaskChangeNotifier>();
builder.Services.AddSingleton<TaskStateMachine>();
builder.Services.AddSingleton<TaskMutationService>();
builder.Services.AddSingleton<TaskFileHistoryService>();
// Consolidation/merge API + completed-lane audit (Part 1+2 of the
// api-consolidationmerge-api task). All mutations route through
// MergeService / CompletedLaneAuditService; the audit log lives at
// <TaskRepository>/.audit/merges.jsonl. AuditRunStore is in-memory so
// in-flight audit runs are lost on restart (the persistent trail is the
// per-card quality_loop_reopened events in each timeline.jsonl).
builder.Services.AddSingleton<AgentStudio.Tasks.MergeAuditLog>();
builder.Services.AddSingleton<AgentStudio.Tasks.MergeCandidateFinder>();
builder.Services.AddSingleton<AgentStudio.Tasks.MergeService>();
builder.Services.AddSingleton<AgentStudio.Tasks.AcceptanceEvidenceDetector>();
builder.Services.AddSingleton<AgentStudio.Tasks.AuditRunStore>();
builder.Services.AddSingleton<AgentStudio.Tasks.CompletedLaneAuditService>();
builder.Services.AddSingleton<FixtureMigrationService>();
builder.Services.AddSingleton<TaskSessionLog>();
builder.Services.AddSingleton<TimelineLog>();
builder.Services.AddSingleton<ProjectThroughputService>();
builder.Services.AddSingleton<ProjectCycleTimeService>();
builder.Services.AddSingleton<ProjectVisualEvidenceService>();
builder.Services.AddSingleton<ProjectGraphDiscoveryService>();
// T2b (ASS-1740): the single per-task read layer. Loads all raw sources
// (detail, session-events, cli-output, timeline ledger) once and projects the
// run timeline + meshed ledger so the /runs and /timeline views stop
// re-parsing the same files independently.
builder.Services.AddSingleton<AgentStudio.Tasks.TaskReader>();
builder.Services.AddSingleton<OrchestratorChatLog>();
builder.Services.AddSingleton<OrchestratorLog>();
builder.Services.AddSingleton<OrchestratorChat>();
builder.Services.AddSingleton<LocalOrchestratorChatPersistence>();
builder.Services.AddSingleton<AgentStudio.Host.OrchestratorContextHubBroadcaster>();
builder.Services.AddSingleton<TaskServerOrchestratorChatPersistence>();
builder.Services.AddSingleton<IOrchestratorChatPersistence>(sp =>
    TaskServerPlaneProxy.IsConfigured(sp.GetRequiredService<IConfiguration>())
        ? sp.GetRequiredService<TaskServerOrchestratorChatPersistence>()
        : sp.GetRequiredService<LocalOrchestratorChatPersistence>());
builder.Services.AddSingleton<OrchestratorContextDigestService>();
builder.Services.AddSingleton<OrchestratorTaskPromptContextComposer>();
builder.Services.AddSingleton<RemoteChatWorkBroker>();
builder.Services.AddSingleton<OrchestratorChatService>();
builder.Services.AddSingleton<ProjectChatStore>();
builder.Services.AddSingleton<ProjectChatIndex>();
builder.Services.AddSingleton<ProjectChatMigration>();
builder.Services.AddSingleton<OrchestratorRunner>(sp => new OrchestratorRunner(
    sp.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Claude),
    sp.GetRequiredService<ILogger<OrchestratorRunner>>(),
    sp.GetService<CliUsageParserRegistry>(),
    sp.GetService<ICliModelRegistry>(),
    sp.GetService<CliOneShotRegistry>()));
builder.Services.AddSingleton<OrchestratorSessionStore>();
builder.Services.AddSingleton<GlobalOrchestratorSessionStore>();
builder.Services.AddSingleton<OrchestratorSessionRegistry>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<OrchestratorChatLegacyMigrationHostedService>();
builder.Services.AddSingleton<OrchestratorTurnService>();
builder.Services.AddSingleton<GlobalOrchestratorBootstrap>();
builder.Services.AddSingleton<TokenSummaryCacheStore>();
builder.Services.AddSingleton<WorkspaceTokensCacheStore>();
builder.Services.AddSingleton<AgentStudio.Tokens.TokenPricingDriftMonitor>();
builder.Services.AddSingleton<TokenSummaryService>();
builder.Services.AddSingleton<ProjectTokenUsageService>();
builder.Services.AddSingleton<WorkspaceTokensTimelineService>();
builder.Services.AddSingleton<WorkspaceSummaryService>();
builder.Services.AddSingleton<AutoReviewPostProcessingQueue>();
builder.Services.AddSingleton<IAutoReviewPostProcessingQueue>(sp =>
    sp.GetRequiredService<AutoReviewPostProcessingQueue>());
builder.Services.AddSingleton<TaskProvenanceService>();
builder.Services.AddSingleton<BoardMergeStatusService>();
builder.Services.AddSingleton<ProjectGitGraphService>();
// AGT-2202: honest git-derived integration verdict for accepted cards (is the
// work actually in develop?). Batched + cached per repo like BoardMergeStatusService.
builder.Services.AddSingleton<TaskIntegrationStatusService>();
builder.Services.AddSingleton<TaskIntegrationRecoveryService>();
builder.Services.AddSingleton<SupersededCommitSweep>();
builder.Services.AddSingleton<RemoteTokenReceiptService>();
builder.Services.AddSingleton<RemoteCompletionAttributionSweep>();
builder.Services.AddSingleton<TaskListGitProjectionCache>();
builder.Services.AddSingleton<OperatorReviewRequeueService>();
// PUB-1: read-only publish-target derivation (repo facts -> Hub badges + task
// chips). PublishTargetService derives + caches per project; TaskPublishableService
// folds the per-task chip signal onto accepted cards (O(projects), no per-card git).
builder.Services.AddSingleton<PublishTargetService>();
builder.Services.AddSingleton<TaskPublishableService>();
builder.Services.AddSingleton<PublishActionService>();
builder.Services.AddSingleton<ProjectDeploymentSummaryService>();
builder.Services.AddSingleton<ProjectDeploymentCompiler>();
builder.Services.AddSingleton<TestRunStore>();
builder.Services.AddSingleton<TestRunService>();
builder.Services.AddSingleton<TaskTransitionService>();
builder.Services.AddSingleton<IBatchMoveItemExecutor, BatchMoveItemExecutor>();
builder.Services.AddSingleton<BatchMoveJobCoordinator>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BatchMoveJobCoordinator>());
// Out-of-band task completion (docs/concepts/out-of-band-task-completion.md §3):
// reconciles a task finished outside the runner in one atomic call.
builder.Services.AddSingleton<ExternalCompletionService>();
builder.Services.AddSingleton<TaskWatcherService>();
// Cycle 1: in-memory snapshot of all jobs across watch paths. Reads from
// TaskScannerService.ScanAllJobsRaw on miss, invalidated by TaskWatcherService
// events and by mutation services. Wired into TaskScannerService below via
// SetIndexCache so existing ScanAllJobs callers transparently benefit.
builder.Services.AddSingleton<TaskIndexCache>();
builder.Services.AddHostedService<TaskIndexCacheDiagnosticsService>();
builder.Services.AddSingleton<JobStatsMetadataCache>();
// TaskAccess layer (ADR-0024 phase 2-4): the typed façade in front of
// TaskScannerService / TaskMutationService / TaskStateMachine /
// TaskTransitionService. Outside callers (endpoints, runner, supervisor)
// resolve ITaskAccess so the lane-folder shape stays inside this layer.
builder.Services.AddSingleton<AgentStudio.TaskAccess.TaskAccessService>();
builder.Services.AddSingleton<AgentStudio.TaskAccess.ITaskAccess>(sp =>
    sp.GetRequiredService<AgentStudio.TaskAccess.TaskAccessService>());
builder.Services.AddSingleton<AgentStudio.TaskAccess.ITaskAccessHost>(sp =>
    sp.GetRequiredService<AgentStudio.TaskAccess.TaskAccessService>());
builder.Services.AddSingleton<CliEnvironment>();
builder.Services.AddSingleton<CodexModelDiscovery>();
builder.Services.AddSingleton<ClaudeModelDiscovery>();
// The per-CLI execution engines: one concrete GenericCliExecutionService per
// CLI, parameterized by a CliBehavior from BuiltInCliBehaviors. Keyed by CLI
// type so the router + the Claude-specific consumers (orchestrator runner,
// session-info endpoint) resolve the exact engine. The log category is kept
// stable (per-CLI) via a named logger so existing log filters still match.
builder.Services.AddKeyedSingleton<GenericCliExecutionService>(CliTypes.Claude, (sp, _) =>
    GenericCliExecutionService.ForClaude(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentStudio.Cli.ClaudeCliService"),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetService<CliUsageParserRegistry>(),
        sp.GetService<ICliModelRegistry>(),
        sp.GetService<ClaudeModelDiscovery>()));
builder.Services.AddKeyedSingleton<GenericCliExecutionService>(CliTypes.Codex, (sp, _) =>
    GenericCliExecutionService.ForCodex(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentStudio.Cli.CodexCliService"),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<CodexModelDiscovery>(),
        sp.GetRequiredService<CliUsageParserRegistry>(),
        sp.GetRequiredService<ICliModelRegistry>()));
builder.Services.AddKeyedSingleton<GenericCliExecutionService>(CliTypes.Gemini, (sp, _) =>
    GenericCliExecutionService.ForAntigravity(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentStudio.Cli.AntigravityCliService"),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ClaudeSessionInspector>();
builder.Services.AddSingleton<CliWorkingMemoryService>();
builder.Services.AddSingleton<CliRouter>(sp => new CliRouter(
    sp.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Claude),
    sp.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Codex),
    sp.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Gemini)));
builder.Services.AddSingleton<SessionToTaskIndex>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ContextUsageParser>();
builder.Services.AddSingleton<SummaryGenerationService>();
builder.Services.AddSingleton<PromptCallTelemetryService>();
builder.Services.AddSingleton<RuntimePromptService>();
builder.Services.AddSingleton<VisualQaService>();
builder.Services.AddSingleton<PromptReviewService>();
builder.Services.AddSingleton<PromptAdminService>();
builder.Services.AddSingleton<TitleGenerationService>();
builder.Services.AddSingleton<PromptEnhancementService>();
builder.Services.AddSingleton<AgentStudio.AdHoc.AdHocUsageRecorder>();
builder.Services.AddSingleton<AgentStudio.AdHoc.AdHocUsageService>();
builder.Services.AddSingleton<PickupLockFile>();
builder.Services.AddSingleton<IntegrationLeaseService>();
// RM-3 / ADR-0060: the fenced task-run lease + this backend's runner identity
// back the productive /api/runner/lease API (§8.2C), the prepared successor to
// the disk-backed .pickup-lock.json guard.
builder.Services.AddSingleton<AttemptAuthorityService>();
builder.Services.AddSingleton<ReviewAttemptTaskLifecycleService>();
builder.Services.AddSingleton<V1ReviewExecutorRegistry>();
builder.Services.AddSingleton<RemoteDispatchRejectionStore>();
builder.Services.AddSingleton<RemoteQueueStarvationWatchdog>();
builder.Services.AddSingleton<AutoReviewQueueStagnationWatchdog>();
builder.Services.AddSingleton<AdaptiveReviewParallelismAdvisor>();
builder.Services.AddSingleton<CodingYieldAdvisor>();
if (!publicDemoExecutionProfile)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteQueueStarvationWatchdog>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoReviewQueueStagnationWatchdog>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AdaptiveReviewParallelismAdvisor>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CodingYieldAdvisor>());
}
builder.Services.AddSingleton(sp => new RunLeaseService(
    sp.GetRequiredService<ILogger<RunLeaseService>>(),
    sp.GetRequiredService<AttemptAuthorityService>()));
builder.Services.AddSingleton(sp => RunnerIdentity.Resolve(sp.GetRequiredService<IConfiguration>()));
// ASS-1729: keep the host awake while >=1 agent run is active. Default ON;
// disable via "KeepAwakeDuringRuns": false. Uses the Windows Power Request API
// on Windows (visible under `powercfg /requests`, system-required only so the
// display may still sleep); a no-op elsewhere. SystemRequired prevents the idle
// sleep timer from firing mid-run; lid-close / manual sleep can still win, which
// the sleep-aware watchdog handles on resume.
builder.Services.AddSingleton<SystemKeepAwake>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var enabled = cfg.GetValue("KeepAwakeDuringRuns", true);
    AgentStudio.Runner.ISystemPowerRequest request =
        OperatingSystem.IsWindows() ? new WindowsPowerRequest() : new NoopPowerRequest();
    return new SystemKeepAwake(request, enabled);
});
builder.Services.AddSingleton<TaskRunnerService>();
builder.Services.AddSingleton<CrashRecoveryService>();
builder.Services.AddSingleton<StaleProgressArchiver>();
// Run-Liveness Slice A: the phase-aware "no zombie survives 60s" monitor
// (boot adoption scan + uptime sweep). See
// docs/concepts/run-liveness-and-slot-semantics.md.
builder.Services.AddSingleton<RunLivenessMonitor>();
// Run-Liveness Slice B: the steer-timeout monitor - no steered / NeedsInput card
// waits indefinitely. See docs/concepts/run-liveness-and-slot-semantics.md Rule 2.
builder.Services.AddSingleton<ISteerTimeoutResolver, SteerTimeoutResolver>();
builder.Services.AddSingleton<SteerTimeoutMonitor>();
builder.Services.AddSingleton<PickupFailureLog>();
builder.Services.AddSingleton<InfraHaltLog>();
builder.Services.AddSingleton<CrossSlugInfraCircuitBreaker>();
builder.Services.AddSingleton<ProviderLimitRegistry>();
builder.Services.AddSingleton<HumanReviewEscalation>();
builder.Services.AddSingleton<AgentMessageBusStore>();
builder.Services.AddSingleton<AgentMessageBusBridge>();
builder.Services.AddSingleton<ICliModelRegistry, CliModelRegistry>();
builder.Services.AddSingleton<ICliUsageParser, ClaudeUsageParser>();
builder.Services.AddSingleton<ICliUsageParser, CodexUsageParser>();
builder.Services.AddSingleton<CliUsageParserRegistry>();
builder.Services.AddSingleton<BusAggregationCache>();
builder.Services.AddSingleton<AgentStudio.Tokens.BusBackedAdHocUsageReader>();
builder.Services.AddSingleton<AgentStudio.Tokens.BusBackedTokenSummaryReader>();
builder.Services.AddSingleton<AgentStudio.Tokens.BusBackedWorkspaceTimelineReader>();
builder.Services.AddSingleton<AgentStudio.Tokens.ProjectTokenReceiptReader>();
builder.Services.AddSingleton<AgentStudio.Tokens.BusBackedProjectTokenUsageReader>();
builder.Services.AddSingleton<AgentStudio.Tokens.ITokenAggregator, AgentStudio.Tokens.TokenAggregationService>();
// Central step-call dispatch: the concrete Claude runner is wrapped by the
// PromptLoggingCliOneShot decorator so every one-shot step prompt (aspects,
// code-review-grade, orchestrator-decision, drift, ...) is captured raw into
// the task's .metadata/prompts.jsonl when the call site sets JobFolderPath +
// StepId. ICliOneShot resolves to the decorator; the registry enumerates it.
builder.Services.AddSingleton<AgentStudio.Cli.StepPromptLog>();
builder.Services.AddSingleton<AgentStudio.Runner.SystemLoadThrottle>();
builder.Services.AddSingleton<AgentStudio.Runner.ILoadThrottleGate>(sp =>
    sp.GetRequiredService<AgentStudio.Runner.SystemLoadThrottle>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentStudio.Runner.SystemLoadThrottle>());
builder.Services.AddSingleton<AgentStudio.Cli.ClaudeOneShot>();
builder.Services.AddSingleton<AgentStudio.Cli.CodexOneShot>();
builder.Services.AddSingleton<AgentStudio.Cli.ICliOneShot>(sp =>
    new AgentStudio.Cli.PromptLoggingCliOneShot(
        new AgentStudio.Cli.LoadAwareCliOneShot(
            sp.GetRequiredService<AgentStudio.Cli.ClaudeOneShot>(),
            sp.GetRequiredService<AgentStudio.Runner.ILoadThrottleGate>(),
            sp.GetRequiredService<ILogger<AgentStudio.Cli.LoadAwareCliOneShot>>()),
        sp.GetRequiredService<AgentStudio.Cli.StepPromptLog>()));
builder.Services.AddSingleton<AgentStudio.Cli.ICliOneShot>(sp =>
    new AgentStudio.Cli.PromptLoggingCliOneShot(
        new AgentStudio.Cli.LoadAwareCliOneShot(
            sp.GetRequiredService<AgentStudio.Cli.CodexOneShot>(),
            sp.GetRequiredService<AgentStudio.Runner.ILoadThrottleGate>(),
            sp.GetRequiredService<ILogger<AgentStudio.Cli.LoadAwareCliOneShot>>()),
        sp.GetRequiredService<AgentStudio.Cli.StepPromptLog>()));
builder.Services.AddSingleton<AgentStudio.Cli.CliOneShotRegistry>();
builder.Services.AddSingleton<CodePatternDriftAnalysisService>();
builder.Services.AddSingleton<AgentStudio.Persistence.IJsonlAppender, AgentStudio.Persistence.JsonlAppender>();
builder.Services.AddSingleton<AgentStudio.Diagnostics.RunnerEventJournal>();
builder.Services.AddSingleton<AgentStudio.DemoReplay.DemoReplayEpochLedger>();
builder.Services.AddSingleton<AgentStudio.Runtime.ProductRuntimeEventStore>();
builder.Services.AddSingleton<AgentStudio.State.SupervisorAdvisoryStore>();
builder.Services.AddSingleton<AgentStudio.State.SupervisorInterventionStore>();
builder.Services.AddSingleton<AgentStudio.Analysis.AnalysisReportStore>();
builder.Services.AddSingleton<AgentStudio.Analysis.RoadmapAlignmentReviewService>();
builder.Services.AddSingleton<AgentStudio.Analysis.SteeringDocsSummaryDriftService>();
builder.Services.AddSingleton<AgentStudio.RegressionRadar.RegressionRadarService>();
builder.Services.AddSingleton<AgentStudio.Drift.DriftReportStore>();
builder.Services.AddSingleton<AgentStudio.Drift.AdrCodeDriftAnalysisService>();
builder.Services.AddSingleton<AgentStudio.Drift.DocsMarketingDriftAnalysisService>();
builder.Services.AddSingleton<AgentStudio.Drift.SpecTaskDriftAnalysisService>();
builder.Services.AddSingleton<AgentStudio.Drift.SoftwareArchitectureDriftAnalysisService>();
builder.Services.AddSingleton<AgentStudio.Drift.ArchitectureElementStateStore>();
builder.Services.AddSingleton<AgentStudio.Drift.DriftPostStepRunner>();
builder.Services.AddSingleton<AgentStudio.Tags.TagRegistryService>();
builder.Services.AddSingleton<ProjectObservationService>();
builder.Services.AddSingleton<FilesystemLayerSnapshotService>();
builder.Services.AddSingleton<SupervisorInterventionService>();
if (!publicDemoExecutionProfile)
{
    builder.Services.AddHostedService<HardHealthCheckHostedService>();
    builder.Services.AddHostedService<SoftReasoningHostedService>();
    builder.Services.AddHostedService<AutoInterventionHostedService>();
    builder.Services.AddHostedService<MetaCycleHostedService>();
    builder.Services.AddHostedService<OrchestratorPrepHostedService>();
    builder.Services.AddHostedService<ChatNoteHostedService>();
}
builder.Services.AddSingleton<AgentStudio.Pipeline.PipelineExecutionLog>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PipelineHealthDetector>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PipelineHealthService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IPipelineHealthSensor>(sp =>
    sp.GetRequiredService<AgentStudio.Pipeline.PipelineHealthService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<AgentStudio.Pipeline.PipelineHealthService>());
builder.Services.AddSingleton<AgentStudio.Tasks.TaskLiveStatusProjection>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IModelEconomyAdvisor,
    AgentStudio.Pipeline.CatalogueModelEconomyAdvisor>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ModelRoutingPolicyRegistry>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ModelRoutingPolicyStateStore>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IModelRoutingModeProvider>(sp =>
    sp.GetRequiredService<AgentStudio.Pipeline.ModelRoutingPolicyStateStore>());
builder.Services.AddSingleton<AgentStudio.Pipeline.ModelQualificationService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IPipelineModelCatalogueProvider,
    AgentStudio.Pipeline.CliPipelineModelCatalogueProvider>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PipelineStepEconomyAdvisor>();
builder.Services.AddSingleton<AgentStudio.Pipeline.MergeIntoDevelopRunner>();
builder.Services.AddSingleton<AgentStudio.GeneratedFiles.FileGenerationIndex>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ProjectPipelineCostService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ILintScssRunner,
    AgentStudio.Pipeline.LintScssRunner>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IQualityStudioAnalysisCore,
    AgentStudio.Pipeline.QualityStudioAnalysisCoreAdapter>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IQualityAnalysisStepRunner,
    AgentStudio.Pipeline.QualityAnalysisStepRunner>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ITestSelectionAdvisor,
    AgentStudio.Pipeline.LlmTestSelectionAdvisor>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IBuildTestGateRunner,
    AgentStudio.Pipeline.BuildTestGateRunner>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PreMainTestGate>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PipelineStepProbeService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.PreDevelopBuildGate>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WikiMaintenancePostStepRunner>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WikiLearningsPostStepRunner>();
// Opt-in AGENTS.md <-> wiki designated-topics sync (AGT-1782): keeps the
// designated-topic pointers consistent and collects each topic's current state.
// Injected into the review orchestrator; default-OFF per project.
builder.Services.AddSingleton<AgentStudio.Pipeline.AgentsWikiSyncPostStepRunner>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IManagedProjectArtifactCommitService,
    AgentStudio.Pipeline.ManagedProjectArtifactCommitService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IConceptWorkbenchPublisher,
    AgentStudio.Pipeline.ConceptWorkbenchPublisher>();
builder.Services.AddSingleton<AgentStudio.Pipeline.ConceptPromotionService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.OnDemandPostStepService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WikiTaskCrossReferenceService>();
// Opt-in task-spawner post-step (AGT-2028): relevance judgment + follow-up
// card creation into a configured target project. Injected into the review
// orchestrator; default-OFF per project (ProjectSettings.TaskSpawner + the
// post-task-spawner pipeline-step enable flag).
builder.Services.AddSingleton<AgentStudio.Pipeline.TaskSpawnerPostStepRunner>();
builder.Services.AddSingleton<AspectRunnerService>();
builder.Services.AddSingleton<RemoteReviewPlanBuilder>();
builder.Services.AddSingleton<RemotePipelineReviewEvidenceProjector>();
builder.Services.AddSingleton<AgentStudio.Review.CodeReviewStepService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WorkspaceArtifactPushQueue>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<AgentStudio.Pipeline.WorkspaceArtifactPushWorker>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WorkspaceArtifactCommitService>();
// Transition-Committer (WorkspaceEvidence): every successful lane transition
// enqueues an evidence-commit wish (TaskStateMachine.EnqueueEvidence); the
// worker debounces and commits the touched projects/<name> data paths per
// workspace repo off the request path, plus a one-shot boot catch-up. Reuses
// WorkspaceArtifactCommitService's git plumbing and (when Push=true) the
// existing WorkspaceArtifactPushQueue.
builder.Services.AddSingleton<AgentStudio.Pipeline.WorkspaceEvidenceQueue>();
builder.Services.AddSingleton<AgentStudio.Pipeline.WorkspaceEvidenceBatcher>(sp =>
    new AgentStudio.Pipeline.WorkspaceEvidenceBatcher(
        sp.GetRequiredService<AgentStudio.Pipeline.WorkspaceArtifactCommitService>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentStudio.Pipeline.WorkspaceEvidence"),
        sp.GetService<TimeProvider>(),
        sp.GetService<AgentStudio.Pipeline.WorkspaceArtifactPushQueue>()));
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<AgentStudio.Pipeline.WorkspaceEvidenceWorker>();
// Intelligente Abbruch-Bewertung (ADR-0032): the post-abort LLM review step.
// Forwarded into ProjectRunner via TaskRunnerService; default-OFF per project.
builder.Services.AddSingleton<AgentStudio.Runner.PostAbortReviewStepService>();
builder.Services.AddSingleton<AutoReviewStatusSnapshot>();
builder.Services.AddSingleton<ReviewDecisionOrchestrator>();
// Transitional single-owner boundary. Engine mode deliberately registers none
// of the legacy review/council/post-processing hosted loops.
if (!publicDemoExecutionProfile)
    builder.Services.AddOrchestrationExecutionLoops(orchestrationExecutionMode);
// Orchestrator-intake (ready-orchestrator-intake-lane). Off by default per
// project; see ProjectSettings.IntakeEnabled. The hosted service is cheap
// (heuristic only, no LLM) and skips projects that have not opted in.
builder.Services.AddSingleton<IntakeRunner>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<IntakeHostedService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<ProjectIntegrationViewService>();
builder.Services.AddSingleton<AgentStudio.Search.GlobalSearchService>();
builder.Services.AddSingleton<ProjectSettingsService>();
builder.Services.AddSingleton<GitCleanupService>();
builder.Services.AddSingleton<GitBranchRetentionService>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<GitBranchRetentionHostedService>();
// Slice P (ASS-1663): build-profile onboarding validation dry-run.
builder.Services.AddSingleton<IBuildCommandRunner, ProcessBuildCommandRunner>();
builder.Services.AddSingleton<BuildProfileValidationService>();
// Completed-job auto-push runs off the request path: TaskTransitionService
// enqueues here on the move to 6-completed (instant), CompletedPushWorker
// drains and performs the git push, CompletedPushBackstopHostedService is the
// periodic safety net for missed / shutdown-dropped pushes.
builder.Services.AddSingleton<CompletedPushQueue>();
if (!publicDemoExecutionProfile)
{
    builder.Services.AddHostedService<CompletedPushWorker>();
    builder.Services.AddHostedService<CompletedPushBackstopHostedService>();
}
// Accepted integration is a transactional two-stage background chain. Accept
// keeps the card in Human Review with phase=integrating and enqueues merge +
// gate here. Only successful integration moves it to Completed; failures return
// it to ordinary Human Review with durable evidence. Both queues are latency
// optimizations; the backstops recover from phase and pipeline facts. Remote
// deliveries use the same merge runner before Human Review.
builder.Services.AddSingleton<AgentStudio.Pipeline.AcceptedIntegrationQueue>();
builder.Services.AddSingleton<AcceptedIntegrationInventorySweep>();
builder.Services.AddSingleton<HistoricalIntegrationVerificationSweep>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<AgentStudio.Pipeline.AcceptedIntegrationWorker>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IntegrationAgentRoundService>();
builder.Services.AddSingleton<AgentStudio.Pipeline.RemoteDeliveryIntegrationCoordinator>();
builder.Services.AddSingleton<AgentStudio.Pipeline.IntegrationPushQueue>();
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<AgentStudio.Pipeline.IntegrationPushWorker>();
// The channel is intentionally in-memory. Durable phase and pipeline facts
// recover both an interrupted accept transaction and a successful merge whose
// queued origin push was dropped by restart.
builder.Services.AddSingleton<AgentStudio.Pipeline.AcceptedIntegrationBackstopHostedService>();
builder.Services.AddSingleton<AcceptanceRailHostedService>();
if (!publicDemoExecutionProfile)
{
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<AgentStudio.Pipeline.AcceptedIntegrationBackstopHostedService>());
    builder.Services.AddHostedService<AgentStudio.Pipeline.IntegrationPushBackstopHostedService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AcceptanceRailHostedService>());
}
// Periodic reap of orphaned CLI process trees (codex/node) that a finished or
// crashed run left behind. Closes the days-long accumulation gap the startup
// reaper alone cannot: those survivors hold job-folder handles and wedge the
// next lane move with "file in use by another process".
if (!publicDemoExecutionProfile)
{
    builder.Services.AddHostedService<OrphanReaperHostedService>();
    builder.Services.AddHostedService<CleanContextRetentionHostedService>();
}
// Runtime stale-progress sweep. The boot sweep handles already-stuck
// 3-progress folders; this closes the gap where a folder crosses the resume
// window while the backend stays up.
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<StaleProgressSweepHostedService>();
// Runtime run-liveness sweep (Run-Liveness Slice A). Demotes a 3-progress card
// within the 60s budget when its owning run dies while the backend stays up;
// the boot adoption scan below handles zombies already present at startup.
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<RunLivenessMonitorHostedService>();
// Runtime steer-timeout sweep (Run-Liveness Slice B). Resolves an unanswered
// steer / NeedsInput wait (auto-answer from the task context, else a blocked
// escalation) within timeout + one interval, so a steered card never hangs for
// hours the way 2062/2067/2068 did on 2026-07-10.
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<SteerTimeoutMonitorHostedService>();
// AGT-2492 Wiedervorlage sweep. Parked cards carry a machine-readable blocker;
// this re-checks those conditions so a card whose infrastructure precondition
// was cleared is reported instead of sitting unnoticed (AGT-2220 lost four days
// exactly that way). Report-only: it never re-queues a card.
builder.Services.AddSingleton<AgentStudio.Tasks.IParkedBlockerProbe>(sp =>
    new AgentStudio.Tasks.ParkedBlockerProbe(sp.GetService<AgentStudio.Git.GitService>()));
builder.Services.AddSingleton<AgentStudio.Tasks.ParkedCardRecallSweep>();
builder.Services.AddHostedService<AgentStudio.Tasks.ParkedCardRecallSweepHostedService>();
builder.Services.AddSingleton<ProjectDocsService>();
builder.Services.AddSingleton<WikiContentCache>();
// Warms the central wiki cache off the startup path and logs the periodic
// hit/miss/fill rollup. See WikiCacheWarmupService for why this must not block
// StartAsync.
builder.Services.AddHostedService<WikiCacheWarmupService>();
// Lexical wiki search (BM25 in-memory index, lazily rebuilt on a docs
// fingerprint change) with the fail-open semantic query-expansion layer.
builder.Services.AddSingleton<WikiSearchService>();
builder.Services.AddSingleton<ProjectStyleGuideService>();
builder.Services.AddSingleton<PromptEnrichmentService>();
builder.Services.AddSingleton<ManagedRepositoryMutationService>();
builder.Services.AddSingleton<WorkbenchCatalogueService>();
builder.Services.AddSingleton<DossierMaintenanceService>();
builder.Services.AddSingleton<WorkbenchChangeNotifier>();
builder.Services.AddSingleton<WorkbenchDecisionService>();
builder.Services.AddSingleton<WorkbenchLifecycleService>();
builder.Services.AddSingleton<AgentStudio.Proposals.ProjectProposalService>();
builder.Services.AddSingleton<AgentStudio.Proposals.ProjectProposalDraftingService>();
// Wiki-grading maintenance run (AGT-2051): the maintenance-model default (its own
// config class in the CLI-management area), the companion sidecar writer, the
// grader seam (production = the one-shot CLI rail), and the run orchestrator.
builder.Services.AddSingleton<AgentStudio.Docs.WikiMaintenanceModelService>();
builder.Services.AddSingleton<AgentStudio.Docs.WikiAgentReadStore>();
builder.Services.AddSingleton<AgentStudio.Docs.WikiCompanionStore>();
builder.Services.AddSingleton<AgentStudio.Docs.WikiAgentReadService>();
builder.Services.AddSingleton<AgentStudio.Docs.IWikiPageGrader, AgentStudio.Docs.CliWikiPageGrader>();
builder.Services.AddSingleton<AgentStudio.Docs.WikiGradingService>();
builder.Services.AddSingleton<ProjectSteeringDocsService>();
builder.Services.AddSingleton<AgentDocsReadAnalyticsService>();
builder.Services.AddSingleton<SkillReadinessService>();
builder.Services.AddSingleton<ConceptDocsService>();
builder.Services.AddSingleton<SecurityReviewService>();
builder.Services.AddSingleton<AgentStudio.Design.DesignEvidenceService>();
// Quota probes: each CLI gets its own probe instance, all surfaced through QuotaService.
builder.Services.AddSingleton<IQuotaProbe, ClaudeQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, CodexQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, AntigravityQuotaProbe>();
builder.Services.AddSingleton<QuotaCacheStore>();
builder.Services.AddSingleton<CliVersionTracker>();
builder.Services.AddSingleton<NpmGlobalInstaller>();
builder.Services.AddSingleton<LocalCliRepairService>();
builder.Services.AddSingleton<QuotaService>();
if (!builder.Environment.IsEnvironment("Test") && !underTestHost)
    builder.Services.AddHostedService<CliVersionMonitorHostedService>();
builder.Services.AddSingleton<CliQuotaCapsService>();
builder.Services.AddSingleton<CliQuotaWaitPolicyService>();
builder.Services.AddSingleton<CliQuotaFallbackService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskWatcherService>());
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRunnerService>());
// F22: server-rendered conversation projection. The projector serves the
// GET /api/tasks/{id}/conversation endpoint and (when the feature flag is
// on) broadcasts deltas over TaskHub. Sources are registered so the
// IEnumerable<IConversationEventSource> ctor gets a deterministic order.
builder.Services.AddSingleton<IMarkdownRenderer, MarkdigRenderer>();
var projectionCacheSize = int.TryParse(builder.Configuration["ConversationProjection:CacheSize"], out var pcs) ? pcs : 50;
builder.Services.AddSingleton(new ConversationCache(projectionCacheSize));
builder.Services.AddSingleton<IConversationEventSource, CliOutputSource>();
builder.Services.AddSingleton<IConversationEventSource, OrchestratorSource>();
builder.Services.AddSingleton<IConversationEventSource, AutoReviewSource>();
builder.Services.AddSingleton<IConversationEventSource, RunnerEventSource>();
builder.Services.AddSingleton<IConversationEventSource, SystemEventSource>();
builder.Services.AddSingleton<ConversationProjector>();
builder.Services.AddSingleton<SourceWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceWatcher>());
// Companion app sync (ADR-0018). Default-off; the HostedService loop exits
// immediately when Companion:Enabled is false. Bound from appsettings*.json.
builder.Services.Configure<CompanionSyncOptions>(builder.Configuration.GetSection(CompanionSyncOptions.SectionName));
builder.Services.AddSingleton<CompanionCommandDispatcher>();
builder.Services.AddHttpClient("companion-relay");
if (!publicDemoExecutionProfile)
    builder.Services.AddHostedService<CompanionSyncService>();
// Serialise enums as camelCase strings so the frontend can use string-literal
// unions (e.g. TaskSummaryStatus = 'none' | 'generating' | 'ready' | 'failed')
// instead of numeric values.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
// Public-demo request-rate ceiling (W34 §6 "Minimum mutation surface" /
// "Transport and browser boundary"). The global limiter itself is always
// registered so UseRateLimiter has one policy to run; the partition function
// below is the only branch on profile, and it hands back an unlimited
// partition for local/networked so this is a no-op there.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejected, token) =>
    {
        rejected.HttpContext.Response.ContentType = "application/json";
        rejected.HttpContext.Response.Headers.CacheControl = "no-store";
        await rejected.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "public-demo-rate-limited",
            message = "Too many requests. Slow down and try again shortly.",
        }, token);
    };

    var publicDemoRateLimited = SecurityProfiles.IsPublicDemo(builder.Configuration);
    var permitLimit = builder.Configuration.GetValue("Security:PublicDemoEdge:RateLimit:PermitLimit", 120);
    var windowSeconds = builder.Configuration.GetValue("Security:PublicDemoEdge:RateLimit:WindowSeconds", 60);
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!publicDemoRateLimited)
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("unrestricted");
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
            });
    });
});
builder.Services.AddSignalR();
// Bridges TaskChangeNotifier + TaskTransitionService move events onto TaskHub
// (jobCreated / jobUpdated / jobMoved / jobDeleted / jobsReordered /
// jobsBulkChanged). Resolved + move-source-attached during startup wiring
// below so the notifier subscriptions are live before the first mutation.
builder.Services.AddSingleton<AgentStudio.Host.TaskHubBroadcaster>();
builder.Services.AddSingleton<AgentStudio.Host.WorkbenchHubBroadcaster>();
if (SecurityProfiles.IsLocal(builder.Configuration))
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins("http://localhost:4010", "http://localhost:4200")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials());
    });
}

var app = builder.Build();

// Under a test host, WebApplicationFactory<Program> boots this entry point once
// per fixture and disposes the host at the end of each. Detach the process-
// global crash handlers on stop so reboots don't accumulate a growing chain of
// subscribers pinned to already-deleted temp log directories - that leak is what
// put dozens of finalizer-thread callbacks onto dead paths (the "[BackendFileLogger]
// WriteRaw failed: ... path not found" spam) and turned the crash-surface test's
// GC.WaitForPendingFinalizers() into a host-crash risk. Production keeps them for
// the whole process lifetime (the host stops only at real shutdown), so the
// ProcessExit shutdown marker still gets written there.
if (underTestHost)
{
    app.Lifetime.ApplicationStopped.Register(() =>
    {
        TaskScheduler.UnobservedTaskException -= onUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= onUnhandledException;
        AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
    });
}

var networkedSecurityProfile = SecurityProfiles.IsNetworked(app.Configuration);
var projectScopedSignalRProfile = networkedSecurityProfile || publicDemoExecutionProfile;
var includeExceptionDetails = SecurityProfiles.IsLocal(app.Configuration)
    && app.Configuration.GetValue<bool>("ErrorHandling:IncludeExceptionDetails");

app.UseForwardedHeaders();
app.UseRouting();
if (networkedSecurityProfile || publicDemoExecutionProfile) app.UseHsts();
app.UseRateLimiter();
app.UsePublicDemoExecutionLock();
// Runs after the execution lock: a route S2 already denies by identity keeps
// its specific execution-disabled code, and this is the broader net behind
// it (blanket unsafe-method + explicit read-allowlist denial, TLS, headers,
// viewer session). See PublicDemoEdgeMiddleware.
app.UsePublicDemoReadOnlyEdge();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = feature?.Error;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var response = new Dictionary<string, object?>
        {
            ["error"] = includeExceptionDetails
                ? exception?.Message ?? "An unexpected server error occurred."
                : "An unexpected server error occurred.",
            ["path"] = feature?.Path,
            ["traceId"] = context.TraceIdentifier,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        if (includeExceptionDetails)
        {
            response["exceptionType"] = exception?.GetType().FullName;
            response["stackTrace"] = exception?.StackTrace;
            response["exception"] = exception?.ToString();
        }

        await context.Response.WriteAsJsonAsync(response);
    });
});

if (SecurityProfiles.IsLocal(app.Configuration)) app.UseCors();

// In the networked profile this is the authentication and authorization
// boundary. X-Client-Id remains attribution only and is never consulted as a
// credential. Local development retains the legacy attribution middleware.
app.UseAccessSecurity();

// The local profile's X-Client-Id registration boundary rejects mutations from
// unregistered identities and stamps lastSeenAt on known ones. This is local
// attribution only, never authentication. Carve-outs for client registration,
// hubs, and health checks live in the middleware itself.
if (!networkedSecurityProfile) app.UseClientIdentity();

// Management intent is authoritative for admission. Reads, recovery controls,
// and the bounded set of writes needed to drain an existing Runner remain live.
app.UseManagementMode();

// Touch the identity store at boot so the bootstrap "local-default" identity
// is created before any caller looks at it.
app.Services.GetRequiredService<ClientIdentityStore>().EnsureLoaded();

// Ensure state folders exist and migrate legacy flat jobs
app.Services.GetRequiredService<TaskStateMachine>().EnsureStateFoldersAndMigrate();

// Backfill jobs that carry agent:"human" + cliType:null + model:null with owner
// client defaults. Idempotent; no-op once all jobs are already migrated.
{
    var backfillCount = app.Services.GetRequiredService<TaskMutationService>().BackfillAgentDefaults();
    if (backfillCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Backfilled agent defaults on {Count} job(s)", backfillCount);
}

// F45a: populate a missing project registry from configured WatchPaths.
// An existing registry is authoritative, and an invalid file aborts startup.
// The pass does not move or rename watched project data.
try
{
    AgentStudio.Registry.RegistryBootstrap.Run(
        app.Services.GetRequiredService<AgentStudio.Registry.WorkspaceRegistry>(),
        app.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>(),
        app.Services.GetRequiredService<TaskScannerService>(),
        app.Services.GetRequiredService<ILogger<Program>>());
}
catch (ProjectRegistryLoadException ex)
{
    crashRecorder.Record("RegistryBootstrap", ex);
    throw;
}
catch (Exception ex)
{
    crashRecorder.Record("RegistryBootstrap", ex);
}

// Backfill task keys (ATP-NNN) on jobs created before key generation was wired
// into CreateJob. Runs after RegistryBootstrap so project short codes exist.
// Idempotent; no-op once all jobs carry a key.
{
    var mutations = app.Services.GetRequiredService<TaskMutationService>();
    var keyCount = mutations.BackfillTaskKeys();
    if (keyCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Backfilled task keys on {Count} job(s)", keyCount);

    // Resolve any duplicate display keys (two tasks sharing one key). The
    // sweep is idempotent and a no-op once every key is unique; it keeps the
    // oldest task on the contested key and re-keys the namesakes. Runs after
    // the backfill so freshly stamped jobs are part of the uniqueness check.
    var dedupCount = mutations.DeduplicateTaskKeys();
    if (dedupCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning("Resolved duplicate task keys by re-keying {Count} task(s)", dedupCount);
}

// ReviewAttempts are claimable only while their owning task remains in Auto
// Review. Repair stale authority left behind by older terminal lane moves
// before any Remote Review Executor can poll this process.
try
{
    var repaired = app.Services.GetRequiredService<ReviewAttemptTaskLifecycleService>()
        .SweepUnclaimableAttempts();
    if (repaired > 0)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning(
                "review-attempt-boot-sweep superseded={Superseded}",
                repaired);
    }
}
catch (Exception ex)
{
    crashRecorder.Record("ReviewAttemptTaskLifecycle.BootSweep", ex);
}

// One-time initialization of durable per-page agent read counters from the
// historical cli-output.log inventory. The marker makes subsequent boots a
// cheap no-op; this runs before CLI reattachment and before the listener starts
// so historical and new live observations cannot race each other.
try
{
    app.Services.GetRequiredService<AgentStudio.Docs.WikiAgentReadService>().EnsureBackfilled();
}
catch (Exception ex)
{
    crashRecorder.Record("WikiAgentReadBackfill", ex);
}

// One-time, idempotent repair of Git-derived commit file metadata on live
// cards. The sweep reads Git and writes only task.json through the owning
// mutation service; entries with complete metadata make later boots a no-op.
try
{
    app.Services.GetRequiredService<TaskMutationService>().BackfillMissingCommitMetadata();
}
catch (Exception ex)
{
    crashRecorder.Record("CommitMetadataBackfill", ex);
}

// One-time, conservative repair of historical rebase/steer generations. The
// persisted report is also the completion marker. Ambiguous file-breadth cases
// remain untouched and visible in that report.
try
{
    app.Services.GetRequiredService<SupersededCommitSweep>().RunOnce();
}
catch (Exception ex)
{
    crashRecorder.Record("SupersededCommitSweep", ex);
}

// One-time repair for recent remote completions whose integration branch had
// already advanced to the result SHA and therefore collapsed commits[] to an
// empty merge-base range. The same pass materializes missing remote token
// receipts from the durable CLI log.
try
{
    app.Services.GetRequiredService<RemoteCompletionAttributionSweep>().RunOnce();
}
catch (Exception ex)
{
    crashRecorder.Record("RemoteCompletionAttributionSweep", ex);
}

// Cap legacy durable CLI logs after the one-time full-history wiki read
// backfill but before CLI reattachment can append new output. The sweep also
// ensures the sole rotation file stays outside workspace evidence commits.
// It is idempotent and cheap once every active/rotated file is bounded.
try
{
    app.Services.GetRequiredService<CliOutputLogMaintenanceService>().Run();
}
catch (Exception ex)
{
    crashRecorder.Record("CliOutputLogMaintenance", ex);
}

// AGT-2438: one-time, idempotent repair for accepted legacy cards whose
// status.md is missing. The same TaskTransitionService owns the live invariant
// and this backfill, so both paths synthesize exactly the same honest Result
// scaffold. Repaired files carry an operator-backfill marker; later boots are
// no-ops because existing non-empty Result documents are never overwritten.
try
{
    app.Services.GetRequiredService<TaskTransitionService>().BackfillMissingResultDocuments();
}
catch (Exception ex)
{
    crashRecorder.Record("ResultDocumentBackfill", ex);
}

// ADR-0020: run the crash-recovery sweep BEFORE the first runner tick. Any
// surviving completion-marker.json finishes its 3-progress -> 4-review move
// here, and any orphan working-tree changes are queued for operator
// confirmation before a crash-recovery commit is created. Sync wait is
// intentional: we want the runner to see the recovered state on its first scan.
//
// F21 boot order: CrashRecoveryService runs before StaleProgressArchiver
// because crash-recovery may complete a half-finished transition
// (3-progress -> 4-auto-review) that the archiver would otherwise mistake
// for a stuck orphan. The two services also share the per-project
// LaneMutexRegistry, so even an accidental reorder cannot produce a
// concurrent rename against the same slug - the sequential ordering is
// belt-and-braces.
// Run-Liveness Slice A boot adoption scan (Rule 4, "no zombie survives 60s"):
// every 3-progress card without a live run-heartbeat is acted on immediately.
// This is the authoritative, PHASE-AWARE handler for the after-restart zombie
// wave (belegt AGT-1811/1914/1941) and runs BEFORE CrashRecoveryService so the
// blanket stale-lock requeue never mis-handles the AGT-1932 case (run finished
// AND merged, only post-processing died): the adoption scan demotes an
// interrupted execution run to 2-ready (clearing the resume pointer to break the
// "No conversation found" launch-fail chain) but re-triggers post-processing for
// a finished run instead of re-running the completed agent. Sync wait is
// intentional: the runner must see the adopted state on its first scan.
if (!publicDemoExecutionProfile) try
{
    app.Services.GetRequiredService<RunLivenessMonitor>().AdoptOnBootAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    crashRecorder.Record("RunLivenessMonitor.AdoptOnBoot", ex);
}

if (!publicDemoExecutionProfile) try
{
    app.Services.GetRequiredService<CrashRecoveryService>().RecoverAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    // Recovery never blocks boot; a failure here is logged and surfaced
    // through the crash recorder so the operator can find it.
    crashRecorder.Record("CrashRecoveryService", ex);
}

// After file-level crash recovery, sweep the 3-progress lane for folders that
// have been wedged past the resume window. Pairs with crash recovery: that
// rescues changes, this rescues the lane (one running job per project, ADR-0001).
if (!publicDemoExecutionProfile) try
{
    var archiver = app.Services.GetRequiredService<StaleProgressArchiver>();
    archiver.SweepAsync().GetAwaiter().GetResult();
    // Failed-pickup-elimination (supersedes ADR-0028/0029): drain any folders
    // that linger in the retired 3a-failed-pickup lane from before this change
    // - real tasks back to 2-ready, debris to 7-archive - after the sweep so a
    // folder requeued from 3-progress is never also drained. Idempotent once
    // the lane is empty.
    archiver.DrainFailedPickupLaneAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    crashRecorder.Record("StaleProgressArchiver", ex);
}

// Seed the Agent Message Bus participant registry. Workspace-scoped, idempotent
// across boots; safe to fire-and-forget. See docs/system/architecture/bus/agent-message-bus.md section 2.
try
{
    var bus = app.Services.GetRequiredService<AgentMessageBusBridge>();
    _ = bus.SeedBuiltInParticipantsAsync();
}
catch (Exception ex) { crashRecorder.Record("AgentMessageBusBridge.Seed", ex); }

// Wire the aggregation cache onto the bus store. Every successful append
// updates the per-project tallies in O(1), so /token-aggregate requests
// do not scan messages. The cache backfills lazily on first use.
//
// SignalR push: also broadcast every appended message as `busMessageAdded`
// so the Observability panel can drop its polling loop. The push is
// fire-and-forget; subscribers reconnect their own snapshot if they fall
// behind.
try
{
    var store = app.Services.GetRequiredService<AgentMessageBusStore>();
    var cache = app.Services.GetRequiredService<BusAggregationCache>();
    var pushHub = app.Services.GetRequiredService<IHubContext<TaskHub>>();
    store.OnAppended = (workspace, msg) =>
    {
        try { cache.OnAppended(workspace, msg); } catch (Exception ex) { SilentCatch.Note(ex, "BusAggregationCache.OnAppended"); }
        try
        {
            var recipients = projectScopedSignalRProfile
                ? !string.IsNullOrWhiteSpace(msg.Project)
                    ? pushHub.Clients.Group(TaskHub.ProjectGroup(msg.Project, app.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()))
                    : pushHub.Clients.Group(TaskHub.UnscopedSecurityGroup)
                : pushHub.Clients.All;
            _ = recipients.SendAsync("busMessageAdded", msg);
        }
        catch (Exception ex) { SilentCatch.Note(ex, "busMessageAdded SignalR push"); }
    };
}
catch (Exception ex) { crashRecorder.Record("BusAggregationCache.Wire", ex); }

// Slice D project-chat: migrate the legacy `orchestrator-chat.jsonl`
// per-project file into the new per-month markdown tree, then ensure
// the per-project FTS5 index is fresh. Idempotent; cheap when the
// migration has already run. Fire-and-forget so a slow disk does not
// hold up boot.
_ = Task.Run(() =>
{
    var migrationState = app.Services.GetRequiredService<MigrationStateStore>();
    const string migrationId = "project-chat-v1";
    try { migrationState.Begin(migrationId, "Migrating legacy project chat and refreshing indexes."); }
    catch (Exception ex) { crashRecorder.Record("MigrationState.Begin:ProjectChatMigration", ex); }
    try
    {
        var migration = app.Services.GetRequiredService<ProjectChatMigration>();
        migration.MigrateAll();

        var scanner = app.Services.GetRequiredService<TaskScannerService>();
        var index = app.Services.GetRequiredService<ProjectChatIndex>();
        foreach (var entry in scanner.GetWatchPaths())
        {
            try { index.EnsureFresh(entry.Path); }
            catch (Exception ex) { crashRecorder.Record($"ProjectChatIndex.EnsureFresh:{entry.Name}", ex); }
        }
        try { migrationState.Complete(migrationId); }
        catch (Exception ex) { crashRecorder.Record("MigrationState.Complete:ProjectChatMigration", ex); }
    }
    catch (Exception ex)
    {
        try { migrationState.Fail(migrationId, ex.Message); }
        catch (Exception stateEx) { crashRecorder.Record("MigrationState.Fail:ProjectChatMigration", stateEx); }
        crashRecorder.Record("ProjectChatMigration", ex);
    }
});

// Wire up FileSystemWatcher → SignalR push
var watcher = app.Services.GetRequiredService<TaskWatcherService>();
var hubContext = app.Services.GetRequiredService<IHubContext<TaskHub>>();
watcher.OnJobChanged += _ => hubContext.Clients.All.SendAsync("jobsChanged");
var eventProjects = app.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>();
IClientProxy ProjectEventClients(string projectName)
    => projectScopedSignalRProfile
        ? hubContext.Clients.Group(TaskHub.ProjectGroup(projectName, eventProjects))
        : hubContext.Clients.All;
IClientProxy TaskEventClients(string jobId)
{
    if (!projectScopedSignalRProfile) return hubContext.Clients.All;
    var task = app.Services.GetRequiredService<TaskScannerService>().FindJob(jobId);
    return task is null
        ? hubContext.Clients.Group("project:unresolved")
        : ProjectEventClients(task.ProjectName);
}

// Fine-grained job-mutation push (jobCreated / jobUpdated / jobMoved /
// jobDeleted / jobsReordered / jobsBulkChanged). Resolving the singleton
// attaches its TaskChangeNotifier subscriptions; AttachMoveSource hooks the
// transition service's move event. See backend/Hubs/TaskHubBroadcaster.cs.
var jobHubBroadcaster = app.Services.GetRequiredService<AgentStudio.Host.TaskHubBroadcaster>();
jobHubBroadcaster.AttachMoveSource(app.Services.GetRequiredService<TaskTransitionService>());
var workbenchHubBroadcaster = app.Services.GetRequiredService<AgentStudio.Host.WorkbenchHubBroadcaster>();
workbenchHubBroadcaster.Attach(
    watcher,
    app.Services.GetRequiredService<WorkbenchChangeNotifier>());

// Cycle 1: bind the in-memory snapshot cache. TaskScannerService.ScanAllJobs
// now serves from cache; TaskWatcherService.OnJobChanged invalidates it on
// external file changes, mutation services invalidate it on API writes.
// Without this two-line bridge the cache exists but nothing fills or
// invalidates it, and ScanAllJobs falls back to per-call disk walks.
var jobIndexCache = app.Services.GetRequiredService<TaskIndexCache>();
var jobStatsMetadataCache = app.Services.GetRequiredService<JobStatsMetadataCache>();
var taskScanner = app.Services.GetRequiredService<TaskScannerService>();
taskScanner.SetIndexCache(jobIndexCache);
taskScanner.SetStatsMetadataCache(jobStatsMetadataCache);
watcher.OnJobChanged += _ => jobIndexCache.Invalidate(TaskIndexCache.InvalidationSource.External);
watcher.OnJobChanged += _ => jobStatsMetadataCache.Invalidate();

// Central wiki read model: bind the process-wide cache and rebuild it eagerly
// on debounced docs/ watcher events. All wiki endpoints then read an
// already-published snapshot instead of validating it with a per-request
// filesystem walk. The binding has to happen here, before the host starts, so
// that WikiCacheWarmupService (registered above) fills the same instance the
// endpoints read. The warmup itself runs in the background - preloading a
// multi-hundred-file docs/ tree synchronously would delay the HTTP listener.
var wikiContentCache = app.Services.GetRequiredService<WikiContentCache>();
var projectDocs = app.Services.GetRequiredService<ProjectDocsService>();
projectDocs.SetWorkbenchCatalogue(app.Services.GetRequiredService<WorkbenchCatalogueService>());
projectDocs.SetWikiContentCache(wikiContentCache);
watcher.OnWikiChanged += (projectName, _) =>
    wikiContentCache.Invalidate(projectName, WikiContentCache.InvalidationSource.Watcher);

// TaskAccess layer (ADR-0024): force a synchronous first index read so
// boot-time disk problems surface here rather than on the first HTTP
// request. The host's other lifecycle calls (ReloadProjectAsync,
// ShutdownAsync) are wired through the typed interface and used by
// callers, not at startup.
_ = app.Services.GetRequiredService<AgentStudio.TaskAccess.ITaskAccessHost>()
    .BootAsync();

// Pre-warm AgentMessageBusStore projections for every watched project
// BEFORE the HTTP listener starts. The grouped-jobs endpoint folds in
// per-project token totals (BuildTokenLookup -> SummarizePerJob ->
// BusTokenEntryConverter.LoadOrchestratorEntries -> Store.Query ->
// GetOrLoad), so a cold projection forces the first /api/tasks/grouped
// caller to wait for tens of seconds while a multi-megabyte JSONL tree
// is parsed. On real workspaces (Runbook ~ 100MB / >100k lines) that
// lazy-load wedges the post-restart UpdateVerifier window — the verifier
// sees /healthz=200 but /api/tasks/grouped never drains. Paying the
// parse cost here moves the cost out of the first request. Per-project
// warmups run in parallel so total boot time is bounded by the slowest
// project rather than the sum.
{
    var warmupSw = System.Diagnostics.Stopwatch.StartNew();
    var workspaceRoot = app.Configuration["TaskRepository"];
    var scannerForWarmup = app.Services.GetRequiredService<TaskScannerService>();
    var busStore = app.Services.GetRequiredService<AgentMessageBusStore>();
    var warmupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
        warmupLogger.LogInformation("bus-warmup-skipped reason=TaskRepository-unset");
    }
    else
    {
        var projects = scannerForWarmup.GetWatchPaths()
            .Select(e => e.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Parallel.ForEach(projects, project =>
            {
                try
                {
                    var n = busStore.WarmProject(workspaceRoot!, project);
                    counts[project] = n;
                }
                catch (Exception ex)
                {
                    failures[project] = ex.GetType().Name + ": " + ex.Message;
                }
            });
        }
        catch (Exception ex)
        {
            crashRecorder.Record("BusProjectionWarmup", ex);
        }
        warmupSw.Stop();
        var totalMessages = counts.Values.Sum();
        warmupLogger.LogInformation(
            "bus-warmup-complete projects={ProjectCount} messages={MessageCount} failures={FailureCount} elapsedMs={ElapsedMs}",
            counts.Count, totalMessages, failures.Count, warmupSw.ElapsedMilliseconds);
        foreach (var kv in failures)
        {
            warmupLogger.LogWarning("bus-warmup-failure project={Project} error={Error}", kv.Key, kv.Value);
        }
    }
}

// Best-effort Codex model-catalog warm-up (AGT-2025). Publishing the detected
// default (gpt-5.6-* when the installed CLI advertises it) into
// ModelMetadataRegistry makes new-task creation resolve the current default
// before the first UI catalog fetch. Fire-and-forget so a slow or absent codex
// CLI never delays boot; discovery's own disk-cache TTL means a warm cache
// skips the PTY spawn, and any failure just leaves the gpt-5.5 baseline in
// place. Skipped under the test host and when explicitly opted out so the
// integration suite never spawns a real codex process.
if (!app.Environment.IsEnvironment("Test")
    && !app.Environment.IsEnvironment("Testing")
    && !publicDemoExecutionProfile
    && app.Configuration.GetValue("CodexModels:WarmupOnBoot", true))
{
    _ = Task.Run(async () =>
    {
        var codexWarmupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var router = app.Services.GetRequiredService<CliRouter>();
            var catalog = await router.Get(CliTypes.Codex).GetModelCatalogAsync(false, cts.Token);
            codexWarmupLogger.LogInformation(
                "codex-model-warmup-complete models={ModelCount} detectedDefault={DetectedDefault} source={Source}",
                catalog.Models?.Count ?? 0,
                ModelMetadataRegistry.DetectedCodexDefault ?? "<none>",
                catalog.Source);
        }
        catch (Exception ex)
        {
            codexWarmupLogger.LogInformation(
                "codex-model-warmup-skipped reason={Reason}", ex.GetType().Name + ": " + ex.Message);
        }
    });
}

// Wire TaskTransitionService move events to atomically clear the per-project
// runner's _activeJobId when the active job is moved out of 3-progress.
// Without this, an external move (API or otherwise) leaves the runner pinned
// to a slug whose folder has left the lane, every pickup tick short-circuits
// on `active != null`, and the project wedges until backend restart.
var transitionsForRunner = app.Services.GetRequiredService<TaskTransitionService>();
var runnerForTransitions = app.Services.GetRequiredService<TaskRunnerService>();
transitionsForRunner.OnJobMoved += (projectName, jobId, fromState, toState) =>
{
    if (fromState != TaskStates.Progress) return;
    runnerForTransitions.ClearActiveJobForProject(
        projectName, jobId,
        $"job moved out of 3-progress externally ({fromState} -> {toState})");
};

// PUB-2 automation ladder. Package targets are clamped to suggest; only the
// website auto rung subscribes to acceptance and waits for the asynchronous
// integration merge before dispatching the existing deploy workflow.
var publishActionsForTransitions = app.Services.GetRequiredService<PublishActionService>();
transitionsForRunner.OnJobMoved += (projectName, jobId, _, toState) =>
{
    if (toState == TaskStates.Completed)
        publishActionsForTransitions.HandleTaskAccepted(projectName, jobId);
};

// Defensive: when a non-API task change touches the watch tree, reconcile only
// the affected project's active runner against its captured task folder. This
// deliberately avoids a global FindJob/index scan on the watcher callback.
watcher.OnJobChanged += path => runnerForTransitions.ReconcileRunnerForPath(path);

// Wire up CLI events → SignalR push (across all CLI backends via the router)
var cliRouter = app.Services.GetRequiredService<CliRouter>();
var wikiAgentReads = app.Services.GetRequiredService<AgentStudio.Docs.WikiAgentReadService>();
cliRouter.OnOutput += (cliType, jobId, line) =>
{
    try { wikiAgentReads.ProcessOutput(jobId, new[] { line }); }
    catch (Exception ex) { SilentCatch.Note(ex, "WikiAgentReadService: live CLI output attribution failed."); }
    _ = TaskEventClients(jobId).SendAsync("cliOutput", jobId, line.Text, line.Stream, line.Timestamp, cliType);
};
cliRouter.OnStarted += (cliType, jobId, exec) =>
    TaskEventClients(jobId).SendAsync("cliStarted", jobId, exec.ProcessId, exec.StartedAt, cliType);
cliRouter.OnFinished += (cliType, jobId, exec) =>
    TaskEventClients(jobId).SendAsync("cliFinished", jobId, exec.ExitCode, exec.DurationSeconds, exec.Status, cliType);
// Plan strip live push: when the agent emits a TodoWrite / update_plan frame the
// runner persists a snapshot; tell the open detail view to re-fetch /plan. Uses
// the same job identifier as cliOutput so the frontend correlates identically.
cliRouter.OnRunEvent += (cliType, jobId, evt) =>
{
    if (evt is CliRunEvent.PlanUpdated)
        TaskEventClients(jobId).SendAsync("planUpdated", jobId, cliType);
};

// Per-CLI startup hook. Claude / Codex / Gemini reap orphans - see
// GenericCliExecutionService.ReattachOnStartup. Must run before any new CLI run
// is started so we never have two processes editing the same repo.
//
// Skipped under a test host: a WebApplicationFactory<Program> in-process boot
// has no "previous backend run" of its own to reap, and the reaper reads a
// PROCESS-SHARED active-jobs file (GetActiveJobsPath falls back to
// <bin>/runtime/active-jobs-*.json when TaskRepository is unset) and calls
// Process.Kill(entireProcessTree) on the recorded PIDs. A stale entry left by
// an earlier run whose PID has since been recycled - on Linux, potentially to
// the test host itself - would be blind-killed, taking the whole run down with
// a "died silently" signature. Boot orphan-reaping belongs to the real,
// long-lived backend, not to ephemeral test boots.
if (!underTestHost)
    cliRouter.ReattachAll();
// A detached ng/esbuild helper is no longer reachable from its original CLI
// PID and therefore has no useful active-jobs entry. At boot there are no live
// runs yet, so reclaim helpers whose command line still points into an
// ephemeral task worktree before pickup starts.
var worktreeOrphanLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("WorktreeOrphanBootSweep");
if (underTestHost || Environment.GetEnvironmentVariable("ATP_DEV_BACKEND_FROM_FIXTURE") == "1")
{
    // A worktree-path sweep classifies processes by command line and kills the
    // matches - it would classify and kill its own harness: the Playwright node
    // launcher in fixture mode, or the xunit test host under a
    // WebApplicationFactory<Program> boot. Never sweep in either case.
    worktreeOrphanLogger.LogInformation(
        "worktree-orphan-boot-sweep-skipped reason={Reason}",
        underTestHost ? "test-host" : "playwright-fixture");
}
else
{
    WindowsWorktreeOrphanSweeper.Sweep(worktreeOrphanLogger);
}

// Wire up Runner status → SignalR push
var taskRunner = app.Services.GetRequiredService<TaskRunnerService>();
taskRunner.OnRunnerStatusChanged += (projectName, status) =>
    ProjectEventClients(projectName).SendAsync("runnerStatusChanged", projectName, status.Mode, status.ActiveJobId);

app.MapAllEndpoints();
app.MapConversationEndpoints();
app.MapHub<TaskHub>("/hubs/jobs");
PublicDemoExecutionRouteInventory.ValidateStartup(
    app,
    app.Services.GetRequiredService<StartupExecutionAdmission>());

app.Run();
