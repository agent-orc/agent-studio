

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;

namespace AgentStudio.Runner;

public sealed record RunnerCircuitBreakerOptions(
    int PerTaskFailureThreshold,
    TimeSpan GlobalCooldownBase,
    double GlobalCooldownBackoffMultiplier,
    TimeSpan GlobalCooldownMax)
{
    public static RunnerCircuitBreakerOptions Default { get; } = new(
        ProjectRunner.AutoFailureHaltThreshold,
        TimeSpan.FromMinutes(20),
        2.0,
        TimeSpan.FromMinutes(60));

    public RunnerCircuitBreakerOptions Normalize()
    {
        var threshold = PerTaskFailureThreshold <= 0
            ? Default.PerTaskFailureThreshold
            : PerTaskFailureThreshold;
        var baseCooldown = GlobalCooldownBase <= TimeSpan.Zero
            ? Default.GlobalCooldownBase
            : GlobalCooldownBase;
        var multiplier = GlobalCooldownBackoffMultiplier < 1.0
            ? Default.GlobalCooldownBackoffMultiplier
            : GlobalCooldownBackoffMultiplier;
        var max = GlobalCooldownMax < baseCooldown ? baseCooldown : GlobalCooldownMax;
        return this with
        {
            PerTaskFailureThreshold = threshold,
            GlobalCooldownBase = baseCooldown,
            GlobalCooldownBackoffMultiplier = multiplier,
            GlobalCooldownMax = max
        };
    }

    public static RunnerCircuitBreakerOptions FromConfig(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var section = config.GetSection("Runner:CircuitBreaker");
        return new RunnerCircuitBreakerOptions(
            section.GetValue<int?>("PerTaskFailureThreshold") ?? Default.PerTaskFailureThreshold,
            TimeSpan.FromMinutes(section.GetValue<double?>("GlobalCooldownMinutes") ?? Default.GlobalCooldownBase.TotalMinutes),
            section.GetValue<double?>("GlobalCooldownBackoffMultiplier") ?? Default.GlobalCooldownBackoffMultiplier,
            TimeSpan.FromMinutes(section.GetValue<double?>("GlobalCooldownMaxMinutes") ?? Default.GlobalCooldownMax.TotalMinutes))
            .Normalize();
    }
}

/// <summary>
/// Per-project runner: owns the lifecycle state for one watched workspace
/// (active job, mode, processing flag) and applies the side-effects of one
/// CLI invocation. The decision tree itself lives in <see cref="RunPlanner"/>;
/// this class is intentionally thin so the lifecycle and the planning concerns
/// can be read and tested independently.
/// </summary>
public class ProjectRunner
{
    private const string AgentCliFooterUsageSource = "AGENT (CLI FOOTER) / reported";
    private const string AgentSessionTranscriptUsageSource = "AGENT (SESSION TRANSCRIPT) / reconstructed";
    private static readonly Regex CompactTokenValueRegex = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<suffix>[kKmM])?",
        RegexOptions.Compiled);
    private static readonly Regex GitMutationCommandRegex = new(
        @"(?:^|[`""'\s>$:])git\s+(?:-C\s+\S+\s+)?(?:commit|push)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitMutationClaimRegex = new(
        @"\b(?:i(?:'ve| have)?|changes?\s+(?:were|are)?|work\s+(?:was|is)?)\s*(?:committed|pushed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NegatedGitMutationRegex = new(
        @"\b(?:did not|didn't|do not|don't|without|not going to|never)\b.{0,60}\b(?:git\s+)?(?:commit|push|committed|pushed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitPushCommandOrClaimRegex = new(
        @"(?:^|[`""'\s>$:])git\s+(?:-C\s+\S+\s+)?push\b|\b(?:i(?:'ve| have)?|changes?\s+(?:were|are)?|work\s+(?:was|is)?)\s*pushed\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record CoreAgentUsage(
        string? Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        string Source);

    private readonly ILogger _logger;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskSessionLog _sessions;
    // ADR-0049: per-job timeline.jsonl ledger. Optional so existing test
    // fixtures keep working; production DI always supplies an instance.
    private readonly AgentStudio.Tasks.TimelineLog? _timeline;
    // Records the core agent run into pipeline-execution.json so the Overview
    // pipeline table shows the CORE "Agent execution" step live (Running at
    // spawn) and completed (Passed/Failed + duration/times) at exit, instead
    // of a permanent "- -". Optional so test fixtures that build the runner
    // directly keep working; production DI always supplies an instance.
    private readonly AgentStudio.Pipeline.PipelineExecutionLog? _pipelineLog;
    private readonly AgentStudio.Pipeline.IConceptWorkbenchPublisher? _conceptWorkbenchPublisher;
    private readonly AgentStudio.Pipeline.ModelQualificationService? _modelQualification;
    private readonly AgentStudio.ModelMigrations.ModelMigrationCoordinator? _modelMigrations;
    private readonly AgentStudio.Pipeline.IntegrationPushQueue? _integrationPushQueue;
    private readonly PromptEnrichmentService? _promptEnrichment;
    private readonly DossierMaintenanceService? _dossierMaintenance;
    private readonly VisualQaService? _visualQa;
    private readonly CliRouter _router;
    private readonly SummaryGenerationService _summaryService;
    private readonly RuntimePromptService _prompts;

    // Runtime prompt-registry template names. Every instruction/role prose
    // string ProjectRunner emits lives in one of these files under
    // prompts/runtime so the text is inspectable and project-overridable;
    // code only fills the named slots.
    public const string ConflictResolutionTemplate = "orchestrator-conflict-resolution.md";
    public const string ProjectBootTemplate = "orchestrator-project-boot.md";
    public const string DecisionResumeTemplate = "orchestrator-decision-resume.md";
    public const string DecisionAttachmentsResumeTemplate = "orchestrator-decision-attachments-resume.md";
    public const string DecisionOneshotTemplate = "orchestrator-decision-oneshot.md";
    public const string DecisionAttachmentsOneshotTemplate = "orchestrator-decision-attachments-oneshot.md";

    private readonly TaskTransitionService _transitions;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly TaskMutationService _mutations;
    private readonly OrchestratorRunner _orchestratorRunner;
    private readonly OrchestratorSessionStore _orchestratorSessions;
    private readonly ProjectSettingsService _projectSettings;
    /// <summary>
    /// AGT-1812: resolves the orchestrator model override through the two-tier
    /// config (project -> workspace default). Optional so existing test callers
    /// that construct a runner directly keep working; a null provider falls back
    /// to the project-only <see cref="ProjectSettings.OrchestratorModel"/>.
    /// </summary>
    private readonly AgentStudio.Registry.OrchestratorDefaultsProvider? _orchestratorDefaults;
    private readonly QuotaService _quotaService;
    private readonly CliQuotaCapsService _quotaCaps;
    private readonly CliQuotaWaitPolicyService? _quotaWaitPolicy;
    private readonly CliQuotaFallbackService? _quotaFallback;
    private readonly ProviderLimitRegistry _providerLimits;
    private readonly ILoadThrottleGate? _loadThrottle;
    private readonly GitService _git;
    private readonly PickupFailureLog _pickupFailures;
    private readonly CrossSlugInfraCircuitBreaker _infraBreaker;
    // The single funnel for SYSTEM-initiated moves into 5e-escalated (watchdog
    // kill, permission/environment block, auto-failure park, pickup zombie). It
    // pairs every move with an Escalate verdict in the decision journal and a
    // status.md stub, so an escalated card never lands verdict-less / blank. DI
    // always supplies it; test fixtures that build the runner directly may pass
    // null, in which case a workspace-less fallback is built (move + status stub
    // still fire; the journal append is skipped when no workspace root is known).
    private readonly HumanReviewEscalation _humanReviewEscalation;
    private readonly AgentMessageBusBridge? _bus;
    private readonly AgentStudio.TaskAccess.ITaskAccess _taskAccess;
    // "Intelligente Abbruch-Bewertung" (ADR-0032): the LLM abort-review step.
    // Optional + default-OFF per project. When null (every existing
    // construction site / test fixture) or disabled, OnCliFinishedAsync keeps
    // its byte-identical fixed terminal route to typed escalation. When wired AND
    // enabled, a non-clean run end consults the step before escalating.
    private readonly PostAbortReviewStepService? _postAbortReview;
    // Reads Claude session transcripts (~/.claude/projects) to reconstruct
    // token usage post-hoc when the CLI never reported a footer (always, for
    // Claude) or the run was killed before emitting one. Null in tests that
    // construct the runner without it.
    private readonly AgentStudio.Cli.ClaudeSessionInspector? _sessionInspector;
    // Per-job count of automatic abort-review reruns already spent. The
    // breaker: budget remaining = DefaultRerunBudget - used. Cleared when the
    // job leaves the loop (accepted, moved to review, or escalated).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _abortReviewRerunsUsed = new();
    // Per-job count of automatic completion-loop re-triggers already spent on
    // transient (watchdog) aborts. The breaker: budget remaining =
    // CompletionRetriggerDecider.DefaultBudget - used. Cleared when the job
    // leaves the run loop (moved to acceptance review, or escalated for intervention).
    // Loop id completion.retrigger-transient-abort-per-job.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _completionRetriggerUsed = new();
    private string _mode = "manual";
    // The human-readable reason recorded the last time _mode changed, plus
    // when the change happened. Surfaced via ProjectRunnerStatus so the
    // board can render a different pill (PAUSED vs MANUAL) when the mode
    // was flipped by a circuit-breaker or supervisor rather than by the
    // operator.
    private string? _modeReason;
    private DateTime? _modeChangedAt;
    private string? _modeSource;
    // Set when the CLI-unspawnable pickup pause forced auto -> manual: holds
    // the CLI type whose recovery we are waiting for. While set (and the mode
    // is still manual), TickCliRecoveryResume probes the CLI at most once per
    // minute and restores the operator's DesiredRunnerMode as soon as the CLI
    // spawns again — a transient CLI break (half-healed npm shim, mid-update)
    // must degrade the runner temporarily, not permanently. Cleared by ANY
    // explicit SetMode (an operator/system decision supersedes the pending
    // auto-resume) and after a successful resume. Deliberately NOT set by the
    // circuit-breaker paths — those own their own cooldown semantics.
    private string? _autoResumeCliAfterPause;
    private DateTime _autoResumeNextProbeUtc = DateTime.MinValue;
    private RunnerCircuitBreakerOptions _circuitBreakerOptions = RunnerCircuitBreakerOptions.Default;
    private DateTime? _globalBreakerCooldownUntil;
    private string? _globalBreakerReason;
    private int _globalBreakerTripCount;
    // Slice P (ASS-1663): last reason the build-profile onboarding gate blocked
    // auto-pickup, so the tick logs the block once on change instead of every
    // tick. Null when the gate is open.
    private string? _lastBuildGateBlockReason;
    // Backend role (orchestrator vs test-subject). Set in the ctor from
    // Runner:Role config; defaults to Orchestrator so an unconfigured backend
    // behaves like stable rather than silently going dark. TestSubject skips
    // the auto-pickup branch in TickAsync entirely - explicit start endpoints
    // still work so Playwright fixtures can drive a specific job on demand.
    private readonly RunnerRole _role;
    // Disk-backed lock primitive: stamps .pickup-lock.json on the job folder
    // at spawn time so a second backend sharing the same workspace skips
    // rather than races. Null in tests that don't need the cross-process
    // belt-and-braces (single-process unit tests).
    private readonly PickupLockFile? _pickupLock;
    private readonly PickupLockOwner? _pickupLockOwner;
    private readonly IntegrationLeaseService? _integrationLeases;
    private string? _activePickupLockFolder;
    // Deferred mode: when SetMode(manual|paused) arrives while a job is
    // active, store the requested mode here, leave _mode at its auto-* value,
    // and apply on the next active-job clear. Lets the operator say "stop
    // after this finishes" without killing the run, while keeping the
    // semantics of the response visible in the status payload. Null when no
    // change is pending.
    private string? _pendingMode;
    private string? _pendingModeReason;
    private string? _pendingModeWillApplyAfter;
    private readonly HashSet<string> _pendingModeDrainJobIds = new(StringComparer.Ordinal);
    private readonly object _modeChangeGate = new();
    // ADR-0052 slice 2: the former single-active scalar fields
    // (_activeJobId/_activeCliType/_activeIntent/_activeFollowup/_activePlan/
    // _activeReissueAttempt) are consolidated into this slot registry — the
    // single point of change for the admit latch + run state. At
    // MaxParallelism==1 it holds at most one run (byte-identical to the old
    // latch); going to N slots is localized to ActiveRuns.
    private readonly ActiveRuns _activeRuns = new();
    // Transitional read accessors over the registry so the many read-sites stay
    // byte-identical at MaxParallelism==1; assignment sites are migrated to
    // _activeRuns.TryClaim / Release / Get. (Per-run read-correctness for N>1 is
    // wired in the admission + worktree slices, where the single-active reads
    // are replaced by per-job lookups.)
    private string? _activeJobId => _activeRuns.SingleJobId;
    private string? _activeCliType => _activeRuns.Single?.CliType;
    private RunIntent _activeIntent => _activeRuns.Single?.Intent ?? default;
    private string? _activeFollowup => _activeRuns.Single?.Followup;
    private RunPlan? _activePlan => _activeRuns.Single?.Plan;
    private int _activeReissueAttempt => _activeRuns.Single?.ReissueAttempt ?? 0;
    private bool _processing;
    // ASS-1753: latches the one-shot post-restart slot reconcile. A backend
    // restart clears _activeRuns, but the CLI router can still own live runs
    // for this project (a CLI that reattaches on startup, or a process that
    // outlived the restart and is still tracked). The first tick re-books those
    // into the registry so occupied slots == genuinely-tracked live runs; after
    // that the normal claim/release path keeps the count accurate.
    private bool _recoveredRunsReconciled;
    // ADR-0052 slice 2: worktree-isolated parallel execution. Lazily built from
    // the injected GitService. _integrateLock is the per-project merge-queue:
    // it serializes integrations into the work branch so concurrent slot merges
    // never race. Only engaged when MaxParallelism > 1; the max==1 path never
    // touches a worktree (byte-identical to the sequential runner).
    private WorktreeTaskLifecycle? _worktreeLifecycle;
    private WorktreeTaskLifecycle Worktree => _worktreeLifecycle ??=
        new WorktreeTaskLifecycle(_git, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorktreeTaskLifecycle>.Instance);
    private readonly System.Threading.SemaphoreSlim _integrateLock = new(1, 1);
    // ADR-0052: the pick-gate rationale recorded the last time a task was
    // admitted into a runner slot. Surfaced on the status payload + the
    // runner_slot_admission timeline event so the UI can show the pick
    // decision + slot occupancy. Pure observability; never gates execution.
    private string? _lastPickReason;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pendingPickReasons = new(StringComparer.Ordinal);
    // AGT-2055: last emitted pre-launch quota decision per job, so a card that
    // waits/throttles across many pickup ticks records ONE timeline + feed
    // entry per decision change instead of one per tick.
    private readonly Dictionary<string, string> _lastAdmissionDecisionByJob = new(StringComparer.Ordinal);
    // Run intent / follow-up / plan / reissue-attempt now live on the
    // ActiveRun record inside _activeRuns (see ActiveRuns.cs), so OnCliFinished
    // reads them per-run rather than from shared single-active scalars.
    // Suppression state for repeated meta messages. When the same heuristic
    // verdict fires twice in a row in Recovery, we skip the second meta
    // message so the chat does not pile orchestrator notes on a stuck run.
    private string? _lastMetaSignature;

    // Per-job stuck-loop counters. The auto-mode loop (agent emits
    // NEEDS_INPUT -> orchestrator decides -> reply re-issued as Continue)
    // can in theory run forever if the agent keeps asking. We track
    // iterations + cumulative orchestrator tokens per job and let
    // StuckLoopGuard decide when to break the loop. State lives in
    // memory only; a backend restart resets it (a restart is itself a
    // recovery boundary, so that's the desired behavior).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StuckLoopState> _stuckLoops = new();
    private StuckLoopBudget _stuckLoopBudget = StuckLoopBudget.Default;

    // Consecutive auto-pickup failures. Auto-mode flips back to manual when
    // this hits the threshold so a single bad event (mid-flight kill, rotated
    // session id, watchdog regression) cannot cascade through every queued
    // job. Reset on any auto-issued run that reaches Review.
    private int _consecutiveAutoFailureCount;

    // Per-job latch: have we already issued the sentinel-detected stop for
    // this run? claude-code can emit multiple TurnCompleted frames in a
    // single run if the model produces several result-shaped responses; we
    // only want to kill once on the first sentinel-bearing one. Cleared on
    // ProcessExited / Killed in OnRunEventReceived.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sentinelStopRequested = new();
    internal const int AutoFailureHaltThreshold = 3;
    // Job ids of the recent auto-failures, kept so the halt message can name
    // the offenders without re-scanning.
    private readonly Queue<string> _recentAutoFailureJobIds = new();

    // Distinct tasks that have each failed AutoFailureHaltThreshold times and
    // been parked in 5e-escalated. A single bad task is parked and auto-mode
    // CONTINUES with the next task (no project-wide halt on one offender). Only
    // a systemic pattern - AutoFailureDistinctTaskHaltThreshold distinct tasks
    // parked this way without a success in between ("3x3") - flips the project
    // to manual. Reset on any auto-issued run that reaches Review.
    private readonly HashSet<string> _parkedFailedJobIds = new(StringComparer.Ordinal);
    internal const int AutoFailureDistinctTaskHaltThreshold = 3;

    // Per-job consecutive capture-fail counter. A capture-fail run is one
    // that exited without claude/codex/gemini emitting a usable session id
    // - the prior run's UUID is dead, can't be resumed, and we have no new
    // UUID to chain to. The recovery-marker write that follows is the
    // semantic fix; this counter is the secondary stop-gap so a structural
    // failure (planner re-reads stale cache, scanner returns a snapshot
    // taken before the recovery write completed, etc.) cannot loop forever.
    private string? _consecutiveCaptureFailJobId;
    private int _consecutiveCaptureFailCount;
    internal const int CaptureFailHaltThreshold = 3;

    // Per-task anti-endless-reissue circuit breaker. Counts consecutive runs
    // for the same task that finished as a no-progress soft failure (did not
    // reach review, produced no commit, was not a deliberate stop) across BOTH
    // the auto-pickup run and the UserContinue re-issue it spawns - the
    // ping-pong that bypassed every existing breaker. Once a task reaches
    // QuarantineFailThreshold it is parked in 5e-escalated instead of being
    // re-issued again. Reset on any progress (a new commit) or on reaching
    // review. In-memory by design: a backend restart is a recovery boundary
    // and clears the streak, mirroring the capture-fail breaker above. See
    // RunQuarantineBreaker for the pure decision.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consecutiveFailNoProgress = new(StringComparer.Ordinal);
    internal const int QuarantineFailThreshold = RunQuarantineBreaker.DefaultFailThreshold;

    // Per-task rapid-crash governor (RapidCrashBreaker). Keyed by jobId. The
    // value is the UTC instant until which pickup must skip this task — an
    // exponential backoff armed on each rapid crash so the few retries that
    // precede the quarantine park cannot tight-loop and saturate the host
    // (incident 2026-06-07). In-memory only: a backend restart resets it,
    // acceptable now that the build-process leak that crashed the backend
    // mid-loop is fixed. The crash COUNT rides on _consecutiveFailNoProgress
    // so the existing quarantine route parks the task after the threshold.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _rapidCrashBackoffUntil = new(StringComparer.Ordinal);

    private sealed class RevertLogState
    {
        public DateTime LastEmittedUtc;
        public int Suppressed;
    }

    private static readonly TimeSpan RevertLogInterval = TimeSpan.FromMinutes(10);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RevertLogState> _revertLogStates = new(StringComparer.Ordinal);

    private bool IsInRapidCrashBackoff(string jobId)
        => _rapidCrashBackoffUntil.TryGetValue(jobId, out var until) && until > DateTime.UtcNow;

    private static string? ResolveConfiguredRemoteRunnerId(ProjectSettings settings)
    {
        var location = ProjectExecutionPolicy.ResolveExecutionLocation(settings);
        return location == ExecutionLocations.Local ? null : location;
    }

    private string ResolveCliExecutionEngine()
    {
        var resolution = _orchestratorDefaults?.ResolveCliExecutionEngine(ProjectName)
            ?? AgentStudio.Registry.OrchestratorSettingsResolver.ResolveCliExecutionEngine(
                _projectSettings.Get(ProjectName),
                workspace: null);
        _logger.LogInformation(
            "[taskboard] CLI execution engine for {Project}: {ExecutionEngine} ({Source})",
            ProjectName,
            resolution.ExecutionEngine,
            resolution.Source);
        return resolution.ExecutionEngine;
    }

    // Continuous decision review: while a job sits in 3-progress, we scan
    // its live output buffer every tick for an unresolved interruptive
    // sentinel ([[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]]). The latch is
    // cleared when the active job changes, when the scan returns null
    // (resolved or no longer present), or on backend restart. Surfaced
    // via GET /api/runner/{project}/pending-decisions so the project
    // view can render a prominent banner. See ADR-0027 and
    // docs/research/orchestrator-decision-protocol-2026-05.md.
    private PendingDecisionEntry? _activePendingDecision;
    private readonly object _pendingDecisionLock = new();

    public string ProjectName { get; }
    public WatchPathEntry Entry { get; }

    public event Action<ProjectRunnerStatus>? OnStatusChanged;
    /// <summary>
    /// Raised whenever the mode changes through any path (explicit
    /// <see cref="SetMode"/> or implicit auto-single → manual revert). The
    /// arguments are the new mode and its <see cref="ClassifyModeSource"/>
    /// classification (<c>user</c> / <c>circuit-breaker</c> / <c>supervisor</c>
    /// / <c>system</c>). The source lets the persistence layer tell an operator
    /// toggle apart from a system-driven flip (update-quiesce, circuit-breaker)
    /// so the latter does not clobber the operator's durable mode intent
    /// (ASS-1753). Wired by <see cref="TaskRunnerService"/> to persist the new
    /// mode. Restoration via <see cref="RestoreMode"/> does NOT fire this event.
    /// </summary>
    public event Action<string, string>? OnModePersist;

    public ProjectRunner(
        string projectName,
        WatchPathEntry entry,
        ILogger logger,
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskSessionLog sessions,
        CliRouter router,
        SummaryGenerationService summaryService,
        RuntimePromptService prompts,
        TaskTransitionService transitions,
        OrchestratorChatLog chatLog,
        TaskMutationService mutations,
        OrchestratorLog orchestratorLog,
        OrchestratorRunner orchestratorRunner,
        OrchestratorSessionStore orchestratorSessions,
        ProjectSettingsService projectSettings,
        QuotaService quotaService,
        CliQuotaCapsService quotaCaps,
        GitService git,
        PickupFailureLog pickupFailures,
        CrossSlugInfraCircuitBreaker infraBreaker,
        AgentStudio.TaskAccess.ITaskAccess taskAccess,
        AgentMessageBusBridge? bus = null,
        RunnerRole role = RunnerRole.Orchestrator,
        PickupLockFile? pickupLock = null,
        PickupLockOwner? pickupLockOwner = null,
        IntegrationLeaseService? integrationLeases = null,
        AgentStudio.Tasks.TimelineLog? timeline = null,
        AgentStudio.Pipeline.PipelineExecutionLog? pipelineLog = null,
        HumanReviewEscalation? humanReviewEscalation = null,
        PostAbortReviewStepService? postAbortReview = null,
        AgentStudio.Cli.ClaudeSessionInspector? sessionInspector = null,
        AgentStudio.Registry.OrchestratorDefaultsProvider? orchestratorDefaults = null,
        CliQuotaFallbackService? quotaFallback = null,
        ILoadThrottleGate? loadThrottle = null,
        AgentStudio.Pipeline.ModelQualificationService? modelQualification = null,
        AgentStudio.Pipeline.IntegrationPushQueue? integrationPushQueue = null,
        CliQuotaWaitPolicyService? quotaWaitPolicy = null,
        AgentStudio.Pipeline.IConceptWorkbenchPublisher? conceptWorkbenchPublisher = null,
        PromptEnrichmentService? promptEnrichment = null,
        DossierMaintenanceService? dossierMaintenance = null,
        VisualQaService? visualQa = null,
        ProviderLimitRegistry? providerLimits = null,
        AgentStudio.ModelMigrations.ModelMigrationCoordinator? modelMigrations = null)
    {
        ProjectName = projectName;
        Entry = entry;
        _logger = logger;
        _scanner = scanner;
        _states = states;
        _sessions = sessions;
        _router = router;
        _summaryService = summaryService;
        _prompts = prompts;
        _transitions = transitions;
        _chatLog = chatLog;
        _mutations = mutations;
        _orchestratorLog = orchestratorLog;
        _orchestratorRunner = orchestratorRunner;
        _orchestratorSessions = orchestratorSessions;
        _projectSettings = projectSettings;
        _orchestratorDefaults = orchestratorDefaults;
        _quotaService = quotaService;
        _quotaCaps = quotaCaps;
        _quotaFallback = quotaFallback;
        _quotaWaitPolicy = quotaWaitPolicy;
        _providerLimits = providerLimits ?? new ProviderLimitRegistry();
        _loadThrottle = loadThrottle;
        _git = git;
        _pickupFailures = pickupFailures;
        _infraBreaker = infraBreaker;
        _taskAccess = taskAccess;
        // DI supplies the funnel; tests that construct the runner directly may
        // not. The fallback wires the same state-machine + transition service so
        // the move (and its OnJobMoved side-effects / status stub) still fire;
        // without a configured workspace root the journal verdict is skipped.
        _humanReviewEscalation = humanReviewEscalation
            ?? new HumanReviewEscalation(states, transitions, workspaceRoot: null, logger);
        _bus = bus;
        _role = role;
        _pickupLock = pickupLock;
        _pickupLockOwner = pickupLockOwner;
        _integrationLeases = integrationLeases;
        _timeline = timeline;
        _pipelineLog = pipelineLog;
        _modelQualification = modelQualification;
        _modelMigrations = modelMigrations;
        _integrationPushQueue = integrationPushQueue;
        _conceptWorkbenchPublisher = conceptWorkbenchPublisher;
        _promptEnrichment = promptEnrichment;
        _dossierMaintenance = dossierMaintenance;
        _visualQa = visualQa;
        _postAbortReview = postAbortReview;
        _sessionInspector = sessionInspector;

        // Listen across all CLI backends for completion of the active job.
        _router.OnFinished += (cliType, jobKey, exec) => OnCliFinished(cliType, jobKey, exec);
        // ADR-0013: typed events drive the phase-aware watchdog. Each
        // adapter advances the phase as its native protocol moves; the
        // runner stores per-job phase + last-event timestamp and uses
        // PhaseAwareWatchdog.DecideState below.
        _router.OnRunEvent += (_, jobKey, evt) => OnRunEventReceived(jobKey, evt);
    }

    public void ConfigureCircuitBreaker(RunnerCircuitBreakerOptions options)
    {
        _circuitBreakerOptions = options.Normalize();
    }

    /// <summary>
    /// Backend role assigned at construction time. Read-only after the runner
    /// is built; the role is a process-wide policy decided from
    /// <c>Runner:Role</c> config, not a per-call mutation.
    /// </summary>
    public RunnerRole Role => _role;

    /// <summary>
    /// True while the operator's last mode-change request is waiting on the
    /// request-time active task set to drain. The deferred value is exposed via
    /// <see cref="ProjectRunnerStatus.PendingMode"/> and applied automatically
    /// by <see cref="ApplyPendingModeIfAny"/> when the last snapshot task clears.
    /// </summary>
    public bool HasPendingMode => _pendingMode != null;

    /// <summary>
    /// Mutate the runner's auto-pickup mode and persist it. <paramref name="reason"/>
    /// is the human-readable cause that lands in the structured log so the
    /// "why did the runner flip" question is answerable from the day's log
    /// alone (F16). Default reason names the API toggle, since that is the
    /// only path that calls <c>SetMode</c> without supplying its own
    /// motivation.
    /// </summary>
    public void SetMode(string mode, string? reason = null)
    {
        var fromMode = _mode;
        _mode = mode;
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "api-toggle" : reason!;
        _modeReason = effectiveReason;
        _modeChangedAt = DateTime.UtcNow;
        var modeSource = ClassifyModeSource(effectiveReason);
        _modeSource = modeSource;
        // A direct, fully-applied SetMode supersedes any deferred change still
        // waiting on the active job. Clear the pending slot so the status DTO
        // does not advertise a "MANUAL (after current)" pill that will never
        // fire because the live mode just moved past it.
        lock (_modeChangeGate)
        {
            _pendingMode = null;
            _pendingModeReason = null;
            _pendingModeWillApplyAfter = null;
            _pendingModeDrainJobIds.Clear();
        }
        // Any explicit mode change also supersedes a pending CLI-recovery
        // auto-resume (the pause path re-arms the marker right after its own
        // SetMode call).
        _autoResumeCliAfterPause = null;
        _logger.LogInformation(
            "Runner '{Project}' mode '{From}' -> '{To}' because '{Reason}' (source={Source})",
            ProjectName, fromMode, mode, effectiveReason, _modeSource);
        try { OnModePersist?.Invoke(mode, modeSource); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnModePersist subscriber threw for {Project}", ProjectName); }
        NotifyStatus();
    }

    /// <summary>
    /// Restores the operator's durable mode after a CLI-unspawnable pickup
    /// pause, once the CLI spawns again. Armed only by the spawn-failure pause
    /// path (never by circuit-breaker trips, which own their cooldown
    /// semantics). Probes at most once per minute because the availability
    /// check launches <c>&lt;cli&gt; --version</c>. The resume target is
    /// <see cref="ProjectSettings.DesiredRunnerMode"/> — the value the boot
    /// restore would use — so a restart and an in-place recovery land in the
    /// same mode. Public-ish via TickAsync; internal for tests.
    /// </summary>
    internal void TickCliRecoveryResume()
    {
        var cli = _autoResumeCliAfterPause;
        if (cli == null) return;
        if (_mode != "manual") { _autoResumeCliAfterPause = null; return; }
        var now = DateTime.UtcNow;
        if (now < _autoResumeNextProbeUtc) return;
        _autoResumeNextProbeUtc = now.AddSeconds(60);

        string? desired;
        try { desired = _projectSettings.Get(ProjectName).DesiredRunnerMode; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI-recovery resume: could not read settings for {Project}", ProjectName);
            return;
        }
        if (desired is not ("auto-single" or "auto-continuous"))
        {
            // No durable auto intent to restore — disarm instead of probing forever.
            _autoResumeCliAfterPause = null;
            return;
        }

        bool available;
        try { available = _router.Get(cli).IsAvailable(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI-recovery resume: availability probe for '{Cli}' threw", cli);
            return;
        }
        if (!available) return;

        _logger.LogInformation(
            "Runner '{Project}': the {Cli} CLI is available again; restoring desired mode '{Mode}' after the pickup pause",
            ProjectName, cli, desired);
        // SetMode clears the marker; reason starts with "auto-resume" so
        // ClassifyModeSource files it as system and DesiredRunnerMode stays
        // untouched.
        SetMode(desired!, $"auto-resume: the {cli} CLI is available again after the pickup pause");
    }

    /// <summary>
    /// Operator-initiated mode change with the deferred-on-active semantics
    /// (the rule the API endpoint applies):
    /// <list type="bullet">
    ///   <item>When no job is active <i>or</i> the new mode is one of the
    ///   <c>auto-*</c> values, the change applies immediately via
    ///   <see cref="SetMode"/> and the result is <see cref="ModeChangeOutcome.Applied"/>.</item>
    ///   <item>When a job is active and the requested mode is <c>manual</c> or
    ///   <c>paused</c>, the live mode is left alone and the requested mode is
    ///   queued; auto admission closes immediately, and the current active task
    ///   set is snapshotted. The queued value applies after the last task in that
    ///   snapshot clears.</item>
    /// </list>
    /// Invalid mode strings produce <see cref="ModeChangeOutcome.Invalid"/>; the
    /// caller (typically <see cref="AgentStudio.Runner.TaskRunnerService.SetMode"/>)
    /// turns that into a 400.
    /// </summary>
    public ModeChangeResult RequestModeChange(string mode, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return new ModeChangeResult(ModeChangeOutcome.Invalid, _mode, null, null);
        var isManualSide = mode is "manual" or "paused";
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "api-toggle" : reason!;
        var activeJobIds = _activeRuns.Snapshot().Select(run => run.JobId).ToArray();
        if (activeJobIds.Length > 0 && isManualSide && _mode is "auto-single" or "auto-continuous")
        {
            bool alreadyDrained;
            lock (_modeChangeGate)
            {
                _pendingMode = mode;
                _pendingModeReason = effectiveReason + " (deferred until active tasks drain)";
                _pendingModeDrainJobIds.Clear();
                foreach (var jobId in activeJobIds) _pendingModeDrainJobIds.Add(jobId);
                _pendingModeDrainJobIds.RemoveWhere(jobId => !_activeRuns.HoldsExecutionSlot(jobId));
                alreadyDrained = _pendingModeDrainJobIds.Count == 0;
                _pendingModeWillApplyAfter = _pendingModeDrainJobIds.Count == 1
                    ? _pendingModeDrainJobIds.First()
                    : null;
            }
            if (alreadyDrained)
            {
                SetMode(mode, effectiveReason + " (active tasks drained during mode request)");
                return new ModeChangeResult(ModeChangeOutcome.Applied, _mode, null, null);
            }
            _logger.LogInformation(
                "Runner '{Project}' deferred mode change '{From}' -> '{To}'; draining {ActiveTaskCount} active task(s) and blocking new auto-picks (reason '{Reason}')",
                ProjectName, _mode, mode, activeJobIds.Length, effectiveReason);
            NotifyStatus();
            return new ModeChangeResult(ModeChangeOutcome.Deferred, _mode, mode, _pendingModeWillApplyAfter);
        }
        SetMode(mode, effectiveReason);
        return new ModeChangeResult(ModeChangeOutcome.Applied, _mode, null, null);
    }

    /// <summary>
    /// Advances the deferred-mode drain when one request-time active task clears. The
    /// caller (the same <c>finally</c> block that releases <c>_activeJobId</c>
    /// in <see cref="OnCliFinishedAsync"/> / <see cref="ClearActiveJobIfMatches"/>)
    /// pays one comparison when no defer is pending. When a defer is pending
    /// the recorded reason is preserved so the structured log still shows the
    /// original intent is preserved while the remaining snapshot count advances.
    /// </summary>
    private void ApplyPendingModeIfAny(string? clearedJobId)
    {
        string? pendingMode = null;
        string? reason = null;
        int remaining;
        lock (_modeChangeGate)
        {
            if (_pendingMode == null) return;
            if (clearedJobId != null) _pendingModeDrainJobIds.Remove(clearedJobId);
            remaining = _pendingModeDrainJobIds.Count;
            _pendingModeWillApplyAfter = remaining == 1 ? _pendingModeDrainJobIds.First() : null;
            if (remaining == 0)
            {
                pendingMode = _pendingMode;
                reason = _pendingModeReason ?? "deferred mode change applied after active tasks drained";
            }
        }
        if (remaining > 0)
        {
            _logger.LogInformation(
                "Runner '{Project}' deferred mode drain advanced after '{Job}'; {RemainingTaskCount} active task(s) remain",
                ProjectName, clearedJobId, remaining);
            NotifyStatus();
            return;
        }
        _logger.LogInformation(
            "Runner '{Project}' applying deferred mode '{Mode}' after the pending active-task set drained",
            ProjectName, pendingMode);
        SetMode(pendingMode!, reason);
    }

    /// <summary>
    /// Coarse classification of where a mode change came from so the board
    /// can render circuit-breaker-induced pauses differently from operator
    /// toggles. Kept as a static string lookup against the reason text the
    /// caller passes to <see cref="SetMode"/>; new circuit-breaker callsites
    /// only need to keep their reason text starting with "circuit-breaker"
    /// or containing "circuit-breaker:" to be recognised here.
    /// </summary>
    private static string ClassifyModeSource(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "system";
        if (reason.Contains("circuit-breaker", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("infra breaker", StringComparison.OrdinalIgnoreCase))
            return "circuit-breaker";
        if (reason.StartsWith("supervisor", StringComparison.OrdinalIgnoreCase))
            return "supervisor";
        // The update-service quiesces runners to manual before an update and
        // restores them afterwards (reason "update-quiesce" / "update-resume").
        // That transient flip is NOT operator intent, so it must classify as
        // system - otherwise the persistence layer would record manual as the
        // operator's durable mode and a failed/early-returning update would
        // leave auto-continuous clobbered across the restart (ASS-1753).
        if (reason.StartsWith("update-", StringComparison.OrdinalIgnoreCase))
            return "system";
        if (reason.StartsWith("api", StringComparison.OrdinalIgnoreCase))
            return "user";
        return "system";
    }

    /// <summary>
    /// Re-applies a previously saved mode at startup without re-firing the persist
    /// hook (the value already came from the store). Status is broadcast so any
    /// already-connected clients see the restored mode.
    /// </summary>
    public void RestoreMode(string mode)
    {
        _mode = mode;
        NotifyStatus();
    }

    /// <summary>
    /// Cheap read-only check against the latest cached quota snapshot for
    /// <paramref name="cliType"/>. Returns "not blocked" when no snapshot is
    /// cached yet (the user hasn't loaded any quota in this session) - we
    /// prefer "let the run start" over "stall the queue waiting for a probe".
    /// </summary>
    public CapEvaluation EvaluateQuotaCap(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return CapEvaluation.NotBlocked;
        var snap = _quotaService.GetCachedFor(cliType);
        return _quotaCaps.Evaluate(snap);
    }

    /// <summary>
    /// AGT-2055: the <b>admission</b> quota view - strict cap OR a projected
    /// breach before the window resets. Used to route the pre-launch decision
    /// (so a primary about to hit the wall switches to its fallback early) while
    /// the strict <see cref="EvaluateQuotaCap"/> stays the sole trigger for
    /// stopping an already-running job. Cheap and non-blocking; safe on a tick.
    /// </summary>
    public CapEvaluation EvaluateAdmissionQuota(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return CapEvaluation.NotBlocked;
        var snap = _quotaService.GetCachedFor(cliType);
        var strict = _quotaCaps.Evaluate(snap);
        if (strict.Blocked) return strict;
        return QuotaWindowProjection.EvaluateProjectedBreach(snap, _quotaCaps, DateTime.UtcNow)
               ?? CapEvaluation.NotBlocked;
    }

    /// <summary>
    /// AGT-2055: run the algorithmic pre-launch quota check for a card. Pure
    /// decision over the cached snapshots + the AGT-2040 routing map; never
    /// spawns anything. See <see cref="QuotaAdmissionPlanner"/>.
    /// </summary>
    private QuotaAdmissionPlan PlanQuotaAdmission(TaskInfo info)
        => QuotaAdmissionPlanner.Plan(
            info.CliType, info.Model, info.ThinkingLevel,
            _quotaFallback, _quotaCaps,
            c => string.IsNullOrWhiteSpace(c) ? null : _quotaService.GetCachedFor(c!),
            DateTime.UtcNow,
            _activeRuns.Count,
            _quotaWaitPolicy?.Resolve(_projectSettings.Get(ProjectName)));

    private void RecordNearbyQuotaWait(TaskInfo info, QuotaAdmissionPlan plan)
    {
        if (!plan.NearbyResetWait || plan.NextResetAt is not { } resetAt) return;
        var policy = _quotaWaitPolicy?.Resolve(_projectSettings.Get(ProjectName));
        var existing = QuotaWaitMarker.TryRead(info.FolderPath, _logger);
        QuotaWaitMarker.Write(info.FolderPath, new QuotaWaitRecord
        {
            CliType = plan.CliType,
            StartedAt = existing?.StartedAt ?? DateTime.UtcNow,
            ResetAt = resetAt,
            ThresholdMinutes = policy?.ThresholdMinutes ?? CliQuotaWaitPolicyService.DefaultThresholdMinutes,
            Reason = plan.Reason,
        }, _logger);
    }

    private void ClearQuotaWait(TaskInfo info)
        => ClearQuotaWaitMarker(info, _scanner, _logger);

    internal static void ClearQuotaWaitMarker(
        TaskInfo info,
        TaskScannerService scanner,
        ILogger logger)
    {
        if (!QuotaWaitMarker.Clear(info.FolderPath, logger)) return;
        // quota-wait.json participates in the indexed TaskInfo projection.
        // Make deletion visible synchronously so a recovered provider wait
        // cannot be re-read from a stale task snapshot on the next tick.
        scanner.InvalidateCache();
    }

    private ProviderLimitStatus? ActiveProviderLimit(string? cliType, DateTime utcNow)
    {
        var active = _providerLimits.GetActive(cliType, utcNow);
        if (active is not null || string.IsNullOrWhiteSpace(cliType)) return active;

        // The task marker is durable while the provider registry is in memory.
        // Rehydrate the process-wide circuit after a backend restart before any
        // project can launch another card against the same shared account.
        var marker = _scanner.ScanAllAutomationJobs()
            .Select(task => QuotaWaitMarker.TryRead(task.FolderPath, _logger))
            .Where(wait => wait is not null
                           && string.Equals(wait.Scope, "provider", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(wait.CliType, cliType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(wait => wait!.ResetAt)
            .FirstOrDefault();
        if (marker is null) return null;
        return _providerLimits.Record(new ProviderLimitStatus(
            marker.CliType,
            marker.StartedAt,
            marker.ResetAt,
            marker.Reason,
            ResetTimeReported: true));
    }

    private void HoldForProviderLimit(
        TaskInfo info,
        string cliType,
        ProviderLimitStatus limit,
        CliExecution execution,
        DateTime finishedAt)
    {
        _providerLimits.Record(limit);
        var policy = _quotaWaitPolicy?.Resolve(_projectSettings.Get(ProjectName));
        QuotaWaitMarker.Write(info.FolderPath, new QuotaWaitRecord
        {
            CliType = cliType,
            StartedAt = limit.ObservedAt,
            ResetAt = limit.LimitedUntil,
            ThresholdMinutes = policy?.ThresholdMinutes ?? CliQuotaWaitPolicyService.DefaultThresholdMinutes,
            Reason = limit.Reason,
            Scope = "provider",
        }, _logger);
        if (info.State == TaskStates.Progress)
            _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.QuotaWaiting);

        // Close the run as an intentional wait, not as a failed task attempt.
        // The original task stays in Progress with its worktree/session intact.
        _sessions.CloseSessionEvent(info.Id, new RunSessionCloseout
        {
            StartedAt = execution.StartedAt,
            FinishedAt = finishedAt,
            Status = "waiting",
            Result = "quota-wait",
            ExitCode = execution.ExitCode,
            DurationSeconds = execution.DurationSeconds,
        }, Entry.Path);
        _consecutiveFailNoProgress.TryRemove(info.Id, out _);
        _rapidCrashBackoffUntil.TryRemove(info.Id, out _);
        _zombieResumeFailures.TryRemove(Path.GetFileName(info.FolderPath), out _);

        _logger.LogWarning(
            "provider_limit_wait project={Project} job={JobId} cliType={CliType} observedAt={ObservedAt:o} limitedUntil={LimitedUntil:o} resetReported={ResetReported}; task held without escalation",
            ProjectName,
            info.Id,
            cliType,
            limit.ObservedAt,
            limit.LimitedUntil,
            limit.ResetTimeReported);
        _chatLog.Append(
            info,
            OrchestratorMessageKind.Decision,
            $"[quota-wait] {limit.Reason}. The task is waiting and will resume automatically; other CLI providers remain eligible.");
        _timeline?.Append(
            info.FolderPath,
            TimelineEventKinds.QuotaAdmissionDecision,
            TimelineActors.System,
            summary: limit.Reason,
            details: new()
            {
                ["outcome"] = "ProviderLimited",
                ["decision"] = "account-provider-wait",
                ["cli"] = cliType,
                ["resetAt"] = limit.LimitedUntil.ToString("o"),
                ["resetTimeReported"] = limit.ResetTimeReported ? "true" : "false",
            });
        NotifyStatus();
    }

    private async Task RefreshQuotaAfterResetAsync(TaskInfo info, string cliType)
    {
        try { await _quotaService.RefreshAsync(cliType); }
        finally { ClearQuotaWait(info); }
    }

    private async Task ProbeProviderRecoveryAsync(ProviderLimitStatus limit)
    {
        var recovered = false;
        string? failureReason = null;
        try
        {
            var snapshot = await _quotaService.RefreshAsync(limit.CliType);
            var cap = _quotaCaps.Evaluate(snapshot);
            recovered = snapshot is not null
                        && string.IsNullOrWhiteSpace(snapshot.Error)
                        && !cap.Blocked;
            failureReason = recovered
                ? null
                : snapshot?.Error
                  ?? cap.DescribeReason();
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            _logger.LogWarning(
                ex,
                "provider_limit_probe_failed project={Project} cliType={CliType}",
                ProjectName,
                limit.CliType);
        }
        finally
        {
            var completedAt = DateTime.UtcNow;
            _providerLimits.CompleteRecoveryProbe(
                limit.CliType,
                completedAt,
                recovered,
                failureReason);

            var current = _providerLimits.GetActive(limit.CliType, completedAt);
            foreach (var task in _scanner.ScanAllAutomationJobs())
            {
                var wait = QuotaWaitMarker.TryRead(task.FolderPath, _logger);
                if (wait is null
                    || !string.Equals(wait.Scope, "provider", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(wait.CliType, limit.CliType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (current is null)
                {
                    ClearQuotaWait(task);
                    continue;
                }

                QuotaWaitMarker.Write(task.FolderPath, new QuotaWaitRecord
                {
                    CliType = current.CliType,
                    StartedAt = current.ObservedAt,
                    ResetAt = current.LimitedUntil,
                    ThresholdMinutes = Math.Max(
                        1,
                        (int)Math.Ceiling((current.LimitedUntil - completedAt).TotalMinutes)),
                    Reason = current.Reason,
                    Scope = "provider",
                }, _logger);
            }

            _logger.LogInformation(
                "provider_limit_probe_completed cliType={CliType} recovered={Recovered} retryAt={RetryAt} reason={Reason}",
                limit.CliType,
                recovered,
                current?.LimitedUntil,
                failureReason);
            NotifyStatus();
        }
    }

    /// <summary>
    /// Emit the pre-launch load-steering decision (AGT-2055 req 3 + 7). Always a
    /// structured log line - the data source for the load-distribution view -
    /// and, for the notable decisions (switch / throttle / wait), a task
    /// timeline + feed entry. The task-facing entries are de-duplicated per job
    /// so a card that waits across many ticks does not spam its timeline.
    /// </summary>
    private void EmitQuotaAdmissionDecision(TaskInfo info, QuotaAdmissionPlan plan)
    {
        var proj = plan.Projection;
        var warning = plan.ProjectionWarning;
        var logLevel = warning is null ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            logLevel,
            "cli_quota_admission_decision jobId={JobId} project={Project} outcome={Outcome} cli={Cli} model={Model} isFallback={IsFallback} projectedPct={Projected} burnPctPerHour={Burn} hoursRemaining={Hours} resetAt={ResetAt} assumedStart={AssumedStart} elapsedFraction={ElapsedFraction} projectionWarning={ProjectionWarning} reason={Reason}",
            info.Id, ProjectName, plan.Outcome, plan.CliType, plan.Model ?? "<default>", plan.IsFallback,
            proj?.ProjectedUsedPct ?? warning?.ProjectedUsedPct, proj?.BurnRatePctPerHour, proj?.HoursRemaining,
            proj?.ResetAt ?? warning?.ResetAt ?? plan.NextResetAt,
            proj?.AssumedStartAt ?? warning?.AssumedStartAt,
            proj?.ElapsedFraction ?? warning?.ElapsedFraction,
            warning?.Reason, plan.Reason);

        // The healthy "launch primary" decision is the silent normal path; only
        // the load-steering decisions reach the task surface.
        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary && warning is null) return;

        var key = $"{plan.Outcome}|{plan.CliType}|{plan.Model}|{plan.Reason}";
        lock (_lastAdmissionDecisionByJob)
        {
            if (_lastAdmissionDecisionByJob.TryGetValue(info.Id, out var prev) && prev == key) return;
            _lastAdmissionDecisionByJob[info.Id] = key;
        }

        _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-admission] " + plan.Reason);
        _timeline?.Append(
            info.FolderPath,
            TimelineEventKinds.QuotaAdmissionDecision,
            TimelineActors.System,
            summary: plan.Reason,
            details: new()
            {
                ["outcome"] = plan.Outcome.ToString(),
                ["cli"] = plan.CliType,
                ["model"] = plan.Model ?? string.Empty,
                ["isFallback"] = plan.IsFallback ? "true" : "false",
                ["projectedPct"] = proj?.ProjectedUsedPct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["burnPctPerHour"] = proj?.BurnRatePctPerHour.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["hoursRemaining"] = proj?.HoursRemaining.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["nextReset"] = plan.NextResetAt?.ToString("o") ?? string.Empty,
                ["resetAt"] = (proj?.ResetAt ?? warning?.ResetAt)?.ToString("o") ?? string.Empty,
                ["assumedStart"] = (proj?.AssumedStartAt ?? warning?.AssumedStartAt)?.ToString("o") ?? string.Empty,
                ["elapsedFraction"] = (proj?.ElapsedFraction ?? warning?.ElapsedFraction)?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["projectionWarning"] = warning?.Reason ?? string.Empty,
            });

        // AGT-2055 req 3 ("+ Feed-Zeile") + req 7: every load-steering decision
        // also lands on the global orchestrator feed under the load-distribution
        // topic, carrying the burn-rate / remaining-budget / remaining-time
        // numbers. That feed is the data source for the separate
        // load-distribution view, so the switch / throttle / wait is never a
        // silent decision the operator has to reconstruct from logs.
        _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            JobId = info.Id,
            Summary = plan.Reason,
            Reasoning = QuotaAdmissionPlanner.DescribeLoadNumbers(plan),
        });
    }

    /// <summary>
    /// If a job is currently running on this project and its CLI has gone
    /// past a configured cap, request a stop. Returns the cap evaluation that
    /// triggered the stop (or "not blocked" when nothing was stopped) so the
    /// caller can produce a single chat note instead of one per tick.
    /// </summary>
    public CapEvaluation EnforceQuotaCapsOnActiveJob(RunStopReason reason = RunStopReason.UserStop)
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || string.IsNullOrWhiteSpace(cliType)) return CapEvaluation.NotBlocked;
        var active = _activeRuns.Single;
        // A same-CLI fallback is explicitly allowed to run past the primary
        // model's cap. Cross-CLI fallbacks remain guarded by their own quota.
        if (active?.FallbackFromCliType != null &&
            string.Equals(active.FallbackFromCliType, cliType, StringComparison.OrdinalIgnoreCase))
            return CapEvaluation.NotBlocked;
        var ev = EvaluateQuotaCap(cliType);
        if (!ev.Blocked) return CapEvaluation.NotBlocked;

        _logger.LogWarning(
            "[taskboard] stopping active job {JobId} on {Project}: quota cap exceeded ({Reason})",
            jobId, ProjectName, ev.DescribeReason());

        PreserveUnconsumedFollowUp(jobId, "quota cap exceeded");

        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info != null)
            {
                _router.Get(info.CliType).Stop(info.TaskKey, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EnforceQuotaCapsOnActiveJob: stop failed for {JobId} on {Project}",
                jobId, ProjectName);
        }
        return ev;
    }

    public ProjectRunnerStatus GetStatus()
    {
        var statusAt = DateTime.UtcNow;
        var providerLimits = _providerLimits.Active(statusAt);
        if (providerLimits.Count == 0)
        {
            foreach (var wait in _scanner.ScanAllAutomationJobs()
                         .Select(task => QuotaWaitMarker.TryRead(task.FolderPath, _logger))
                         .Where(wait => wait is not null
                                        && string.Equals(wait.Scope, "provider", StringComparison.OrdinalIgnoreCase)))
            {
                _providerLimits.Record(new ProviderLimitStatus(
                    wait!.CliType,
                    wait.StartedAt,
                    wait.ResetAt,
                    wait.Reason,
                    ResetTimeReported: !wait.Reason.Contains("retry probe", StringComparison.OrdinalIgnoreCase)));
            }
            providerLimits = _providerLimits.Active(statusAt);
        }
        var queued = GetQueuedJobIds();
        var activeJobKey = GetActiveJobKey();
        CliExecution? activeExec = null;
        if (activeJobKey != null && _activeCliType != null)
        {
            activeExec = _router.Get(_activeCliType).GetExecution(activeJobKey);
        }
        var activeRun = _activeRuns.Single;
        return new ProjectRunnerStatus
        {
            ProjectName = ProjectName,
            Mode = _mode,
            ActiveJobId = _activeJobId,
            ActiveExecution = activeExec,
            QuotaFallbackModel = activeRun?.FallbackFromCliType == null ? null : activeExec?.Model,
            QuotaFallbackReason = activeRun?.QuotaFallbackReason,
            ProviderLimits = providerLimits,
            QueuedJobIds = queued,
            Role = RunnerRoles.Format(_role),
            PendingMode = _pendingMode,
            PendingModeWillApplyAfter = _pendingModeWillApplyAfter,
            PendingModeActiveTaskCount = PendingModeActiveTaskCount(),
            PendingModeActiveTaskTitle = PendingModeActiveTaskTitle(),
            ModeReason = _modeReason,
            ModeChangedAt = _modeChangedAt,
            ModeSource = _modeSource,
            BreakerState = _globalBreakerCooldownUntil == null ? null : "cooldown",
            BreakerCooldownUntil = _globalBreakerCooldownUntil,
            BreakerReason = _globalBreakerReason,
            BreakerTripCount = _globalBreakerTripCount,
            MaxParallelism = ParallelSlotPolicy.ClampMax(_projectSettings.Get(ProjectName).MaxParallelism),
            OccupiedSlots = _activeRuns.Count,
            LastPickReason = _lastPickReason
        };
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // Codex-specific: recognise the silent-completion hang shape
        // BEFORE the watchdog escalates. If the detector trips, the run is
        // finalized as Completed (not Watchdog-killed) and the rest of the
        // post-run pipeline (auto-review move, aspect calls, tagging) runs
        // as if Codex had cleanly exited. Cheap (one dictionary lookup +
        // a struct construction per active job).
        TickSilentCompletion();

        // Watchdog ticks regardless of runner mode: even when auto-pickup is
        // disabled, an active CLI on this project still needs to be watched
        // for hangs. Cheap (one timestamp arithmetic per active job).
        TickWatchdog();

        // CLI-recovery auto-resume: if the unspawnable-CLI pickup pause forced
        // this runner to manual, probe the CLI (throttled to once per minute)
        // and restore the operator's durable mode as soon as it spawns again.
        // No-op unless the pause path armed the marker.
        TickCliRecoveryResume();

        // Continuous decision review (ADR-0027): scan the active job's live
        // output buffer for an unresolved [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]]
        // sentinel so the project banner can stand out the moment the agent
        // emits one, not only after the run ends.
        TickPendingDecision();

        // Defensive reconciliation: if the in-memory active-job latch is
        // pointing at a job whose folder is no longer in 3-progress, release
        // it now so the rest of this tick (and future pickup ticks) are not
        // wedged. Covers external-script moves and the boot-time stuck-folder
        // sweep where no API event fired to clear us synchronously.
        ReconcileActiveJobAgainstDisk();

        // ASS-1753: one-shot post-restart slot reconcile. A restart cleared the
        // in-memory slot registry; re-book any run the CLI router still tracks
        // as live so occupied slots reflect the genuinely-running runs again.
        // Runs before the role gate so a test-subject backend's reattached runs
        // are still accounted for, and before the pickup gate so a recovered run
        // both blocks a duplicate pick and surfaces "Run aktiv" on the board.
        ReconcileRecoveredRunsIntoSlots();

        // Role gate (ADR-0044 / AGENTS.md "Dev backend lifecycle: Playwright-
        // only"). A test-subject backend never auto-picks: watchdog,
        // pending-decision scan, and reconciliation above all still ran so
        // the surface Playwright is observing stays live, but we structurally
        // refuse to claim work here. Explicit POST /api/tasks/{id}/start still
        // routes through RunCliAsync directly and is allowed.
        if (_role == RunnerRole.TestSubject) return;

        TryAutoResumeGlobalBreaker();

        if (_mode is "manual" or "paused") return;

        // A deferred switch to manual/paused closes admission immediately.
        // Runs active at request time keep going, but no new auto-pick may
        // refill a slot and move the flip point further into the future.
        if (!DeferredModePickupPolicy.AllowsAutoPickup(_pendingMode)) return;

        // Placement decides who may claim; the live mode gate above decides
        // whether pickup is automatic. Explicit user starts remain available.
        var pickupSettings = _projectSettings.Get(ProjectName);
        if (!ProjectExecutionPolicy.IsLocalExecution(pickupSettings))
        {
            _logger.LogDebug(
                "remote-pickup-owned project={Project} runner={Runner}; local auto-pickup skipped",
                ProjectName, ProjectExecutionPolicy.ResolveExecutionLocation(pickupSettings));
            return;
        }

        // Onboarding gate (Slice P / ASS-1663): a project that has DECLARED a
        // build profile but has not yet passed a green validation dry-run is not
        // "pipeline-ready" - refuse auto-pickup until install+build went green
        // ("Ohne gruenen Dry-Run kein Auto-Pickup"). A project with no declared
        // profile is unaffected (legacy behaviour). Explicit POST .../start still
        // routes through RunCliAsync directly and bypasses this loop.
        var buildGate = BuildProfileGate.Evaluate(_projectSettings.Get(ProjectName).BuildProfile);
        if (!buildGate.AllowsPickup)
        {
            if (!string.Equals(_lastBuildGateBlockReason, buildGate.Reason, StringComparison.Ordinal))
            {
                _lastBuildGateBlockReason = buildGate.Reason;
                _logger.LogInformation(
                    "[build-profile] auto-pickup gated for {Project}: {Reason}", ProjectName, buildGate.Reason);
            }
            return;
        }
        _lastBuildGateBlockReason = null;

        var slotMax = SlotMax();
        if (_processing || !_activeRuns.HasFreeSlot(slotMax)) return;

        // Pickup gating ends here. The picker below considers 3-progress and
        // 2-ready only; jobs sitting in 1-preparation, 1a-orchestrator-prep,
        // 4-auto-review, or 5-human-review do NOT
        // block this tick. Those lanes are owned by their own background
        // services (OrchestratorPrepHostedService, IntakeHostedService,
        // ReviewDecisionOrchestrator) and run in parallel with the runner.
        // ADR-0001 is preserved by the active-job latch above (one coding
        // CLI per project at a time); ADR-0026 was clarified to make the
        // parallelism explicit. See ParallelLanesPickupTests.

        // Display-order pickup: the runner consumes the same lane/list order
        // the UI shows for this project. There is no hidden progress-first
        // priority; a 3-progress resume only wins when it appears earlier in
        // the visible candidate stream. The only intentional deviation is
        // parallel admission: when maxParallelism > 1 and the visible head
        // conflicts with an active task, the loop may continue to the next
        // non-conflicting candidate and records that deviation in
        // _lastPickReason.
        while (_activeRuns.HasFreeSlot(slotMax))
        {
            var nextJob = PickNextDisplayedCandidate(slotMax);
            if (nextJob == null)
            {
                if (_mode == "auto-single" && !HasProjectRunInFlightForAutoSingle())
                    SetMode("manual", "auto-single revert: pickup queue empty");
                break;
            }

            await RunCliAsync(nextJob.Id, RunIntent.AutoPickup, followupPrompt: null, reissueAttempt: 0, mode: null, ct);
            var graceRunsRemaining = _projectSettings.ConsumeBuildProfileRevalidationGraceRun(ProjectName);
            if (graceRunsRemaining is not null)
            {
                _logger.LogWarning(
                    "build-profile-revalidation-grace-consumed project={Project} task={TaskKey} remainingRuns={RemainingRuns}",
                    ProjectName, nextJob.Key ?? nextJob.TaskKey ?? nextJob.Id, graceRunsRemaining);
            }
        }
    }

    /// <summary>
    /// Auto-single owns one project run chain through coding, runner-side
    /// finalisation, and auto-review. An empty pickup result is not a drained
    /// chain while the only candidate is already executing, while its
    /// execution slot has been released for post-processing, or while the card
    /// is in auto-review and may still be reissued to Ready.
    ///
    /// <para>
    /// The durable lane/phase checks also preserve the invariant across a
    /// backend restart, where the in-memory <see cref="ActiveRuns"/> registry
    /// may no longer contain the post-processing run.
    /// </para>
    /// </summary>
    private bool HasProjectRunInFlightForAutoSingle()
    {
        if (_activeRuns.HasInFlight) return true;

        return _scanner.ScanAllAutomationJobs().Any(job =>
            string.Equals(job.ProjectName, ProjectName, StringComparison.Ordinal)
            && (string.Equals(job.State, TaskStates.AutoReview, StringComparison.Ordinal)
                || string.Equals(job.Phase, LifecyclePhases.PostProcessingRunning, StringComparison.Ordinal)));
    }

    private TaskInfo? PickNextDisplayedCandidate(int slotMax)
    {
        RelocateStrayHumanDecisionCards();
        var skippedForConflict = new List<string>();

        foreach (var candidate in ListPickupCandidatesInDisplayedOrder())
        {
            if (_mode is "manual" or "paused") return null;
            if (candidate.Info == null) continue;

            // Never double-claim a folder already occupying a slot. This is not
            // a visible-order deviation; the card is already running.
            if (_activeRuns.Contains(candidate.Info.Id)) continue;

            // CPU-Lastwaechter: do not add another build/install workload while
            // the host has been saturated for a full minute. Existing runs are
            // left alone; the next scheduler tick naturally retries admission.
            if (_loadThrottle?.Current.Throttle == true)
            {
                EmitLoadThrottleDecision(candidate.Info, _loadThrottle.Current);
                _lastPickReason = $"load-throttle: {candidate.Info.Id}: {_loadThrottle.Current.Reason}";
                continue;
            }

            // AGT-2055 pre-launch quota gate: an algorithmic check against the
            // cached quota snapshots BEFORE any launch is attempted. A card whose
            // target CLI is exhausted (Wait) - or, with a slot already busy, only
            // projected to breach (Throttle) - is skipped quietly here: no spawn,
            // no usage-limit error, no burned reissue budget. A different-CLI card
            // later in the stream can still be picked; when every candidate is
            // blocked the loop returns null and the runner simply waits for the
            // next reset (the scheduler re-ticks and wakes on its own). The
            // switch/throttle/wait decision is emitted (de-duplicated per job) so
            // the load-steering is never silent.
            var providerLimit = ActiveProviderLimit(candidate.Info.CliType, DateTime.UtcNow);
            if (providerLimit is not null)
            {
                _lastPickReason =
                    $"provider-limited: {candidate.Info.Id}: {providerLimit.CliType} until {providerLimit.LimitedUntil:O}";
                if (_providerLimits.TryBeginRecoveryProbe(providerLimit.CliType, DateTime.UtcNow))
                    _ = ProbeProviderRecoveryAsync(providerLimit);
                continue;
            }

            if (candidate.Info.QuotaWait is { } dueWait && dueWait.ResetAt <= DateTime.UtcNow)
            {
                _ = RefreshQuotaAfterResetAsync(candidate.Info, dueWait.CliType);
                _lastPickReason = $"quota-reset-refresh: {candidate.Info.Id}: reset reached; refreshing {dueWait.CliType}";
                _logger.LogInformation(
                    "[taskboard] quota reset reached for {Job} on {Project}; refreshing {Cli} before the next admission decision",
                    candidate.Info.Id, ProjectName, dueWait.CliType);
                continue;
            }

            var qplan = PlanQuotaAdmission(candidate.Info);
            if (qplan.Outcome is QuotaAdmissionOutcome.Wait or QuotaAdmissionOutcome.Throttle)
            {
                if (qplan.NearbyResetWait) RecordNearbyQuotaWait(candidate.Info, qplan);
                else ClearQuotaWait(candidate.Info);
                EmitQuotaAdmissionDecision(candidate.Info, qplan);
                _lastPickReason = $"quota-defer: {candidate.Info.Id}: {qplan.Reason}";
                _logger.LogInformation(
                    "[taskboard] quota admission gate deferring {Job} on {Project}: {Reason}",
                    candidate.Info.Id, ProjectName, qplan.Reason);
                continue;
            }

            if (_activeRuns.Count > 0)
            {
                var adm = DecideAdmission(candidate.Info.Id, PredictParallelism(candidate.Info), slotMax);
                if (!adm.Admitted)
                {
                    var reason = $"{candidate.Info.Id}: {adm.Reason}";
                    skippedForConflict.Add(reason);
                    _lastPickReason = $"parallel-conflict-skip: {reason}";
                    _logger.LogInformation(
                        "[taskboard] skipping visible pickup candidate {Job} on {Project}: {Reason}",
                        candidate.Info.Id, ProjectName, adm.Reason);
                    continue;
                }

                var pickReason = skippedForConflict.Count == 0
                    ? adm.Reason
                    : $"{adm.Reason}; skipped earlier conflicting candidate(s): {string.Join("; ", skippedForConflict)}";
                _lastPickReason = pickReason;
                _pendingPickReasons[candidate.Info.Id] = pickReason;
                return candidate.Info;
            }

            var firstPickReason = $"display-order: {candidate.State} {candidate.Info.Id}";
            _lastPickReason = firstPickReason;
            _pendingPickReasons[candidate.Info.Id] = firstPickReason;
            return candidate.Info;
        }

        return null;
    }

    public Task<RunOutcome> StartJobManualAsync(string jobId, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.ManualStart, followupPrompt: null, reissueAttempt: 0, mode: null, ct);

    /// <summary>
    /// Sends a follow-up prompt into the CLI session that was originally created
    /// for this job (via <c>--resume</c>). When no compatible session is on
    /// record, the planner falls back to <b>recovery mode</b>: a fresh CLI run
    /// instructed to reconstruct context from the job folder. Moves the job back
    /// to <c>3-progress</c> if it sits in <c>4-review</c> or <c>5-completed</c>.
    /// </summary>
    public Task<RunOutcome> ContinueJobAsync(string jobId, string followupPrompt, string? mode, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.UserContinue, followupPrompt, reissueAttempt: 0, mode: mode, ct);

    /// <summary>
    /// Single entry point for spawning the CLI for a job. <see cref="RunPlanner.PlanRun"/>
    /// owns the full decision tree (resume vs recovery vs fresh, prompt choice,
    /// session-event shape, state moves); this method only applies the side
    /// effects the plan describes. Both the start endpoints and the continue
    /// endpoint route through here so a fix in one path can never miss its
    /// sibling - that divergence is the bug class this design exists to prevent.
    /// </summary>
    // ── ADR-0052 slice 2: parallel admission + worktree/merge helpers ──────────

    /// <summary>Effective, clamped MaxParallelism for this project (live setting).</summary>
    private int SlotMax() => ParallelSlotPolicy.ClampMax(_projectSettings.Get(ProjectName).MaxParallelism);

    private static readonly System.Text.RegularExpressions.Regex ScopePathRegex =
        new(@"\b((?:backend|frontend|src|docs|tools)/[A-Za-z0-9_./-]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Predict a task's parallelisability facts so the pick-gate can prove it
    /// disjoint from running tasks. Heuristic: repo-relative path prefixes
    /// mentioned in the title + prompt.md form the predicted scope; none found =
    /// unknown scope (the pick-gate admits it optimistically under worktree
    /// isolation). The
    /// orchestrator can later replace this with an LLM scope prediction.
    /// </summary>
    private TaskParallelism PredictParallelism(TaskInfo info)
    {
        var text = info.Title ?? string.Empty;
        try
        {
            var pf = Path.Combine(info.FolderPath, "prompt.md");
            if (File.Exists(pf)) text += "\n" + File.ReadAllText(pf);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort"); /* best-effort */ }
        var scope = ScopePathRegex.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Select(p => { var i = p.LastIndexOf('/'); return i > 0 ? p.Substring(0, i) : p; })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
        return new TaskParallelism(false, scope);
    }

    /// <summary>
    /// Pick-gate admission for one candidate against what is already running.
    /// Delegates to <see cref="ParallelSlotPolicy.Decide"/>. Unknown scopes are
    /// admitted optimistically because each coding run is worktree-isolated; a
    /// real file conflict then surfaces deterministically at integrate-merge
    /// (-&gt; escalate), never as shared-checkout corruption. Exclusive tasks and
    /// declared scope conflicts are still serialized.
    /// </summary>
    private SlotAdmission DecideAdmission(string jobId, TaskParallelism p, int slotMax)
    {
        return ParallelSlotPolicy.Decide(jobId, p, _activeRuns.RunningTasks(), slotMax);
    }

    /// <summary>Where isolated coding worktrees live (sibling temp root, off the repo).</summary>
    private string WorktreeRoot()
        => Path.Combine(Path.GetTempPath(), "ass-worktrees", System.Text.RegularExpressions.Regex.Replace(ProjectName, "[^A-Za-z0-9_.-]", "-"));

    private sealed record WorktreeCommitRange(string HeadShaBefore, string HeadShaAfter);

    /// <summary>
    /// Post-run integration for an isolated coding run: commit the agent's
    /// edits onto the task branch, then under the per-project merge-queue lock
    /// merge the branch into the work branch (develop). Teardown is DEFERRED to
    /// <see cref="TeardownWorktreeForJob"/> at the terminal accept/escalate point
    /// so a resume/reissue can reuse this worktree+branch (the worktree is owned
    /// by the task, not the run). On conflict the branch is left for resolution.
    /// Every coding path, including max==1 local runs, enters here.
    /// </summary>
    private async Task<WorktreeCommitRange?> IntegrateWorktreeRunAsync(ActiveRun run, TaskInfo info)
    {
        if (!run.IsWorktreeRun) return null;
        var repositoryRoot = run.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            _logger.LogError("[taskboard] worktree run {Job} lost its authoritative repository root", run.JobId);
            return null;
        }
        if (MainCheckoutChangedDuringWorktreeRun(run))
        {
            var summary = $"Worktree run {run.JobId} changed the shared main checkout `{repositoryRoot}`; integration skipped.";
            _logger.LogWarning("[taskboard] worktree containment violation for {Job}: main checkout changed; skipping integration", run.JobId);
            RecordWorktreeContainment(info, PipelineStepStatus.Failed, "main-checkout-modified", summary);
            _chatLog.Append(info, OrchestratorMessageKind.WorktreeContainment,
                "[worktree-containment] " + summary);
            return null;
        }

        RecordWorktreeContainment(info, PipelineStepStatus.Passed, "contained",
            $"Worktree run stayed contained in `{run.WorktreePath}`.");

        var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
            _projectSettings.Get(ProjectName),
            info)!;
        var workBranch = _git.ResolveIntegrationBranch(repositoryRoot, settings.IntegrationBranch);
        var strategy = string.IsNullOrWhiteSpace(settings.IntegrationStrategy) ? IntegrationStrategies.DirectMerge : settings.IntegrationStrategy!;
        try
        {
            // 1) commit the agent's work inside the worktree onto task/<id>
            //    (no-op if clean). This is what guarantees the agent's edits land
            //    on the task branch before the merge reads them - in a managed run
            //    the agent itself does not commit, so without this the branch tip
            //    stays at develop and Integrate would merge nothing.
            var commit = _git.WorktreeRunCommit(ProjectName, run.WorktreePath!,
                $"{info.Title}\n\n{GitService.WorktreeRunCommitTrailer(run.JobId)}",
                run.JobId, info.Runner?.RunnerId, run.Branch);
            if (commit.Success)
            {
                _logger.LogInformation("[taskboard] parallel run {Job} committed agent edits on {Branch} at {Sha}",
                    run.JobId, run.Branch, commit.Sha ?? "<unknown>");
            }
            else if (!string.IsNullOrEmpty(commit.Error)
                     && !commit.Error.Contains("Nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                // A genuine commit failure (not a benign clean tree) must never be
                // silent (AGT-1945 option 2): the branch tip stays at develop, so
                // the merge below would fold in nothing and the run's deliverable
                // would only live as uncommitted files in the worktree. Surface it
                // as a High integration issue. The pre-teardown WIP safety commit
                // (WorktreeTaskLifecycle.TeardownIfIntegrated) still preserves the
                // work, but the operator must see that the landing failed here.
                _logger.LogWarning("[taskboard] parallel run {Job} could not commit agent edits on {Branch}: {Error}",
                    run.JobId, run.Branch, commit.Error);
                _chatLog.Append(info, OrchestratorMessageKind.IntegrationError,
                    $"[integration-error] Could not commit worktree edits onto `{run.Branch}` before integration: {commit.Error}. "
                    + "The work stays in the worktree and is snapshotted as a WIP safety commit at teardown.");
            }
            var branchHeadAfterRun = _git.ReadHeadShaAt(run.WorktreePath!);
            var pushResult = await PushTaskBranchForPortabilityAsync(info, run, branchHeadAfterRun);
            // 2) serialize integration into the shared work branch. The local
            // semaphore keeps same-process slots orderly; the Task Server
            // integration lease is the cross-runner / cross-machine fence.
            // Do NOT tear down here: the worktree survives for resume/reissue.
            await _integrateLock.WaitAsync();
            IntegrationLeaseGrant? integrationLease = null;
            CancellationTokenSource? integrationHeartbeatCts = null;
            Task? integrationHeartbeat = null;
            try
            {
                var integrateStarted = DateTime.UtcNow;
                RecordIntegrationStep(info, PipelineStepStatus.Running, "running",
                    $"Waiting for the integration queue before folding `{run.Branch}` into `{workBranch}`.",
                    integrateStarted);

                if (string.Equals(strategy, IntegrationStrategies.DirectMerge, StringComparison.OrdinalIgnoreCase))
                {
                    integrationLease = await AcquireIntegrationLeaseAsync(info, run, workBranch);
                    if (integrationLease is not null)
                    {
                        integrationHeartbeatCts = new CancellationTokenSource();
                        integrationHeartbeat = StartIntegrationLeaseHeartbeat(integrationLease, integrationHeartbeatCts.Token);
                    }
                }

                var integrationBaseSha = _git.ReadHeadShaAt(repositoryRoot);
                var leaseSuffix = integrationLease is null
                    ? ""
                    : $" Integration lease token `{integrationLease.FencingToken}` is current.";
                RecordIntegrationStep(info, PipelineStepStatus.Running, "running",
                    (pushResult.Success
                        ? $"Integrating `{run.Branch}` into `{workBranch}` after pushing it to `origin`."
                        : $"Integrating `{run.Branch}` into `{workBranch}` locally; branch push to `origin` is still pending.")
                    + leaseSuffix,
                    integrateStarted);

                if (integrationLease is not null && !IntegrationLeaseStillCurrent(integrationLease))
                {
                    var lost = IntegrationLeaseLostResult(integrationLease);
                    RecordIntegrationStep(info, PipelineStepStatus.Failed, "lease-lost",
                        IntegrationSummary("Integration lease was lost before merge.", run, workBranch, lost),
                        integrateStarted);
                    AppendWorktreeIntegrationIssue(
                        info,
                        OrchestratorMessageKind.IntegrationError,
                        "Worktree branch integration failed because the integration lease was lost.",
                        run,
                        workBranch,
                        lost);
                    return BuildBranchCommitRange(run, branchHeadAfterRun);
                }

                var res = Worktree.Integrate(
                    repositoryRoot,
                    run.WorktreePath!,
                    run.Branch!,
                    workBranch,
                    strategy,
                    preserveConflictForResolution: true);
                if (res.Outcome == IntegrationOutcome.Merged)
                {
                    _logger.LogInformation("[taskboard] parallel run {Job} integrated into {Branch} at {Sha}",
                        run.JobId, workBranch, res.IntegratedSha ?? "<unknown>");
                    RecordIntegrationStep(info, PipelineStepStatus.Passed, "merged",
                        $"Task branch `{run.Branch}` merged into `{workBranch}` at `{res.IntegratedSha ?? "<unknown>"}`.",
                        integrateStarted);
                    RecordConflictResolutionStep(info, PipelineStepStatus.Skipped, "not-needed",
                        "No merge conflict was detected.", DateTime.UtcNow);
                    EnqueueIntegrationPush(info, workBranch);
                    return BuildIntegratedCommitRange(integrationBaseSha, res.IntegratedSha)
                        ?? BuildBranchCommitRange(run, branchHeadAfterRun);
                }
                else if (res.Outcome == IntegrationOutcome.Conflict)
                {
                    _logger.LogWarning("[taskboard] parallel run {Job} merge conflict into {Branch}: {Err} (left for resolution)",
                        run.JobId, workBranch, res.Error);
                    RecordIntegrationStep(info, PipelineStepStatus.Failed, "conflict",
                        IntegrationSummary("Merge conflict detected.", run, workBranch, res),
                        integrateStarted);

                    var resolution = await RunConflictResolutionStepAsync(info, run, workBranch, res, integrationLease);
                    if (resolution.Outcome == IntegrationOutcome.Merged)
                    {
                        _logger.LogInformation("[taskboard] parallel run {Job} conflict resolved and integrated into {Branch} at {Sha}",
                            run.JobId, workBranch, resolution.IntegratedSha ?? "<unknown>");
                        RecordIntegrationStep(info, PipelineStepStatus.Passed, "merged-after-resolution",
                            $"Task branch `{run.Branch}` merged into `{workBranch}` after conflict resolution at `{resolution.IntegratedSha ?? "<unknown>"}`.",
                            integrateStarted);
                        EnqueueIntegrationPush(info, workBranch);
                        return BuildIntegratedCommitRange(integrationBaseSha, resolution.IntegratedSha)
                            ?? BuildBranchCommitRange(run, branchHeadAfterRun);
                    }
                    else
                    {
                        AppendWorktreeIntegrationIssue(
                            info,
                            resolution.Outcome == IntegrationOutcome.Error
                                ? OrchestratorMessageKind.IntegrationError
                                : OrchestratorMessageKind.IntegrationConflict,
                            resolution.Outcome == IntegrationOutcome.Error
                                ? "Integration blocked: conflict-resolution failed before a fenced merge could complete."
                                : "Integration blocked: conflict-resolution could not produce a mergeable task branch.",
                            run,
                            workBranch,
                            resolution);
                        return BuildBranchCommitRange(run, branchHeadAfterRun);
                    }
                }
                else
                {
                    _logger.LogWarning("[taskboard] parallel run {Job} integration outcome {Outcome}: {Err}",
                        run.JobId, res.Outcome, res.Error);
                    RecordIntegrationStep(info, PipelineStepStatus.Failed, "error",
                        IntegrationSummary($"Integration failed with outcome `{res.Outcome}`.", run, workBranch, res),
                        integrateStarted);
                    AppendWorktreeIntegrationIssue(
                        info,
                        OrchestratorMessageKind.IntegrationError,
                        $"Worktree branch integration failed with outcome `{res.Outcome}`.",
                        run,
                        workBranch,
                        res);
                    return BuildBranchCommitRange(run, branchHeadAfterRun);
                }
            }
            finally
            {
                if (integrationHeartbeatCts != null)
                {
                    integrationHeartbeatCts.Cancel();
                    if (integrationHeartbeat != null)
                    {
                        try { await integrationHeartbeat; }
                        catch (OperationCanceledException __ex) { SilentCatch.Note(__ex, "ProjectRunner:1034"); }
                    }
                    integrationHeartbeatCts.Dispose();
                }

                if (integrationLease != null)
                    ReleaseIntegrationLease(info, integrationLease);
                _integrateLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[taskboard] worktree integration failed for {Job}", run.JobId);
            RecordIntegrationStep(info, PipelineStepStatus.Failed, "error",
                $"Worktree branch integration failed before completion: {ex.Message}", DateTime.UtcNow);
            AppendWorktreeIntegrationIssue(
                info,
                OrchestratorMessageKind.IntegrationError,
                "Worktree branch integration failed before completion.",
                run,
                workBranch,
                new IntegrationResult(IntegrationOutcome.Error, null, ex.Message));
            return null;
        }
    }

    private async Task<IntegrationLeaseGrant?> AcquireIntegrationLeaseAsync(TaskInfo info, ActiveRun run, string workBranch)
    {
        if (_integrationLeases is null)
            return null;

        var request = BuildIntegrationLeaseAcquireRequest(run, workBranch);
        RecordIntegrationLeaseEvent(info, "waiting",
            $"Waiting for integration lease on `{ProjectName}/{workBranch}` for `{run.Branch}`.");
        var lease = await _integrationLeases.WaitAcquireAsync(
            request,
            retryDelay: TimeSpan.FromSeconds(1),
            CancellationToken.None);
        RecordIntegrationLeaseEvent(info, "acquired",
            $"Acquired integration lease `{lease.LeaseId}` token `{lease.FencingToken}` for `{ProjectName}/{workBranch}`.",
            lease);
        return lease;
    }

    private IntegrationLeaseAcquireRequest BuildIntegrationLeaseAcquireRequest(ActiveRun run, string workBranch)
    {
        var host = string.IsNullOrWhiteSpace(_pickupLockOwner?.Hostname)
            ? System.Environment.MachineName
            : _pickupLockOwner!.Hostname;
        var pid = _pickupLockOwner?.Pid > 0 ? _pickupLockOwner.Pid : System.Environment.ProcessId;
        var backend = string.IsNullOrWhiteSpace(_pickupLockOwner?.BackendName)
            ? "backend"
            : _pickupLockOwner!.BackendName;
        var runnerId = $"{backend}@{host}:{pid}/{ProjectName}";
        return new IntegrationLeaseAcquireRequest(
            ProjectName,
            workBranch,
            run.JobId,
            runnerId,
            host,
            pid,
            backend,
            RequestedTtlSeconds: (int)IntegrationLeaseService.DefaultTtl.TotalSeconds);
    }

    private Task StartIntegrationLeaseHeartbeat(IntegrationLeaseGrant lease, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_integrationLeases is null) return;
                var renewed = _integrationLeases.Renew(new IntegrationLeaseHeartbeatRequest(
                    lease.ProjectName,
                    lease.IntegrationBranch,
                    lease.LeaseId,
                    lease.FencingToken,
                    lease.RunnerId,
                    RequestedTtlSeconds: (int)IntegrationLeaseService.DefaultTtl.TotalSeconds));
                if (!renewed.Granted)
                {
                    _logger.LogWarning(
                        "[integration-lease] heartbeat failed for {Project}/{Branch} lease={LeaseId} token={FencingToken}: {Outcome} {Message}",
                        lease.ProjectName,
                        lease.IntegrationBranch,
                        lease.LeaseId,
                        lease.FencingToken,
                        renewed.Outcome,
                        renewed.Message ?? "<no message>");
                    return;
                }
            }
        }, CancellationToken.None);
    }

    private bool IntegrationLeaseStillCurrent(IntegrationLeaseGrant lease)
        => _integrationLeases?.IsCurrent(lease) ?? true;

    private IntegrationResult IntegrationLeaseLostResult(IntegrationLeaseGrant lease)
        => new(
            IntegrationOutcome.Error,
            null,
            $"Integration lease `{lease.LeaseId}` token `{lease.FencingToken}` for `{lease.ProjectName}/{lease.IntegrationBranch}` is no longer current.");

    private void ReleaseIntegrationLease(TaskInfo info, IntegrationLeaseGrant lease)
    {
        if (_integrationLeases is null) return;

        var released = _integrationLeases.Release(new IntegrationLeaseReleaseRequest(
            lease.ProjectName,
            lease.IntegrationBranch,
            lease.LeaseId,
            lease.FencingToken,
            lease.RunnerId));
        RecordIntegrationLeaseEvent(info, released.Outcome.ToLowerInvariant(),
            $"Released integration lease `{lease.LeaseId}` token `{lease.FencingToken}` for `{lease.ProjectName}/{lease.IntegrationBranch}`: {released.Outcome}.",
            lease);
        if (!string.Equals(released.Outcome, "Released", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[integration-lease] release returned {Outcome} for {Project}/{Branch} lease={LeaseId} token={FencingToken}: {Message}",
                released.Outcome,
                lease.ProjectName,
                lease.IntegrationBranch,
                lease.LeaseId,
                lease.FencingToken,
                released.Message ?? "<no message>");
        }
    }

    private void RecordIntegrationLeaseEvent(
        TaskInfo info,
        string outcome,
        string summary,
        IntegrationLeaseGrant? lease = null)
    {
        if (_timeline == null || string.IsNullOrWhiteSpace(info.FolderPath)) return;
        try
        {
            var details = new Dictionary<string, string>
            {
                ["project"] = ProjectName,
                ["outcome"] = outcome,
            };
            if (lease is not null)
            {
                details["integrationBranch"] = lease.IntegrationBranch;
                details["leaseId"] = lease.LeaseId;
                details["fencingToken"] = lease.FencingToken.ToString(CultureInfo.InvariantCulture);
                details["runnerId"] = lease.RunnerId;
            }

            _timeline.Append(info.FolderPath, new TimelineEvent
            {
                Ts = DateTime.UtcNow,
                Kind = TimelineEventKinds.IntegrationLease,
                Actor = TimelineActors.System,
                Summary = summary,
                Details = details,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record integration lease timeline event for {JobId}", info.Id);
        }
    }

    private WorktreeCommitRange? BuildIntegratedCommitRange(string? beforeSha, string? afterSha)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return null;
        return new WorktreeCommitRange(beforeSha, afterSha);
    }

    private WorktreeCommitRange? BuildBranchCommitRange(ActiveRun run, string? afterSha)
    {
        if (string.IsNullOrWhiteSpace(afterSha)) return null;
        var beforeSha = _sessions.ReadSessionEvents(run.JobId, Entry.Path).LastOrDefault()?.HeadShaBefore;
        if (string.IsNullOrWhiteSpace(beforeSha)) return null;
        return new WorktreeCommitRange(beforeSha, afterSha);
    }

    private async Task<GitPushResult> PushTaskBranchForPortabilityAsync(TaskInfo info, ActiveRun run, string? branchHeadAfterRun)
    {
        if (string.IsNullOrWhiteSpace(run.Branch))
        {
            var missingBranch = new GitPushResult(false, branchHeadAfterRun ?? "<unknown>", "missing-branch", "Task branch is not known.");
            AppendTaskBranchUnpushedIssue(info, run, missingBranch);
            return missingBranch;
        }

        if (string.IsNullOrWhiteSpace(branchHeadAfterRun))
        {
            var missingHead = new GitPushResult(false, "<unknown>", "missing-head", "Could not read task branch HEAD.");
            AppendTaskBranchUnpushedIssue(info, run, missingHead);
            return missingHead;
        }

        var pushed = await Worktree.PushTaskBranchWithRetryAsync(
            run.RepositoryRoot!,
            branchHeadAfterRun,
            run.Branch,
            CancellationToken.None,
            attempts: 3,
            retryDelay: TimeSpan.FromSeconds(1));

        if (pushed.Success)
        {
            _logger.LogInformation(
                "[taskboard] parallel run {Job} pushed task branch {Branch} to origin at {Sha} ({Status})",
                run.JobId,
                run.Branch,
                branchHeadAfterRun,
                pushed.Status);
        }
        else
        {
            _logger.LogWarning(
                "[taskboard] parallel run {Job} could not push task branch {Branch} to origin after retry: {Status} {Error}",
                run.JobId,
                run.Branch,
                pushed.Status,
                pushed.Error ?? "<no error>");
            AppendTaskBranchUnpushedIssue(info, run, pushed);
        }

        return pushed;
    }

    private void AppendTaskBranchUnpushedIssue(TaskInfo info, ActiveRun run, GitPushResult result)
    {
        _chatLog.Append(info, OrchestratorMessageKind.TaskBranchUnpushed,
            BuildTaskBranchUnpushedIssueMessage(run.Branch, result));
    }

    internal static string BuildTaskBranchUnpushedIssueMessage(string? taskBranch, GitPushResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.Error)
            ? "No git error text was reported."
            : result.Error.Trim();
        return $"Task branch `{taskBranch ?? "<unknown>"}` could not be pushed to `origin` after retry. " +
               $"The run continued locally, but the per-task branch is not durable on the remote. " +
               $"Push status: {result.Status}. SHA: `{result.Sha}`. Error: {error}";
    }

    private void AppendWorktreeIntegrationIssue(
        TaskInfo info,
        OrchestratorMessageKind kind,
        string summary,
        ActiveRun run,
        string workBranch,
        IntegrationResult result)
    {
        _chatLog.Append(info, kind,
            BuildWorktreeIntegrationIssueMessage(summary, run.Branch, run.WorktreePath, workBranch, result));
    }

    internal static string BuildWorktreeIntegrationIssueMessage(
        string summary,
        string? taskBranch,
        string? worktreePath,
        string workBranch,
        IntegrationResult result)
    {
        var conflicted = result.ConflictedFiles is { Count: > 0 }
            ? string.Join(", ", result.ConflictedFiles.Take(12))
            : "none reported";
        var overflow = result.ConflictedFiles is { Count: > 12 }
            ? $" (+{result.ConflictedFiles.Count - 12} more)"
            : "";
        var error = string.IsNullOrWhiteSpace(result.Error)
            ? "No git error text was reported."
            : result.Error.Trim();

        return $"{summary} Task branch `{taskBranch ?? "<unknown>"}` was not merged into `{workBranch}`. " +
               $"Worktree: `{worktreePath ?? "<unknown>"}`. Conflicted files: {conflicted}{overflow}. Error: {error}";
    }

    private async Task<IntegrationResult> RunConflictResolutionStepAsync(
        TaskInfo info,
        ActiveRun run,
        string workBranch,
        IntegrationResult conflict,
        IntegrationLeaseGrant? integrationLease = null)
    {
        var started = DateTime.UtcNow;
        var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
            _projectSettings.Get(ProjectName),
            info)!;
        var step = AgentStudio.Pipeline.PipelineCatalogue.Standard.AllSteps.First(s =>
            string.Equals(s.Id, AgentStudio.Pipeline.PipelineCatalogue.ConflictResolutionStepId, StringComparison.OrdinalIgnoreCase));
        var resolverCliType = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveCliType(settings, step)
            ?? AgentStudio.Pipeline.PipelineStepModelDefaults.DefaultCli;
        var resolverModel = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveModel(
            settings, step, AgentStudio.Pipeline.PipelineStepModelDefaults.SupportModel);
        var resolverThinkingLevel = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveThinkingLevel(
            settings,
            step,
            resolverCliType,
            resolverModel,
            AgentStudio.Pipeline.PipelineStepModelDefaults.SupportThinkingLevel);
        RecordConflictResolutionStep(info, PipelineStepStatus.Running, "running",
            IntegrationSummary($"Starting managed {resolverCliType} conflict-resolution run.", run, workBranch, conflict),
            started,
            model: resolverModel);

        try
        {
            var resolver = _router.Get(resolverCliType);
            if (resolver.CliType == AgentTypes.Human || !resolver.IsAvailable())
            {
                var unavailable = $"{resolverCliType} resolver is unavailable at `{resolver.GetCliPath()}`.";
                var result = new IntegrationResult(IntegrationOutcome.Conflict, null, unavailable, conflict.ConflictedFiles);
                RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "merge-blocked",
                    IntegrationSummary(unavailable, run, workBranch, result), started, model: resolverModel);
                return result;
            }

            var resolverJobKey = $"{GetJobKey(info.Id)}:conflict-resolution";
            var permissionMode = _projectSettings.ResolveCliMode(ProjectName, resolverCliType).Mode;
            var contextMode = _projectSettings.ResolveContextMode(ProjectName, resolverCliType, info.ContextMode).Mode;
            var executionEngine = ResolveCliExecutionEngine();
            var prompt = BuildConflictResolutionPrompt(info, run, workBranch, conflict);
            var (execution, error) = await resolver.StartAsync(
                $"{info.Id}-conflict-resolution",
                resolverJobKey,
                prompt,
                run.WorktreePath!,
                sessionName: null,
                resumeSession: false,
                model: resolverModel,
                thinkingLevel: resolverThinkingLevel,
                jobFolderPath: info.FolderPath,
                permissionMode: permissionMode,
                contextMode: contextMode,
                executionEngine: executionEngine,
                ct: CancellationToken.None);

            if (execution == null)
            {
                var failed = new IntegrationResult(IntegrationOutcome.Conflict, null,
                    error ?? "Codex resolver failed to start.", conflict.ConflictedFiles);
                RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "merge-blocked",
                    IntegrationSummary(failed.Error ?? "Codex resolver failed to start.", run, workBranch, failed),
                    started,
                    model: resolverModel);
                return failed;
            }

            var deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes(20));
            while (DateTime.UtcNow < deadline)
            {
                var current = resolver.GetExecution(resolverJobKey);
                if (current == null || !string.Equals(current.Status, RunStatuses.Running, StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }

            var final = resolver.GetExecution(resolverJobKey) ?? execution;
            resolver.ReleaseOutputResources(resolverJobKey);
            resolver.DiscardPersistedOutput(resolverJobKey);

            if (string.Equals(final.Status, RunStatuses.Running, StringComparison.OrdinalIgnoreCase))
            {
                resolver.Stop(resolverJobKey, RunStopReason.Watchdog);
                var timedOut = new IntegrationResult(IntegrationOutcome.Conflict, null,
                    "Codex conflict resolver timed out.", _git.ListUnmergedFiles(run.WorktreePath!));
                RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "merge-blocked",
                    IntegrationSummary(timedOut.Error!, run, workBranch, timedOut), started, model: final.Model ?? resolverModel);
                return timedOut;
            }

            if (integrationLease is not null && !IntegrationLeaseStillCurrent(integrationLease))
            {
                var lost = IntegrationLeaseLostResult(integrationLease);
                RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "lease-lost",
                    IntegrationSummary("Integration lease was lost during conflict-resolution.", run, workBranch, lost),
                    started,
                    model: final.Model ?? resolverModel);
                return lost;
            }

            var commit = _git.WorktreeRunCommit(ProjectName, run.WorktreePath!,
                $"{info.Title}\n\n[conflict-resolution; jobId={run.JobId}]",
                run.JobId, info.Runner?.RunnerId, run.Branch);
            if (commit.Success)
                _logger.LogInformation("[taskboard] conflict resolver committed changes for {Job} at {Sha}",
                    run.JobId, commit.Sha ?? "<unknown>");

            var retry = Worktree.CompleteIntegrationAfterResolution(
                run.RepositoryRoot!,
                run.WorktreePath!,
                run.Branch!,
                workBranch);
            if (retry.Outcome == IntegrationOutcome.Merged)
            {
                RecordConflictResolutionStep(info, PipelineStepStatus.Passed, "resolved",
                    $"Conflict resolved and `{run.Branch}` merged into `{workBranch}` at `{retry.IntegratedSha ?? "<unknown>"}`.",
                    started,
                    model: final.Model ?? resolverModel);
                return retry;
            }

            var conflictedFiles = retry.ConflictedFiles is { Count: > 0 }
                ? retry.ConflictedFiles
                : _git.ListUnmergedFiles(run.WorktreePath!);
            var blocked = new IntegrationResult(IntegrationOutcome.Conflict, null,
                retry.Error ?? "Conflict resolver finished, but the branch is still not mergeable.",
                conflictedFiles);
            RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "merge-blocked",
                IntegrationSummary("Integration blocked.", run, workBranch, blocked),
                started,
                model: final.Model ?? resolverModel);
            return blocked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[taskboard] conflict-resolution step failed for {Job}", run.JobId);
            var failed = new IntegrationResult(IntegrationOutcome.Conflict, null, ex.Message, conflict.ConflictedFiles);
            RecordConflictResolutionStep(info, PipelineStepStatus.Failed, "merge-blocked",
                IntegrationSummary("Integration blocked.", run, workBranch, failed), started, model: resolverModel);
            return failed;
        }
    }

    private string BuildConflictResolutionPrompt(
        TaskInfo info,
        ActiveRun run,
        string workBranch,
        IntegrationResult conflict)
    {
        var files = conflict.ConflictedFiles is { Count: > 0 }
            ? string.Join(Environment.NewLine, conflict.ConflictedFiles.Select(f => $"- {f}"))
            : "- none reported; run git diff --name-only --diff-filter=U";
        return _prompts.Render(ConflictResolutionTemplate, new Dictionary<string, string?>
        {
            ["job_id"] = info.Id,
            ["job_title"] = info.Title,
            ["task_branch"] = run.Branch,
            ["integration_branch"] = workBranch,
            ["worktree"] = run.WorktreePath,
            ["conflicted_files"] = files,
        }).TrimEnd('\r', '\n');
    }

    private static string IntegrationSummary(
        string prefix,
        ActiveRun run,
        string workBranch,
        IntegrationResult result)
    {
        var conflicted = result.ConflictedFiles is { Count: > 0 }
            ? string.Join(", ", result.ConflictedFiles.Take(12))
            : "none reported";
        var overflow = result.ConflictedFiles is { Count: > 12 }
            ? $" (+{result.ConflictedFiles.Count - 12} more)"
            : "";
        var error = string.IsNullOrWhiteSpace(result.Error) ? "No git error text was reported." : result.Error.Trim();
        return $"{prefix} Task branch `{run.Branch ?? "<unknown>"}` into `{workBranch}`. " +
               $"Worktree: `{run.WorktreePath ?? "<unknown>"}`. Conflicted files: {conflicted}{overflow}. Error: {error}";
    }

    private bool MainCheckoutChangedDuringWorktreeRun(ActiveRun run)
    {
        if (!run.IsWorktreeRun) return false;
        var before = run.MainCheckoutStatusBefore;
        var after = _git.GetPorcelainStatus(run.RepositoryRoot!);
        if (before == null || after == null)
        {
            _logger.LogDebug(
                "[taskboard] worktree containment status unavailable for {Job} (before={Before} after={After})",
                run.JobId, before == null ? "null" : "ok", after == null ? "null" : "ok");
            return false;
        }
        return !string.Equals(before, after, StringComparison.Ordinal);
    }

    private void RecordWorktreeContainment(
        TaskInfo info,
        PipelineStepStatus status,
        string verdict,
        string summary)
    {
        if (_pipelineLog == null) return;
        try
        {
            var now = DateTime.UtcNow;
            _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.WorktreeContainmentStepId,
                Kind = StepKind.Tool,
                Status = status,
                StartedAt = now,
                CompletedAt = now,
                Verdict = verdict,
                VerdictSummary = summary,
                Reason = summary,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record worktree-containment step for {JobId}", info.Id);
        }
    }

    private void RecordIntegrationStep(
        TaskInfo info,
        PipelineStepStatus status,
        string verdict,
        string summary,
        DateTime startedAt)
        => RecordPipelineStep(
            info,
            AgentStudio.Pipeline.PipelineCatalogue.IntegrateMergeStepId,
            StepKind.Tool,
            status,
            verdict,
            summary,
            startedAt,
            model: null);

    private void RecordConflictResolutionStep(
        TaskInfo info,
        PipelineStepStatus status,
        string verdict,
        string summary,
        DateTime startedAt,
        string? model = null)
        => RecordPipelineStep(
            info,
            AgentStudio.Pipeline.PipelineCatalogue.ConflictResolutionStepId,
            StepKind.Orchestrator,
            status,
            verdict,
            summary,
            startedAt,
            model);

    private void RecordPipelineStep(
        TaskInfo info,
        string stepId,
        StepKind kind,
        PipelineStepStatus status,
        string? verdict,
        string? summary,
        DateTime startedAt,
        string? model)
    {
        if (_pipelineLog == null) return;
        try
        {
            var completedAt = status == PipelineStepStatus.Running ? (DateTime?)null : DateTime.UtcNow;
            _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
            {
                StepId = stepId,
                Kind = kind,
                Model = model,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = completedAt.HasValue ? Math.Max(0, (long)(completedAt.Value - startedAt).TotalMilliseconds) : 0,
                Verdict = verdict,
                VerdictSummary = summary,
                Reason = status == PipelineStepStatus.Failed ? summary : null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record pipeline step {StepId} for {JobId}", stepId, info.Id);
        }
    }

    /// <summary>
    /// Terminal worktree cleanup: when a task leaves the run loop (accepted into
    /// acceptance review or escalated for operator intervention) tear down its task/&lt;id&gt; worktree
    /// + branch - but only if the branch is already folded into the work branch,
    /// so unresolved conflict work is never dropped (the gate lives in
    /// <see cref="WorktreeTaskLifecycle.TeardownIfIntegrated"/>). Deferred here,
    /// not per-run, so resume/reissue can reuse the worktree. This applies at
    /// every slot count.
    /// </summary>
    private void TeardownWorktreeForJob(string jobId)
    {
        try
        {
            var repositoryRoot = _git.ResolveRepositoryRoot(Entry);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                _logger.LogWarning("[taskboard] worktree teardown skipped for {Job}: authoritative repository root unavailable", jobId);
                return;
            }
            var settings = _projectSettings.Get(ProjectName);
            var workBranch = _git.ResolveIntegrationBranch(repositoryRoot, settings.IntegrationBranch);
            var res = Worktree.TeardownIfIntegrated(repositoryRoot, jobId, workBranch, WorktreeRoot());
            if (!res.Success)
                _logger.LogWarning("[taskboard] worktree teardown for {Job} reported: {Err}", jobId, res.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[taskboard] worktree teardown failed for {Job}", jobId);
        }
    }

    private async Task<RunOutcome> RunCliAsync(
        string jobId, RunIntent intent, string? followupPrompt, int reissueAttempt, string? mode, CancellationToken ct)
    {
        if (!_activeRuns.HasFreeSlot(SlotMax()) || _activeRuns.Contains(jobId))
        {
            if (intent == RunIntent.ManualStart)
                _logger.LogWarning("Runner '{Project}' has no free slot / job already active {JobId}", ProjectName, _activeJobId);
            // Look up the active job's title for the queued response so the
            // TaskRunnerService can shape a friendly meta message without
            // re-scanning. Best-effort; null title is fine.
            string? activeTitle = null;
            try { activeTitle = _scanner.FindJob(_activeJobId!, Entry.Path)?.Title; } catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner:1628"); }
            return RunOutcome.Reject(new RunRejection(
                Reason: RunRejectReason.ProjectBusy,
                Message: $"Runner '{ProjectName}' is already executing job '{_activeJobId}'",
                BusyJobId: _activeJobId,
                BusyJobTitle: activeTitle));
        }

        _processing = true;
        TaskInfo? admissionInfo = null;
        var movedToProgressThisCall = false;
        var claimedRunThisCall = false;
        var processStartConfirmed = false;
        string? acquiredPickupLockFolder = null;
        // Set only when THIS call stashed a saved follow-up. Every rollback is
        // gated on it: pending-intent.consumed.json now survives a successful
        // run as consumption evidence (AGT-2747), so an unrelated later failure
        // must not move that stale copy back into pending-intent.json and replay
        // a follow-up an agent already acted on.
        PendingIntent? consumedIntent = null;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return RunOutcome.Reject(new RunRejection(RunRejectReason.TaskNotFound, "Job not found"));
            admissionInfo = info;

            // Part 2 will submit human feedback through the ordinary Continue
            // path. Refuse that continuation once the durable review contract
            // says the configured cap is reached; a finish action does not call
            // the runner and remains available to the review UI.
            var pendingUiReview = SteerPendingMarker.TryRead(info.FolderPath, _logger);
            if (UiIterationGate.IsFeedbackContinuation(intent, info.PendingIntent is not null)
                && string.Equals(pendingUiReview?.Kind, SteerPendingKinds.UiIterationReview, StringComparison.OrdinalIgnoreCase)
                && UiIterationGate.MustEscalateFeedbackContinuation(pendingUiReview?.UiIterationReview))
            {
                var ui = pendingUiReview!.UiIterationReview!;
                var reason = $"UI iteration cap {ui.MaxIterations} was reached without a finish decision; additional feedback iterations are not allowed.";
                var escalation = await _humanReviewEscalation.EscalateAsync(
                    jobId, info.WatchPath, ProjectName,
                    HumanReviewEscalationCategories.UiIterationCap, reason, ct);
                if (escalation.Status == MoveJobStatus.Success && !string.IsNullOrWhiteSpace(escalation.NewFolderPath))
                    SteerPendingMarker.Clear(escalation.NewFolderPath!, _logger);
                _logger.LogWarning(
                    "ui_pipeline_cap project={Project} job={JobId} iteration={Iteration}/{MaxIterations} action=escalate status={Status}",
                    ProjectName, jobId, ui.Iteration, ui.MaxIterations, escalation.Status);
                return RunOutcome.Reject(new RunRejection(
                    RunRejectReason.UiIterationCapReached, reason));
            }

            if (info.QuotaWait is { } dueWait && dueWait.ResetAt <= DateTime.UtcNow)
            {
                await _quotaService.RefreshAsync(dueWait.CliType, ct);
                ClearQuotaWait(info);
                info = _scanner.FindJob(jobId, Entry.Path) ?? info;
                admissionInfo = info;
            }

            // Resolve safe model migrations before quota routing, model
            // qualification, or prompt shaping observes the task. In
            // particular, the primary quota route carries the requested model
            // into the launch command, so calculating it from the old card
            // value would persist an audit event while still launching the
            // superseded model for this admission.
            if (_modelMigrations != null)
            {
                info = await _modelMigrations.ApplyAtAdmissionAsync(info, ct);
                admissionInfo = info;
            }

            // Resolve the workspace route from the latest cached quota. The
            // decision is per-run and never mutates job.json, so a reset makes
            // the next invocation return to primary automatically.
            //
            // AGT-2055: route against the PROJECTION-AWARE admission view so a
            // primary that is about to breach its window switches to the AGT-2040
            // fallback pre-emptively (before the wall), not after a burned launch.
            // The hard block below stays on the STRICT cap so a manual start is
            // never refused purely on a projection - only when a model is truly
            // exhausted and no fallback saved it.
            var route = _quotaFallback?.Resolve(
                info.CliType, info.Model, info.ThinkingLevel, EvaluateAdmissionQuota);
            var strictCap = EvaluateQuotaCap(info.CliType);
            // AGT-2055: the algorithmic pre-launch decision for THIS run, computed
            // once here - before the run claims a slot, so its projected-throttle
            // slot count matches the pickup gate's view. Reused for the quiet-wait
            // reject just below and emitted at the commit point further down, so
            // every launch (a healthy primary or a pre-emptive model switch) is
            // documented with its burn-rate / projection numbers.
            var admissionPlan = PlanQuotaAdmission(info);
            if (admissionPlan.Outcome == QuotaAdmissionOutcome.Wait)
            {
                // Everything is exhausted: wait quietly with a reason + next
                // reset, and record the decision. No spawn, no reissue burn.
                if (admissionPlan.NearbyResetWait) RecordNearbyQuotaWait(info, admissionPlan);
                else ClearQuotaWait(info);
                EmitQuotaAdmissionDecision(info, admissionPlan);
                _logger.LogInformation(
                    "[taskboard] {Intent} for job {JobId} deferred by quota admission: {Reason}",
                    intent, jobId, admissionPlan.Reason);
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.QuotaCapExceeded,
                    Message: admissionPlan.Reason));
            }

            // Auto-pickup consumes a saved pending-intent if there is one,
            // turning what would have been a fresh-start run into a
            // UserContinue with the saved prompt + mode. This is the runtime
            // half of the busy-project queue: TaskRunnerService writes the
            // intent + promotes to 2-ready; the auto-pickup here picks the
            // job and runs the saved continue.
            if (intent == RunIntent.AutoPickup && info.PendingIntent != null)
            {
                var stashed = _mutations.ReadAndStashPendingIntent(info.FolderPath);
                if (stashed != null && !string.IsNullOrWhiteSpace(stashed.Prompt))
                {
                    _logger.LogInformation(
                        "[taskboard] auto-pickup of {JobId} consuming saved {Mode} intent ({Chars} chars)",
                        jobId, stashed.Mode, stashed.Prompt.Length);
                    intent = RunIntent.UserContinue;
                    followupPrompt = stashed.Prompt;
                    mode = stashed.Mode;
                    consumedIntent = stashed;
                }
            }

            var cli = route == null ? GetCliFor(info) : _router.Get(route.CliType);
            ClearQuotaWait(info);
            var initialState = info.State;
            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var jobFolder = info.FolderPath;

            // Session ids are CLI-owned. A cross-CLI fallback, and the later
            // return to primary, must start a fresh session even when both CLIs
            // happen to use UUID-shaped ids.
            var sessionNameForPlan = info.SessionName;
            IReadOnlyList<string>? sessionChainForPlan = info.SessionChain;
            try
            {
                var latestSessionCli = _sessions.ReadSessionEvents(jobId, Entry.Path)
                    .LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Cli))?.Cli;
                if (!string.IsNullOrWhiteSpace(latestSessionCli)
                    && !string.Equals(latestSessionCli, cli.CliType, StringComparison.OrdinalIgnoreCase))
                {
                    sessionNameForPlan = null;
                    sessionChainForPlan = [];
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not validate session CLI for {JobId}; planner will use recorded session", jobId); }

            var plan = RunPlanner.PlanRun(
                intent,
                initialState,
                sessionNameForPlan,
                cli.CliType,
                cli.IsCompatibleSessionName,
                jobId,
                promptPath,
                jobFolder,
                followupPrompt,
                sessionChainForPlan,
                continueMode: mode);

            // Pick<->Start atomicity (ASS-1655): remember whether THIS call is the
            // one that moved the task into 3-progress. Every early return between
            // here and a confirmed CLI start must roll that move back to 2-ready,
            // otherwise the task is stranded as a zombie in 3-progress while its
            // slot is already free -> the runner picks the next task and ends up
            // with two folders in 3-progress at maxParallelism=1.
            if (plan.MoveJobToProgress && info.State != TaskStates.Progress)
            {
                var move = _states.MoveJob(
                    jobId, TaskStates.Progress, Entry.Path,
                    transitionCause: LaneChangeCauses.Claimed, transitionDetail: "local-pickup");
                if (move.Status != MoveJobStatus.Success)
                {
                    _logger.LogWarning(
                        "[taskboard] refusing to start job {JobId} on {Project}: could not move to 3-progress before spawn ({Status} {Message})",
                        jobId, ProjectName, move.Status, move.Message);
                    if (intent == RunIntent.AutoPickup)
                        _rapidCrashBackoffUntil[jobId] = DateTime.UtcNow + RapidCrashBreaker.Backoff(1);
                    return RunOutcome.Reject(new RunRejection(
                        Reason: RunRejectReason.ProjectBusy,
                        Message: $"Could not move job '{jobId}' to {TaskStates.Progress}: {move.Status} {move.Message}"));
                }

                var movedInfo = _scanner.FindJob(jobId, Entry.Path);
                info = movedInfo
                       ?? (!string.IsNullOrWhiteSpace(move.NewFolderPath)
                           ? info with { State = TaskStates.Progress, FolderPath = move.NewFolderPath! }
                           : info with { State = TaskStates.Progress });
                promptPath = Path.Combine(info.FolderPath, "prompt.md");
                jobFolder = info.FolderPath;
                plan = RebindPlanJobPaths(plan, promptPath, jobFolder);
                movedToProgressThisCall = true;
                admissionInfo = info;
            }

            // Way 3 (non-deterministic half): an epic card runs a planning /
            // decomposition step instead of a coding run. We keep the normal
            // start lifecycle and only swap the prompt template (the agent
            // authors a sub-task list) and, optionally, the planning model.
            // A user continue on an epic is the user steering the plan, not a
            // fresh decomposition, so it is left on the normal path.
            var isEpicPlanningRun = EpicRunPolicy.IsPlanningRun(info.Kind, intent);
            ModelQualificationDecision? qualification = null;
            if (_modelQualification != null)
            {
                // Qualification is the input choice for the card's primary
                // CLI. Quota fallback remains a separate admission concern
                // and may still replace this selection for the one run below.
                qualification = await QualifyModelAsync(info, promptPath, GetCliFor(info), ct);
            }
            var runModel = route?.Model ?? qualification?.SelectedModel ?? info.Model;
            var runThinkingLevel = route?.ThinkingLevel ?? qualification?.SelectedThinkingLevel ?? info.ThinkingLevel;
            if (isEpicPlanningRun)
            {
                plan = plan with { PromptTemplate = RuntimePromptService.EpicDecomposition, PromptOverride = null };
                var projectSettings = _projectSettings.Get(ProjectName);
                var planningModel = projectSettings.EpicPlanningModel;
                if (!string.IsNullOrWhiteSpace(planningModel)) runModel = planningModel;
                if (projectSettings.EpicPlanningThinkingLevel is not null)
                    runThinkingLevel = projectSettings.EpicPlanningThinkingLevel;
                _logger.LogInformation(
                    "[taskboard] epic {JobId} -> planning/decomposition run (model={Model}, thinkingLevel={ThinkingLevel})",
                    jobId, runModel ?? "<task-default>", runThinkingLevel ?? "<model-default>");
            }

            // Resolve prompt-shaping pre-steps before acquiring the pickup lock
            // or the in-memory run claim. The enrichment report is the durable
            // admission record for the exact prompt that will be dispatched.
            ReissueOpenItemsPreCheck.PreCheckDecision? reissueOpenItems = null;
            if (!isEpicPlanningRun && string.IsNullOrWhiteSpace(followupPrompt))
            {
                reissueOpenItems = EvaluateReissueOpenItems(info);
                if (reissueOpenItems.Intervenes)
                {
                    plan = BuildReissueChangePlan(plan, reissueOpenItems, ProjectName, info.Id);
                }
            }

            PromptEnrichmentPreparation? preparedPromptEnrichment = null;
            if (_promptEnrichment is not null && ShouldForegroundIntakeEnrichment(plan))
            {
                preparedPromptEnrichment = _promptEnrichment.Prepare(
                    info,
                    ReadPromptText(promptPath),
                    runModel);
            }

            // Disk-backed pickup lock (ADR-0044). When the lock is configured
            // and an attempt to acquire it shows a foreign live owner, refuse
            // to spawn: another backend on the same workspace is already on
            // this job. AlreadyOwn (re-issue path), Stale (stale clean), and
            // Acquired (first claim) all mean "we hold the lock - proceed".
            // Re-entrancy (AlreadyOwn) is the case where the re-issue path
            // released the in-memory active-job latch but kept us alive in
            // the same process.
            if (_pickupLock != null && _pickupLockOwner != null)
            {
                var owner = _pickupLockOwner with { ProjectName = ProjectName, JobId = jobId };
                var outcome = _pickupLock.TryAcquire(jobFolder, owner, out var foreign);
                if (outcome == LockAcquireOutcome.ForeignHeld)
                {
                    _logger.LogWarning(
                        "[taskboard] refusing to spawn job {JobId}: pickup lock held by backend '{Backend}' (pid={Pid} host={Host} role={Role})",
                        jobId,
                        foreign?.BackendName ?? "<unknown>",
                        foreign?.Pid ?? -1,
                        foreign?.Hostname ?? "<unknown>",
                        foreign?.Role ?? "<unknown>");
                    if (movedToProgressThisCall)
                        RevertFailedStartFromProgress(jobId, info, intent);
                    return RunOutcome.Reject(new RunRejection(
                        Reason: RunRejectReason.ProjectBusy,
                        Message: $"Job '{jobId}' is being processed by backend '{foreign?.BackendName ?? "<unknown>"}' (pid={foreign?.Pid ?? -1}); refusing duplicate pickup.",
                        BusyJobId: jobId,
                        BusyJobTitle: info.Title));
                }
                _activePickupLockFolder = jobFolder;
                acquiredPickupLockFolder = jobFolder;
            }

            var uiProjectSettings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                _projectSettings.Get(ProjectName),
                info);
            var isUiIterationPipeline = string.Equals(
                UiTaskPipelineRouter.Select(info, uiProjectSettings).Id,
                AgentStudio.Pipeline.PipelineCatalogue.UiPipelineId,
                StringComparison.Ordinal);
            var reviewedUiIteration = string.Equals(
                    pendingUiReview?.Kind,
                    SteerPendingKinds.UiIterationReview,
                    StringComparison.OrdinalIgnoreCase)
                ? pendingUiReview?.UiIterationReview
                : null;
            var uiIteration = UiIterationGate.ResolveRunIteration(
                info.FolderPath,
                reviewedUiIteration);
            if (isUiIterationPipeline)
                UiIterationGate.PrepareIterationDirectory(info.FolderPath, uiIteration);

            claimedRunThisCall = _activeRuns.TryClaim(new ActiveRun
            {
                JobId = jobId,
                JobFolder = jobFolder,
                Intent = intent,
                Followup = followupPrompt,
                FollowupMode = mode,
                Plan = plan,
                ReissueAttempt = reissueAttempt,
                IsUiIterationPipeline = isUiIterationPipeline,
                // The prepared directory is the durable current-iteration
                // checkpoint. Only a Human Review feedback marker advances it;
                // retries therefore cannot accidentally reuse prior evidence.
                UiIteration = uiIteration,
                UiMaxIterations = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveUiMaxIterations(
                    uiProjectSettings),
                PickupLockFolder = _activePickupLockFolder,
            });
            _activePickupLockFolder = null;
            if (!claimedRunThisCall)
            {
                if (_pickupLock != null && _pickupLockOwner != null && acquiredPickupLockFolder != null)
                {
                    var owner = _pickupLockOwner with { ProjectName = ProjectName, JobId = jobId };
                    _pickupLock.Release(acquiredPickupLockFolder, owner);
                }
                if (movedToProgressThisCall)
                    RevertFailedStartFromProgress(jobId, info, intent);
                _logger.LogWarning(
                    "run_admission_duplicate_prevented jobId={JobId} project={Project} reason=active-run-claim-lost",
                    jobId, ProjectName);
                return RunOutcome.Reject(new RunRejection(
                    RunRejectReason.ProjectBusy,
                    $"Job '{jobId}' was claimed by another launch before admission completed."));
            }
            // ASS-1732 "always-worktree": a CODING run mutates the source tree, so
            // it ALWAYS executes in its own isolated task/<id> worktree - the
            // primary/sequential slot included, and for every intent (fresh
            // pickup, reissue, resume, crash-recovery requeue). The shared main
            // checkout is read-only reference + the integration target; letting an
            // agent coding run touch it cross-contaminates `develop` with another
            // task's work and lets a resume run land on someone else's dirty tree
            // (the bug this fixes). The worktree is per-TASK, not per-run: a
            // resume/reissue REUSES the existing task/<id> worktree+branch
            // (PrepareOrReuse) so the run continues where it left off instead of
            // opening a second independent landing. Read-only modes
            // (planning/research) and epic planning runs write nothing, so they
            // legitimately run in-place. A mutating run without an authoritative
            // Git repository is rejected: task storage / a local folder is never
            // a safe fallback checkout.
            var requiresWorktree = WorktreeRunPolicy.RequiresWorktree(info.Mode, isEpicPlanningRun);
            var repositoryRoot = requiresWorktree ? _git.ResolveRepositoryRoot(Entry) : null;
            if (requiresWorktree && string.IsNullOrWhiteSpace(repositoryRoot))
            {
                const string missingRepository = "No authoritative Git repository is configured for this coding run.";
                RecordWorktreePreparationFailure(jobId, missingRepository);
                ReleaseRun(jobId);
                if (consumedIntent is not null) _mutations.RollbackStashedPendingIntent(info.FolderPath);
                if (movedToProgressThisCall)
                    RevertFailedStartFromProgress(jobId, info, intent);
                NotifyStatus();
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.ProjectBusy,
                    Message: $"{missingRepository} Refusing shared-checkout/in-place execution for '{jobId}'.",
                    BusyJobId: jobId,
                    BusyJobTitle: info.Title));
            }
            if (requiresWorktree && _activeRuns.Get(jobId) is { } claimed)
            {
                claimed.Parallelism = PredictParallelism(info);
                var wtSettings = _projectSettings.Get(ProjectName);
                var workBranch = _git.ResolveIntegrationBranch(repositoryRoot!, wtSettings.IntegrationBranch);
                var prep = Worktree.PrepareOrReuse(repositoryRoot!, jobId, workBranch, WorktreeRoot());
                if (prep.Success)
                {
                    claimed.WorktreePath = prep.WorktreePath;
                    claimed.WorkingDirectory = WorktreeRunPolicy.ResolveWorkingDirectory(
                        repositoryRoot!, Entry.RootPath, prep.WorktreePath!);
                    claimed.RepositoryRoot = repositoryRoot;
                    claimed.Branch = prep.Branch;
                    claimed.WorktreeReused = prep.Reused;
                    _logger.LogInformation(
                        "[taskboard] worktree for {Job}: {Path} (cwd={Cwd}) on {Branch} from {RepositoryRoot} ({Mode})",
                        jobId, prep.WorktreePath, claimed.WorkingDirectory, prep.Branch, repositoryRoot,
                        prep.Reused ? "reused" : "fresh-cut");
                }
                else
                {
                    // NEVER fall back to the shared main checkout for a coding run:
                    // two tasks (or a resume + a fresh pickup) running in
                    // Entry.RootPath overwrite each other on `develop` - the
                    // cross-contamination this fix exists to prevent. Release the
                    // slot and serialize; the next auto-pickup tick retries once a
                    // worktree can be prepared/reused.
                    RecordWorktreePreparationFailure(jobId, prep.Error);
                    ReleaseRun(jobId);
                    if (consumedIntent is not null) _mutations.RollbackStashedPendingIntent(info.FolderPath);
                    // The run never started: roll the just-applied 3-progress move
                    // back to 2-ready so the deferred task does not linger as a
                    // zombie in 3-progress while its slot is free.
                    if (movedToProgressThisCall)
                        RevertFailedStartFromProgress(jobId, info, intent);
                    NotifyStatus();
                    return RunOutcome.Reject(new RunRejection(
                        Reason: RunRejectReason.ProjectBusy,
                        Message: $"Worktree isolation unavailable for '{jobId}' ({prep.Error}); deferring to keep the coding run off the shared checkout.",
                        BusyJobId: jobId,
                        BusyJobTitle: info.Title));
                }
            }
            NotifyStatus();

            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));

            // AGT-2055 req 3/7: document the pre-launch admission decision for the
            // run we are now committing to spawn - a pre-emptive model switch
            // ("model switched pre-launch: ...") or a healthy primary launch -
            // as a task timeline entry + a load-distribution feed line carrying
            // the projection numbers. Deferrals (wait / throttle) are recorded at
            // the pickup gate; this is the launch side of the same ledger. A
            // healthy primary launch stays a silent log-only line (the planner's
            // LaunchPrimary outcome early-returns before the task-facing tee), so
            // only genuine load-steering reaches the timeline and feed.
            EmitQuotaAdmissionDecision(info, admissionPlan);

            if (route?.IsFallback == true)
            {
                if (_activeRuns.Get(jobId) is { } fallbackRun)
                {
                    fallbackRun.FallbackFromCliType = info.CliType ?? CliTypes.Claude;
                    fallbackRun.QuotaFallbackReason = route.Reason;
                }
                var fallbackNote = $"Fallback: {route.CliType}/{route.Model}; reason: quota ({route.Reason})";
                _logger.LogWarning(
                    "cli_quota_fallback_activated jobId={JobId} primaryCli={PrimaryCli} fallbackCli={FallbackCli} fallbackModel={FallbackModel} reason={Reason}",
                    jobId, info.CliType, route.CliType, route.Model, route.Reason);
                _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-fallback] " + fallbackNote);
                _timeline?.Append(
                    info.FolderPath,
                    TimelineEventKinds.QuotaFallbackActivated,
                    TimelineActors.System,
                    summary: fallbackNote,
                    details: new()
                    {
                        ["primaryCli"] = info.CliType ?? string.Empty,
                        ["primaryModel"] = info.Model ?? string.Empty,
                        ["fallbackCli"] = route.CliType,
                        ["fallbackModel"] = route.Model ?? string.Empty,
                        ["reason"] = "quota",
                        ["quotaDetail"] = route.Reason ?? string.Empty,
                    });
            }

            // Diagnostic logs - surface the planner's decision in one place so
            // operators reading the log can tell which branch fired without
            // grepping for old per-method messages.
            _logger.LogInformation(
                "[taskboard] {Intent} for job {JobId} on {Cli}: kind={Kind} resume={Resume} session={Session} reason={Reason}",
                intent, jobId, cli.CliType, plan.EventKind, plan.ResumeFlag,
                plan.SessionToResume ?? "<none>", plan.EventReason ?? "<none>");
            // Log the ACTUAL working directory the CLI will run in: every coding
            // slot runs inside its isolated worktree, not the shared checkout.
            // (Previously this always printed Entry.RootPath, masking the worktree
            // path and hiding shared-checkout fallbacks in the log.)
            var runWorkingDir = _activeRuns.Get(jobId)?.WorkingDirectory
                                ?? _activeRuns.Get(jobId)?.WorktreePath
                                ?? Entry.RootPath;
            _logger.LogInformation("[taskboard] using working directory {Path}", runWorkingDir);

            // ASS-1732 guard (defense-in-depth): a coding run that REQUIRES a
            // worktree must never start with its working directory pointed at the
            // shared main checkout. The gate above already rejects on a failed
            // worktree prepare/reuse, so reaching here anywhere inside the
            // authoritative checkout means a worktree was silently skipped - an
            // invariant violation we refuse loudly + escalate rather than let the
            // agent dirty `develop`. Read-only / planning runs
            // (requiresWorktree==false) legitimately run in-place and never trip
            // this.
            if (WorktreeRunPolicy.IsMainCheckoutViolation(requiresWorktree, runWorkingDir, repositoryRoot))
            {
                var violation = $"Coding run {jobId} resolved to the shared main checkout `{repositoryRoot}` without an isolated worktree; refusing to start (worktree isolation is mandatory for coding runs).";
                _logger.LogError("[taskboard] worktree isolation guard tripped for {Job}: {Violation}", jobId, violation);
                _chatLog.Append(info, OrchestratorMessageKind.WorktreeContainment,
                    "[worktree-containment] " + violation);
                _timeline?.Append(
                    info.FolderPath,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.System,
                    summary: "Worktree isolation guard refused a coding run targeting the shared main checkout.",
                    details: new()
                    {
                        ["jobId"] = jobId,
                        ["mainCheckout"] = repositoryRoot ?? string.Empty,
                        ["mode"] = info.Mode ?? string.Empty,
                    });
                ReleaseRun(jobId);
                if (consumedIntent is not null) _mutations.RollbackStashedPendingIntent(info.FolderPath);
                if (movedToProgressThisCall)
                    RevertFailedStartFromProgress(jobId, info, intent);
                NotifyStatus();
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.ProjectBusy,
                    Message: violation,
                    BusyJobId: jobId,
                    BusyJobTitle: info.Title));
            }

            if (_activeRuns.Get(jobId) is { IsWorktreeRun: true } worktreeRun)
                worktreeRun.MainCheckoutStatusBefore = _git.GetPorcelainStatus(worktreeRun.RepositoryRoot!);

            if (reissueOpenItems is { Intervenes: true })
            {
                var interventionKind =
                    reissueOpenItems.Action == ReissueOpenItemsPreCheck.PreCheckAction.Escalate
                        ? OrchestratorMessageKind.Steer
                        : OrchestratorMessageKind.SoftIntervention;
                _chatLog.Append(
                    info,
                    interventionKind,
                    $"[reissue-open-items] {reissueOpenItems.Note}");
            }

            // Resolve context before rendering the prompt because Codex resume
            // viability depends on the effective CODEX_HOME. A clean run reuses
            // the task's persistent clean home (session-stable across attempts;
            // MKT-8 / WEB-14), so its resume is viable when THAT home carries
            // the rollout from a previous attempt; a shared run must have the
            // referenced rollout in the shared home. Falling back here (rather
            // than after spawn) lets the recovery template carry prompt.md,
            // job-folder evidence, and the user follow-up in full.
            var contextMode = _projectSettings.ResolveContextMode(ProjectName, cli.CliType, info.ContextMode).Mode;
            if (plan.ResumeFlag
                && string.Equals(cli.CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
                && !CodexRolloutStore.CanResume(
                    plan.SessionToResume, contextMode,
                    sharedHome: null,
                    cleanHome: cli.GetPersistentCleanContextHome(GetJobKey(jobId))))
            {
                var missingSession = plan.SessionToResume;
                var reason = CliContextModes.Normalize(contextMode) == CliContextModes.Clean
                    ? "Codex rollout is absent from the task's clean-context CODEX_HOME"
                    : "Codex rollout is absent from the current CODEX_HOME";
                _logger.LogInformation(
                    "codex_resume_precondition_fallback job={JobId} session={SessionId} contextMode={ContextMode} reason=no-rollout; starting full-context fresh run",
                    jobId, missingSession, contextMode);
                _chatLog.Append(info, OrchestratorMessageKind.Recovery,
                    $"[codex-resume-fallback] {reason}; starting fresh with full job context instead of thread/resume.");
                plan = RunPlanner.FallBackToRecovery(
                    plan, promptPath, info.FolderPath, followupPrompt, reason);
            }

            var prompt = RenderPrompt(plan, info, runWorkingDir, preparedPromptEnrichment);
            if (_activeRuns.Get(jobId) is { IsUiIterationPipeline: true } uiRun)
            {
                prompt += UiIterationGate.BuildAgentInstructions(
                    info.FolderPath, uiRun.UiIteration, uiRun.UiMaxIterations);
                _logger.LogInformation(
                    "pipeline_route project={Project} job={JobId} pipeline={PipelineId} iteration={Iteration}/{MaxIterations} classifier=evidence-gate-ui-heuristic",
                    ProjectName, jobId, AgentStudio.Pipeline.PipelineCatalogue.UiPipelineId,
                    uiRun.UiIteration, uiRun.UiMaxIterations);
            }

            if (plan.ClearStaleSessionName)
                _sessions.SetJobSessionName(jobId, null, Entry.Path);
            if (plan.PersistSessionName != null)
                _sessions.SetJobSessionName(jobId, plan.PersistSessionName, Entry.Path);
            if (plan.MarkSessionChainRecovery)
                _sessions.MarkSessionChainRecovery(jobId, Entry.Path);
            if (plan.WriteCutMarker)
                AppendSessionCutMarkerToCliLog(info, plan.CutMarkerReason ?? "session lost");

            // Continue-routed-to-Recovery: the user clicked Send (or chose
            // Continue / Steer / Extend / NewTask), but no resumable session
            // is on record. The cut marker tells the activity log a chain
            // break happened; this orchestrator note explains, in user-
            // language, why their conversation context did not carry over.
            if (intent == RunIntent.UserContinue
                && string.Equals(plan.EventKind, "recovery", StringComparison.OrdinalIgnoreCase))
            {
                var modeLabel = ContinueModes.Normalize(mode);
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[fallback] No {cli.CliType} session on record (mode: {modeLabel}); rebuilding context from job folder.");
            }

            // Capture the project's HEAD SHA right before the CLI starts.
            // Combined with the post-run capture in OnCliFinishedAsync, this
            // gives us the deterministic SHA range the per-run commits
            // endpoint uses ("commits made during this run" = git rev-list
            // HeadShaBefore..HeadShaAfter). Best-effort: a missing repo or
            // a git failure leaves the SHAs null and we fall back to the
            // wall-clock window. See docs/quality/design-principles.md for why we
            // treat the software-side change set as a first-class signal.
            var headShaBefore = SafeGetHeadSha(jobId);
            if (_activeRuns.Get(jobId) is { } activeRunAtSpawn)
            {
                activeRunAtSpawn.WorkerHeadShaBefore = _git.ReadHeadShaAt(runWorkingDir);
                activeRunAtSpawn.WorkerBranchBefore = _git.ReadCurrentBranchAt(runWorkingDir);
                activeRunAtSpawn.ProtectedRemoteTipsBefore = CaptureProtectedRemoteTips();
            }

            // Capture the exact context handed to the agent for this run so the
            // run timeline can show *what* the run was started with (prompt +
            // foregrounded open-items + resume framing). `prompt` is final here:
            // RenderPrompt + the optional reissue open-items prepend have both
            // run. Stored in its own file (multi-KB), referenced from the event.
            var contextRef = _sessions.PersistRunContext(info.FolderPath, prompt);

            // ADR-0052: surface the slot pick-decision + occupancy on the
            // timeline. At MaxParallelism == 1 this is the single sequential
            // slot (one admit, all others serialized); the slot model
            // generalizes to N without changing this emission site.
            var slotMax = ParallelSlotPolicy.ClampMax(_projectSettings.Get(ProjectName).MaxParallelism);
            var slotDecision = ParallelSlotPolicy.Decide(
                jobId, TaskParallelism.Default, Array.Empty<RunningTask>(), slotMax);
            var pickReason = _pendingPickReasons.TryRemove(jobId, out var pendingPickReason)
                ? pendingPickReason
                : slotDecision.Reason;
            _lastPickReason = pickReason;
            _logger.LogInformation(
                "[taskboard] slot admission for {JobId} on {Project}: {Decision} ({Reason}); occupancy 1/{Max}",
                jobId, ProjectName, slotDecision.Decision, pickReason, slotMax);
            _timeline?.Append(
                info.FolderPath,
                TimelineEventKinds.RunnerSlotAdmission,
                TimelineActors.System,
                summary: $"Slot 1/{slotMax}: {pickReason}",
                runId: plan.EventInputSessionId,
                details: new()
                {
                    ["maxParallelism"] = slotMax.ToString(),
                    ["occupied"] = "1",
                    ["decision"] = slotDecision.Decision.ToString(),
                });

            if (_activeRuns.Get(jobId) is { } claimedRun) claimedRun.CliType = cli.CliType;
            // Resolve the per-project permission mode at spawn time (default
            // YOLO). Reading the live ProjectSettingsService here is what makes
            // a toggle take effect on the next run without a backend restart.
            var permissionMode = _projectSettings.ResolveCliMode(ProjectName, cli.CliType).Mode;
            // T1b / ASS-1742: resolve the per-task / per-project context mode at
            // spawn time (default CLEAN). The adapter seeds an isolated config
            // home only when the run resolves to clean AND the CLI supports it;
            // shared-only CLIs run shared regardless. Resolving live here (like
            // permissionMode) makes a toggle take effect on the next run.
            // ASS-1732: the CLI keys `--resume <id>` by working directory - it
            // looks for the session marker under the cwd it is launched in. A
            // session is therefore resumable only when this run's cwd matches the
            // one the session was born in:
            //  - non-worktree run (read-only): always the same place ->
            //    resume normally.
            //  - REUSED worktree (resume / reissue / crash-recovery requeue of a
            //    task whose worktree already existed): same canonical worktree
            //    path the prior worktree run used -> the session lives there, so
            //    resume continues the conversation in the worktree.
            //  - FRESH-CUT worktree (first isolation for this task, incl. the
            //    one-time migration of a task whose old session was born in the
            //    main checkout): any recorded session lived in a DIFFERENT
            //    directory; `--resume` would wait for a marker that never appears
            //    and StartAsync would hang. Start FRESH; the agent re-derives
            //    context from the task spec + the code in the worktree.
            var activeRunForSpawn = _activeRuns.Get(jobId);
            var isWorktreeRun = activeRunForSpawn?.IsWorktreeRun == true;
            // S2 (AGT-1784): resolve where the session we're about to resume was
            // BORN. A reissue/resume of a session whose birth cwd differs from
            // this run's cwd (e.g. the worktree path moved because the project
            // display name changed) must start fresh — the CLI keys --resume by
            // cwd and would otherwise mint a dead session and crash (observed:
            // claude exited -1 after 164s → infra-crash).
            string? sessionBirthCwd = null;
            if (!string.IsNullOrWhiteSpace(plan.SessionToResume))
            {
                try
                {
                    sessionBirthCwd = _sessions.ReadSessionEvents(jobId, Entry.Path)
                        .LastOrDefault(e =>
                            string.Equals(e.CapturedSessionId, plan.SessionToResume, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(e.InputSessionId, plan.SessionToResume, StringComparison.OrdinalIgnoreCase))
                        ?.Cwd;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[taskboard] {Job}: could not resolve session birth cwd; treating as unknown", jobId); }
            }
            var canResumeSession = WorktreeRunPolicy.CanResumeSession(
                isWorktreeRun, activeRunForSpawn?.WorktreeReused == true, runWorkingDir, sessionBirthCwd);
            var effSessionToResume = canResumeSession ? plan.SessionToResume : null;
            var effResumeFlag = canResumeSession && plan.ResumeFlag;
            var executionEngine = ResolveCliExecutionEngine();
            var admittedSessionReason = plan.EventReason;
            if (isWorktreeRun && !canResumeSession && plan.ResumeFlag)
            {
                var why = !string.IsNullOrWhiteSpace(sessionBirthCwd)
                    ? $"prior session born in {sessionBirthCwd} != this run cwd {runWorkingDir}"
                    : "prior session cwd was not this worktree";
                _logger.LogInformation("[taskboard] {Job}: starting FRESH session ({Why})", jobId, why);
                // Keep the admitted session event truthful about the fallback.
                admittedSessionReason = why;
            }
            var (execution, cliError) = await cli.StartAsync(
                jobId, GetJobKey(jobId), prompt, runWorkingDir,
                effSessionToResume, effResumeFlag, runModel, runThinkingLevel,
                jobFolderPath: info.FolderPath,
                permissionMode: permissionMode,
                contextMode: contextMode,
                executionEngine: executionEngine,
                ct: ct);

            if (execution == null)
            {
                ReleaseRun(jobId);
                // Mandatory diagnostic: a spawn failure used to leave ZERO
                // trace in the job folder (no cli-output.log, empty logs/),
                // so an operator could only guess why the run "finished
                // failed". Write the reason into the job's cli-output.log
                // before any other cleanup so the failure is never silent.
                WriteSpawnFailureDiagnostic(info, cli.CliType, cliError);
                NotifyStatus();
                // Roll back the consumed pending-intent on spawn failure so
                // the next auto-pickup retries instead of losing the user's
                // input.
                if (consumedIntent is not null) _mutations.RollbackStashedPendingIntent(info.FolderPath);
                // A spawn failure on autopickup is a silent attempt for
                // dead-letter purposes: the CLI never produced output. The
                // OnCliFinished path that normally records this never fires
                // because there is no execution.
                if (intent == RunIntent.AutoPickup && info.State == TaskStates.Progress)
                {
                    RecordPickupAttemptResult(
                        slug: jobId,
                        outputLines: 0,
                        durationSeconds: 0.0,
                        executionStatus: SpawnFailedExecutionStatus);
                }
                // Pick<->Start atomicity (ASS-1655): the CLI never started, so the
                // task must not stay parked in 3-progress. Roll the lane move this
                // call applied straight back to 2-ready (or, once it has burned the
                // per-slug spawn budget, hand off to the over-budget reroute which
                // also pauses the runner). This must run AFTER RecordPickupAttemptResult
                // so the budget decision sees this attempt.
                if (movedToProgressThisCall)
                    RevertFailedStartFromProgress(jobId, info, intent);
                // Spawn failure is the terminal end of this run's lifecycle;
                // ReleaseRun above already dropped its per-run pickup lock.
                ApplyPendingModeIfAny(jobId);
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.CliUnavailable,
                    Message: cliError ?? $"Failed to start {cli.CliType} CLI process"));
            }
            processStartConfirmed = true;

            if (plan.ReissuePromptAssignment is { } promptAssignment)
            {
                ReissuePromptExperimentLog.Append(
                    info.FolderPath,
                    ProjectName,
                    info.Id,
                    promptAssignment,
                    execution.StartedAt,
                    runModel,
                    runThinkingLevel,
                    _logger);
                _logger.LogInformation(
                    "reissue_prompt_experiment project={Project} job={JobId} experiment={ExperimentId} arm={Arm} template={TemplateVersion} attempt={Attempt} family={PromptFamily} cause={Cause}",
                    ProjectName, info.Id, promptAssignment.ExperimentId, promptAssignment.Arm,
                    promptAssignment.TemplateVersion, promptAssignment.Attempt,
                    promptAssignment.PromptFamily, promptAssignment.Cause);
            }

            // A run-start is durable only after the CLI adapter confirms a
            // process. Neither the canonical session event nor its timeline
            // projection may exist for a rejected admission, otherwise a later
            // read invents a historical run that never owned execution.
            // AGT-2159: capture the actual execution owner (runner host +
            // backend from the pickup-lock owner) on the durable run-start
            // event so card + detail can show where the run really executes.
            var executionHost = string.IsNullOrWhiteSpace(_pickupLockOwner?.Hostname)
                ? System.Environment.MachineName
                : _pickupLockOwner!.Hostname;
            var executionBackend = string.IsNullOrWhiteSpace(_pickupLockOwner?.BackendName)
                ? "local"
                : _pickupLockOwner!.BackendName;
            var localRunnerId = $"{executionBackend}@{executionHost}".ToLowerInvariant();
            _sessions.AppendSessionEvent(jobId, new SessionEvent
            {
                Ts = execution.StartedAt,
                Kind = plan.EventKind,
                Cli = cli.CliType,
                Model = execution.Model ?? runModel,
                ThinkingLevel = execution.ThinkingLevel ?? runThinkingLevel,
                ExecutionLocation = new TaskExecutionLocation
                {
                    State = TaskExecutionStates.LocalRunning,
                    ExecutionKind = "local",
                    RunnerId = localRunnerId,
                    ClientId = localRunnerId,
                    HostDisplayName = executionHost,
                    ConfiguredRunnerId = ResolveConfiguredRemoteRunnerId(_projectSettings.Get(ProjectName)),
                    StartedAt = execution.StartedAt,
                    LastActivityAt = execution.StartedAt,
                    SessionId = effSessionToResume,
                    Branch = info.Provenance?.Branch,
                    WorktreePath = runWorkingDir,
                    ConnectionState = "connected",
                    LeaseState = "local-process",
                    TrustReason = "Captured from the local pickup owner and worktree at confirmed CLI process start.",
                },
                InputSessionId = effSessionToResume,
                CapturedSessionId = null,
                Cwd = runWorkingDir,
                Resumed = effResumeFlag,
                Reason = admittedSessionReason,
                HeadShaBefore = headShaBefore,
                ContextRef = contextRef
            }, Entry.Path);
            _timeline?.Append(
                info.FolderPath,
                TimelineEventKinds.AgentRunStarted,
                TimelineActors.System,
                summary: $"{cli.CliType} CLI {plan.EventKind}{(string.IsNullOrWhiteSpace(plan.EventReason) ? "" : $" ({plan.EventReason})")}",
                runId: effSessionToResume,
                details: new()
                {
                    ["cli"] = cli.CliType ?? string.Empty,
                    ["model"] = runModel ?? string.Empty,
                    ["quotaFallback"] = route?.IsFallback == true ? "true" : "false",
                    ["fallbackReason"] = route?.Reason ?? string.Empty,
                    ["intent"] = plan.EventKind ?? string.Empty,
                    ["resumed"] = effResumeFlag ? "true" : "false",
                });

            // Spawn succeeded, so the saved follow-up is now this run's prompt.
            // The stash stays on disk as pending-intent.consumed.json: it is the
            // operator's proof that a queued steer actually reached an agent,
            // paired with a ledger row naming the run that took it (AGT-2747).
            if (consumedIntent is not null)
            {
                _logger.LogInformation(
                    "follow-up-consumed job={JobId} project={Project} mode={Mode} savedReason={SavedReason} run={RunId}",
                    jobId, ProjectName, consumedIntent.Mode, consumedIntent.SavedReason, effSessionToResume ?? "<new-session>");
                _timeline?.Append(
                    info.FolderPath,
                    TimelineEventKinds.FollowUpConsumed,
                    TimelineActors.System,
                    summary: $"Follow-up consumed by this run ({consumedIntent.Mode}).",
                    runId: effSessionToResume,
                    details: new()
                    {
                        ["mode"] = consumedIntent.Mode,
                        ["savedReason"] = consumedIntent.SavedReason,
                        ["savedAt"] = consumedIntent.SavedAt.ToString("O"),
                        ["evidence"] = "pending-intent.consumed.json",
                    });
            }
            // Only a confirmed process start ends a visible no-slot wait. Early
            // admission/quota/spawn failures intentionally leave the wait visible.
            SteerPendingMarker.Clear(info.FolderPath, _logger);
            _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.ExecutionRunning);
            PostProcessingLifecycleStore.ResetForExecution(
                info.FolderPath,
                execution.StartedAt,
                _logger);

            // Mirror run-start onto the bus. Existing canonical signals
            // (session-events.jsonl + cli-output.log "[taskboard] Started ..."
            // marker) stay; the bus message is a typed projection so the
            // project screen does not need to scan log text for run boundaries.
            try { _ = _bus?.EmitRunStartedAsync(info, cli.CliType, execution.StartedAt, plan.SessionToResume, intent.ToString()); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of run-start failed for {JobId}", jobId); }

            // AGT-2100: record the CLI's cached quota snapshot at run-start so the
            // cap-forecast history has a datapoint per run boundary. Cached-only -
            // no fresh probe is forced.
            EmitQuotaSnapshotToBus(info, cli.CliType, execution.StartedAt, runModel, runThinkingLevel, QuotaSnapshotPhases.Start);

            // Open / resume the pipeline-execution record and mark the CORE
            // "Agent execution" step Running so the Overview pipeline table
            // shows a live running indicator on the most important step from
            // t=0 instead of "- -". EnsureAgentRunStart starts a fresh record
            // when the prior run completed or crossed core/post before a
            // reissue short-circuit, so a restart is visible.
            RecordCoreRunStart(info, execution);

            // Record the deterministic reissue open-items pre-step next to the
            // loop guard so the Overview pipeline shows whether the orchestrator
            // intervened on this re-issue. Only fires on an actual re-issue.
            if (reissueOpenItems is { IsReissue: true })
                RecordReissueOpenItemsPreStep(info, reissueOpenItems, execution.StartedAt);

            return RunOutcome.Started(execution);
        }
        catch (Exception ex) when (!processStartConfirmed)
        {
            // Admission is a transaction until StartAsync returns a live
            // process. Any unexpected fault before that boundary must release
            // ownership and undo this call's lane move, otherwise Ready becomes
            // Progress with neither a run nor a bounded wake.
            if (claimedRunThisCall)
            {
                ReleaseRun(jobId);
            }
            else if (_pickupLock != null && _pickupLockOwner != null && acquiredPickupLockFolder != null)
            {
                var owner = _pickupLockOwner with { ProjectName = ProjectName, JobId = jobId };
                _pickupLock.Release(acquiredPickupLockFolder, owner);
            }

            if (admissionInfo != null)
            {
                try { WriteSpawnFailureDiagnostic(admissionInfo, admissionInfo.CliType ?? "unknown", ex.Message); }
                catch (Exception diagnosticEx) { _logger.LogDebug(diagnosticEx, "Could not persist admission-fault diagnostic for {JobId}", jobId); }
                if (consumedIntent is not null) _mutations.RollbackStashedPendingIntent(admissionInfo.FolderPath);
                if (movedToProgressThisCall)
                    RevertFailedStartFromProgress(jobId, admissionInfo, intent);
            }

            _logger.LogError(
                ex,
                "run_admission_failed jobId={JobId} project={Project} movedToProgress={Moved} claimed={Claimed}; ownership released and lane reconciled",
                jobId, ProjectName, movedToProgressThisCall, claimedRunThisCall);
            NotifyStatus();
            return RunOutcome.Reject(new RunRejection(
                RunRejectReason.CliUnavailable,
                $"Run admission failed before the CLI started: {ex.Message}"));
        }
        finally
        {
            if (processStartConfirmed)
                _activeRuns.Get(jobId)?.CompleteStartHandshake();
            _processing = false;
        }
    }

    private static RunPlan RebindPlanJobPaths(RunPlan plan, string promptPath, string jobFolder)
    {
        if (plan.PromptVariables.Count == 0) return plan;
        var variables = plan.PromptVariables.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (variables.ContainsKey("prompt_path")) variables["prompt_path"] = promptPath;
        if (variables.ContainsKey("job_folder")) variables["job_folder"] = jobFolder;
        return plan with { PromptVariables = variables };
    }

    /// <summary>
    /// Legacy Codex silent-completion hook. Codex now runs through
    /// <c>exec --experimental-json</c> and completion is process-exit based
    /// after stdout/stderr close, matching the official SDK. A missing
    /// terminal sentinel is only an outcome hint, not a reason to kill the
    /// process early.
    /// </summary>
    private void TickSilentCompletion()
    {
        return;
    }

    /// <summary>
    /// Per-tick watchdog pass. Walks the active CLI run for this project,
    /// computes silence + age, calls <see cref="Watchdog.DecideState"/>,
    /// and on a state transition either posts a chat meta message
    /// (Quiet -> Suspicious, Suspicious -> Hung, etc.) or kills the
    /// process tree (when transitioning into Hung). Same-state ticks are
    /// silent so the chat does not pile up identical notes.
    /// </summary>
    /// <summary>
    /// Number of in-flight coding runs occupying this project's slots. Summed
    /// across every project by <see cref="TaskRunnerService"/> to drive the
    /// system keep-awake power request.
    /// </summary>
    public int ActiveRunCount => _activeRuns.Count;

    /// <summary>
    /// Absorb a detected OS sleep of <paramref name="sleptSeconds"/> by resetting
    /// the silence clocks of every active run, so the watchdog does not mistake
    /// the nap for agent silence on the resume tick (the wall clock jumped
    /// forward by the sleep duration, but no agent actually went quiet). Resets
    /// both the per-phase activity clock (<see cref="_phaseByJob"/>) and the CLI
    /// service's last-streamed clock. Returns the number of runs reset.
    ///
    /// <para>
    /// The CLI child process is deliberately left untouched here: if it survived
    /// the suspend it simply keeps running with a fresh clock; if the OS killed
    /// it during sleep, the process has already exited and the normal
    /// exit/crash-recovery path picks it up - this method does not classify or
    /// reissue.
    /// </para>
    /// </summary>
    public int AbsorbSleep(double sleptSeconds)
    {
        var now = DateTime.UtcNow;
        var reset = 0;
        foreach (var run in _activeRuns.Snapshot())
        {
            var jobKey = TaskIdentity.CreateKey(Entry.Path, run.JobId);

            if (_phaseByJob.TryGetValue(jobKey, out var snap))
            {
                _phaseByJob[jobKey] = snap with { LastActivityAt = now };
            }

            if (run.CliType is { } cliType)
            {
                try { _router.Get(cliType).ResetSilenceClock(jobKey); }
                catch (Exception ex) { _logger.LogDebug(ex, "ResetSilenceClock skipped for {JobId}", run.JobId); }
            }

            reset++;
        }
        return reset;
    }

    private void TickWatchdog()
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || cliType == null) return;

        ICliExecutionService cli;
        try { cli = _router.Get(cliType); }
        catch { return; }

        var jobKey = TaskIdentity.CreateKey(Entry.Path, jobId);
        var exec = cli.GetExecution(jobKey);
        if (exec == null || !string.Equals(exec.Status, "running", StringComparison.OrdinalIgnoreCase))
            return;

        var lastStreamed = cli.GetLastStreamedAt(jobKey) ?? exec.StartedAt;
        var now = DateTime.UtcNow;
        var age = (now - exec.StartedAt).TotalSeconds;

        // ADR-0013: prefer the typed-event phase tracker when available.
        // It advances on actual protocol events, so silence here means
        // "no protocol activity in this phase", not "no stdout byte" -
        // a stronger signal that surfaces via the per-phase budgets.
        // Fall back to the legacy silence-only signal for CLIs that do
        // not yet emit run events.
        WatchdogState next;
        double silence;
        RunPhase? phase = null;
        var longOpActive = false;
        if (_phaseByJob.TryGetValue(jobKey, out var phaseSnap))
        {
            phase = phaseSnap.Phase;
            silence = (now - phaseSnap.LastActivityAt).TotalSeconds;
            // ASS-665: a known long-op (ng serve / build / dev-server-wait /
            // curl-poll-loop) legitimately produces no stdout while it runs.
            // While such a tool is in flight, widen the silence budget so the
            // wait is not mistaken for a hang.
            longOpActive = LongRunningOperationDetector.IsLongRunningOperation(phaseSnap.LastToolCommand);
            next = PhaseAwareWatchdog.DecideState(silence, age, phaseSnap.Phase, _watchdogConfig, _phaseBudgets, longOpActive);
        }
        else
        {
            silence = (now - lastStreamed).TotalSeconds;
            next = Watchdog.DecideState(silence, age, _watchdogConfig);
        }

        var prev = cli.GetWatchdogState(jobKey);
        if (!Watchdog.ShouldAnnounce(prev, next)) return;

        cli.SetWatchdogState(jobKey, next);

        var info = _scanner.FindJob(jobId, Entry.Path);
        if (info == null) return;

        var hungAtSeconds = phase is null
            ? _watchdogConfig.HungSeconds
            : PhaseAwareWatchdog.EffectiveBudget(phase.Value, _phaseBudgets, longOpActive).HungSeconds;
        var phaseTag = phase is null ? "" : $" [{PhaseAwareWatchdog.FormatBudgetReason(phase.Value, silence, _phaseBudgets, longOpActive)}]";
        var title = string.IsNullOrWhiteSpace(info.Title) ? info.Id : info.Title;
        var cliLabel = string.IsNullOrWhiteSpace(info.CliType) ? cliType : info.CliType;
        switch (next)
        {
            case WatchdogState.Quiet:
                // Soft first warning. Operator-friendly copy with task title +
                // CLI so the notification stands on its own without context.
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                    $"\"{title}\" ({cliLabel}): no output for {silence:F0}s yet. No action needed unless this repeats.{phaseTag}");
                break;
            case WatchdogState.Suspicious:
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                    $"\"{title}\" ({cliLabel}): no output for {silence:F0}s. Run will be auto-cancelled at {hungAtSeconds:F0}s. No action needed unless this repeats.{phaseTag}");
                break;
            case WatchdogState.Hung:
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogTimeout,
                    $"\"{title}\" ({cliLabel}): auto-cancelled after {silence:F0}s of silence. The run will finalize as failed.{phaseTag}");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Action,
                    Topic = OrchestratorLogTopics.Watchdog,
                    JobId = jobId,
                    Summary = $"Watchdog auto-cancelled \"{info.Title}\" after {silence:F0}s of silence.",
                    Reasoning = $"No streamed activity for {silence:F0}s (run age {age:F0}s){phaseTag}. Process tree terminated; the run finalizes as failed."
                });
                PreserveUnconsumedFollowUp(jobId, $"watchdog auto-cancel after {silence:F0}s of silence");
                try { cli.Stop(jobKey, RunStopReason.Watchdog); }
                catch (Exception ex) { _logger.LogWarning(ex, "Watchdog kill failed for {JobId}", jobId); }
                break;
            case WatchdogState.Healthy:
                if (prev != WatchdogState.Healthy)
                {
                    _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                        $"\"{title}\" ({cliLabel}): streaming output again.");
                }
                break;
        }
    }

    /// <summary>
    /// Watchdog thresholds for this runner. Loaded once from configuration
    /// when the runner is constructed; reuse across ticks. Defaults applied
    /// when nothing is configured.
    /// </summary>
    private WatchdogConfig _watchdogConfig = WatchdogConfig.Default;

    /// <summary>
    /// Per-phase silence budgets for this runner. Defaults to the hardcoded
    /// profile; replaced with a config-derived table (honoring any
    /// <c>Watchdog:Phase:*</c> overrides) by <see cref="ConfigureWatchdog"/>.
    /// </summary>
    private PhaseBudgetTable _phaseBudgets = PhaseBudgetTable.Default;

    /// <summary>
    /// Per-jobKey phase tracker. Populated as adapters emit
    /// <see cref="CliRunEvent"/> via the router. Cleared on
    /// <see cref="CliRunEvent.ProcessExited"/> / <see cref="CliRunEvent.Killed"/>.
    /// When a jobKey is missing from this map, the runner falls back to
    /// the silence-only watchdog (<see cref="Watchdog.DecideState"/>).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunPhaseSnapshot> _phaseByJob = new();

    /// <summary>
    /// Last seen phase + UTC of last activity-classified event, plus the
    /// command of the tool currently in flight. <see cref="LastToolCommand"/>
    /// is set on <see cref="CliRunEvent.ToolStarted"/> and cleared on
    /// <see cref="CliRunEvent.ToolCompleted"/>, so a non-null value means a
    /// tool is actively running; the watchdog reads it to widen the silence
    /// budget while that tool is a known long-op (ASS-665).
    /// </summary>
    private sealed record RunPhaseSnapshot(RunPhase Phase, DateTime LastActivityAt, string? LastToolCommand);

    /// <summary>Updates per-job phase + activity clock from a typed event.</summary>
    private void OnRunEventReceived(string jobKey, CliRunEvent evt)
    {
        var prev = _phaseByJob.TryGetValue(jobKey, out var existing) ? existing : new RunPhaseSnapshot(RunPhase.Spawning, DateTime.UtcNow, null);
        var nextPhase = RunPhaseTransitions.Apply(prev.Phase, evt);
        var lastActivity = RunPhaseTransitions.IsActivitySignal(evt) ? DateTime.UtcNow : prev.LastActivityAt;
        // Track the in-flight tool command so the watchdog can recognise a
        // long-op (dev server / build / poll loop). Set when a tool starts,
        // cleared when it completes; preserved across non-tool events.
        var lastToolCommand = evt switch
        {
            CliRunEvent.ToolStarted s   => $"{s.ToolName} {s.Argument}".Trim(),
            CliRunEvent.ToolCompleted   => null,
            _                           => prev.LastToolCommand
        };
        _phaseByJob[jobKey] = new RunPhaseSnapshot(nextPhase, lastActivity, lastToolCommand);

        // CAR 0.6 wait-on-quota lifecycle. Reuse the Run-Liveness visible
        // substate pattern: a durable marker backs the card, while Progress
        // receives an explicit phase instead of looking silently hung.
        if (evt is CliRunEvent.QuotaWaitStarted quotaWaitStarted)
        {
            try
            {
                var run = _activeRuns.ByJobKey(GetJobKey, jobKey);
                var info = run == null ? null : _scanner.FindJob(run.JobId, Entry.Path);
                if (info != null)
                {
                    var cliType = run?.CliType ?? "unknown";
                    var policy = _quotaWaitPolicy?.Resolve(_projectSettings.Get(ProjectName));
                    var reason = $"waiting for quota reset {quotaWaitStarted.ResetAt:HH:mm} UTC: {quotaWaitStarted.Reason}";
                    QuotaWaitMarker.Write(info.FolderPath, new QuotaWaitRecord
                    {
                        CliType = cliType,
                        StartedAt = quotaWaitStarted.ObservedAt,
                        ResetAt = quotaWaitStarted.ResetAt,
                        ThresholdMinutes = policy?.ThresholdMinutes ?? CliQuotaWaitPolicyService.DefaultThresholdMinutes,
                        Reason = reason,
                    }, _logger);
                    if (info.State == TaskStates.Progress)
                        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.QuotaWaiting);
                    _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-wait] " + reason);
                    _timeline?.Append(
                        info.FolderPath,
                        TimelineEventKinds.QuotaAdmissionDecision,
                        TimelineActors.System,
                        summary: reason,
                        details: new()
                        {
                            ["outcome"] = "Wait",
                            ["decision"] = "library-quota-wait-started",
                            ["cli"] = cliType,
                            ["resetAt"] = quotaWaitStarted.ResetAt.ToString("o"),
                        });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not project quota-wait start for {TaskKey}", jobKey); }
        }
        else if (evt is CliRunEvent.QuotaWaitEnded)
        {
            try
            {
                var run = _activeRuns.ByJobKey(GetJobKey, jobKey);
                var info = run == null ? null : _scanner.FindJob(run.JobId, Entry.Path);
                if (info != null)
                {
                    ClearQuotaWait(info);
                    if (info.State == TaskStates.Progress)
                        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.ExecutionRunning);
                    _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-wait] reset reached; restarting the same request");
                    _timeline?.Append(
                        info.FolderPath,
                        TimelineEventKinds.QuotaAdmissionDecision,
                        TimelineActors.System,
                        summary: "Quota reset reached; restarting the same request",
                        details: new() { ["decision"] = "library-quota-wait-ended" });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not project quota-wait end for {TaskKey}", jobKey); }
        }

        // Surface tool-call boundaries to disk so a post-mortem of a
        // watchdog kill can answer "what was the last tool the agent
        // started, with what arguments, did the result come back?".
        // The legacy text log already contains this implicitly; the
        // structured file makes it grep-friendly without parsing.
        try { AppendToolCallLog(jobKey, evt); }
        catch (Exception ex) { _logger.LogDebug(ex, "tool-calls.jsonl append failed for {TaskKey}", jobKey); }

        // Persist the agent's own task plan (Claude TodoWrite / Codex
        // update_plan) so the per-job plan strip can render progress without a
        // second model call. Read-only observability; best-effort like the
        // tool-call log above.
        if (evt is CliRunEvent.PlanUpdated plan)
        {
            try { AppendPlanSnapshotLog(jobKey, plan); }
            catch (Exception ex) { _logger.LogDebug(ex, "plan-snapshots.jsonl append failed for {TaskKey}", jobKey); }
        }

        // Sentinel-on-TurnCompleted gate. claude-code in stream-json mode
        // emits a `result:success` frame (mapped to TurnCompleted) and can
        // then linger indefinitely without exiting; AgentOutcomeAnalyzer
        // only fires from OnCliFinished, which only fires on OS exit, so a
        // job whose agent already wrote [[TASK_DONE]] hangs forever in
        // 3-progress until the watchdog or operator intervenes. Detect the
        // sentinel here and kill the lingering process so the existing exit
        // handler runs the analyzer + policy.
        if (evt is CliRunEvent.TurnCompleted && _sentinelStopRequested.TryAdd(jobKey, 1))
        {
            try { TryStopOnSentinel(jobKey); }
            catch (Exception ex) { _logger.LogWarning(ex, "Sentinel-stop check failed for {TaskKey}", jobKey); }
        }

        // Mirror per-turn token usage emitted by the coding-agent CLI onto
        // the agent message bus. Without this, the Codex streaming path is
        // invisible to BusAggregationCache, the project token summary, and
        // the workspace quota strip - they read kind:token-usage messages,
        // and a Codex run that never produces an orchestrator decision turn
        // would otherwise leave the workspace timeline blank for the run's
        // own spend. One emit per turn.completed frame.
        if (evt is CliRunEvent.TurnCompleted)
        {
            try { MirrorAgentTurnUsageToBus(jobKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "Per-turn bus mirror failed for {TaskKey}", jobKey); }
        }

        // Clean up on terminal events so a later run with the same key
        // does not inherit stale phase state.
        if (evt is CliRunEvent.RunEnded)
        {
            _phaseByJob.TryRemove(jobKey, out _);
            _sentinelStopRequested.TryRemove(jobKey, out _);
        }
    }

    /// <summary>
    /// Scan the buffered CLI output for a typed sentinel; if one is present
    /// AND the run's owning project is this one (active job match), ask the
    /// CLI service to kill the still-alive process tree. The kill flows back
    /// through the existing ProcessExited path, which fires
    /// <see cref="OnCliFinished"/> and lets the analyzer + policy do their
    /// usual work. Marker reason <see cref="RunStopReason.SentinelDetected"/>
    /// keeps the run-status classifier from labelling the kill as "stopped".
    /// </summary>
    private void TryStopOnSentinel(string jobKey)
    {
        // Only act for the run we're currently tracking. A stale TurnCompleted
        // from a previous run should never reach here, but guard anyway.
        if (GetActiveJobKey() != jobKey) return;
        var cliType = _activeCliType;
        if (string.IsNullOrEmpty(cliType)) return;
        if (string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)) return;

        var cli = _router.Get(cliType);
        var snapshot = cli.GetOutput(jobKey);
        if (snapshot == null || snapshot.Count == 0) return;

        // Use the same regex the analyzer uses, so detection here matches
        // the post-run path exactly. SentinelRegex is the published surface.
        // ROOT CAUSE FIX (2026-06-23): the live-stream sentinel scanner used to
        // match SentinelRegex on EVERY raw output line, so a run that merely READ
        // a file containing a [[TASK_DONE]] literal (the backend's own runner
        // code, AGENTS.md, and docs/system/contracts/agent-task.md are full of them - the
        // file content rides the "user"/tool-result stream) was killed mid-work as
        // a false "completion". The decision now lives in the tested pure helper
        // LiveSentinelScanner: agent-stream only + standalone sentinel line.
        if (!LiveSentinelScanner.HasStandaloneAgentSentinel(snapshot)) return;

        _logger.LogInformation(
            "TurnCompleted with sentinel for {TaskKey}; killing lingering {Cli} process so OnCliFinished can run.",
            jobKey, cliType);
        cli.Stop(jobKey, RunStopReason.SentinelDetected);
    }

    /// <summary>
    /// Mirror the coding-agent CLI's most recent <c>turn.completed</c> usage
    /// snapshot onto the agent message bus as a <c>kind:token-usage</c>
    /// message attributed to <c>agent:&lt;cli&gt;</c>. The CLI driver parses
    /// the frame (Codex uses <see cref="CodexUsageParser"/>) and stashes a
    /// <see cref="AgentStudio.Cli.ParsedTurnUsage"/>; the runner
    /// reads it here when the matching typed event arrives.
    /// <para>
    /// Without this, <c>BusAggregationCache.OnAppended</c> never sees the
    /// coding agent's input/output/cached tokens. The project token summary
    /// and workspace quota strip read off the bus, so a Codex run that
    /// completes without an orchestrator decision turn shows zero spend even
    /// though the CLI reported usage in its <c>turn.completed</c> frame.
    /// </para>
    /// </summary>
    private void MirrorAgentTurnUsageToBus(string jobKey)
    {
        if (_bus == null) return;
        if (GetActiveJobKey() != jobKey) return;
        var jobId = _activeJobId;
        if (string.IsNullOrEmpty(jobId)) return;
        var cliType = _activeCliType;
        if (string.IsNullOrEmpty(cliType)) return;

        var cli = _router.Get(cliType);
        // The coding-agent drivers that parse their CLI's terminal usage frame
        // expose the same (usage, observedAt, startedAt) stash. Codex parses
        // turn.completed; Claude parses its stream-json result frame. The
        // CORE agent run is usually claude, so omitting it here was the
        // "no token activity recorded" symptom. Any other CLI stays a clean
        // no-op until its adapter moves onto the shared parser.
        var snapshot = cli.GetLastParsedTurnUsage(jobKey);
        if (snapshot is null) return;
        var (usage, observedAt, startedAt) = snapshot.Value;

        var latency = new AgentMessageLatency(
            RequestedAt: startedAt,
            CompletedAt: observedAt,
            TotalMs: (long)Math.Max(0, (observedAt - startedAt).TotalMilliseconds));

        var runId = AgentMessageBusBridge.DeriveRunId(jobId!, startedAt);
        var participantId = AgentMessageBusBridge.ParticipantForCli(cliType);
        var topic = $"{cliType!.ToLowerInvariant()}-turn";

        _ = _bus.EmitTokenUsageRichAsync(
            ProjectName,
            jobId,
            runId,
            participantId,
            topic,
            usage,
            latency);
    }

    /// <summary>
    /// <summary>
    /// AGT-2100: mirror the CLI's currently cached quota snapshot onto the bus
    /// as a compact <c>observation</c> at a run boundary (start / end). This is a
    /// pure-read datapoint for the cap-forecast history: it uses
    /// <see cref="QuotaService.GetCachedFor"/> only, never forcing a fresh probe
    /// (no extra CLI call per run), and records the snapshot's age so a reader can
    /// tell a fresh reading from a stale one. Best-effort like every other bus
    /// mirror - a failure is logged and swallowed.
    /// </summary>
    private void EmitQuotaSnapshotToBus(
        TaskInfo? info, string? cliType, DateTime startedAt,
        string? model, string? thinkingLevel, string phase)
    {
        if (_bus == null || info == null || string.IsNullOrWhiteSpace(cliType)) return;
        try
        {
            var snapshot = _quotaService.GetCachedFor(cliType!);
            var runId = AgentMessageBusBridge.DeriveRunId(info.Id, startedAt);
            _ = _bus.EmitQuotaSnapshotAsync(
                ProjectName, info.Id, runId, cliType!, model, thinkingLevel,
                phase, snapshot, _quotaService.Ttl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bus mirror of quota snapshot ({Phase}) failed for {JobId}", phase, info.Id);
        }
    }

    /// Append one structured line to <c>logs/tool-calls.jsonl</c> per
    /// <see cref="CliRunEvent.ToolStarted"/> / <see cref="CliRunEvent.ToolCompleted"/>
    /// observed. Silent on other event types. The file lives next to
    /// <c>cli-output.log</c> in the job folder so a post-mortem has both
    /// in the same place.
    /// </summary>
    private void AppendToolCallLog(string jobKey, CliRunEvent evt)
    {
        if (evt is not CliRunEvent.ToolStarted and not CliRunEvent.ToolCompleted) return;

        var jobFolder = TaskKeyToFolderPath(jobKey);
        if (jobFolder == null) return;
        var logsDir = System.IO.Path.Combine(jobFolder, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { return; }
        var path = System.IO.Path.Combine(logsDir, "tool-calls.jsonl");

        object record = evt switch
        {
            CliRunEvent.ToolStarted s   => new { ts = DateTime.UtcNow, kind = "started",   tool = s.ToolName, argument = s.Argument },
            CliRunEvent.ToolCompleted c => new { ts = DateTime.UtcNow, kind = "completed", tool = c.ToolName, isError = c.IsError, firstLine = c.FirstLine },
            _ => new { ts = DateTime.UtcNow, kind = "other" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        try { System.IO.File.AppendAllText(path, json + Environment.NewLine); } catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort"); /* best-effort */ }
    }

    /// <summary>
    /// Append one snapshot line to <c>logs/plan-snapshots.jsonl</c> for a
    /// <see cref="CliRunEvent.PlanUpdated"/>. Append-only, one line per distinct
    /// plan frame. Consecutive frames whose items are byte-identical to the
    /// previous snapshot are skipped so a CLI that re-emits an unchanged plan
    /// does not inflate the ticker. The taxonomy's "at most one active item"
    /// rule is enforced here: a frame marking two items active keeps the first
    /// and downgrades the rest to <c>pending</c>, so the persisted snapshot is
    /// already clean for the reader.
    /// </summary>
    private void AppendPlanSnapshotLog(string jobKey, CliRunEvent.PlanUpdated evt)
    {
        if (evt.Items.Count == 0) return;

        var jobFolder = TaskKeyToFolderPath(jobKey);
        if (jobFolder == null) return;
        var logsDir = System.IO.Path.Combine(jobFolder, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { return; }
        var path = System.IO.Path.Combine(logsDir, "plan-snapshots.jsonl");

        // Enforce single-active: keep the first 'active', demote the rest.
        var seenActive = false;
        var items = new List<object>(evt.Items.Count);
        var signature = new System.Text.StringBuilder();
        foreach (var it in evt.Items)
        {
            var status = it.Status;
            if (status == "active")
            {
                if (seenActive) status = "pending";
                else seenActive = true;
            }
            items.Add(new { id = it.Id, title = it.Title, status });
            signature.Append(it.Id).Append('=').Append(status).Append(';');
        }
        var sig = signature.ToString();

        // Dedup against the last snapshot to keep the file noise-free.
        int seq = 1;
        try
        {
            if (System.IO.File.Exists(path))
            {
                string? lastLine = null;
                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    seq++;
                    lastLine = raw;
                }
                if (lastLine != null && SnapshotSignature(lastLine) == sig) return; // unchanged plan
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: fall through with seq=1; a torn file should not block a write"); /* fall through with seq=1; a torn file should not block a write */ }

        var record = new { ts = DateTime.UtcNow, seq, source = evt.Source, items };
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        try { System.IO.File.AppendAllText(path, json + Environment.NewLine); }
        catch { return; }

        _logger.LogInformation(
            "plan-snapshot seq={Seq} source={Source} items={ItemCount} for {TaskKey}",
            seq, evt.Source, evt.Items.Count, jobKey);
    }

    /// <summary>
    /// Recompute the id=status;... signature of a persisted snapshot line so
    /// <see cref="AppendPlanSnapshotLog"/> can dedup against it without trusting
    /// a serialized field. Returns empty string for an unparseable line so a
    /// torn record never matches a real one.
    /// </summary>
    private static string SnapshotSignature(string jsonLine)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonLine.TrimStart('﻿'));
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != System.Text.Json.JsonValueKind.Array) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var it in items.EnumerateArray())
            {
                var id = it.TryGetProperty("id", out var i) ? i.GetString() : null;
                var status = it.TryGetProperty("status", out var s) ? s.GetString() : null;
                sb.Append(id).Append('=').Append(status).Append(';');
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Resolve a <c>jobKey</c> shaped as <c>watchPath::jobId</c> back into
    /// the on-disk folder for the job. The job may currently live in any
    /// lane; we walk the canonical lane order until one resolves.
    /// </summary>
    private string? TaskKeyToFolderPath(string jobKey)
    {
        var sep = jobKey.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return null;
        var watchPath = jobKey[..sep];
        var jobId = jobKey[(sep + 2)..];
        // Most likely to find an active job in 3-progress; fall through
        // the rest of the lifecycle if not.
        foreach (var lane in new[] { TaskStates.Progress, TaskStates.FailedPickup, TaskStates.CodeNotComplete, TaskStates.AutoReview, TaskStates.Escalated, TaskStates.HumanReview, TaskStates.Preparation, TaskStates.Ready, TaskStates.Backlog, TaskStates.Completed, TaskStates.Archive })
        {
            var candidate = System.IO.Path.Combine(watchPath, lane, jobId);
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Set by <see cref="TaskRunnerService"/> on construction.</summary>
    public void ConfigureWatchdog(WatchdogConfig config, PhaseBudgetTable? phaseBudgets = null)
    {
        _watchdogConfig = config;
        _phaseBudgets = phaseBudgets ?? PhaseBudgetTable.Default;
    }

    /// <summary>Set by <see cref="TaskRunnerService"/> on construction.</summary>
    public void ConfigureStuckLoopBudget(StuckLoopBudget budget) => _stuckLoopBudget = budget;

    /// <summary>
    /// Snapshot of the current auto-loop state for a job. Used by the
    /// jobs endpoint so the UI can render a "stuck loop N/5" badge.
    /// Returns null when no loop is in flight for this job.
    /// </summary>
    public StuckLoopState? GetStuckLoopState(string jobId) =>
        _stuckLoops.TryGetValue(jobId, out var s) ? s : null;

    /// <summary>
    /// In-memory run facts for one task (ASS-1751): whether it occupies a live
    /// slot, the rapid-crash backoff deadline (if armed), and the
    /// fail-without-progress streak. Read-only snapshot of the same dictionaries
    /// the pickup loop consults; all are cleared on a backend restart, so an
    /// orphaned task naturally reports no slot / no backoff / zero failures. The
    /// endpoint overlay maps these onto a <see cref="TaskRunActivity"/> via
    /// <see cref="TaskRunActivityClassifier"/>.
    /// </summary>
    public RunActivityFacts GetRunActivity(string jobId)
    {
        var slotActive = _activeRuns.HoldsExecutionSlot(jobId);
        DateTime? backoffUntil = _rapidCrashBackoffUntil.TryGetValue(jobId, out var until) ? until : null;
        var failures = _consecutiveFailNoProgress.TryGetValue(jobId, out var n) ? n : 0;
        return new RunActivityFacts(slotActive, backoffUntil, failures);
    }

    public QuotaFallbackStatus? GetQuotaFallback(string jobId)
    {
        var run = _activeRuns.Get(jobId);
        if (run?.FallbackFromCliType == null || string.IsNullOrWhiteSpace(run.CliType)) return null;
        var execution = _router.Get(run.CliType).GetExecution(GetJobKey(jobId));
        return new QuotaFallbackStatus(run.CliType, execution?.Model, run.QuotaFallbackReason);
    }

    /// <summary>
    /// ASS-1753: re-book a CLI-tracked live run into the in-memory slot registry
    /// after a backend restart cleared it. Idempotent - a no-op when the job
    /// already holds a slot (e.g. it was re-picked or resumed in the meantime),
    /// so a per-tick / per-boot caller can run it freely. Additive only: this
    /// claims a slot but NEVER releases one (release stays owned by the
    /// run-finish path), so it cannot race the spawn-time claim. Returns true
    /// only when this call booked a fresh slot.
    /// </summary>
    internal bool RegisterRecoveredRun(string jobId, string? cliType)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        if (_activeRuns.Contains(jobId)) return false;

        var recoveredRun = new ActiveRun
        {
            JobId = jobId,
            CliType = cliType,
            Intent = RunIntent.AutoPickup,
        };
        var claimed = _activeRuns.TryClaim(recoveredRun);
        if (!claimed) return false;

        var slotMax = ParallelSlotPolicy.ClampMax(_projectSettings.Get(ProjectName).MaxParallelism);
        _logger.LogInformation(
            "[taskboard] recovered live run re-booked into slot for {JobId} on {Project} (cli={Cli}); occupancy {Occupied}/{Max}",
            jobId, ProjectName, cliType ?? "unknown", _activeRuns.Count, slotMax);

        TaskInfo? info = null;
        try { info = _scanner.FindJob(jobId, Entry.Path); }
        catch (Exception ex) { _logger.LogDebug(ex, "RegisterRecoveredRun: FindJob threw for {JobId}", jobId); }
        if (info != null && !string.IsNullOrWhiteSpace(info.FolderPath))
        {
            recoveredRun.JobFolder = info.FolderPath;
            _timeline?.Append(
                info.FolderPath,
                TimelineEventKinds.RunnerSlotAdmission,
                TimelineActors.System,
                summary: $"Slot {_activeRuns.Count}/{slotMax} recovered after restart: re-booked live {cliType ?? "cli"} run",
                details: new()
                {
                    ["recovered"] = "true",
                    ["cli"] = cliType ?? string.Empty,
                    ["occupied"] = _activeRuns.Count.ToString(),
                    ["maxParallelism"] = slotMax.ToString(),
                });
        }

        NotifyStatus();
        return true;
    }

    /// <summary>
    /// One-shot post-restart slot reconcile (ASS-1753). A restart clears the
    /// in-memory slot registry, but the CLI router can still own live runs for
    /// this project's tasks - a CLI that reattaches on startup, or a
    /// run whose OS process outlived the restart and is still tracked. Re-book
    /// those into the registry so occupied slots == genuinely-tracked live runs.
    /// Without this the pickup gate under-counts occupancy and, at
    /// MaxParallelism &gt; 1 (where the sequential <c>IsRunningForProject</c>
    /// guard is bypassed), could admit a duplicate run against a still-live
    /// task; the 3-progress badge also mis-renders a running task as "no active
    /// run". Runs once per process; the normal claim/release path keeps the
    /// registry accurate from then on.
    /// </summary>
    private void ReconcileRecoveredRunsIntoSlots()
    {
        if (_recoveredRunsReconciled) return;
        _recoveredRunsReconciled = true;

        // jobKey == "<watchPath>::<jobId>"; build the project-scoped prefix once
        // so only this project's tracked live runs are considered.
        var prefix = TaskIdentity.CreateKey(Entry.Path, string.Empty);
        foreach (var cli in _router.All)
        {
            IReadOnlyList<(string JobKey, CliExecution Execution)> running;
            try { running = cli.RunningExecutions(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ReconcileRecoveredRunsIntoSlots: RunningExecutions threw for {Cli}", cli.CliType);
                continue;
            }

            foreach (var (jobKey, _) in running)
            {
                if (!jobKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var jobId = jobKey.Substring(prefix.Length);
                RegisterRecoveredRun(jobId, cli.CliType);
            }
        }
    }

    private static bool IsAutoMode(string mode)
        => string.Equals(mode, "auto-continuous", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, "auto-single", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A NeedsInput run is unattended when it was auto-picked, even if the
    /// project mode flipped to manual before the completion callback observed
    /// the sentinel. Current auto mode also covers automatic continuations.
    /// This decision deliberately does not depend on a captured run plan: the
    /// bounded-wait invariant must survive missing optional run metadata.
    /// </summary>
    internal static bool ShouldHandleNeedsInputUnattended(
        AgentOutcomeKind outcomeKind, RunIntent intent, string mode, TaskInfo? activeInfo)
        => activeInfo != null
           && outcomeKind == AgentOutcomeKind.NeedsInput
           && !TaskModes.IsConcept(activeInfo.Mode)
           && (intent == RunIntent.AutoPickup || IsAutoMode(mode));

    /// <summary>
    /// Boot the long-lived orchestrator session for this project (Phase H).
    /// Reads the persisted session id; if one is already on disk, we
    /// keep it and skip the boot call so a backend restart does not
    /// re-burn a few thousand input tokens. Otherwise sends a boot
    /// prompt that loads the project's README / AGENTS / ROADMAP plus
    /// recent orchestrator activity, captures the resulting session id,
    /// and persists it. Fire-and-forget from the runner host service.
    /// </summary>
    public async Task BootOrchestratorSessionAsync(CancellationToken ct)
    {
        var existing = _orchestratorSessions.Read(Entry.Path);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.SessionId))
        {
            _logger.LogInformation(
                "[orchestrator] reusing persisted session {SessionId} for {Project} (calls so far: {Calls})",
                existing.SessionId, ProjectName, existing.Calls);
            return;
        }

        // AGT-1812: project override -> workspace default (-> platform default below).
        var modelOverride = _orchestratorDefaults?.ResolveModelOverride(ProjectName)
            ?? _projectSettings.Get(ProjectName).OrchestratorModel;
        var modelId = string.IsNullOrWhiteSpace(modelOverride) ? OrchestratorRunner.DefaultModel : modelOverride!;

        var bootPrompt = BuildOrchestratorBootPrompt(modelId);

        _logger.LogInformation("[orchestrator] booting session for {Project} on {Model}", ProjectName, modelId);
        var result = await _orchestratorRunner.DecideAsync(bootPrompt, modelId, Entry.RootPath, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.CapturedSessionId))
        {
            _logger.LogWarning(
                "[orchestrator] boot failed for {Project}: success={Success}, sessionId={SessionId}, error={Error}",
                ProjectName, result.Success, result.CapturedSessionId, result.ErrorMessage);
            return;
        }

        var session = new OrchestratorSession(
            SessionId: result.CapturedSessionId!,
            Model: result.Model,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: TruncatePreview(bootPrompt, 2000),
            BootReplyPreview: TruncatePreview(result.ReplyText, 600),
            CumulativeInputTokens: result.TokenUsage?.InputTokens ?? 0,
            CumulativeOutputTokens: result.TokenUsage?.OutputTokens ?? 0,
            CumulativeCacheReadTokens: result.TokenUsage?.CacheReadTokens ?? 0,
            CumulativeCacheCreationTokens: result.TokenUsage?.CacheCreationTokens ?? 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow,
            LastError: null);
        _orchestratorSessions.Write(Entry.Path, session);

        _orchestratorLog.Append(Entry.Path, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Action,
            Topic = "orchestrator-boot",
            Summary = $"Orchestrator session booted on {result.Model}.",
            Reasoning = $"Session id: {session.SessionId}. Boot loaded project README / AGENTS / ROADMAP plus recent orchestrator activity. Subsequent decisions resume this session.",
            TokenUsage = result.TokenUsage
        });

        // Mirror the boot's token spend onto the bus so the workspace timeline
        // captures the boot cost as a first-class event. Prefer the rich emit
        // (carries context-window snapshot + per-call latency) when the runner
        // has the parsed usage; fall back to the legacy emit otherwise.
        if (result.ParsedUsage != null)
        {
            try
            {
                _ = _bus?.EmitTokenUsageRichAsync(
                    ProjectName, jobId: null, runId: null,
                    AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: "orchestrator-boot",
                    usage: result.ParsedUsage,
                    latency: result.Latency);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator boot token usage failed for {Project}", ProjectName); }
        }
        else if (result.TokenUsage != null)
        {
            try
            {
                _ = _bus?.EmitTokenUsageAsync(
                    ProjectName, jobId: null,
                    AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: "orchestrator-boot",
                    usage: result.TokenUsage);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator boot token usage failed for {Project}", ProjectName); }
        }
    }

    /// <summary>
    /// Build the boot prompt: project facts, the top of any README /
    /// AGENTS / ROADMAP at the watched path's repository, and a
    /// summary of recent orchestrator activity. Truncated so the boot
    /// stays cheap. Total target: under 8 KB so even on Opus the boot
    /// is a few cents at most.
    /// </summary>
    private string BuildOrchestratorBootPrompt(string model)
    {
        var context = new System.Text.StringBuilder();
        context.Append($"- Watch path: {Entry.Path}");
        context.Append($"\n- Working directory: {Entry.RootPath}");
        if (!string.IsNullOrWhiteSpace(Entry.RepositoryPath))
            context.Append($"\n- Git repository: {Entry.RepositoryPath}");

        var docs = new System.Text.StringBuilder();
        AppendDocSnippet(docs, "AGENTS.md", Entry.RootPath, 2_000);
        AppendDocSnippet(docs, "README.md", Entry.RootPath, 2_000);
        AppendDocSnippet(docs, "ROADMAP.md", Entry.RootPath, 1_500);

        // Recent orchestrator activity, last 10 entries newest-first.
        var activity = new System.Text.StringBuilder();
        var entries = _orchestratorLog.Read(Entry.Path);
        if (entries.Count > 0)
        {
            activity.AppendLine("Recent orchestrator activity (newest first, latest 10):");
            foreach (var e in entries.AsEnumerable().Reverse().Take(10))
                activity.AppendLine($"- [{e.Kind}/{e.Topic}] {e.Summary}");
            activity.AppendLine();
        }

        return _prompts.Render(ProjectBootTemplate, new Dictionary<string, string?>
        {
            ["project_name"] = ProjectName,
            ["project_context"] = context.ToString(),
            ["doc_snippets"] = docs.ToString(),
            ["activity_block"] = activity.ToString(),
        }, new PromptCallContext(ProjectName, "orchestrator-boot", model));
    }

    private static void AppendDocSnippet(System.Text.StringBuilder sb, string fileName, string root, int maxChars)
    {
        try
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            sb.AppendLine($"--- {fileName} (truncated to {maxChars} chars) ---");
            sb.AppendLine(text.Length > maxChars ? text[..maxChars] + "\n... [truncated]" : text);
            sb.AppendLine();
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort: missing or unreadable docs are fine"); /* best-effort: missing or unreadable docs are fine */ }
    }

    private static string TruncatePreview(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>
    /// Phase E. The active agent emitted [[TASK_NEEDS_INPUT:...]] in auto
    /// mode; the user is not here to answer. Spawn the orchestrator with
    /// the project's configured model (default Opus 4.7), ask it for the
    /// reply the user would give, log the decision (with token usage), and
    /// feed the reply back as a Continue follow-up so the run picks up
    /// where it asked. If the orchestrator declines (returns BLOCK or
    /// errors), accept the NeedsInput state and notify the user via the
    /// chat - the same fallback the manual path uses.
    /// </summary>
    private async Task RunOrchestratorDecisionAsync(TaskInfo info, string jobId, AgentOutcome outcome)
    {
        try
        {
            // Circuit breaker check BEFORE we release the active-job latch
            // or call the orchestrator. If we've already burned the loop's
            // iteration / token budget on this job, surface a meta line
            // and leave the question for the user instead of spending more.
            // The state survives until cleared on a non-NeedsInput outcome
            // (Done/Blocked) - see OnCliFinishedAsync.
            var existingLoop = _stuckLoops.TryGetValue(jobId, out var prior) ? prior : null;
            if (existingLoop != null
                && StuckLoopGuard.Decide(existingLoop, _stuckLoopBudget) == StuckLoopVerdict.CircuitBreak)
            {
                _logger.LogWarning(
                    "[orchestrator] circuit-breaker fired for {JobId}: {Iters} iters, {Tokens} tokens",
                    jobId, existingLoop.IterationCount, existingLoop.CumulativeOrchestratorTokens);
                _chatLog.Append(info, OrchestratorMessageKind.GiveUp,
                    StuckLoopGuard.FormatBreakerMessage(existingLoop, _stuckLoopBudget));
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Intervention,
                    Topic = "auto-loop-circuit-break",
                    JobId = jobId,
                    Summary = $"Auto-loop circuit-breaker fired for \"{info.Title}\".",
                    Reasoning = $"Iterations {existingLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}; orchestrator tokens {existingLoop.CumulativeOrchestratorTokens}/{_stuckLoopBudget.MaxOrchestratorTokens}. Loop stopped to preserve quota; awaiting user."
                });
                // Mark the detected loop in the pipeline table (acceptance:
                // "erkannter Loop wird frueh markiert + in der Step-Tabelle angezeigt").
                RecordLoopGuard(info, PipelineStepStatus.Failed,
                    verdict: "loop-detected",
                    summary: StuckLoopGuard.FormatBreakerMessage(existingLoop, _stuckLoopBudget));
                // The card is now waiting on the user with the loop budget spent.
                // Release the active-job latch (as the STEER / decline branches
                // below do) so the seat is freed and this card is no longer the
                // runner's active job - otherwise it would pin the slot with no
                // live run behind it. Slice B then bounds the wait: the
                // steer-timeout monitor escalates it after the timeout so it
                // cannot hang forever.
                ReleaseRun(jobId);
                NotifyStatus();
                MarkSteerPending(info, jobId, SteerPendingKinds.BlockedDeferral, outcome.Summary, ask: null);
                return;
            }

            // The agent came back asking again (existingLoop non-null) but we are
            // still under budget: a loop is forming. Surface the pressure on the
            // loop-guard row now, before the breaker fires, so a building loop is
            // visible early rather than only at the hard stop.
            if (existingLoop != null)
            {
                RecordLoopGuard(info, PipelineStepStatus.Passed,
                    verdict: "looping",
                    summary: $"Auto-mode loop forming: {existingLoop.IterationCount}/{_stuckLoopBudget.MaxIterations} iterations, "
                           + $"{existingLoop.CumulativeOrchestratorTokens}/{_stuckLoopBudget.MaxOrchestratorTokens} orchestrator tokens used.");
            }

            // Release the active-job latch so the orchestrator's spawned
            // Continue can claim it; we mirror the re-issue path's release.
            ReleaseRun(jobId);
            _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.LoopWaiting);
            _logger.LogInformation(
                "run-loop-waiting project={Project} job={JobId} occupied={Occupied}/{Max}",
                ProjectName, jobId, _activeRuns.Count, SlotMax());
            NotifyStatus();

            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var promptText = ReadPromptText(promptPath);
            var lastAgentText = outcome.Summary ?? "(no agent summary captured)";
            var attachmentsList = BuildAttachmentsList(info.FolderPath);

            var orchestratorPrompt = BuildOrchestratorPrompt(_prompts, info, promptText, lastAgentText, attachmentsList);
            // AGT-1812: project override -> workspace default; null keeps the
            // existing session-model / platform-default fallback chain below.
            var modelOverride = _orchestratorDefaults?.ResolveModelOverride(info.ProjectName)
                ?? _projectSettings.Get(info.ProjectName).OrchestratorModel;

            // Resume the long-lived session if one is on disk; the
            // orchestrator already has project context + recent decisions
            // in its history, so a tighter "current question" prompt is
            // enough. Falls back to one-shot if no session is booted yet
            // (boot at app start may still be in flight or have failed).
            var session = _orchestratorSessions.Read(Entry.Path);
            var modelToUse = modelOverride ?? session?.Model ?? OrchestratorRunner.DefaultModel;
            _logger.LogInformation(
                "[orchestrator] auto-deciding for {JobId} on model {Model} (session={SessionId})",
                jobId, modelToUse, session?.SessionId ?? "<one-shot>");

            OrchestratorDecisionResult result;
            if (session != null && !string.IsNullOrWhiteSpace(session.SessionId))
            {
                var resumePrompt = BuildOrchestratorResumePrompt(_prompts, info, lastAgentText, attachmentsList);
                // Rejection-recovery lives on the runner (ResumeWithFallbackAsync)
                // so the per-job and global-chat orchestrator paths cannot drift
                // apart again - see docs/system/contracts/code-patterns.md "orchestrator-resume-with-fallback".
                var resumeRejected = false;
                result = await _orchestratorRunner.ResumeWithFallbackAsync(
                    session.SessionId,
                    resumePrompt,
                    fallbackPromptBuilder: () => orchestratorPrompt,
                    onSessionRejected: () =>
                    {
                        _orchestratorSessions.Clear(Entry.Path);
                        resumeRejected = true;
                    },
                    modelToUse,
                    Entry.RootPath,
                    CancellationToken.None);

                if (!resumeRejected && result.Success)
                {
                    // Accumulate cumulative usage onto the persisted session.
                    var updated = OrchestratorSessionStore.AccumulateUsage(session, result.TokenUsage, error: null);
                    _orchestratorSessions.Write(Entry.Path, updated);
                }
            }
            else
            {
                result = await _orchestratorRunner.DecideAsync(
                    orchestratorPrompt, modelToUse, Entry.RootPath, CancellationToken.None);
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.ReplyText))
            {
                // Orchestrator errored. Surface the question to the user the
                // same way the manual path does, plus a meta line explaining
                // why the orchestrator could not decide. Update the loop
                // counter so a series of declines also hits the circuit
                // breaker; the iteration is the "we tried" event, not "we
                // succeeded".
                _stuckLoops[jobId] = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: null,
                    error: result.ErrorMessage,
                    now: DateTime.UtcNow);

                var why = result.ErrorMessage ?? "the orchestrator chose to defer this decision";
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[orchestrator] Declined to auto-decide on agent's NEEDS_INPUT: {why}. Leaving the question for you.");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Observation,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Orchestrator declined to decide for \"{info.Title}\".",
                    Reasoning = why,
                    TokenUsage = result.TokenUsage
                });
                // Slice B: the orchestrator errored / deferred, so this auto-mode
                // card is now waiting unattended. Bound the wait via the marker.
                MarkSteerPending(info, jobId, SteerPendingKinds.BlockedDeferral, outcome.Summary, ask: null);
                return;
            }

            // Three-way classification on the orchestrator's reply. STEER is
            // the productive escalation: the orchestrator could not pick a
            // path on its own but identified a concrete unblocking ask
            // (screenshot, choice between options, missing doc). REPLY is
            // the existing happy path - feed it back as a Continue. BLOCK
            // is the silent deferral preserved as a last resort.
            var parsed = OrchestratorReplyParser.Parse(result.ReplyText);

            if (parsed.Kind == OrchestratorReplyKind.Block)
            {
                _stuckLoops[jobId] = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: null,
                    error: parsed.ParseWarning,
                    now: DateTime.UtcNow);

                var why = parsed.ParseWarning ?? "the orchestrator chose to defer this decision";
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[orchestrator] Declined to auto-decide on agent's NEEDS_INPUT: {why}. Leaving the question for you.");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Observation,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Orchestrator declined to decide for \"{info.Title}\".",
                    Reasoning = why,
                    TokenUsage = result.TokenUsage
                });
                // Slice B: the orchestrator deferred to the user, so this auto-mode
                // card is now waiting unattended. Bound the wait via the marker.
                MarkSteerPending(info, jobId, SteerPendingKinds.BlockedDeferral, outcome.Summary, ask: null);
                return;
            }

            if (parsed.Kind == OrchestratorReplyKind.Steer)
            {
                // Productive escalation: write a typed Steer chat message so
                // the frontend renders it distinctly (question-mark glyph,
                // option buttons, screenshot affordance). The job stays in
                // NeedsInput - we never re-issue on Steer; the user answers
                // and that becomes the next Continue.
                var nextLoopSteer = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: parsed.ReplyText,
                    error: null,
                    now: DateTime.UtcNow);
                _stuckLoops[jobId] = nextLoopSteer;

                var formatted = OrchestratorReplyParser.FormatSteerForChat(parsed);
                _chatLog.Append(info, OrchestratorMessageKind.Steer,
                    $"[orchestrator] {formatted}");

                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Decision,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Steered for \"{info.Title}\" (loop {nextLoopSteer.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(parsed.Need ?? "", 140)}",
                    Reasoning = $"Orchestrator could not pick a path alone but identified a concrete unblocking ask. Need: {parsed.Need}. Why: {parsed.Why ?? "(not given)"}. Options: {(parsed.Options is { Count: > 0 } ? string.Join(" | ", parsed.Options) : "(none)")}. Job left in NeedsInput; the user's answer will become the next Continue.",
                    TokenUsage = result.TokenUsage
                });

                if (result.ParsedUsage != null)
                {
                    try
                    {
                        _ = _bus?.EmitTokenUsageRichAsync(
                            info.ProjectName, info.Id, runId: null,
                            AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                            topic: "orchestrator-steer",
                            usage: result.ParsedUsage,
                            latency: result.Latency);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator steer token usage failed for {JobId}", jobId); }
                }
                else if (result.TokenUsage != null)
                {
                    try
                    {
                        _ = _bus?.EmitTokenUsageAsync(
                            info.ProjectName, info.Id,
                            AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                            topic: "orchestrator-steer",
                            usage: result.TokenUsage);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator steer token usage failed for {JobId}", jobId); }
                }

                // Slice B: the job is left in NeedsInput waiting for the user's
                // answer to the steer. Record the durable steer-pending marker +
                // visible phase so the wait is bounded (the steer-timeout monitor
                // auto-answers or escalates after the timeout) and never hangs.
                MarkSteerPending(info, jobId, SteerPendingKinds.Steer, outcome.Summary, parsed.Need);
                return;
            }

            var reply = parsed.ReplyText;

            // Successful decision. Advance the loop counter so we know how
            // many auto-decisions this job has burned and bail through the
            // circuit breaker on the next NEEDS_INPUT if budget is gone.
            var nextLoop = StuckLoopGuard.Next(
                existingLoop, result.TokenUsage,
                question: outcome.Summary, reply: reply,
                error: null,
                now: DateTime.UtcNow);
            _stuckLoops[jobId] = nextLoop;

            _chatLog.Append(info, OrchestratorMessageKind.Decision,
                $"[orchestrator] Auto-mode decision (loop {nextLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(reply, 200)}");
            _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Decision,
                Topic = "agent-needs-input",
                JobId = jobId,
                Summary = $"Auto-decided for \"{info.Title}\" (loop {nextLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(reply, 140)}",
                Reasoning = $"Project mode is {_mode}; the active agent emitted NEEDS_INPUT and the orchestrator was invoked to reply on the user's behalf. " +
                            $"The reply will be sent as a Continue follow-up. Model: {result.Model}. " +
                            $"Cumulative orchestrator tokens this loop: {nextLoop.CumulativeOrchestratorTokens}.",
                TokenUsage = result.TokenUsage
            });

            // Mirror orchestrator token spend onto the bus so the project
            // screen can rank expensive turns. orchestrator.jsonl stays
            // canonical for the per-job rollup; the bus carries one event
            // per decision turn.
            if (result.ParsedUsage != null)
            {
                try
                {
                    _ = _bus?.EmitTokenUsageRichAsync(
                        info.ProjectName, info.Id, runId: null,
                        AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                        topic: "orchestrator-decision",
                        usage: result.ParsedUsage,
                        latency: result.Latency);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator token usage failed for {JobId}", jobId); }
            }
            else if (result.TokenUsage != null)
            {
                try
                {
                    _ = _bus?.EmitTokenUsageAsync(
                        info.ProjectName, info.Id,
                        AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                        topic: "orchestrator-decision",
                        usage: result.TokenUsage);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator token usage failed for {JobId}", jobId); }
            }

            // Feed the orchestrator's reply back as a Continue. Reuses the
            // existing path (RunPlanner picks Resume vs Recovery based on
            // captured session id), so this is structurally identical to
            // the user typing the same reply in the chat.
            var continuation = await RunCliAsync(jobId, RunIntent.UserContinue, reply, reissueAttempt: 0,
                                                 mode: ContinueModes.Continue, CancellationToken.None);
            if (continuation.Rejection?.Reason == RunRejectReason.ProjectBusy)
            {
                // Another live CLI won the freed seat. Persist the continuation
                // on this still-visible loop-waiting card; the ordinary progress
                // pickup path consumes it once admission has room again.
                _mutations.SavePendingIntent(
                    jobId, ContinueModes.Continue, reply,
                    reason: "loop-continuation-slot-wait",
                    activeJobId: continuation.Rejection.BusyJobId,
                    watchPath: Entry.Path);
                _logger.LogInformation(
                    "loop-continuation-queued project={Project} job={JobId} busyJob={BusyJob} occupied={Occupied}/{Max}",
                    ProjectName, jobId, continuation.Rejection.BusyJobId, _activeRuns.Count, SlotMax());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator decision flow crashed for {JobId}", jobId);
            try
            {
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    "[orchestrator] Auto-decision flow crashed; left the agent's question for you.");
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner:3033"); }
        }
    }

    /// <summary>
    /// Tighter prompt for an orchestrator session that already has the
    /// project's boot context loaded. We only re-send the current
    /// situation; everything else is in the session memory.
    /// <para>
    /// Attachments: when the user attached files to the task (typically a
    /// screenshot that the agent's question hinges on), we list the
    /// absolute paths so the orchestrator can read them with its Read tool.
    /// Without this, the orchestrator decides blind on tasks whose entire
    /// context lives in an image.
    /// </para>
    /// </summary>
    internal static string BuildOrchestratorResumePrompt(RuntimePromptService prompts, TaskInfo info, string lastAgentText, string attachmentsList)
    {
        var attachmentsBlock = AttachmentsHasFiles(attachmentsList)
            ? "\n\n" + prompts.Render(DecisionAttachmentsResumeTemplate, new Dictionary<string, string?>
              { ["attachments_list"] = attachmentsList }).TrimEnd('\r', '\n')
            : string.Empty;
        return prompts.Render(DecisionResumeTemplate, new Dictionary<string, string?>
        {
            ["task_title"] = info.Title,
            ["task_id"] = info.Id,
            ["attachments_block"] = attachmentsBlock,
            ["last_agent_text"] = lastAgentText,
        }).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Build the prompt the orchestrator's one-shot Claude call sees. The
    /// framing is load-bearing for the decision contract: the orchestrator
    /// must know it can return BLOCK to defer, and must reply in the user's
    /// voice not the orchestrator's. The text lives in the runtime prompt
    /// registry (orchestrator-decision-oneshot.md) so it can be inspected and
    /// overridden per project; code only fills the named slots.
    /// <para>
    /// Attachments: when the user attached files to the task (typically a
    /// screenshot that the agent's question hinges on), we list the
    /// absolute paths so the orchestrator can read them with its Read tool.
    /// Without this, the orchestrator decides blind on tasks whose entire
    /// context lives in an image.
    /// </para>
    /// </summary>
    internal static string BuildOrchestratorPrompt(RuntimePromptService prompts, TaskInfo info, string promptText, string lastAgentText, string attachmentsList)
    {
        var attachmentsBlock = AttachmentsHasFiles(attachmentsList)
            ? "\n\n" + prompts.Render(DecisionAttachmentsOneshotTemplate, new Dictionary<string, string?>
              { ["attachments_list"] = attachmentsList }).TrimEnd('\r', '\n')
            : string.Empty;
        return prompts.Render(DecisionOneshotTemplate, new Dictionary<string, string?>
        {
            ["project_name"] = info.ProjectName,
            ["task_title"] = info.Title,
            ["task_description"] = string.IsNullOrWhiteSpace(promptText) ? "(empty)" : promptText,
            ["attachments_block"] = attachmentsBlock,
            ["last_agent_text"] = lastAgentText,
        }).TrimEnd('\r', '\n');
    }

    private static bool AttachmentsHasFiles(string attachmentsList)
        => !string.IsNullOrWhiteSpace(attachmentsList)
           && !string.Equals(attachmentsList.Trim(), "(none)", StringComparison.Ordinal);

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..(max - 1)].TrimEnd() + "...";
    }

    /// <summary>
    /// Run-Liveness Slice B (concept Rule 2): mark an auto-mode run as waiting on
    /// an unanswered steer / NeedsInput question the orchestrator could not answer
    /// on its own. Writes the durable <see cref="SteerPendingRecord"/> marker (so
    /// <see cref="SteerTimeoutMonitor"/> can enforce a bounded wait even across a
    /// restart), stamps the visible <see cref="LifecyclePhases.SteerPending"/>
    /// phase so the card shows "waiting for answer", and tees an
    /// <see cref="TimelineEventKinds.OrchestratorSteered"/> event onto the
    /// timeline. Best-effort: a marker/timeline failure must never crash the
    /// decision path. The wait timeout itself is owned by the monitor's config
    /// (<c>Runner:SteerTimeout:TimeoutSeconds</c>); the marker leaves it unset.
    /// </summary>
    private void MarkSteerPending(TaskInfo info, string jobId, string kind, string? question, string? ask)
    {
        if (info == null || string.IsNullOrWhiteSpace(info.FolderPath)) return;
        try
        {
            SteerPendingMarker.Write(info.FolderPath, new SteerPendingRecord
            {
                WaitStartedAt = DateTime.UtcNow,
                Kind = kind,
                Question = string.IsNullOrWhiteSpace(question) ? null : Truncate(question, 2000),
                Ask = string.IsNullOrWhiteSpace(ask) ? null : Truncate(ask, 2000),
                CliType = info.CliType,
            }, _logger);
            _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.SteerPending);
            _timeline?.Append(
                info.FolderPath,
                TimelineEventKinds.OrchestratorSteered,
                TimelineActors.Orchestrator,
                summary: kind == SteerPendingKinds.Steer
                    ? $"Steered - waiting for an answer: {Truncate(ask ?? question ?? "", 160)}"
                    : $"Waiting for an answer (unattended): {Truncate(question ?? ask ?? "", 160)}",
                details: new Dictionary<string, string> { ["kind"] = kind });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MarkSteerPending failed for {JobId}", jobId);
        }
    }

    private async Task<ModelQualificationDecision?> QualifyModelAsync(
        TaskInfo info,
        string promptPath,
        ICliExecutionService cli,
        CancellationToken ct)
    {
        if (_modelQualification == null) return null;
        var startedAt = DateTime.UtcNow;
        try
        {
            var prompt = File.Exists(promptPath)
                ? await File.ReadAllTextAsync(promptPath, ct)
                : string.Empty;
            var catalogue = await cli.GetModelCatalogAsync(false, ct);
            var history = _scanner.ScanAllAutomationJobs()
                .Where(task => string.Equals(task.ProjectName, info.ProjectName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var decision = _modelQualification.Qualify(info, prompt, catalogue, history, startedAt);

            if (_pipelineLog != null)
            {
                var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                    _projectSettings.Get(ProjectName),
                    info);
                var pipelineRecord = _pipelineLog.EnsureAgentRunStart(
                    info.FolderPath,
                    AgentStudio.Pipeline.ProjectPipelineOrder.Apply(
                        UiTaskPipelineRouter.Select(info, settings),
                        settings),
                    ProjectName,
                    info.Id);
                using var pipelineAttempt = _pipelineLog.EnterAttempt(
                    info.FolderPath, pipelineRecord.Attempt);
                var finishedAt = DateTime.UtcNow;
                _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
                {
                    StepId = AgentStudio.Pipeline.PipelineCatalogue.ModelQualificationStepId,
                    Kind = StepKind.Module,
                    Model = decision.SelectedModel,
                    ThinkingLevel = decision.SelectedThinkingLevel,
                    RecommendedModel = decision.RecommendedModel,
                    RecommendedThinkingLevel = decision.RecommendedThinkingLevel,
                    SelectionSource = decision.SelectionSource,
                    EstimatedSavingsPercent = decision.EstimatedSavingsPercent,
                    Status = PipelineStepStatus.Passed,
                    StartedAt = startedAt,
                    CompletedAt = finishedAt,
                    DurationMs = Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds),
                    Verdict = decision.SelectionSource == "task-override" ? "override" : "selected",
                    VerdictSummary = decision.Reason,
                    Reason = decision.Reason,
                });
            }

            await _modelQualification.RecordDecisionAsync(info.FolderPath, decision, ct);
            _logger.LogInformation(
                "model-qualification jobId={JobId} taskType={TaskType} policy={PolicyVersion} tier={PolicyTier} economyMode={EconomyMode} complexity={Complexity} surface={Surface} recommendedModel={RecommendedModel} recommendedThinking={RecommendedThinking} selectedModel={SelectedModel} selectedThinking={SelectedThinking} source={SelectionSource} expectedSavingsPercent={Savings}",
                info.Id, decision.TaskType,
                decision.PolicyVersion, decision.PolicyTier, decision.EconomyMode,
                decision.Complexity, decision.Surface,
                decision.RecommendedModel, decision.RecommendedThinkingLevel,
                decision.SelectedModel, decision.SelectedThinkingLevel,
                decision.SelectionSource, decision.EstimatedSavingsPercent);
            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "model-qualification failed for {JobId}; using card/project defaults", info.Id);
            if (_pipelineLog != null)
            {
                var finishedAt = DateTime.UtcNow;
                _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
                {
                    StepId = AgentStudio.Pipeline.PipelineCatalogue.ModelQualificationStepId,
                    Kind = StepKind.Module,
                    Status = PipelineStepStatus.Skipped,
                    StartedAt = startedAt,
                    CompletedAt = finishedAt,
                    DurationMs = Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds),
                    Verdict = "fallback",
                    Reason = $"Qualification unavailable; project/card default retained: {ex.Message}",
                    VerdictSummary = $"Qualification unavailable; project/card default retained: {ex.Message}",
                });
            }
            return null;
        }
    }

    private void EnqueueIntegrationPush(TaskInfo info, string branch)
    {
        if (_integrationPushQueue == null) return;
        var repositoryRoot = _git.ResolveRepositoryRoot(Entry);
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            _logger.LogWarning("integration-push enqueue skipped project={Project} job={JobId} reason=repository-root-unavailable", ProjectName, info.Id);
            return;
        }
        var enqueued = _integrationPushQueue.Enqueue(new AgentStudio.Pipeline.IntegrationPushRequest(
            ProjectName, info.Id, info.FolderPath, repositoryRoot, branch));
        if (!enqueued)
            _logger.LogWarning("integration-push enqueue failed project={Project} job={JobId} branch={Branch}", ProjectName, info.Id, branch);
    }

    /// <summary>
    /// Open (or resume) the job's pipeline-execution record and mark the CORE
    /// "Agent execution" step <see cref="PipelineStepStatus.Running"/> at spawn,
    /// stamping its start time. <see cref="AgentStudio.Pipeline.PipelineExecutionLog.EnsureAgentRunStart"/>
    /// begins a fresh record when the prior run completed or reached core/post
    /// before a short-circuit moved it back to Ready, so a re-issue surfaces as
    /// a new record rather than overwriting silently.
    /// Best-effort: the record is observability, never a state-machine input,
    /// so any write failure is swallowed with a debug log.
    /// </summary>
    private void RecordCoreRunStart(TaskInfo info, CliExecution execution)
    {
        if (_pipelineLog == null) return;
        try
        {
            var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                _projectSettings.Get(ProjectName),
                info);
            var record = _pipelineLog.EnsureAgentRunStart(
                info.FolderPath,
                AgentStudio.Pipeline.ProjectPipelineOrder.Apply(
                    UiTaskPipelineRouter.Select(info, settings),
                    settings),
                ProjectName,
                info.Id);
            using var pipelineAttempt = _pipelineLog.EnterAttempt(info.FolderPath, record.Attempt);
            // Carry the CORE step's accumulated duration forward. A re-run of
            // the same task reuses one in-flight record, so without preserving
            // this the run-start write would zero the total and the prior
            // attempts' duration would be lost (Symptom 2). RecordStep replaces
            // the whole step, so the value must be passed through here.
            var priorCore = CoreStep(record);
            var accumulatedMs = priorCore?.DurationMs ?? 0;
            _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.CoreAgentRunStepId,
                Kind = StepKind.Core,
                Model = execution.Model ?? info.Model,
                ThinkingLevel = execution.ThinkingLevel ?? info.ThinkingLevel,
                Status = PipelineStepStatus.Running,
                StartedAt = execution.StartedAt,
                DurationMs = accumulatedMs,
                InputTokens = priorCore?.InputTokens ?? 0,
                OutputTokens = priorCore?.OutputTokens ?? 0,
                CacheReadTokens = priorCore?.CacheReadTokens ?? 0,
                CacheCreationTokens = priorCore?.CacheCreationTokens ?? 0,
                TokenUsageSource = priorCore?.TokenUsageSource,
            });
            // Arm the loop-guard row. At spawn there is no auto-mode loop yet,
            // so it reads as a clean pass (no verdict pill). RunOrchestratorDecisionAsync
            // flips it to "looping" / "loop-detected" if the Ralph-loop builds up
            // or trips the circuit-breaker.
            RecordLoopGuard(info, PipelineStepStatus.Passed, verdict: null, summary: null,
                startedAt: execution.StartedAt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record CORE run-start for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Record the auto-mode loop guard (<see cref="StuckLoopGuard"/>) as the
    /// pipeline's <see cref="AgentStudio.Pipeline.PipelineCatalogue.LoopGuardStepId"/>
    /// step so a forming or stopped Ralph-loop is visible in the Overview pipeline
    /// table - early, ahead of the core run and the aspect verdicts. The status
    /// drives the row icon (Passed = healthy / forming under budget, Failed =
    /// circuit-breaker fired); <paramref name="verdict"/> + <paramref name="summary"/>
    /// drive the pill and its tooltip. Best-effort observability, never a
    /// state-machine input, so any write failure is swallowed.
    /// </summary>
    private void RecordLoopGuard(
        TaskInfo info,
        PipelineStepStatus status,
        string? verdict,
        string? summary,
        DateTime? startedAt = null)
    {
        if (_pipelineLog == null) return;
        try
        {
            var now = DateTime.UtcNow;
            _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.LoopGuardStepId,
                Kind = StepKind.Module,
                Status = status,
                StartedAt = startedAt ?? now,
                CompletedAt = now,
                Verdict = verdict,
                VerdictSummary = summary,
                Reason = summary,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record loop-guard step for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Gather the deterministic inputs for the reissue open-items pre-check from
    /// the job folder (re-issue tag, prior pipeline attempt, follow-up reason,
    /// aspect concerns) and run the pure
    /// <see cref="ReissueOpenItemsPreCheck.Evaluate"/>. Best-effort: a read
    /// failure yields a no-op decision so a flaky folder never blocks the run.
    /// </summary>
    private ReissueOpenItemsPreCheck.PreCheckDecision EvaluateReissueOpenItems(TaskInfo info)
    {
        try
        {
            var hasReissueTag = info.Tags.Any(t =>
                string.Equals(t, ReviewDecisionOrchestrator.ReissueTagId, StringComparison.OrdinalIgnoreCase));

            // Read() returns the prior run's record here; this run's fresh
            // record is opened later in RecordCoreRunStart. A prior record can
            // be a re-issued attempt even when it was short-circuited before
            // Complete() stamped it, as long as it already crossed core/post.
            var prior = _pipelineLog?.Read(info.FolderPath);
            var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                _projectSettings.Get(ProjectName),
                info);
            var pipeline = AgentStudio.Pipeline.ProjectPipelineOrder.Apply(
                UiTaskPipelineRouter.Select(info, settings),
                settings);
            var priorRunExists = prior != null
                && (prior.IsComplete
                    || AgentStudio.Pipeline.PipelineExecutionLog.HasReachedAgentRunBoundary(prior, pipeline));

            var followUpPath = Path.Combine(info.FolderPath, "orchestrator-follow-up.md");
            var followUpText = File.Exists(followUpPath) ? File.ReadAllText(followUpPath) : string.Empty;

            // Foreground BOTH the auto-review aspect concerns AND the user-run
            // code-review findings (code-review-*.md). The latter were the
            // explicit gap (ASS-1658): a code-review:block/concerns verdict
            // produced a finding the reissue change-prompt never surfaced, so a
            // re-run only saw the old prompt and not "what the review said was
            // wrong". Both feed the same open-items channel - the pre-check
            // treats them generically as items to resolve before anything else.
            var reviewFindings = GatherAspectConcernSummaries(info.FolderPath)
                .Concat(GatherCodeReviewFindings(info.FolderPath))
                .ToList();
            var reissueCause = ResolveLatestReissueCause(info.FolderPath);

            return ReissueOpenItemsPreCheck.Evaluate(new ReissueOpenItemsPreCheck.PreCheckInput
            {
                HasReissueTag = hasReissueTag,
                PriorRunCompleted = priorRunExists,
                PriorRunCount = prior?.Attempt ?? 0,
                FollowUpText = followUpText,
                AspectConcernSummaries = reviewFindings,
                ReissueCause = reissueCause,
                PromptFamily = ReissuePromptExperiment.ResolvePromptFamily(reissueCause),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reissue open-items pre-check failed for {JobId}", info.Id);
            return new ReissueOpenItemsPreCheck.PreCheckDecision
            {
                Action = ReissueOpenItemsPreCheck.PreCheckAction.None,
            };
        }
    }

    private string ResolveLatestReissueCause(string jobFolderPath)
    {
        if (_timeline == null) return "unknown";
        try
        {
            var reopen = _timeline.ReadAll(jobFolderPath)
                .LastOrDefault(evt =>
                    string.Equals(
                        evt.Kind,
                        TimelineEventKinds.QualityLoopReopened,
                        StringComparison.Ordinal)
                    && evt.Details?.ContainsKey("cause") == true);
            return reopen?.Details?["cause"]?.Trim().ToLowerInvariant() is { Length: > 0 } cause
                ? cause
                : "unknown";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve latest reissue cause for {Folder}", jobFolderPath);
            return "unknown";
        }
    }

    /// <summary>
    /// Lift the one-line concern/block summaries from the previous run's
    /// <c>aspect-*.md</c> reports (pass verdicts and unreadable files are
    /// skipped) so the pre-check can foreground them as open items.
    /// </summary>
    private static IReadOnlyList<string> GatherAspectConcernSummaries(string jobFolderPath)
    {
        var summaries = new List<string>();
        foreach (var stepId in AgentStudio.Pipeline.PipelineCatalogue.AspectStepIds)
        {
            var path = Path.Combine(jobFolderPath, stepId + ".md");
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            var status = AspectVerdictParsing.ReadStatusFromReport(text);
            if (status is not (AspectStatus.Concerns or AspectStatus.Block)) continue;

            var fm = AgentStudio.Cli.FrontmatterParser.Parse(text);
            if (fm.Ok && fm.Fields.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
                summaries.Add(summary.Trim());
        }
        return summaries;
    }

    /// <summary>
    /// Lift the one-line summaries from the previous run's user-triggered
    /// code-review reports (<c>code-review-*.md</c>, written by
    /// <see cref="AgentStudio.Review.CodeReviewStepService"/>) so a
    /// re-issue foregrounds them as open items. These reports carry a
    /// <c>verdict:</c> frontmatter field (not the aspect reports' <c>status:</c>),
    /// so they need their own gather; <c>pass</c> verdicts and unreadable files
    /// are skipped. Each finding is prefixed with <c>code review:</c> so the
    /// foregrounded checklist reads unambiguously next to aspect concerns.
    /// Internal for unit coverage of the ASS-1658 gap (the explicit Beleg that
    /// code-review findings were never merged into the reissue change-prompt).
    /// </summary>
    internal static IReadOnlyList<string> GatherCodeReviewFindings(string jobFolderPath)
    {
        var summaries = new List<string>();
        string[] files;
        try { files = Directory.GetFiles(jobFolderPath, "code-review-*.md"); }
        catch { return summaries; }

        // Deterministic order so the foregrounded list is stable across runs.
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var path in files)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            var fm = AgentStudio.Cli.FrontmatterParser.Parse(text);
            if (!fm.Ok) continue;
            if (!fm.Fields.TryGetValue("verdict", out var verdict)) continue;
            var token = verdict.Trim().ToLowerInvariant();
            if (token is not ("concerns" or "block")) continue;

            if (fm.Fields.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
                summaries.Add($"code review ({token}): {summary.Trim()}");
        }
        return summaries;
    }

    /// <summary>
    /// Record the reissue open-items pre-check as the pipeline's
    /// <see cref="AgentStudio.Pipeline.PipelineCatalogue.PreReissueOpenItemsStepId"/>
    /// step: <see cref="PipelineStepStatus.Passed"/> with an <c>open-items</c>
    /// verdict when it foregrounded items, <see cref="PipelineStepStatus.Failed"/>
    /// with an <c>escalate</c> verdict past the bounce budget, and a clean
    /// <see cref="PipelineStepStatus.Passed"/> with no verdict for a re-issue
    /// that had nothing left open. Best-effort observability, never a
    /// state-machine input.
    /// </summary>
    private void RecordReissueOpenItemsPreStep(
        TaskInfo info, ReissueOpenItemsPreCheck.PreCheckDecision decision, DateTime startedAt)
    {
        if (_pipelineLog == null) return;
        try
        {
            var (status, verdict) = decision.Action switch
            {
                ReissueOpenItemsPreCheck.PreCheckAction.Escalate
                    => (PipelineStepStatus.Failed, (string?)"escalate"),
                ReissueOpenItemsPreCheck.PreCheckAction.ForegroundOpenItems
                    => (PipelineStepStatus.Passed, "open-items"),
                _ => (PipelineStepStatus.Passed, null),
            };
            var summary = decision.HasOpenItems
                ? $"{decision.OpenItems.Count} open item(s): " + string.Join("; ", decision.OpenItems.Take(3))
                : null;
            var now = DateTime.UtcNow;
            _pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.PreReissueOpenItemsStepId,
                Kind = StepKind.Module,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = now,
                Verdict = verdict,
                VerdictSummary = summary,
                Reason = summary,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record reissue open-items pre-step for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Containment for read-only modes (planning / research): such a run should
    /// produce only a report and touch no source, and it deliberately skips the
    /// git pre/post steps - so a non-empty working-tree diff at run end means the
    /// agent wrote files it should not have. We do NOT auto-revert (the operator
    /// owns the decision); instead we report it as a hard violation on the
    /// timeline (<see cref="TimelineEventKinds.ReadOnlyContainmentViolation"/>),
    /// log a warning, and drop a one-line orchestrator note so the dirty tree is
    /// visible everywhere the operator looks. No-op for coding mode and for a
    /// clean tree. Best-effort observability: any failure is swallowed.
    /// </summary>
    private void ReportReadOnlyContainmentIfDirty(TaskInfo info)
    {
        // Cheap mode short-circuit before paying for a git status on every
        // coding run; the policy re-checks the mode defensively.
        if (!TaskModes.IsReadOnly(info.Mode)) return;
        try
        {
            var status = _git.GetStatus(
                info.Id,
                Entry.Path,
                preferRunLocation: TaskModes.IsConcept(info.Mode));
            var containment = ReadOnlyContainmentPolicy.Evaluate(
                info.Mode, status.IsRepo, status.Files.Select(f => f.Path).ToList());
            if (!containment.IsViolation) return;

            _logger.LogWarning(
                "Read-only containment violation for {JobId} (mode {Mode}): {Count} changed file(s): {Files}",
                info.Id, info.Mode, containment.ChangedFiles, containment.FileList);

            _timeline?.Append(
                info.FolderPath,
                TimelineEventKinds.ReadOnlyContainmentViolation,
                TimelineActors.System,
                summary: containment.Summary,
                details: new()
                {
                    ["mode"] = info.Mode ?? string.Empty,
                    ["changedFiles"] = containment.ChangedFiles.ToString(),
                    ["files"] = containment.FileList,
                });

            _chatLog.Append(info, OrchestratorMessageKind.Decision,
                $"[containment] {containment.Summary}. Files: {containment.FileList}.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to evaluate read-only containment for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Reconstructs Claude token usage from the session transcript and writes
    /// it to the job's <c>lastUsage</c>. Best-effort and side-effect-light:
    /// only persists when the aggregate found real tokens, and only the
    /// unstructured footer string the Overview agent-usage block renders.
    /// The transcript is read via <see cref="ClaudeSessionInspector"/> using
    /// the project's working directory (the cwd Claude encodes its log folder
    /// from) and the best-available session id for this run.
    /// </summary>
    private CoreAgentUsage? TryRecordPostHocClaudeUsage(
        string jobId, string? capturedSessionId, RunPlan? planSnapshot, TaskInfo? finishedInfo)
    {
        if (_sessionInspector == null) return null;
        try
        {
            // Prefer the id captured during this run (a fresh run, or the new
            // fork id a --resume produced); fall back to the resume target,
            // then the job's recorded session name. On a killed run the init
            // frame - and thus the captured id - normally arrived before the
            // kill, so capturedSessionId is usually present even here.
            var sessionId = !string.IsNullOrWhiteSpace(capturedSessionId) ? capturedSessionId
                : !string.IsNullOrWhiteSpace(planSnapshot?.SessionToResume) ? planSnapshot!.SessionToResume
                : finishedInfo?.SessionName;
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            var cwd = Entry.RootPath;
            if (string.IsNullOrWhiteSpace(cwd)) return null;

            var agg = _sessionInspector.AggregateUsage(sessionId!, cwd!);
            if (agg == null || agg.TotalTokens <= 0) return null;

            var synthetic = new SessionUsage
            {
                At = DateTime.UtcNow,
                Tokens = AgentStudio.Cli.ClaudeSessionInspector.FormatUsageString(agg),
            };
            if (_sessions.UpdateLastUsage(jobId, synthetic, Entry.Path))
            {
                _logger.LogInformation(
                    "[token-posthoc] Reconstructed Claude usage for {JobId} from session {SessionId}: {Total} tokens over {Turns} turns (in={In} out={Out} cacheRead={CacheRead} cacheWrite={CacheWrite})",
                    jobId, sessionId, agg.TotalTokens, agg.TurnCount,
                    agg.InputTokens, agg.OutputTokens, agg.CacheReadTokens, agg.CacheCreationTokens);
            }
            return new CoreAgentUsage(
                agg.Model,
                agg.InputTokens,
                agg.OutputTokens,
                agg.CacheReadTokens,
                agg.CacheCreationTokens,
                AgentSessionTranscriptUsageSource);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[token-posthoc] Failed to reconstruct Claude usage for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Mark the CORE step terminal in pipeline-execution.json once the agent
    /// process exits: <see cref="PipelineStepStatus.Passed"/> when the run
    /// classified as <see cref="RunStatuses.Completed"/>,
    /// <see cref="PipelineStepStatus.Failed"/> otherwise, with the run's real
    /// duration and start/end timestamps. The agent's own verdict
    /// (DONE / BLOCKED / NEEDS_INPUT) drives the lane transition, not this
    /// step: CORE answers "did the agent run execute", which is true
    /// regardless of the verdict, so a clean exit is Passed even on BLOCKED.
    /// The status comes from <see cref="CoreRunStepStatusMapper"/>, which keys
    /// off the deterministic run status and never the OS exit code - a
    /// sentinel-detected / silent-completion run is a completion even though the
    /// process kill yields exitCode = -1 on Windows.
    /// </summary>
    private async Task RecordCoreRunFinish(string jobId, CliExecution execution, CoreAgentUsage? usage)
    {
        if (_pipelineLog == null) return;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            var folder = info?.FolderPath;
            if (string.IsNullOrEmpty(folder)) return;

            // Resume the record opened at spawn. EnsureRun only re-creates the
            // file in the rare case the start write was lost, so the finished
            // step still lands and the row is never left blank.
            var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                _projectSettings.Get(ProjectName),
                info!);
            var record = _pipelineLog.EnsureRun(
                folder,
                AgentStudio.Pipeline.ProjectPipelineOrder.Apply(
                    UiTaskPipelineRouter.Select(info, settings),
                    settings),
                ProjectName,
                jobId);
            using var pipelineAttempt = _pipelineLog.EnterAttempt(folder, record.Attempt);

            var startedAt = execution.StartedAt;
            // Accumulate this run's duration onto the total carried forward from
            // prior runs (Symptom 2): a multi-attempt task shares one CORE step,
            // so the row must reflect every run, not just the last. The reported
            // CompletedAt stays at THIS run's end (start + this run), not the
            // cumulative span, so it never drifts into the future.
            var thisRunMs = CoreRunStepAccumulator.RunDurationMs(
                execution.DurationSeconds, startedAt, DateTime.UtcNow);
            var priorCore = CoreStep(record);
            var durationMs = CoreRunStepAccumulator.Accumulate(priorCore?.DurationMs ?? 0, thisRunMs);
            var completedAt = startedAt.AddMilliseconds(thisRunMs);
            var inputTokens = (priorCore?.InputTokens ?? 0) + (usage?.InputTokens ?? 0);
            var outputTokens = (priorCore?.OutputTokens ?? 0) + (usage?.OutputTokens ?? 0);
            var cacheReadTokens = (priorCore?.CacheReadTokens ?? 0) + (usage?.CacheReadTokens ?? 0);
            var cacheCreationTokens = (priorCore?.CacheCreationTokens ?? 0) + (usage?.CacheCreationTokens ?? 0);

            // Key the CORE step off the deterministic run status, not the OS
            // exit code: a sentinel-detected / silent-completion run is a
            // completion even though the process kill returns exitCode = -1.
            // Resolve binds the status, its failure reason AND the reconciled
            // verdict together so the persisted record can never show a Failed
            // icon next to a SUCCESS badge (bug ASS-2), and so the call site
            // cannot re-introduce an exit-code gate unguarded.
            var (coreStatus, reason, verdict) = CoreRunStepStatusMapper.Resolve(execution);

            // Observability for bug ASS-2: surface the exact moment a
            // contradictory success-class verdict is dropped because the
            // deterministic status was not Passed. The run self-reported
            // success/noop but the classifier did not call it "completed"
            // (a watchdog/kill or crash) - the corrupt/legacy-record signal
            // that used to render as a red Failed icon next to a green SUCCESS
            // badge. Logged at Warning because the divergence is worth a look.
            if (verdict == null && execution.RunOutcome != null)
            {
                _logger.LogWarning(
                    "CORE step verdict reconciled away for {JobId}: status={CoreStatus} dropped contradictory run outcome {RunOutcome} (exit {ExitCode})",
                    jobId, coreStatus, execution.RunOutcome, execution.ExitCode);
            }

            _pipelineLog.RecordStep(folder, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.CoreAgentRunStepId,
                Kind = StepKind.Core,
                Model = execution.Model ?? info?.Model,
                ThinkingLevel = execution.ThinkingLevel ?? info?.ThinkingLevel,
                Status = coreStatus,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = durationMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheReadTokens,
                CacheCreationTokens = cacheCreationTokens,
                TokenUsageSource = CombineTokenUsageSource(priorCore?.TokenUsageSource, usage?.Source),
                Verdict = verdict,
                Reason = reason,
            });

            if (_modelQualification != null)
            {
                await _modelQualification.RecordOutcomeAsync(folder, new ModelQualificationOutcome
                {
                    At = DateTime.UtcNow,
                    JobId = jobId,
                    Project = ProjectName,
                    Model = execution.Model ?? info?.Model,
                    ThinkingLevel = execution.ThinkingLevel ?? info?.ThinkingLevel,
                    Status = execution.Status ?? "unknown",
                    Verdict = verdict,
                    InputTokens = usage?.InputTokens ?? 0,
                    OutputTokens = usage?.OutputTokens ?? 0,
                    CacheReadTokens = usage?.CacheReadTokens ?? 0,
                    CacheCreationTokens = usage?.CacheCreationTokens ?? 0,
                    Attempt = record.Attempt,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record CORE run-finish for {JobId}", jobId);
        }
    }

    /// <summary>
    /// Duration already accumulated onto the CORE "Agent execution" step of a
    /// pipeline record, or 0 when the step is absent. The CORE step persists
    /// across every run of a multi-attempt task, so this is the total to add
    /// the current run's duration onto (Symptom 2). See
    /// <see cref="CoreRunStepAccumulator"/>.
    /// </summary>
    private static PipelineStepExecution? CoreStep(PipelineExecutionRecord? record)
        => record?.Steps.FirstOrDefault(s => string.Equals(
               s.StepId,
               AgentStudio.Pipeline.PipelineCatalogue.CoreAgentRunStepId,
               StringComparison.OrdinalIgnoreCase));

    private static string? CombineTokenUsageSource(string? existing, string? current)
    {
        if (string.IsNullOrWhiteSpace(existing)) return string.IsNullOrWhiteSpace(current) ? null : current;
        if (string.IsNullOrWhiteSpace(current)) return existing;
        if (existing.Contains(current, StringComparison.OrdinalIgnoreCase)) return existing;
        return $"{existing} + {current}";
    }

    private CoreAgentUsage? ResolveCoreAgentUsage(
        ICliExecutionService cli,
        string jobKey,
        SessionUsage? footerUsage)
    {
        var parsed = cli.GetLastParsedTurnUsage(jobKey);
        if (parsed is { } snapshot)
        {
            var u = snapshot.Usage;
            return new CoreAgentUsage(
                u.Model,
                u.Input,
                u.Output,
                u.CacheRead,
                u.CacheWrite,
                AgentCliFooterUsageSource);
        }

        return TryParseFooterUsage(footerUsage?.Tokens, model: null, AgentCliFooterUsageSource);
    }

    private static CoreAgentUsage? TryParseFooterUsage(string? text, string? model, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!;
        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;

        // Claude transcript fallback renders:
        // "13.6M tokens (in 47.5k, out 128k, cache-read 13.4M, cache-write 12k)".
        input = TryReadLabeledCompact(value, "in") ?? 0;
        output = TryReadLabeledCompact(value, "out") ?? 0;
        cacheRead = TryReadLabeledCompact(value, "cache-read") ?? 0;
        cacheCreation = TryReadLabeledCompact(value, "cache-write") ?? 0;

        if (input + output + cacheRead + cacheCreation == 0)
        {
            // Arrow-compact footer example:
            // "↑ 38.6k • ↓ 514 • 34.7k (cached)".
            input = TryReadArrowCompact(value, '↑') ?? 0;
            output = TryReadArrowCompact(value, '↓') ?? 0;
            cacheRead = TryReadCachedCompact(value) ?? 0;
        }

        if (input + output + cacheRead + cacheCreation == 0) return null;
        return new CoreAgentUsage(model, input, output, cacheRead, cacheCreation, source);
    }

    private static long? TryReadLabeledCompact(string text, string label)
    {
        var match = Regex.Match(
            text,
            $@"(?:^|[\s,(]){Regex.Escape(label)}\s+(?<value>\d+(?:[.,]\d+)?)\s*(?<suffix>[kKmM])?",
            RegexOptions.IgnoreCase);
        return match.Success ? ParseCompactTokenValue(match) : null;
    }

    private static long? TryReadArrowCompact(string text, char arrow)
    {
        var idx = text.IndexOf(arrow);
        if (idx < 0 || idx + 1 >= text.Length) return null;
        var match = CompactTokenValueRegex.Match(text[(idx + 1)..]);
        return match.Success ? ParseCompactTokenValue(match) : null;
    }

    private static long? TryReadCachedCompact(string text)
    {
        var match = Regex.Match(
            text,
            @"(?<value>\d+(?:[.,]\d+)?)\s*(?<suffix>[kKmM])?\s*\(cached\)",
            RegexOptions.IgnoreCase);
        return match.Success ? ParseCompactTokenValue(match) : null;
    }

    private static long ParseCompactTokenValue(Match match)
    {
        var raw = match.Groups["value"].Value.Replace(',', '.');
        if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }
        var suffix = match.Groups["suffix"].Value;
        if (suffix.Equals("m", StringComparison.OrdinalIgnoreCase)) n *= 1_000_000m;
        else if (suffix.Equals("k", StringComparison.OrdinalIgnoreCase)) n *= 1_000m;
        return (long)Math.Round(n, MidpointRounding.AwayFromZero);
    }

    private void OnCliFinished(string cliType, string jobKey, CliExecution execution)
    {
        // Find the slot whose run this finish belongs to (by job key), so a
        // second parallel slot's finish is not dropped by a Single-based check.
        var finishedRun = _activeRuns.ByJobKey(GetJobKey, jobKey);
        if (finishedRun == null) return;
        if (finishedRun.CliType != null && !string.Equals(cliType, finishedRun.CliType, StringComparison.OrdinalIgnoreCase)) return;

        _ = Task.Run(async () =>
        {
            await finishedRun.StartHandshake.ConfigureAwait(false);
            await OnCliFinishedAsync(cliType, jobKey, execution, finishedRun.JobId).ConfigureAwait(false);
        });
    }

    private async Task OnCliFinishedAsync(string cliType, string jobKey, CliExecution execution, string jobId)
    {
        // Snapshot the run-scoped fields BEFORE any path can clear them. The
        // re-issue branch and RunOrchestratorDecisionAsync both null out
        // _activePlan as part of releasing the active-job latch, and an
        // upstream tick can re-enter RunCliAsync and reassign these fields
        // before the capture-fail block reads them. Reading from a local
        // snapshot makes the recovery decision deterministic w.r.t. THIS
        // run, regardless of what other paths do concurrently.
        var snapRun = _activeRuns.Get(jobId);
        var planSnapshot = snapRun?.Plan;
        var intentSnapshot = snapRun?.Intent ?? default;
        var followupSnapshot = snapRun?.Followup;
        var reissueAttemptSnapshot = snapRun?.ReissueAttempt ?? 0;
        try
        {
            if (_activeRuns.Get(jobId) is not { } run) return;
            if (run.CliType != null && !string.Equals(cliType, run.CliType, StringComparison.OrdinalIgnoreCase)) return;

            // Slot ownership follows process life, not lane membership. Keep the
            // ActiveRun record for finalisation, but free its execution seat as
            // soon as the CLI process exits. Integration, outcome analysis and
            // review preparation are post-processing and may overlap another CLI.
            if (_activeRuns.ReleaseExecutionSlot(jobId))
            {
                _mutations.SetJobPhase(
                    _scanner.FindJob(jobId, Entry.Path)?.FolderPath ?? string.Empty,
                    LifecyclePhases.PostProcessingRunning);
                _logger.LogInformation(
                    "execution-slot-released project={Project} job={JobId} phase={Phase} occupied={Occupied}/{Max}",
                    ProjectName, jobId, LifecyclePhases.PostProcessingRunning, _activeRuns.Count, SlotMax());
                NotifyStatus();
            }

            // no-silent-death: every run's exit is logged with code + status +
            // duration on entry to finalization, so even if a downstream step
            // below throws (see the catch) the run's exit context is on record.
            _logger.LogInformation(
                "Job {JobId} finished in project '{Project}' on {Cli}: status={Status}, exitCode={ExitCode}, duration={Duration:F1}s",
                jobId, ProjectName, cliType, execution.Status, execution.ExitCode, execution.DurationSeconds ?? 0.0);

            var runFinishedAt = execution.DurationSeconds is double recordedDuration
                ? execution.StartedAt.AddSeconds(Math.Max(0, recordedDuration))
                : DateTime.UtcNow;
            var cli = _router.Get(cliType);
            var earlyOutputSnapshot = cli.GetOutput(jobKey);
            var failedRun = string.Equals(execution.Status, "failed", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(execution.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
            var detectedProviderLimit = failedRun
                ? ProviderLimitDetector.Detect(
                    cliType,
                    earlyOutputSnapshot.Select(line => line.Text),
                    runFinishedAt)
                : null;
            var providerWaitInfo = _scanner.FindJob(jobId, Entry.Path);
            if (detectedProviderLimit is not null && providerWaitInfo is not null)
            {
                // Provider limits are classified before task finalization,
                // worktree integration, pickup-failure accounting, or the
                // normal failed run closeout can consume per-card budgets.
                // Preserve the provider output as diagnosis, then hold the
                // card and release only its execution slot in the finally path.
                if (WriteCliLog(providerWaitInfo, cli))
                    cli.DiscardPersistedOutput(jobKey);
                _mutations.SetJobLastProgressAt(providerWaitInfo.FolderPath, DateTime.UtcNow);
                HoldForProviderLimit(
                    providerWaitInfo,
                    cliType,
                    detectedProviderLimit,
                    execution,
                    runFinishedAt);
                // Do not launch the legacy immediate invalidation probe here.
                // The provider circuit is already the conservative admission
                // authority, and only its reset-time fresh probe may reopen it.
                return;
            }

            _sessions.CloseSessionEvent(jobId, new RunSessionCloseout
            {
                StartedAt = execution.StartedAt,
                FinishedAt = runFinishedAt,
                Status = execution.Status,
                Result = execution.RunOutcome,
                ExitCode = execution.ExitCode,
                DurationSeconds = execution.DurationSeconds
            }, Entry.Path);

            var runInfo = _scanner.FindJob(jobId, Entry.Path);
            var runGitRoot = run.IsWorktreeRun ? run.WorktreePath! : Entry.RootPath;
            var workerHeadAfter = _git.ReadHeadShaAt(runGitRoot);
            var workerHeadChanged = !string.IsNullOrWhiteSpace(run.WorkerHeadShaBefore)
                && !string.IsNullOrWhiteSpace(workerHeadAfter)
                && !string.Equals(run.WorkerHeadShaBefore, workerHeadAfter, StringComparison.OrdinalIgnoreCase);
            var workerHeadLinear = workerHeadChanged
                && _git.IsAncestor(runGitRoot, run.WorkerHeadShaBefore!, workerHeadAfter!);
            var preExistingHistoryRewritten = PreExistingWorkerHistoryWasRewritten(runGitRoot, run);
            var agentGitMutationClaim = DetectAgentGitMutationClaim(earlyOutputSnapshot);
            var agentGitPushClaim = DetectAgentGitPushClaim(earlyOutputSnapshot);
            var protectedRemoteChanged = agentGitPushClaim != null && ProtectedRemoteChanged(run);
            var agentGitDecision = AgentGitMutationPolicy.Decide(
                run.WorkerHeadShaBefore,
                workerHeadAfter,
                workerHeadLinear,
                preExistingHistoryRewritten,
                protectedRemoteChanged,
                agentGitMutationClaim != null);
            var workerCommitsDetected = workerHeadChanged && workerHeadLinear
                ? _git.GetCommitsInRangeAtRoot(runGitRoot, run.WorkerHeadShaBefore!, workerHeadAfter!).Count
                : 0;
            GitWorkerCommitCleanupResult? workerGitCleanup = null;
            if (agentGitDecision.Disposition == AgentGitMutationDisposition.Info
                && agentGitDecision.CleanupEligible
                && runInfo != null
                && !TaskModes.IsReadOnly(runInfo.Mode)
                && !IsAgentGitMutationAllowed(runInfo))
            {
                workerGitCleanup = _git.FoldWorkerCommitsIntoPlatformCommit(
                    runGitRoot,
                    run.WorkerHeadShaBefore,
                    workerHeadAfter);
            }

            WorktreeCommitRange? worktreeCommitRange = null;
            var worktreeTask = runInfo ?? _scanner.FindJob(jobId, Entry.Path);
            // ADR-0052/0057: every coding worktree run commits its edits on the
            // task branch and integrates them into the work branch BEFORE the
            // post-run review reads the result; a merge conflict is left for the
            // review to escalate. Slot count does not select this path.
            if (run.IsWorktreeRun
                && worktreeTask != null
                && !TaskModes.IsConcept(worktreeTask.Mode)
                && agentGitDecision.Disposition != AgentGitMutationDisposition.Escalate)
            {
                worktreeCommitRange = await IntegrateWorktreeRunAsync(run, worktreeTask);
            }

            // Persist last token/usage summary (best-effort)
            var usage = cli.GetLastUsage(jobKey);
            if (usage != null)
            {
                _sessions.UpdateLastUsage(jobId, usage, Entry.Path);
            }
            var coreUsage = ResolveCoreAgentUsage(cli, jobKey, usage);

            // Mirror run-finish + agent-side token usage onto the bus. We emit
            // these even before the post-run policy and lane move so a crash
            // mid-finalisation does not lose the lifecycle event. RunFinished
            // is the matching pair to the RunStarted emitted on spawn.
            TaskInfo? finishedInfo = null;
            try
            {
                finishedInfo = _scanner.FindJob(jobId, Entry.Path);
                if (finishedInfo != null)
                {
                    _ = _bus?.EmitRunFinishedAsync(
                        finishedInfo, cliType, execution.StartedAt,
                        execution.Status ?? "unknown",
                        execution.DurationSeconds, agentOutcome: null);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of run-finish failed for {JobId}", jobId); }

            // AGT-2100: record the CLI's cached quota snapshot at run-end, the
            // matching pair to the run-start emit. Cached-only - the run just
            // consumed quota, but we honour "no extra CLI call per run" and let
            // the snapshot's recorded age carry the honest freshness signal.
            if (finishedInfo != null)
                EmitQuotaSnapshotToBus(
                    finishedInfo, cliType, execution.StartedAt,
                    execution.Model, execution.ThinkingLevel, QuotaSnapshotPhases.End);

            // ADR-0049: mirror the run-finish onto the unified timeline. The
            // runId pairs with the agent_run_started row's runId so the FE
            // can fold a run-pair into one collapsible line.
            if (finishedInfo != null)
            {
                _timeline?.Append(
                    finishedInfo.FolderPath,
                    RunTimelineEventFactory.AgentRunFinished(
                        execution,
                        planSnapshot?.EventInputSessionId));

                // Containment, not trust: a planning / research run is supposed
                // to produce only a report. If it left a non-empty working-tree
                // diff, report it as a hard violation on the timeline (we do not
                // auto-revert - the operator decides). Runs after the git
                // pre/post steps are skipped, so the tree is still dirty here.
                ReportReadOnlyContainmentIfDirty(finishedInfo);
            }

            // Persist the captured session UUID so follow-ups can resume.
            // Claude / Codex / Gemini all auto-create a UUID on first run and
            // surface it in their JSON output; we capture it during streaming
            // and write it back here. Without this, Continue always loses
            // context because info.SessionName never advances past the slug.
            var capturedSessionId = cli.GetCapturedSessionId(jobKey);

            // ASS-1739 / T1a: snapshot the read-only execution context (memory /
            // session paths, instruction-file chain, global config, MCP servers,
            // plus model / permission mode / cwd) while the per-run process info
            // is still alive - DescribeContextSources reads the same _processes
            // entry GetCapturedSessionId just used, which the post-run cleanup
            // evicts later. Persist it onto the run's session event and mirror a
            // slim line onto the unified timeline. Best-effort: a null context or
            // a write failure never affects the run's outcome.
            try
            {
                if (cli.DescribeContextSources(jobKey) is { } describedContext)
                {
                    var (execContext, timelineEvent) = RunTimelineEventFactory.ExecutionContext(
                        cliType,
                        execution,
                        describedContext,
                        planSnapshot?.EventInputSessionId);
                    _sessions.BackfillLatestSessionEventExecutionContext(jobId, execContext, Entry.Path);
                    if (finishedInfo != null)
                        _timeline?.Append(finishedInfo.FolderPath, timelineEvent);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Execution-context capture failed for {JobId}", jobId); }
            // Post-hoc token reconstruction (ASS-626 / ASS-665): the Claude
            // CLI never reports a terminal usage footer,
            // so `usage` above is always null for Claude - and a killed run
            // loses even its final result frame. Read the per-turn usage
            // straight from the session transcript and aggregate it into
            // lastUsage so the Overview tab shows the real token spend instead
            // of nothing, even when the run was aborted. Guarded on the footer
            // being absent so we never clobber a real CLI-reported footer.
            if (usage == null && cli.NeedsPostHocUsageReconstruction)
            {
                coreUsage ??= TryRecordPostHocClaudeUsage(jobId, capturedSessionId, planSnapshot, finishedInfo);
            }

            // Mark the CORE "Agent execution" step done in pipeline-execution.json
            // with its real duration / start+end times and the agent-side token
            // usage reported for this run. Without this the CORE row stayed
            // token-blank while the separate Agent footer block had data.
            // Best-effort; the record is observability, never a state-machine
            // input.
            await RecordCoreRunFinish(jobId, execution, coreUsage);

            // Capture the post-run HEAD SHA so the run's commit set can be
            // derived deterministically via git rev-list HeadShaBefore..After.
            // This must happen before any auto-commit hook fires (the hook
            // is part of the Progress->Review transition, not the run
            // itself, and we want the run to own the agent's own commits).
            string? headShaAfter;
            if (worktreeCommitRange != null)
            {
                headShaAfter = worktreeCommitRange.HeadShaAfter;
                _sessions.BackfillLatestSessionEventHeadShaRange(
                    jobId,
                    worktreeCommitRange.HeadShaBefore,
                    worktreeCommitRange.HeadShaAfter,
                    Entry.Path);
            }
            else
            {
                headShaAfter = SafeGetHeadSha(jobId);
                if (!string.IsNullOrWhiteSpace(headShaAfter))
                {
                    _sessions.BackfillLatestSessionEventHeadShaAfter(jobId, headShaAfter, Entry.Path);
                }
            }

            if (!string.IsNullOrWhiteSpace(capturedSessionId))
            {
                // Append to the chain (and update sessionName in lockstep). Forking
                // CLIs emit a new id on every --resume; preserving the chain lets the
                // user see how often the session has been continued.
                _sessions.AppendSessionToChain(jobId, capturedSessionId!, Entry.Path);
                _sessions.BackfillLatestSessionEventCapturedId(jobId, capturedSessionId!, Entry.Path);

                // Reset the per-job capture-fail circuit-breaker counter on
                // genuine success. Without this, a job that flaked two runs
                // and then succeeded would carry the prior count forward and
                // a single later capture-fail would trip the breaker as if
                // three failures had occurred in a row.
                if (_consecutiveCaptureFailJobId == jobId)
                {
                    _consecutiveCaptureFailCount = 0;
                    _consecutiveCaptureFailJobId = null;
                }
            }
            else if (cli.EmitsSessionId && detectedProviderLimit is null)
            {
                // The CLI normally emits a session UUID on every run; missing
                // it means the next follow-up will fall back to Recovery. Tell
                // the user explicitly so the loop is not silent.
                //
                // When the just-finished run was a --resume attempt, the
                // resume target is the most likely cause: the CLI rejected
                // the id (e.g. claude prints "No conversation found with
                // session ID: <uuid>" on stdout, exits non-zero, and never
                // emits a new init frame). Leaving the dead id in
                // SessionName would make the next follow-up try the same
                // resume and fail identically. Clear it now and mark the
                // chain as recovery so the planner routes the next user
                // turn to Recovery instead of Continue. ADR-0002 / ADR-0006
                // make Recovery the expected hand-off for session loss.
                var captureFailInfo = _scanner.FindJob(jobId, Entry.Path);
                if (captureFailInfo != null)
                {
                    var resumeTargetWasGone = ShouldMarkSessionChainRecovery(planSnapshot);
                    if (resumeTargetWasGone)
                    {
                        _sessions.SetJobSessionName(jobId, null, Entry.Path);
                        _sessions.MarkSessionChainRecovery(jobId, Entry.Path);
                    }

                    var msg = resumeTargetWasGone
                        ? $"[capture-fail] {cli.CliType} rejected the resume target ({planSnapshot!.SessionToResume}); next follow-up will rebuild from disk via Recovery."
                        : $"[capture-fail] No {cli.CliType} session id from this run; next follow-up will rebuild from disk.";
                    _chatLog.Append(captureFailInfo, OrchestratorMessageKind.Decision, msg);

                    // Per-job consecutive capture-fail circuit-breaker.
                    // The recovery marker above SHOULD prevent the next
                    // pickup from resuming the same dead UUID, but several
                    // failure modes (race with planner, planner reads stale
                    // info, scanner cache) can still re-feed the same
                    // session. Past the threshold we pause into an automatic
                    // cooldown, then resume later instead of waiting forever
                    // for a human to re-arm auto-mode. Reset on the success
                    // path above.
                    var prior = _consecutiveCaptureFailJobId == jobId ? _consecutiveCaptureFailCount : 0;
                    _consecutiveCaptureFailJobId = jobId;
                    _consecutiveCaptureFailCount = prior + 1;
                    if (_consecutiveCaptureFailCount >= CaptureFailHaltThreshold && IsAutoMode(_mode))
                    {
                        _logger.LogWarning(
                            "Runner '{Project}' cooling down auto-mode after {N} consecutive capture-fails on {JobId}",
                            ProjectName, _consecutiveCaptureFailCount, jobId);
                        _chatLog.Append(captureFailInfo, OrchestratorMessageKind.Decision,
                            $"Auto-mode cooldown: {_consecutiveCaptureFailCount} consecutive {cli.CliType} runs for this job ended without capturing a session id. The runner will resume automatically after cooldown.");
                        ScheduleGlobalBreakerCooldown(
                            $"capture-fail circuit-breaker: {_consecutiveCaptureFailCount}x no session id on {jobId} ({cli.CliType})",
                            captureFailInfo);
                        _consecutiveCaptureFailCount = 0;
                        _consecutiveCaptureFailJobId = null;
                    }
                }
            }

            // Snapshot the live output before we flush it to disk. The
            // outcome analyzer needs the buffer to classify the run, and the
            // post-run policy may re-issue another run on top before we let
            // the regular review/summary pipeline proceed.
            var liveOutputSnapshot = earlyOutputSnapshot;

            // Strict-iteration progress-first pickup bookkeeping: only
            // autopickup runs feed the per-slug silent-attempt counter.
            // Manual starts and user-driven continues do not count, since
            // they are user-acknowledged and not part of the autonomous
            // queue pacing. A run that streamed any output line resets the
            // counter; a fully silent run increments it. Reaching
            // <see cref="PickupFailureThreshold"/> dead-letters the folder
            // on the next pickup tick.
            if (intentSnapshot == RunIntent.AutoPickup)
            {
                RecordPickupAttemptResult(
                    slug: jobId,
                    outputLines: liveOutputSnapshot.Count,
                    durationSeconds: execution.DurationSeconds ?? 0.0,
                    executionStatus: execution.Status);
            }

            // Write CLI output to log file. The runtime JSONL is the durable
            // backup that lets us recover the Activity Log after a backend
            // restart; once the consolidated cli-output.log has it, the JSONL
            // can go so the disk-fallback path in GetOutput doesn't replay the
            // same lines after the in-memory buffer is evicted.
            var activeInfo = _scanner.FindJob(jobId, Entry.Path);
            if (activeInfo != null && WriteCliLog(activeInfo, cli))
            {
                cli.DiscardPersistedOutput(jobKey);
            }

            // Bump lastProgressAt so CrashRecoveryService can attribute orphan
            // working-tree changes to the most-recently-active job per project
            // on next boot. Cheap (single field write); see ADR-0020.
            if (activeInfo != null)
            {
                _mutations.SetJobLastProgressAt(activeInfo.FolderPath, DateTime.UtcNow);
            }

            if (activeInfo != null
                && agentGitDecision.Disposition == AgentGitMutationDisposition.Info
                && !IsAgentGitMutationAllowed(activeInfo))
            {
                var cleanupDetail = workerGitCleanup?.Success == true
                    ? "cleanup=platform-commit-ready; the worker commit was folded back and its file changes were preserved for the platform commit"
                    : workerHeadChanged
                        ? $"worker advanced HEAD - needs cleanup; pipeline continues ({workerGitCleanup?.Error ?? "automatic cleanup was not safe or not available"})"
                        : "worker reported a commit/push, but no protected-ref or HEAD damage was verified; pipeline continues";
                var message = $"[worker-head-advanced] INFO: {cleanupDetail}.";
                _logger.LogInformation(
                    "worker-head-advanced job={JobId} cli={Cli} commits={Commits} cleanup={Cleanup} claim={Claim}",
                    jobId,
                    cliType,
                    workerCommitsDetected,
                    workerGitCleanup?.Status ?? "not-attempted",
                    agentGitMutationClaim ?? "<head-changed>");
                _chatLog.Append(activeInfo, OrchestratorMessageKind.WorkerHeadAdvanced, message);
            }

            // Apply the orchestrator's post-run policy. The policy is pure;
            // we apply its decision here. The activeInfo lookup may fail
            // (job folder moved between completion and lookup), in which
            // case we skip the meta channel and fall through to the
            // existing accept-path - the policy is a refinement, not a gate.
            var capturedIntent = intentSnapshot;
            var capturedFollowup = followupSnapshot;
            var capturedPlan = planSnapshot;
            var capturedAttempt = reissueAttemptSnapshot;
            var outcome = AgentOutcomeAnalyzer.Analyze(
                liveOutputSnapshot,
                execution.Status ?? "completed",
                execution.DurationSeconds ?? 0.0,
                execution.ExitCode);

            if (outcome.IssueKind == RunIssueKind.QuotaExhausted
                && activeInfo is not null
                && !string.IsNullOrWhiteSpace(cliType))
            {
                var limit = detectedProviderLimit ?? new ProviderLimitStatus(
                        cliType,
                        runFinishedAt,
                        runFinishedAt.Add(ProviderLimitDetector.UnknownResetRetry),
                        $"{cliType}: provider limit detected; retry probe at {runFinishedAt.Add(ProviderLimitDetector.UnknownResetRetry):O}",
                        ResetTimeReported: false);
                HoldForProviderLimit(activeInfo, cliType, limit, execution, runFinishedAt);
                return;
            }

            // Ground-truth quota hook (AGT-2064). A run that died with a
            // usage-limit error is hard proof the cached quota snapshot is wrong,
            // even if it read green - the error text is the evidence. Invalidate
            // it and re-probe now instead of trusting stale numbers for up to the
            // full TTL: otherwise the next quota-aware launch fires on the same
            // wrong snapshot and takes the same fail. The re-probe is
            // fire-and-forget; admission is already conservative because the
            // snapshot is flagged suspicious the instant we invalidate it.
            if (outcome.IssueKind == RunIssueKind.QuotaExhausted && !string.IsNullOrWhiteSpace(cliType))
            {
                _logger.LogWarning(
                    "quota_ground_truth_invalidate job={JobId} cli={Cli}: run hit a usage limit; invalidating cached quota snapshot and re-probing",
                    jobId, cliType);
                _ = _quotaService.InvalidateForGroundTruthLimit(
                    cliType, $"launch {jobId} died with a usage-limit error");
            }

            // Count the commits this run actually produced (HeadShaBefore..After).
            // A run that committed real work but exited non-zero without a
            // sentinel - classically because a post-commit test run was killed
            // by the watchdog (exitCode=-1 on Windows) - must not be hard-failed
            // and re-looped; the commit-aware classifier routes it to review as
            // an honest "committed-partial" instead. See the run-outcome contract.
            int commitsDuringRun = workerCommitsDetected;
            try
            {
                var beforeSha = _sessions.ReadSessionEvents(jobId, Entry.Path).LastOrDefault()?.HeadShaBefore;
                if (!string.IsNullOrWhiteSpace(beforeSha)
                    && !string.IsNullOrWhiteSpace(headShaAfter)
                    && !string.Equals(beforeSha, headShaAfter, StringComparison.OrdinalIgnoreCase))
                {
                    commitsDuringRun = Math.Max(
                        commitsDuringRun,
                        _git.GetCommitsInShaRange(jobId, Entry.Path, beforeSha, headShaAfter).Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "commit-count for {JobId} failed; treating as 0", jobId);
            }

            if (activeInfo != null
                && agentGitDecision.Disposition == AgentGitMutationDisposition.Escalate)
            {
                var message =
                    $"[agent-git-violation] Genuine git damage detected: {agentGitDecision.Reason}. "
                    + "The run is being escalated because protected remote history changed or pre-existing work was rewritten.";
                _logger.LogWarning(
                    "agent-git-violation job={JobId} cli={Cli} reason={Reason} commits={Commits} claim={Claim} status={Status}",
                    jobId,
                    cliType,
                    agentGitDecision.Reason,
                    commitsDuringRun,
                    agentGitPushClaim ?? agentGitMutationClaim ?? "<head-rewritten>",
                    execution.Status);
                _chatLog.Append(activeInfo, OrchestratorMessageKind.AgentGitViolation, message);
                outcome = outcome with
                {
                    IssueKind = RunIssueKind.AgentGitViolation,
                    Summary = message,
                    Reason = agentGitDecision.Reason
                };
            }

            var terminalOutcome = TerminalRunOutcomeClassifier.Classify(execution.Status, outcome, commitsDuringRun);
            _sessions.CloseSessionEvent(jobId, new RunSessionCloseout
            {
                StartedAt = execution.StartedAt,
                FinishedAt = runFinishedAt,
                Status = execution.Status,
                Result = terminalOutcome.Kind,
                ExitCode = execution.ExitCode,
                DurationSeconds = execution.DurationSeconds
            }, Entry.Path);
            if (string.Equals(terminalOutcome.Kind, TerminalRunOutcomeKinds.CommittedPartial, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "run-committed-partial job={JobId} commits={Commits} status={Status} duration={Duration}s: routing to review instead of hard-failing",
                    jobId, commitsDuringRun, execution.Status, execution.DurationSeconds ?? 0.0);
            }
            // Evidence-based completion for Codex silent finishes. Codex ends
            // the large majority of its runs without a terminal sentinel; rather
            // than reissuing every such run, hand the policy the on-disk
            // evidence (commits + the run's own Result:/open-items close-out) so
            // a clean finish is accepted and one with open work is driven to
            // completion via a bounded continuation loop. Claude stays
            // sentinel-based (the evidence is gated on IsCodex inside the policy).
            CodexCompletionEvidence.Inputs? codexEvidence = null;
            if (string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
                && (outcome.IssueKind == RunIssueKind.MissingTerminalSentinel
                    || outcome.IssueKind == RunIssueKind.SilentCompletion))
            {
                var closeOut = string.Join("\n", liveOutputSnapshot.Select(l => l.Text ?? string.Empty));
                var openFindings = CompletionGate.ExtractFindings(closeOut, closeOut);
                codexEvidence = new CodexCompletionEvidence.Inputs(
                    IsCodex: true,
                    HasCommits: commitsDuringRun > 0,
                    StatusResultToken: CompletionGate.ExtractResultToken(closeOut),
                    OpenFindingsCount: openFindings.Count,
                    TimedOutMidTask: false,
                    ContinuationAttemptsUsed: capturedAttempt);
            }

            OutcomeAction? action = capturedPlan != null
                ? RunOutcomePolicy.Decide(
                    capturedIntent,
                    capturedPlan,
                    outcome,
                    capturedFollowup,
                    capturedAttempt,
                    codexEvidence,
                    RunOutcomePolicy.PriorCommitLines(activeInfo))
                : null;

            // A launch-only follow-up must not erase a prior completed run that
            // already has a code-review grade. Preserve that successful run as
            // the review basis and hand the infrastructure failure to a human;
            // reissuing from the empty failure is the CAR-5 spiral.
            var preserveSuccessfulRunContext = action?.IssueKind == RunIssueKind.CliLaunchFailed
                && activeInfo != null
                && HasSuccessfulGradedRun(activeInfo);
            if (preserveSuccessfulRunContext)
            {
                action = new OutcomeAction(
                    Kind: OutcomeActionKind.NotifyUserAndStop,
                    MetaMessage: "The follow-up failed before first agent output. A prior completed run with a code-review grade remains the authoritative review basis; routing to Escalated without reissue.",
                    IsHeuristicFallback: false)
                {
                    IssueKind = RunIssueKind.CliLaunchFailed,
                    MessageKind = OrchestratorMessageKind.GiveUp,
                };
                _logger.LogWarning(
                    "review_basis_preserved job={JobId} failedAttempt=cli-launch-failed basis=last-successful-graded-run action=escalated",
                    jobId);
            }

            // Per-task anti-endless-reissue circuit breaker. A run that did not
            // reach review and produced no commit is a "no-progress failure".
            // Count these per task (across the auto-pickup run AND the
            // UserContinue re-issue it spawns - the loop that bypassed every
            // other breaker); once the streak reaches QuarantineFailThreshold,
            // override the action to a terminal quarantine so we STOP
            // re-issuing and park the task in 5e-escalated. Progress (a new
            // commit) or reaching review resets the streak below, so a healthy
            // long-running task that keeps committing is never quarantined.
            var deliberateStop = WasDeliberatelyStopped(execution.Status);
            var movedToReview = RunCompletionPolicy.ShouldMoveToReview(terminalOutcome);
            // Rapid-crash governor (RapidCrashBreaker): a failed run that finished
            // in seconds with no commit. It counts toward the quarantine streak
            // REGARDLESS of issue-kind classification — closing the gap where a
            // launch-shaped fast crash was excluded by CountsAsNoProgressFailure
            // and tight-looped the host — and arms an exponential pickup backoff
            // so the retries before the park are spaced, not saturating.
            var isRapidCrash = !deliberateStop
                && RapidCrashBreaker.IsRapidCrash(execution.Status ?? "", execution.DurationSeconds ?? 0.0, commitsDuringRun);
            // A transient environmental fault (host file lock / network / dead
            // CLI session) is never the task's fault and has its own bounded
            // retry-with-backoff budget, so an environmental cycle must not accrue
            // toward the per-task no-progress quarantine streak - not even when it
            // happens to crash fast enough to look "rapid" (AGT-1944). The
            // rapid-crash backoff still applies to genuine tight-looping crashes.
            var isRetryableEnvironmental = action != null
                && PostProcessingOutcomeTaxonomy.IsRetryableEnvironmental(action.IssueKind);
            if (action != null && activeInfo != null
                && !movedToReview && !deliberateStop && commitsDuringRun == 0
                && !isRetryableEnvironmental
                && (RunQuarantineBreaker.CountsAsNoProgressFailure(action.IssueKind) || isRapidCrash))
            {
                var fails = _consecutiveFailNoProgress.AddOrUpdate(jobId, 1, (_, n) => n + 1);
                if (RunQuarantineBreaker.ShouldQuarantine(fails, QuarantineFailThreshold))
                {
                    _rapidCrashBackoffUntil.TryRemove(jobId, out _);
                    var priorTopic = ToIssueTopic(action.IssueKind);
                    _logger.LogWarning(
                        "[circuit-breaker] quarantined {JobId} after {N} fails (reason={Reason})",
                        jobId, fails, priorTopic);
                    _orchestratorLog.Append(activeInfo.WatchPath, new OrchestratorLogEntry
                    {
                        Kind = OrchestratorLogKinds.Intervention,
                        Topic = "quarantined",
                        JobId = jobId,
                        Summary = $"Quarantined \"{activeInfo.Title}\" after {fails} consecutive failed runs without progress (last: {priorTopic}).",
                        Reasoning = "Per-task circuit breaker: repeated no-progress failures (no new commit between attempts) would otherwise re-issue forever. Parking in Escalated to stop the loop."
                    });
                    action = new OutcomeAction(
                        Kind: OutcomeActionKind.NotifyUserAndStop,
                        MetaMessage: $"quarantined after {fails} consecutive failed runs without progress (last issue: {priorTopic}). Re-issuing would loop; the task is parked in Escalated for operator intervention.",
                        IsHeuristicFallback: false)
                    {
                        IssueKind = RunIssueKind.Quarantined,
                        MessageKind = OrchestratorMessageKind.Quarantined
                    };
                    _consecutiveFailNoProgress.TryRemove(jobId, out _);
                }
                else if (isRapidCrash)
                {
                    // Not yet at the park threshold: space the next pickup so the
                    // crash cannot tight-loop while the streak accrues.
                    var until = DateTime.UtcNow + RapidCrashBreaker.Backoff(fails);
                    _rapidCrashBackoffUntil[jobId] = until;
                    _logger.LogWarning(
                        "[rapid-crash] {JobId} failed in {Dur:F1}s (#{N}, no commit); backing off pickup until {Until:o}",
                        jobId, execution.DurationSeconds ?? 0.0, fails, until);
                }
            }
            else if (commitsDuringRun > 0 || movedToReview)
            {
                // Progress or a clean finish breaks the streak.
                _consecutiveFailNoProgress.TryRemove(jobId, out _);
                _rapidCrashBackoffUntil.TryRemove(jobId, out _);
            }

            if (action != null && activeInfo != null)
            {
                // Build a short signature so we can suppress the second
                // identical heuristic message in a Recovery cascade. Two
                // back-to-back "needsinput" warnings on a stuck loop do not
                // help the user; one is enough.
                var signature = $"{action.Kind}|{action.IssueKind}|{action.IsHeuristicFallback}|{outcome.Kind}|{string.Equals(capturedPlan!.EventKind, "recovery", StringComparison.OrdinalIgnoreCase)}";
                var suppress = action.Kind == OutcomeActionKind.NotifyUserAndAccept
                            && (action.IsHeuristicFallback || action.IssueKind != RunIssueKind.None)
                            && string.Equals(_lastMetaSignature, signature, StringComparison.Ordinal);

                if (!suppress)
                {
                    if (!string.IsNullOrWhiteSpace(action.MetaMessage))
                    {
                        var kind = action.MessageKind != OrchestratorMessageKind.Decision
                            ? action.MessageKind
                            : action.Kind switch
                        {
                            OutcomeActionKind.ReissueWithStrongerFraming => OrchestratorMessageKind.Reissue,
                            OutcomeActionKind.NotifyUserAndStop          => OrchestratorMessageKind.GiveUp,
                            _                                            => OrchestratorMessageKind.Decision
                        };
                        var category = action.IssueKind == RunIssueKind.None ? null : ToIssueTopic(action.IssueKind);
                        var message = category == null
                            ? action.MetaMessage
                            : $"{action.MetaMessage} (category: {category}; run summary: {outcome.Summary ?? "n/a"})";
                        _chatLog.Append(activeInfo, kind, message);
                    }
                }
                _lastMetaSignature = signature;

                if (action.Kind == OutcomeActionKind.ReissueWithStrongerFraming
                    && !string.IsNullOrWhiteSpace(action.FollowupRetryPrompt))
                {
                    // Release the active-job latch on the original run so the
                    // re-issue can claim it. We then schedule the re-issue on
                    // the thread pool so OnCliFinished returns promptly.
                    ReleaseRun(jobId);
                    NotifyStatus();
                    var wasRecovery = string.Equals(capturedPlan!.EventKind, "recovery", StringComparison.OrdinalIgnoreCase);
                    var retryPrompt = action.IsPreframedRetryPrompt
                        ? action.FollowupRetryPrompt!
                        : RunOutcomePolicy.BuildReissueFollowupPrompt(action.FollowupRetryPrompt!, recoveryContext: wasRecovery);
                    var retryAttempt = action.RetryAttempt;
                    // Environmental retries carry a backoff so a transient host
                    // file lock / network glitch gets real wall-clock time to
                    // clear before the re-run (AGT-1944). Zero for every other
                    // reissue, so ordinary continuations still run promptly.
                    var retryBackoff = action.RetryBackoff;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (retryBackoff > TimeSpan.Zero)
                            {
                                _logger.LogInformation(
                                    "[environmental-retry] {JobId} backing off {Backoff} before retry (attempt {Attempt})",
                                    jobId, retryBackoff, retryAttempt);
                                await Task.Delay(retryBackoff, CancellationToken.None);
                            }
                            await RunCliAsync(jobId, RunIntent.UserContinue, retryPrompt, retryAttempt, ContinueModes.Continue, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Re-issue run failed for {JobId}", jobId);
                        }
                    });
                    return;
                }

                // Intelligente Abbruch-Bewertung (ADR-0032): before the fixed
                // terminal route to Escalated, let the abort-review step
                // (default-OFF per project) judge whether the abort was
                // legitimate. A rerun/accept verdict short-circuits the
                // escalation; an escalate verdict, a disabled step, an
                // unwired service, or a review failure all fall through to
                // the existing typed escalation route below unchanged.
                //
                // The step now also honours its per-project run condition: this
                // is the abort path (Aborted = true) with the run's exit code
                // and the task's own type/tags in scope, so a project can scope
                // abort-review to e.g. only non-zero exits or only bug tasks.
                var abortConditionContext = new AgentStudio.Pipeline.PipelineStepConditionContext
                {
                    Aborted = true,
                    ExitCode = execution.ExitCode,
                    AnyAspectFailed = false,
                    TaskType = activeInfo?.TaskType,
                    Tags = activeInfo?.Tags,
                };
                if (action.Kind == OutcomeActionKind.NotifyUserAndStop
                    && activeInfo != null
                    && ShouldRouteIssueToEscalated(action.IssueKind)
                    && !preserveSuccessfulRunContext
                    // Non-retryable task verdicts skip abort-review entirely.
                    // Account-level provider limits never reach this task
                    // escalation path; they return earlier into quota-waiting.
                    && action.IssueKind is not (RunIssueKind.ContextOverflow or RunIssueKind.ModelInvalid or RunIssueKind.AuthRefreshFailed or RunIssueKind.Quarantined or RunIssueKind.AgentGitViolation)
                    && _postAbortReview != null
                    && AgentStudio.Pipeline.PipelineStepConfigResolver.ShouldRun(
                        AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
                            _projectSettings.Get(ProjectName),
                            activeInfo),
                        AgentStudio.Pipeline.PipelineCatalogue.AbortReviewStep,
                        abortConditionContext))
                {
                    var handled = await TryRunAbortReviewAsync(
                        activeInfo, jobId, jobKey, cliType, execution, action,
                        liveOutputSnapshot, commitsDuringRun, headShaAfter, usage,
                        EpicRunPolicy.IsPlanningRun(activeInfo.Kind, intentSnapshot));
                    if (handled) return;
                }

                // Completion-loop re-trigger (loop id
                // completion.retrigger-transient-abort-per-job). A transient
                // process abort (the watchdog killed the run) is a runner
                // outcome, not an agent decision: instead of dead-ending the
                // task in Escalated, re-spawn the same job up to N times.
                // This is the deterministic default-on fallback that runs only
                // when the LLM abort-review step above did NOT handle the run
                // (that step is default-OFF per project, so for most projects
                // this is the only completion loop). Scoped to WatchdogTimeout
                // - EnvironmentBlocker is unrecoverable and PermissionBlocked
                // needs an operator, so both still fall through to escalation below.
                if (action.Kind == OutcomeActionKind.NotifyUserAndStop
                    && activeInfo != null
                    && CompletionRetriggerDecider.ShouldRetrigger(action.IssueKind, RemainingCompletionRetriggerBudget(jobId, action.IssueKind)))
                {
                    var used = _completionRetriggerUsed.TryGetValue(jobId, out var spent) ? spent : 0;
                    _completionRetriggerUsed[jobId] = used + 1;
                    var attemptNo = used + 1;
                    var issueTopic = ToIssueTopic(action.IssueKind);
                    var maxAttempts = CompletionRetriggerDecider.BudgetFor(action.IssueKind);

                    _chatLog.Append(activeInfo, OrchestratorMessageKind.Recovery,
                        RecoveryChatLine.Format(
                            RecoveryChatLine.ReasonWatchdog,
                            "silence timeout",
                            "reissue",
                            attempt: attemptNo,
                            maxAttempts: maxAttempts,
                            sessionResumed: true));
                    _orchestratorLog.Append(activeInfo.WatchPath, new OrchestratorLogEntry
                    {
                        Kind = OrchestratorLogKinds.Action,
                        Topic = OrchestratorLogTopics.Watchdog,
                        JobId = jobId,
                        Summary = $"Completion-loop re-triggered \"{activeInfo.Title}\" after {issueTopic} (attempt {attemptNo}/{maxAttempts}).",
                            Reasoning = "Transient process abort (watchdog/timeout/infra crash) is a runner outcome, not an agent decision. Re-spawning the same job with its unchanged model instead of escalating for operator intervention; the bounded budget converges to Escalated."
                    });

                    // Release the active-job latch so the re-issue can claim
                    // it, then schedule on the thread pool so OnCliFinished
                    // returns promptly (mirrors the abort-review rerun path).
                    ReleaseRun(jobId);
                    NotifyStatus();
                    var retryPrompt = BuildCompletionRetriggerPrompt(activeInfo);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunCliAsync(jobId, RunIntent.UserContinue, retryPrompt, 0, ContinueModes.Continue, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "completion-loop retrigger failed for {JobId}", jobId);
                        }
                    });
                    return;
                }

                if (action.Kind == OutcomeActionKind.NotifyUserAndStop
                    && activeInfo != null
                    && ShouldRouteIssueToEscalated(action.IssueKind))
                {
                    _orchestratorLog.Append(activeInfo.WatchPath, new OrchestratorLogEntry
                    {
                        Kind = OrchestratorLogKinds.Intervention,
                        Topic = ToIssueTopic(action.IssueKind),
                        JobId = jobId,
                        Summary = $"Routed \"{activeInfo.Title}\" to Escalated after {ToIssueTopic(action.IssueKind)}.",
                        Reasoning = action.MetaMessage
                    });
                    // The job is leaving the run loop for operator intervention; forget
                    // any spent completion-loop budget so a future run starts
                    // fresh (mirrors the abort-review reset).
                    _completionRetriggerUsed.TryRemove(jobId, out _);
                    // Same for the per-task quarantine streak: once an operator owns
                    // the card, a later run should start from zero rather than
                    // inherit a near-trip count.
                    _consecutiveFailNoProgress.TryRemove(jobId, out _);
                    // Terminal: tear down the coding worktree+branch (kept only
                    // if its work is unmerged, so a human can still resolve it).
                    TeardownWorktreeForJob(jobId);
                    var move = await _humanReviewEscalation.EscalateAsync(
                        jobId, activeInfo.WatchPath, ProjectName,
                        ResolveEscalationCategory(action.IssueKind, activeInfo.FolderPath),
                        action.MetaMessage ?? ToIssueTopic(action.IssueKind),
                        CancellationToken.None);
                    if (move.Status != MoveJobStatus.Success)
                    {
                        _logger.LogWarning(
                            "Issue routing to Escalated failed for {JobId}: {Status} {Message}",
                            jobId, move.Status, move.Message);
                    }
                    return;
                }
            }

            // Auto-mode NeedsInput: when the project's runner is in an auto
            // mode and the agent emitted [[TASK_NEEDS_INPUT:...]] (or the
            // heuristic landed on NeedsInput with substantial text), we ask
            // the orchestrator to decide on the user's behalf and feed the
            // decision back as a Continue follow-up. Manual mode keeps
            // today's path: the question stays in the chat for the user.
            if (ShouldHandleNeedsInputUnattended(outcome.Kind, capturedIntent, _mode, activeInfo))
            {
                _ = Task.Run(() => RunOrchestratorDecisionAsync(activeInfo!, jobId, outcome));
                return;
            }

            // Loop closed: the agent did NOT come back with another
            // NEEDS_INPUT. Whether that's Done, Blocked, or anything else,
            // the auto-loop is no longer active for this job, so reset
            // the stuck-loop counter. A future NEEDS_INPUT on the same
            // job starts a fresh loop with a fresh budget.
            _stuckLoops.TryRemove(jobId, out _);

            // movedToReview was computed up-front for the circuit breaker.

            // Concept cards never merge their task branch. Once the core run has
            // produced a reviewable docs-only Dossier, copy that single
            // directory through the managed project-artifact commit/push
            // boundary. The subsequent concept review reads the published
            // document and routes it to the human sight-review marker.
            if (movedToReview
                && activeInfo != null
                && TaskModes.IsConcept(activeInfo.Mode))
            {
                var movedConcept = _scanner.FindJob(jobId, Entry.Path) ?? activeInfo;
                await PublishConceptWorkbenchAsync(movedConcept, run);
            }

            // UI tasks do not enter the standard aspect-review pipeline. A
            // successful core run must first satisfy its iteration-scoped visual
            // contract, then pauses directly in Human Review with the durable
            // marker that Part 2 consumes.
            if (movedToReview
                && activeInfo != null
                && snapRun is { IsUiIterationPipeline: true })
            {
                await HandleUiIterationCompletionAsync(
                    activeInfo, snapRun, jobId, execution,
                    snapRun.UiIteration, snapRun.UiMaxIterations,
                    capturedAttempt);
                return;
            }

            // Way 3 (non-deterministic half): an epic's planning run just
            // finished successfully. Parse the authored plan and create the
            // sub-tasks under the epic BEFORE the epic moves to review, so a
            // crash mid-finalisation cannot strand a successful plan with no
            // sub-tasks. A continue on an epic is steering, not decomposition,
            // so it does not trigger this (mirrors RunCliAsync's gate).
            // A planning run carries no Result-SHA, so its completion lane is
            // never 4-auto-review (EpicRunPolicy.PlanningCompletionLane).
            string? epicPlanningLane = null;
            if (movedToReview
                && activeInfo != null
                && EpicRunPolicy.IsPlanningRun(activeInfo.Kind, intentSnapshot))
            {
                var finalized = DecomposeEpicAndCreateSubTasks(
                    activeInfo,
                    liveOutputSnapshot.Select(l => l.Text).ToList(),
                    planSnapshot?.EventInputSessionId);
                epicPlanningLane = EpicRunPolicy.PlanningCompletionLane(finalized.Valid);
            }

            if (movedToReview)
            {
                var completionLane = epicPlanningLane ?? TaskStates.AutoReview;
                if (activeInfo != null)
                {
                    // Result finalization is an awaited post-core gate. A
                    // summary-side failure retries only this application step;
                    // bounded exhaustion records Degraded and still permits the
                    // completed core run to enter review.
                    await _summaryService.FinalizeAsync(
                        activeInfo,
                        terminalOutcome,
                        CancellationToken.None);
                }
                // Drop a completion marker BEFORE the move so a crash between
                // here and the folder-rename leaves enough state on disk for
                // CrashRecoveryService to finish the transition on next boot.
                // Cleared after a successful move (no point keeping a marker
                // in 4-auto-review). See ADR-0020 + ADR-0025 (post-CLI lane
                // is 4-auto-review now; the orchestrator decides whether to
                // promote to 5-human-review). An Epic planning run is the one
                // exception and names its own lane.
                if (activeInfo != null)
                {
                    CompletionMarker.Write(activeInfo.FolderPath, new CompletionMarker
                    {
                        TargetState = completionLane,
                        ExecutionStatus = execution.Status,
                        AgentOutcome = outcome.Kind.ToString()
                    }, _logger);
                }

                var moveOutcome = await _transitions.MoveAsync(
                    jobId, completionLane, Entry.Path, CancellationToken.None,
                    transitionCause: CompletionLaneChangeCause(completionLane),
                    transitionDetail: epicPlanningLane is null ? outcome.Kind.ToString() : "epic-planning");
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    // The job made it out of the run loop; forget any spent
                    // abort-review rerun + completion-loop re-trigger budgets
                    // so a future run starts fresh.
                    _abortReviewRerunsUsed.TryRemove(jobId, out _);
                    _completionRetriggerUsed.TryRemove(jobId, out _);
                    // Terminal: the task left the loop into review, so tear down
                    // its coding worktree+branch (deferred from per-run).
                    TeardownWorktreeForJob(jobId);
                    var movedInfo = _scanner.FindJob(jobId, Entry.Path);
                    if (movedInfo != null) CompletionMarker.Clear(movedInfo.FolderPath, _logger);
                }
                else
                {
                    _logger.LogWarning(
                        "Job {JobId} completed but could not move to review: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            else if (activeInfo != null
                && StrandedRunBackstop.MustEscalateStrandedRun(execution.Status, outcome.Kind))
            {
                // Drive-to-conclusion backstop (invariant: no FAILED run ever
                // stays in 3-progress). Some failure shapes reach here without
                // being routed, retried, or moved to review: a failed run with
                // no agent text (NoAgentOutput -> Accept) or a CLI launch/resume
                // failure (CliLaunchFailed -> NotifyUserAndAccept). Left as-is
                // they sit in 3-progress forever - pickup only scans 2-ready -
                // a permanent zombie (the recurring "in-progress lane kaputt"
                // incident: a rapid stale-session resume crash, exit=1, 0 output,
                // that the typed routes above never claimed). A deliberate stop
                // (status=stopped) and a manual-mode NeedsInput legitimately stay
                // in progress and are excluded by the guard. See
                // docs/concepts/runner-stability-incidents.html.
                var backstopIssueKind = action?.IssueKind is RunIssueKind k and not RunIssueKind.None
                    ? k
                    : RunIssueKind.OrchestratorInconclusive;
                var backstopTopic = ToIssueTopic(backstopIssueKind);
                var backstopReason = !string.IsNullOrWhiteSpace(action?.MetaMessage)
                    ? action!.MetaMessage!
                    : $"Run failed ({backstopTopic}) and reached no terminal verdict; routed to Escalated so it cannot strand in 3-progress.";
                _orchestratorLog.Append(activeInfo.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Intervention,
                    Topic = backstopTopic,
                    JobId = jobId,
                    Summary = $"Routed \"{activeInfo.Title}\" to Escalated: a failed run that no terminal route claimed ({backstopTopic}) would otherwise strand in 3-progress.",
                    Reasoning = backstopReason
                });
                _completionRetriggerUsed.TryRemove(jobId, out _);
                _consecutiveFailNoProgress.TryRemove(jobId, out _);
                _rapidCrashBackoffUntil.TryRemove(jobId, out _);
                TeardownWorktreeForJob(jobId);
                var backstopMove = await _humanReviewEscalation.EscalateAsync(
                    jobId, activeInfo.WatchPath, ProjectName,
                    ToEscalationCategory(backstopIssueKind),
                    backstopReason,
                    CancellationToken.None);
                if (backstopMove.Status != MoveJobStatus.Success)
                {
                    _logger.LogWarning(
                        "Drive-to-conclusion backstop escalation routing failed for {JobId}: {Status} {Message}",
                        jobId, backstopMove.Status, backstopMove.Message);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Job {JobId} finished with status {Status}. Leaving it in progress for review or recovery.",
                    jobId, execution.Status);
            }

            // Auto-pickup cascade containment. Only auto-issued runs feed
            // the counter; manual starts and user-driven continues do not.
            // Reaching the threshold flips the runner to manual so a single
            // bad event (mid-flight kill, dead session id, watchdog
            // regression) cannot burn through the entire ready queue.
            if (capturedIntent == RunIntent.AutoPickup)
            {
                if (movedToReview)
                {
                    _consecutiveAutoFailureCount = 0;
                    _recentAutoFailureJobIds.Clear();
                    // A success between failures means the project is not
                    // systemically broken; forget previously-parked offenders
                    // so the "3 distinct parked tasks" halt only fires on a
                    // genuine run of failures with no success in between.
                    _parkedFailedJobIds.Clear();
                    // Real progress: the folder left 3-progress, so clear any
                    // zombie-resume streak it accumulated from earlier failed
                    // resumes (reset-on-progress, mirrors the per-slug attempt
                    // counter).
                    if (activeInfo != null)
                        _zombieResumeFailures.TryRemove(Path.GetFileName(activeInfo.FolderPath), out _);
                }
                else if (WasDeliberatelyStopped(execution.Status))
                {
                    // Neutral outcome. A run that was deliberately killed
                    // (user pause, follow-up pause-and-send, silence watchdog,
                    // host shutdown / backend restart, run cancellation) is NOT
                    // a task failure: the agent never got to finish. Counting
                    // it toward the auto-failure circuit-breaker is what let a
                    // burst of backend restarts (the 2026-05-30 incident) trip
                    // the breaker and halt the whole runner. Leave the counter
                    // untouched - neither increment nor reset.
                    _logger.LogInformation(
                        "[taskboard] auto-pickup run for {JobId} ended as '{Status}' (deliberate stop); not counting toward the auto-failure circuit-breaker",
                        jobId, execution.Status);
                }
                else
                {
                    if (IsRateLimitFailure(liveOutputSnapshot))
                    {
                        _logger.LogWarning(
                            "Runner '{Project}' saw rate-limit-shaped auto-pickup failure on {JobId}; cooling down without quarantining the task.",
                            ProjectName, jobId);
                        ScheduleGlobalBreakerCooldown($"rate-limit or transient CLI quota failure on '{jobId}'", activeInfo);
                    }
                    else
                    {
                        HandleAutoPickupFailure(jobId, activeInfo);
                    }
                    // Zombie-resume accounting. This branch means an auto-pickup
                    // run finished WITHOUT reaching review and was not a
                    // deliberate stop, so the folder is left sitting in
                    // 3-progress. If it also carries no resumable session id,
                    // the progress-first picker will resume it again next tick,
                    // jumping ahead of the due 2-ready task forever. Count the
                    // failed resume so the picker dead-letters the zombie after
                    // ZombieResumeFailureThreshold attempts (and reset the
                    // counter when the run actually captured a session). This is
                    // the wire that was missing: without it the per-slug counter
                    // never incremented in production, so the picker's zombie
                    // guard never tripped and the zombie kept getting picked.
                    AccountZombieResumeOutcome(jobId);
                }
            }
        }
        catch (Exception ex)
        {
            // no-silent-death: a throw here abandons the run's outcome processing
            // (completion, lane move, re-issue). Log the full exit context with
            // the cause so the abandoned run is never silent - the orphan changes
            // it leaves are exactly what the boot-time crash-recovery net rescues.
            _logger.LogError(ex,
                "Runner finalization crashed for {JobId} on {Cli}: status={Status}, exitCode={ExitCode}, duration={Duration:F1}s; run outcome abandoned (reason={Reason})",
                jobId, cliType, execution.Status, execution.ExitCode, execution.DurationSeconds ?? 0.0, ex.Message);
        }
        finally
        {
            // Release THIS finished run's slot, addressed by job id. The old
            // guard `_activeJobId == jobId` was a single-slot assumption:
            // `_activeJobId` is SingleJobId (FirstOrDefault), so at
            // MaxParallelism>1 it matches only ONE of the concurrent runs - when
            // the finishing job was not that "first" one the release was skipped
            // and its slot LEAKED (occ stayed high, eventually no free slot ->
            // no further pickups). Contains(jobId) is byte-identical at max==1
            // (one slot) and correct for N slots.
            if (_activeRuns.Contains(jobId))
            {
                ReleaseRun(jobId);
                ApplyPendingModeIfAny(jobId);
                NotifyStatus();
            }
        }
    }

    private async Task PublishConceptWorkbenchAsync(TaskInfo info, ActiveRun run)
    {
        var startedAt = DateTime.UtcNow;
        ConceptWorkbenchPublishResult result;
        if (_conceptWorkbenchPublisher is null)
        {
            var review = new ConceptWorkbenchReview(
                false, null, null, null,
                ["Concept Dossier publisher is unavailable."]);
            result = new ConceptWorkbenchPublishResult(false, review.Summary, review);
        }
        else
        {
            result = await _conceptWorkbenchPublisher.PublishAsync(
                info,
                run.WorktreePath ?? run.WorkingDirectory ?? string.Empty,
                CancellationToken.None);
        }

        _pipelineLog?.RecordStep(info.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.ConceptWorkbenchPlacementStepId,
            Kind = StepKind.Tool,
            Status = result.Success ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            Verdict = result.Success ? "workbench-published" : "workbench-invalid",
            VerdictSummary = result.Summary,
            Reason = result.Success ? null : result.Summary,
        });
        _logger.Log(
            result.Success ? LogLevel.Information : LogLevel.Warning,
            "concept_workbench_placement project={Project} job={JobId} success={Success} path={Path} commit={Commit} summary={Summary}",
            ProjectName,
            info.Id,
            result.Success,
            result.Review.RepoRelativeDirectory ?? "",
            result.CommitSha ?? "",
            result.Summary);
    }

    /// <summary>
    /// Ledger cause of a core-run completion move. A run that lands in a review
    /// lane is a delivery hand-off; an epic planning run that produced no valid
    /// plan returns to Backlog, which is the runner handing the task back.
    /// </summary>
    internal static string CompletionLaneChangeCause(string completionLane)
        => completionLane is TaskStates.AutoReview or TaskStates.HumanReview
            ? LaneChangeCauses.Delivered
            : LaneChangeCauses.RunnerRequeue;

    private async Task HandleUiIterationCompletionAsync(
        TaskInfo info,
        ActiveRun run,
        string jobId,
        CliExecution execution,
        int iteration,
        int maxIterations,
        int evidenceRetryAttempt)
    {
        var decision = UiIterationGate.Evaluate(info.FolderPath, iteration, maxIterations);
        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(info.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.UiIterationArtifactStepId,
            Kind = StepKind.Tool,
            Status = decision.Action == UiIterationGateAction.ReadyForHumanReview
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = decision.Action switch
            {
                UiIterationGateAction.ReadyForHumanReview => "evidence-ready",
                UiIterationGateAction.EscalateCapReached => "cap-exhausted",
                _ => "evidence-missing",
            },
            Reason = decision.Findings.Count == 0 ? null : string.Join(" ", decision.Findings),
            VerdictSummary = decision.Findings.Count == 0
                ? $"Iteration {iteration}/{maxIterations}: {decision.ArtifactPaths.Count} visual artifact(s) and changes.md present."
                : string.Join(" ", decision.Findings),
        });

        if (decision.Action == UiIterationGateAction.Incomplete
            && evidenceRetryAttempt < RunOutcomePolicy.MaxAutoReissueAttempts)
        {
            _chatLog.Append(info, OrchestratorMessageKind.Reissue,
                $"[ui-iteration] Iteration {iteration}/{maxIterations} is incomplete: {string.Join(" ", decision.Findings)} Reissuing once to produce the mandatory evidence.");
            ReleaseRun(jobId);
            NotifyStatus();
            var retryPrompt = UiIterationGate.BuildMissingEvidenceFollowUp(decision);
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunCliAsync(jobId, RunIntent.UserContinue, retryPrompt,
                        evidenceRetryAttempt + 1, ContinueModes.Continue, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UI iteration evidence reissue failed for {JobId}", jobId);
                }
            });
            return;
        }

        if (decision.Action is UiIterationGateAction.Incomplete or UiIterationGateAction.EscalateCapReached)
        {
            _pipelineLog?.Complete(info.FolderPath,
                pendingStepReason: "UI iteration could not reach its human gate.");
            TeardownWorktreeForJob(jobId);
            var reason = decision.Action == UiIterationGateAction.EscalateCapReached
                ? $"UI iteration cap {maxIterations} was exhausted without a human finish decision."
                : $"UI iteration {iteration}/{maxIterations} still lacked mandatory evidence after its bounded retry: {string.Join(" ", decision.Findings)}";
            var escalation = await _humanReviewEscalation.EscalateAsync(
                jobId, info.WatchPath, ProjectName,
                HumanReviewEscalationCategories.UiIterationCap, reason,
                CancellationToken.None);
            _logger.LogWarning(
                "ui_pipeline_escalated project={Project} job={JobId} iteration={Iteration}/{MaxIterations} status={Status} reason={Reason}",
                ProjectName, jobId, iteration, maxIterations, escalation.Status, reason);
            return;
        }

        var visualQa = _visualQa is null
            ? VisualQaResult.NotApplicable
            : await _visualQa.RunAsync(new VisualQaRequest(
                info,
                ResolveVisualQaRepositoryRoot(run),
                ResolveVisualQaChangedFiles(run),
                iteration), CancellationToken.None);
        var visualStartedAt = DateTime.UtcNow;
        _pipelineLog?.RecordStep(info.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.UiVisualCaptureStepId,
            Kind = StepKind.Tool,
            Status = !visualQa.Applicable || visualQa.ScreenshotPaths.Count > 0
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = visualStartedAt,
            CompletedAt = DateTime.UtcNow,
            Verdict = !visualQa.Applicable
                ? "not-applicable"
                : visualQa.ScreenshotPaths.Count > 0 ? "screenshots-captured" : "capture-failed",
            VerdictSummary = visualQa.Applicable
                ? $"Captured {visualQa.ScreenshotPaths.Count} affected view(s); manifest: {visualQa.CaptureManifestPath}."
                : "Visual QA was not applicable to this card.",
        });
        _pipelineLog?.RecordStep(info.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.UiVisualVerdictStepId,
            Kind = StepKind.Orchestrator,
            Status = !visualQa.Applicable || visualQa.Verdict.Status == VisualQaVerdictStatus.Acceptable
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = visualStartedAt,
            CompletedAt = DateTime.UtcNow,
            Model = visualQa.Applicable ? visualQa.Model : null,
            ThinkingLevel = visualQa.Applicable ? visualQa.ThinkingLevel : null,
            Verdict = visualQa.Verdict.Status switch
            {
                VisualQaVerdictStatus.Acceptable => visualQa.Applicable ? "acceptable" : "not-applicable",
                VisualQaVerdictStatus.ClearDefect => "clear-defect",
                _ => "unavailable",
            },
            VerdictSummary = visualQa.Verdict.Summary,
            Reason = visualQa.Decision.Action == VisualQaAction.ProceedToHumanReview
                ? null
                : visualQa.Decision.Reason,
        });

        if (visualQa.Decision.Action == VisualQaAction.RetryWithSteer)
        {
            var steer = visualQa.Decision.SteerPrompt!;
            _mutations.AppendContinuationNote(jobId, steer, info.WatchPath);
            _chatLog.Append(info, OrchestratorMessageKind.Reissue,
                $"[visual-qa] {visualQa.Decision.Reason} Evidence: {string.Join(", ", visualQa.ScreenshotPaths)}");
            ReleaseRun(jobId);
            NotifyStatus();
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunCliAsync(jobId, RunIntent.UserContinue, steer,
                        evidenceRetryAttempt + 1, ContinueModes.Continue, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Automatic visual-QA retry failed for {JobId}", jobId);
                }
            });
            return;
        }

        var contract = new UiIterationReviewContract
        {
            Iteration = iteration,
            MaxIterations = maxIterations,
            CapReached = decision.CapReached,
            ArtifactPaths = decision.ArtifactPaths.Concat(visualQa.EvidenceScreenshotPaths).Distinct().ToArray(),
            ChangeDescriptionPath = decision.ChangeDescriptionPath!,
            VisualQaStatus = visualQa.Applicable ? visualQa.Verdict.Status switch
            {
                VisualQaVerdictStatus.Acceptable => "acceptable",
                VisualQaVerdictStatus.ClearDefect => "clear-defect",
                _ => "unavailable",
            } : "not-applicable",
            VisualQaVerdictPath = visualQa.VerdictPath,
            VisualQaDefects = visualQa.Verdict.Defects,
            VisualQaAutoRetryUsed = visualQa.PriorAutomaticRetries > 0,
        };
        SteerPendingMarker.Write(info.FolderPath, new SteerPendingRecord
        {
            WaitStartedAt = DateTime.UtcNow,
            Kind = SteerPendingKinds.UiIterationReview,
            Question = visualQa.Decision.Action == VisualQaAction.EscalateToHumanReview
                ? $"Visual QA requires human review: {visualQa.Decision.Reason} Inspect the attached screenshots before deciding."
                : decision.CapReached
                ? "Final configured UI iteration. Finish this task or escalate; another feedback iteration is not allowed."
                : "Review the visual result and choose finish or provide feedback for the next iteration.",
            CliType = info.CliType,
            UiIterationReview = contract,
        }, _logger);
        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.AwaitingReview);
        _pipelineLog?.RecordStep(info.FolderPath, new PipelineStepExecution
        {
            StepId = AgentStudio.Pipeline.PipelineCatalogue.UiHumanReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = DateTime.UtcNow,
            Verdict = "awaiting-human-review",
            VerdictSummary = $"Iteration {iteration}/{maxIterations} is ready for visual review.",
        });
        CompletionMarker.Write(info.FolderPath, new CompletionMarker
        {
            TargetState = TaskStates.HumanReview,
            ExecutionStatus = execution.Status,
            AgentOutcome = $"ui-iteration-{iteration:D3}-awaiting-review",
        }, _logger);

        var move = await _transitions.MoveAsync(
            jobId, TaskStates.HumanReview, Entry.Path, CancellationToken.None,
            transitionCause: LaneChangeCauses.Delivered,
            transitionDetail: $"ui-iteration-{iteration}/{maxIterations}");
        if (move.Status == MoveJobStatus.Success)
        {
            TeardownWorktreeForJob(jobId);
            var movedFolder = move.NewFolderPath ?? info.FolderPath;
            CompletionMarker.Clear(movedFolder, _logger);
            _mutations.SetJobPhase(movedFolder, null);
            _logger.LogInformation(
                "ui_pipeline_review_pending project={Project} job={JobId} pipeline={PipelineId} iteration={Iteration}/{MaxIterations} artifacts={ArtifactCount} marker={Marker}",
                ProjectName, jobId, AgentStudio.Pipeline.PipelineCatalogue.UiPipelineId,
                iteration, maxIterations, decision.ArtifactPaths.Count,
                SteerPendingMarker.PathFor(movedFolder));
        }
        else
        {
            _logger.LogWarning(
                "UI iteration {Iteration}/{MaxIterations} for {JobId} is evidenced but could not move to human review: {Status} {Message}",
                iteration, maxIterations, jobId, move.Status, move.Message);
        }
    }

    private IReadOnlyList<string>? ResolveVisualQaChangedFiles(ActiveRun run)
    {
        var root = ResolveVisualQaRepositoryRoot(run);
        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(run.WorkerHeadShaBefore))
            return null;
        var after = _git.ReadHeadShaAt(root);
        if (string.IsNullOrWhiteSpace(after)) return null;
        var files = _git.GetFilesChangedInRangeAtRoot(root, run.WorkerHeadShaBefore, after)
            .Select(change => change.Path.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return files.Length == 0 ? null : files;
    }

    private string ResolveVisualQaRepositoryRoot(ActiveRun run)
        => new[] { run.WorktreePath, Entry.RootPath, run.WorkingDirectory }
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
           ?? string.Empty;

    /// <summary>
    /// Drops the on-disk pickup lock we acquired in <see cref="RunCliAsync"/>
    /// before stamping <c>_activeJobId</c>. Only deletes when this process
    /// still owns the lock - foreign or stale locks are left in place so a
    /// late retry from the real holder cannot be silently clobbered.
    /// </summary>
    private void ReleasePickupLockIfHeld(string? runFolder = null)
    {
        if (_pickupLock == null || _pickupLockOwner == null) return;
        var folder = runFolder ?? _activePickupLockFolder;
        if (runFolder == null) _activePickupLockFolder = null;
        if (string.IsNullOrEmpty(folder)) return;
        try { _pickupLock.Release(folder, _pickupLockOwner); }
        catch (Exception ex) { _logger.LogDebug(ex, "Pickup lock release failed for '{Folder}'", folder); }
    }

    private ActiveRun? ReleaseRun(string jobId, bool releasePickupLock = true)
    {
        var released = _activeRuns.Release(jobId);
        if (releasePickupLock && released?.PickupLockFolder is { } folder)
            ReleasePickupLockIfHeld(folder);
        return released;
    }

    /// <summary>
    /// Way 3 (non-deterministic half): turn the output of an epic's planning /
    /// decomposition run into sub-tasks under the epic. The parser is pure
    /// (<see cref="EpicDecompositionParser"/>); creation goes through the same
    /// <see cref="EpicSubTaskFactory"/> path as the deterministic
    /// <c>POST /api/epics/{id}/sub-tasks</c> endpoint, so the sub-tasks land
    /// with <see cref="TaskInfo.EpicId"/> set and round-trip through the
    /// scanner identically. The target lane is the project's
    /// <see cref="ProjectSettings.EpicSubTasksToReady"/> choice (default
    /// 0-backlog for triage). Emits the "Epic decomposition" timeline step
    /// either way so the operator sees the outcome.
    /// </summary>
    private EpicDecompositionFinalization DecomposeEpicAndCreateSubTasks(
        TaskInfo epic, IReadOnlyList<string> outputLines, string? runId)
        => EpicDecompositionLifecycle.Finalize(
            epic, outputLines, runId, _projectSettings, _mutations, _scanner,
            _states, _timeline, _chatLog, _logger);

    private static bool ShouldRouteIssueToEscalated(RunIssueKind issueKind)
        => issueKind is RunIssueKind.PermissionBlocked
                     or RunIssueKind.WatchdogTimeout
                     or RunIssueKind.EnvironmentBlocker
                     or RunIssueKind.EmptyFastExit
                     or RunIssueKind.ContextOverflow
                     // A non-retryable model rejection reaches Escalated with
                     // an honest reason instead of the inconclusive catch-all.
                     // Provider quota exhaustion is capability state and is
                     // intentionally absent from this task escalation list.
                     or RunIssueKind.ModelInvalid
                     // A failed OAuth-session refresh (AGT-2066 breaker) is
                     // non-retryable and must reach Escalated with a re-auth
                     // instruction instead of stranding in 3-progress.
                     or RunIssueKind.AuthRefreshFailed
                     // A transient environmental fault that persisted after the
                     // bounded retry-with-backoff, and a CLI launch/resume failure
                     // that persisted after the fresh-start retry, both reach human
                     // intervention with their own honest category (AGT-1944).
                     or RunIssueKind.EnvironmentalTransient
                     or RunIssueKind.CliLaunchFailed
                     or RunIssueKind.Quarantined
                     or RunIssueKind.AgentGitViolation
                     // Drive-to-conclusion: a failed run the deterministic
                     // contract could not close out (a hard CLI crash, or real
                     // text that maps to no terminal verdict) returns
                     // NotifyUserAndStop and MUST reach Escalated. Without
                     // these two it stayed in 3-progress forever (the old
                     // classifier-unknown stranding: ASS-1757, AGT dashboard).
                     or RunIssueKind.InfraCrash
                     or RunIssueKind.OrchestratorInconclusive;

    /// <summary>Remaining completion-loop re-trigger budget for a job
    /// (loop id completion.retrigger-transient-abort-per-job). Counts down
    /// from <see cref="CompletionRetriggerDecider.DefaultBudget"/> as
    /// transient aborts are re-triggered; reset when the job leaves the run
    /// loop.</summary>
    private int RemainingCompletionRetriggerBudget(string jobId, RunIssueKind issueKind)
    {
        var used = _completionRetriggerUsed.TryGetValue(jobId, out var c) ? c : 0;
        return Math.Max(0, CompletionRetriggerDecider.BudgetFor(issueKind) - used);
    }

    private void EmitLoadThrottleDecision(TaskInfo info, LoadThrottleDecision decision)
    {
        var key = $"load-throttle|{decision.CurrentPercent:0}";
        lock (_lastAdmissionDecisionByJob)
        {
            if (_lastAdmissionDecisionByJob.TryGetValue(info.Id, out var previous) && previous == key) return;
            _lastAdmissionDecisionByJob[info.Id] = key;
        }

        _logger.LogWarning("load_throttle_pick_deferred jobId={JobId} project={Project} cpuPercent={CpuPercent:0.#} sustainedSeconds={SustainedSeconds:0}",
            info.Id, ProjectName, decision.CurrentPercent, decision.SustainedFor.TotalSeconds);
        _chatLog.Append(info, OrchestratorMessageKind.Decision, "[load-throttle] " + decision.Reason);
        _timeline?.Append(info.FolderPath, TimelineEventKinds.LoadThrottleDecision, TimelineActors.System,
            summary: decision.Reason,
            details: new()
            {
                ["cpuPercent"] = decision.CurrentPercent.ToString("0.#", CultureInfo.InvariantCulture),
                ["sustainedSeconds"] = decision.SustainedFor.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
                ["category"] = "environmental-load",
            });
        _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            JobId = info.Id,
            Summary = "load-throttle: new slot admission deferred",
            Reasoning = decision.Reason,
        });
    }

    /// <summary>
    /// Follow-up prompt for a completion-loop re-trigger after a transient
    /// (watchdog) abort. Tells the agent the previous run was cut off by the
    /// runner rather than by its own decision, and - tying back to the
    /// watchdog long-op fix - asks it to narrate progress during any long
    /// operation so a legitimate wait stays visibly alive.
    /// </summary>
    private static string BuildCompletionRetriggerPrompt(TaskInfo info)
        => "The previous run for this task was cut off by the runner watchdog (a transient timeout), not by your own decision, "
         + "so the work was never finished. Continue the task to completion. "
         + "If you run a long operation (dev server, build, test wait, poll loop), narrate progress periodically so the watchdog can tell you are still alive. "
         + "End with exactly one terminal sentinel on its own line: [[TASK_DONE]], [[TASK_BLOCKED:missing-dependency-xyz]], [[TASK_NEEDS_INPUT:choose-primary-column]], or [[TASK_NOOP]]. Replace the example reason with the actual short reason.\n\n"
         + $"Task: {info.Title}";

    private static string ToIssueTopic(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.PermissionBlocked        => "permission-blocked",
        RunIssueKind.WatchdogTimeout          => "watchdog-timeout",
        RunIssueKind.MissingTerminalSentinel  => "missing-terminal-sentinel",
        RunIssueKind.HeuristicDone             => "heuristic-done",
        RunIssueKind.InfraCrash               => "infra-crash",
        RunIssueKind.OrchestratorInconclusive => "orchestrator-inconclusive",
        RunIssueKind.CliLaunchFailed          => "cli-launch-failed",
        RunIssueKind.EmptyFastExit            => "empty-fast-exit",
        RunIssueKind.NoAgentOutput            => "no-agent-output",
        RunIssueKind.EnvironmentBlocker       => "environment-blocker",
        RunIssueKind.SilentCompletion         => "codex-silent-completion",
        RunIssueKind.ContextOverflow          => "context-overflow",
        RunIssueKind.ModelInvalid             => "model-invalid",
        RunIssueKind.QuotaExhausted           => "quota-exhausted",
        RunIssueKind.AuthRefreshFailed        => "auth-refresh-failed",
        RunIssueKind.EnvironmentalTransient   => "environmental",
        RunIssueKind.Quarantined              => "quarantined",
        RunIssueKind.AgentGitViolation        => "agent-git-violation",
        _                                     => "none"
    };

    /// <summary>Maps the issue that triggered an Escalated route onto a
    /// <see cref="HumanReviewEscalationCategories"/> value so the decision
    /// journal records WHY the card was escalated.</summary>
    private static string ToEscalationCategory(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.WatchdogTimeout    => HumanReviewEscalationCategories.WatchdogKill,
        RunIssueKind.PermissionBlocked  => HumanReviewEscalationCategories.PermissionBlocked,
        RunIssueKind.EnvironmentBlocker => HumanReviewEscalationCategories.EnvironmentBlocker,
        RunIssueKind.EnvironmentalTransient => HumanReviewEscalationCategories.Environmental,
        RunIssueKind.CliLaunchFailed    => HumanReviewEscalationCategories.CliLaunchFailed,
        RunIssueKind.EmptyFastExit      => HumanReviewEscalationCategories.EmptyFastExit,
        RunIssueKind.ContextOverflow    => HumanReviewEscalationCategories.ContextOverflow,
        RunIssueKind.ModelInvalid       => HumanReviewEscalationCategories.ModelInvalid,
        RunIssueKind.QuotaExhausted     => HumanReviewEscalationCategories.QuotaExhausted,
        RunIssueKind.AuthRefreshFailed  => HumanReviewEscalationCategories.AuthRefreshFailed,
        RunIssueKind.Quarantined        => HumanReviewEscalationCategories.Quarantined,
        RunIssueKind.AgentGitViolation  => HumanReviewEscalationCategories.AgentGitViolation,
        RunIssueKind.InfraCrash         => HumanReviewEscalationCategories.InfraCrash,
        RunIssueKind.OrchestratorInconclusive => HumanReviewEscalationCategories.OrchestratorInconclusive,
        _                               => HumanReviewEscalationCategories.AutoFailurePark
    };

    /// <summary>
    /// Results-aware escalation category. For an inconclusive run (the contract
    /// could not map it to a terminal verdict) that nonetheless left files in
    /// <c>results/</c>, this returns the distinct
    /// <see cref="HumanReviewEscalationCategories.InconclusiveWithResults"/>
    /// category so the board routes it to Escalated WITH a "there is partial
    /// work to inspect" hint rather than the bare inconclusive park. Only an
    /// inconclusive run with an EMPTY results/ dir keeps the plain category
    /// (AGT-1944 taxonomy: inconclusive-with-results vs inconclusive-empty).
    /// Every other issue kind is unchanged.
    ///
    /// <para>The inconclusive-with-results decision is delegated to
    /// <see cref="PostProcessingOutcomeTaxonomy.Classify"/> so the taxonomy
    /// classifier is the single source of truth for "no terminal verdict but left
    /// work to inspect", rather than a parallel inline probe that could drift from
    /// the bucket definition. The delegation is gated on the same inconclusive
    /// kinds this method already handled, so it is behaviour-preserving: Classify
    /// returns <see cref="PostProcessingOutcome.InconclusiveWithResults"/> for an
    /// inconclusive kind iff its results/ dir is non-empty.</para>
    /// </summary>
    private string ResolveEscalationCategory(RunIssueKind issueKind, string? jobFolderPath)
    {
        var isInconclusive = issueKind is RunIssueKind.OrchestratorInconclusive or RunIssueKind.InfraCrash;
        if (isInconclusive)
        {
            var outcome = PostProcessingOutcomeTaxonomy.Classify(
                issueKind, terminalKind: null, hasResults: HasNonEmptyResults(jobFolderPath));
            if (outcome == PostProcessingOutcome.InconclusiveWithResults)
                return HumanReviewEscalationCategories.InconclusiveWithResults;
        }
        return ToEscalationCategory(issueKind);
    }

    /// <summary>True when the task's <c>results/</c> dir holds at least one file.
    /// Best-effort and fails closed (no results claim on an unreadable dir).</summary>
    private static bool HasNonEmptyResults(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return false;
        try
        {
            var resultsDir = TaskPaths.ResultsDir(jobFolderPath);
            return Directory.Exists(resultsDir)
                && Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectRunner: best-effort results probe for the inconclusive escalation category.");
            return false;
        }
    }

    private static bool IsAgentGitMutationAllowed(TaskInfo info)
        => info.Tags.Any(tag => string.Equals(tag, "allow-agent-git-mutation", StringComparison.OrdinalIgnoreCase));

    private Dictionary<string, string?> CaptureProtectedRemoteTips()
    {
        var repoRoot = Entry.RootPath;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var settings = _projectSettings.Get(ProjectName);
        var integrationBranch = _git.ResolveIntegrationBranch(repoRoot, settings.IntegrationBranch);
        if (integrationBranch.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            integrationBranch = integrationBranch["origin/".Length..];

        var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            integrationBranch,
            "main",
            "master",
        };

        return branches
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .ToDictionary(
                branch => branch,
                branch => _git.GetBranchTip(repoRoot, $"origin/{branch}"),
                StringComparer.OrdinalIgnoreCase);
    }

    private bool ProtectedRemoteChanged(ActiveRun run)
    {
        if (run.ProtectedRemoteTipsBefore.Count == 0) return false;
        var repoRoot = Entry.RootPath;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;

        foreach (var (branch, before) in run.ProtectedRemoteTipsBefore)
        {
            var after = _git.GetBranchTip(repoRoot, $"origin/{branch}");
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool PreExistingWorkerHistoryWasRewritten(string repoRoot, ActiveRun run)
    {
        if (string.IsNullOrWhiteSpace(run.WorkerHeadShaBefore)
            || string.IsNullOrWhiteSpace(run.WorkerBranchBefore))
        {
            return false;
        }

        var branchTipAfter = _git.GetBranchTip(repoRoot, run.WorkerBranchBefore);
        return string.IsNullOrWhiteSpace(branchTipAfter)
            || !_git.IsAncestor(repoRoot, run.WorkerHeadShaBefore, branchTipAfter);
    }

    private static string? DetectAgentGitMutationClaim(IReadOnlyList<CliOutputLine> lines)
    {
        foreach (var line in lines)
        {
            var text = (line.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            if (string.Equals(line.Stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(line.Stream, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(line.Stream, "orchestrator", StringComparison.OrdinalIgnoreCase)) continue;
            if (NegatedGitMutationRegex.IsMatch(text)) continue;
            if (!GitMutationCommandRegex.IsMatch(text) && !GitMutationClaimRegex.IsMatch(text)) continue;
            return text.Length <= 180 ? text : text[..177] + "...";
        }

        return null;
    }

    private static string? DetectAgentGitPushClaim(IReadOnlyList<CliOutputLine> lines)
    {
        foreach (var line in lines)
        {
            var text = (line.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            if (string.Equals(line.Stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(line.Stream, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(line.Stream, "orchestrator", StringComparison.OrdinalIgnoreCase)) continue;
            if (NegatedGitMutationRegex.IsMatch(text)) continue;
            if (!GitPushCommandOrClaimRegex.IsMatch(text)) continue;
            return text.Length <= 180 ? text : text[..177] + "...";
        }

        return null;
    }

    /// <summary>
    /// Runs the abort-review step for a non-clean run end and applies its
    /// binding action. Returns true when the step handled the run (a rerun
    /// was scheduled, or the run was accepted into review); false when the
    /// step requests operator intervention or fails, in which case the caller
    /// falls through to the existing typed escalation route. Never throws: any
    /// failure inside the step fails closed to false (escalate).
    /// </summary>
    private async Task<bool> TryRunAbortReviewAsync(
        TaskInfo activeInfo,
        string jobId,
        string jobKey,
        string cliType,
        CliExecution execution,
        OutcomeAction action,
        List<CliOutputLine> liveOutputSnapshot,
        int commitsDuringRun,
        string? headShaAfter,
        SessionUsage? usage,
        bool isPlanningRun)
    {
        var review = _postAbortReview;
        if (review == null) return false;

        var settings = AgentStudio.Pipeline.PipelineTypeSettings.ForTask(
            _projectSettings.Get(ProjectName),
            activeInfo)!;
        var used = _abortReviewRerunsUsed.TryGetValue(jobId, out var u) ? u : 0;
        var budgetRemaining = Math.Max(0, PostAbortReviewDecider.DefaultRerunBudget - used);
        var model = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveModel(
            settings,
            AgentStudio.Pipeline.PipelineCatalogue.AbortReviewStep,
            runtimeDefault: AgentStudio.Pipeline.PipelineStepModelDefaults.SupportModel);
        var reviewCliType = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveCliType(
            settings,
            AgentStudio.Pipeline.PipelineCatalogue.AbortReviewStep)
            ?? AgentStudio.Pipeline.PipelineStepModelDefaults.DefaultCli;
        var thinkingLevel = AgentStudio.Pipeline.PipelineStepConfigResolver.ResolveThinkingLevel(
            settings,
            AgentStudio.Pipeline.PipelineCatalogue.AbortReviewStep,
            reviewCliType,
            model,
            AgentStudio.Pipeline.PipelineStepModelDefaults.SupportThinkingLevel);

        var phase = _phaseByJob.TryGetValue(jobKey, out var snap) ? snap.Phase.ToString() : RunPhase.Unknown.ToString();
        var request = new PostAbortReviewRequest(
            Project: ProjectName,
            JobId: jobId,
            JobFolderPath: activeInfo.FolderPath,
            TaskTitle: activeInfo.Title ?? string.Empty,
            TaskBody: ReadTaskBodyBestEffort(activeInfo.FolderPath),
            AbortReason: action.MetaMessage ?? ToIssueTopic(action.IssueKind),
            AbortPhase: phase,
            CliOutputTail: BuildCliOutputTail(liveOutputSnapshot),
            ToolCallsLiveness: BuildToolCallsLiveness(activeInfo.FolderPath),
            GitState: BuildAuthoritativeAbortGitState(activeInfo, commitsDuringRun, headShaAfter),
            TranscriptUsage: BuildTranscriptUsage(usage),
            CliType: reviewCliType,
            Model: model)
        {
            ThinkingLevel = thinkingLevel,
            RerunBudgetRemaining = budgetRemaining,
        };

        PostAbortReviewStepReport report;
        try
        {
            report = await review.RunAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "abort-review step crashed for {JobId}; falling back to Escalated", jobId);
            return false;
        }

        RecordAbortReviewStep(activeInfo.FolderPath, report);
        var recToken = report.Verdict is null
            ? "unparseable"
            : PostAbortReviewStepService.RecommendationToken(report.Verdict.Recommendation);
        _chatLog.Append(activeInfo, OrchestratorMessageKind.Decision,
            $"[abort-review] {recToken} -> {PostAbortReviewStepService.ActionToken(report.Action)} " +
            $"(budget left: {budgetRemaining}). {report.Verdict?.Reasoning}".TrimEnd());

        switch (report.Action)
        {
            case PostAbortAction.Rerun:
            case PostAbortAction.RerunWithStrongerFraming:
                _abortReviewRerunsUsed[jobId] = used + 1;
                // Release the active-job latch so the re-issue can claim it,
                // then schedule on the thread pool so OnCliFinished returns
                // promptly (mirrors the ReissueWithStrongerFraming path).
                ReleaseRun(jobId);
                NotifyStatus();
                var stronger = report.Action == PostAbortAction.RerunWithStrongerFraming;
                var retryPrompt = BuildAbortReviewRerunPrompt(activeInfo, report, stronger);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunCliAsync(jobId, RunIntent.UserContinue, retryPrompt, 0, ContinueModes.Continue, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "abort-review rerun failed for {JobId}", jobId);
                    }
                });
                return true;

            case PostAbortAction.AcceptAndContinue:
                _abortReviewRerunsUsed.TryRemove(jobId, out _);
                // This exit never decomposes, so an accepted planning run holds
                // no validated plan and no Result-SHA. Accepting it into the
                // code-review lane is the TE-8 dead end; it returns to Backlog
                // instead, exactly as an empty plan does.
                var acceptLane = isPlanningRun
                    ? EpicRunPolicy.PlanningCompletionLane(decompositionValid: false)
                    : TaskStates.AutoReview;
                CompletionMarker.Write(activeInfo.FolderPath, new CompletionMarker
                {
                    TargetState = acceptLane,
                    ExecutionStatus = execution.Status,
                    AgentOutcome = "abort-review-accept"
                }, _logger);
                var move = await _transitions.MoveAsync(
                    jobId, acceptLane, Entry.Path, CancellationToken.None,
                    transitionCause: CompletionLaneChangeCause(acceptLane),
                    transitionDetail: "abort-review-accept");
                if (move.Status == MoveJobStatus.Success)
                {
                    // Terminal accept: tear down the coding worktree+branch.
                    TeardownWorktreeForJob(jobId);
                    var movedInfo = _scanner.FindJob(jobId, Entry.Path);
                    if (movedInfo != null) CompletionMarker.Clear(movedInfo.FolderPath, _logger);
                }
                else
                {
                    _logger.LogWarning(
                        "abort-review accept could not move {JobId} to review: {Status} {Message}",
                        jobId, move.Status, move.Message);
                }
                return true;

            default: // EscalateHuman (model said so, budget exhausted, or unparseable)
                _abortReviewRerunsUsed.TryRemove(jobId, out _);
                return false;
        }
    }

    /// <summary>Records the abort-review verdict + decided action into
    /// <c>pipeline-execution.json</c> so the job-detail pipeline view can
    /// render the step like the auto-review aspects (req 4). Best-effort.</summary>
    private void RecordAbortReviewStep(string jobFolderPath, PostAbortReviewStepReport report)
    {
        if (_pipelineLog == null) return;
        try
        {
            var parsed = report.Verdict != null;
            var reasoning = report.Verdict?.Reasoning;
            _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
            {
                StepId = AgentStudio.Pipeline.PipelineCatalogue.PostAbortReviewStepId,
                Kind = StepKind.Orchestrator,
                Model = report.Model,
                Status = parsed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                StartedAt = report.StartedAt,
                CompletedAt = report.StartedAt.AddMilliseconds(report.DurationMs),
                DurationMs = report.DurationMs,
                Verdict = parsed
                    ? PostAbortReviewStepService.RecommendationToken(report.Verdict!.Recommendation)
                    : "unparseable",
                VerdictSummary = $"action={PostAbortReviewStepService.ActionToken(report.Action)}" +
                    (string.IsNullOrWhiteSpace(reasoning) ? string.Empty : $"; {reasoning}"),
                Reason = parsed ? null : "CLI failure / unparseable reply; failed closed to operator escalation",
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "abort-review pipeline-step record failed for {Folder}", jobFolderPath);
        }
    }

    /// <summary>
    /// Builds the follow-up prompt for an abort-review rerun. A plain rerun
    /// continues the task; a stronger reissue adds explicit framing that the
    /// previous abort was judged illegitimate and the work must be completed.
    /// </summary>
    private static string BuildAbortReviewRerunPrompt(TaskInfo info, PostAbortReviewStepReport report, bool stronger)
    {
        var reason = report.Verdict?.Reasoning;
        var head = stronger
            ? "The previous run was aborted, but an automated review judged the abort illegitimate and re-issued the task with stronger framing. "
              + "Do not stop early. Drive the task to a real, verifiable result and only stop when the work is genuinely complete or genuinely blocked. "
            : "The previous run was aborted, but an automated review judged the abort recoverable and re-issued the task. Continue the work to completion. ";
        return head
             + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Review note: {reason!.Trim()} ")
             + "If a long-running operation (dev server, build, test wait, poll loop) is expected, narrate progress so the watchdog can tell you are alive. "
             + "End with exactly one terminal sentinel on its own line: [[TASK_DONE]], [[TASK_BLOCKED:missing-dependency-xyz]], [[TASK_NEEDS_INPUT:choose-primary-column]], or [[TASK_NOOP]]. Replace the example reason with the actual short reason.\n\n"
             + $"Task: {info.Title}";
    }

    /// <summary>Best-effort read of the task prompt body for abort-review
    /// context. Returns a bounded excerpt; never throws.</summary>
    private string ReadTaskBodyBestEffort(string jobFolderPath)
    {
        try
        {
            var promptPath = Path.Combine(jobFolderPath, "prompt.md");
            if (!File.Exists(promptPath)) return string.Empty;
            var text = File.ReadAllText(promptPath);
            const int max = 4000;
            return text.Length <= max ? text : text.Substring(0, max) + "\n... (truncated)";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "abort-review prompt.md read failed for {Folder}", jobFolderPath);
            return string.Empty;
        }
    }

    /// <summary>Joins the tail of the live CLI output for abort-review evidence.</summary>
    private static string BuildCliOutputTail(List<CliOutputLine> lines, int maxLines = 60)
    {
        if (lines == null || lines.Count == 0) return "(no CLI output captured)";
        var start = Math.Max(0, lines.Count - maxLines);
        return string.Join("\n", lines.Skip(start).Select(l => l.Text));
    }

    /// <summary>Tails <c>logs/tool-calls.jsonl</c> so the model can judge
    /// whether a tool call started shortly before the abort and never
    /// returned (a live long-op) vs. a genuine stall. Best-effort.</summary>
    private string BuildToolCallsLiveness(string jobFolderPath, int maxLines = 12)
    {
        try
        {
            var path = Path.Combine(TaskPaths.LogsDir(jobFolderPath), "tool-calls.jsonl");
            if (!File.Exists(path)) return "(no tool-calls.jsonl on disk)";
            var all = File.ReadAllLines(path);
            if (all.Length == 0) return "(tool-calls.jsonl is empty)";
            var start = Math.Max(0, all.Length - maxLines);
            return string.Join("\n", all.Skip(start));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "abort-review tool-calls.jsonl read failed for {Folder}", jobFolderPath);
            return "(tool-calls.jsonl unreadable)";
        }
    }

    /// <summary>Renders the last session usage rollup as a one-line summary.</summary>
    private static string BuildTranscriptUsage(SessionUsage? usage)
    {
        if (usage == null) return "(no session usage captured)";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(usage.Tokens)) parts.Add($"tokens: {usage.Tokens}");
        if (!string.IsNullOrWhiteSpace(usage.Requests)) parts.Add($"requests: {usage.Requests}");
        if (!string.IsNullOrWhiteSpace(usage.Changes)) parts.Add($"changes: {usage.Changes}");
        return parts.Count == 0 ? "(no session usage captured)" : string.Join("; ", parts);
    }

    /// <summary>
    /// Appends a clearly-visible separator line into <c>logs/cli-output.log</c>
    /// when continue falls back to recovery. Lets the protocol pane render a
    /// chain break so the user can see the cut instead of being confused why
    /// the agent re-reads the job folder mid-conversation.
    /// </summary>
    private void AppendSessionCutMarkerToCliLog(TaskInfo info, string reason)
    {
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var line = $"[{ts}] [system] --- Session lost ({reason}) - recovering from job folder ---";
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write session cut marker for {JobId}", info.Id);
        }
    }

    /// <returns>
    /// True if the consolidated <c>logs/cli-output.log</c> was updated. The
    /// caller uses this signal to decide whether the runtime JSONL backup
    /// can now be discarded.
    /// </returns>
    /// <summary>
    /// Appends a human-readable spawn-failure reason to the job's
    /// <c>logs/cli-output.log</c>. A failed CLI spawn produces no process and
    /// therefore no streamed output, so without this the run "finished failed"
    /// with an empty <c>logs/</c> dir - the worst diagnostic shape, because the
    /// operator has nothing to read. Best-effort: a write failure here must not
    /// mask the original spawn failure.
    /// </summary>
    private void WriteSpawnFailureDiagnostic(TaskInfo info, string? cliType, string? cliError)
    {
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var now = DateTime.UtcNow;
            var reason = string.IsNullOrWhiteSpace(cliError)
                ? $"Failed to start {cliType ?? "CLI"} process (no error detail captured)."
                : cliError;
            var line =
                $"[{now:HH:mm:ss.fff}] [system] [taskboard] {cliType ?? "CLI"} spawn failed: {reason}";
            CliOutputLogFile.Append(logPath, line);
            _logger.LogWarning(
                "[taskboard] spawn failed for job {JobId} on {Cli}: {Reason}",
                info.Id, cliType ?? "CLI", reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write spawn-failure diagnostic for job {JobId}", info.Id);
        }
    }

    private bool WriteCliLog(TaskInfo info, ICliExecutionService cli)
    {
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);

            var output = cli.GetOutput(info.TaskKey);
            if (output.Count == 0)
            {
                // GetOutput already falls back to the on-disk JSONL when the
                // in-memory buffer is gone, so an empty result means nothing
                // to flush - don't truncate the existing log.
                return false;
            }

            var logContent = string.Join(Environment.NewLine,
                output.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {l.Text}"));

            // Append so that continuation sessions accumulate rather than overwrite.
            CliOutputLogFile.Append(logPath, logContent);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write CLI log for job {JobId}", info.Id);
            return false;
        }
    }

    private ICliExecutionService GetCliFor(TaskInfo info) => _router.Get(info.CliType);
    private string GetJobKey(string jobId) => TaskIdentity.CreateKey(Entry.Path, jobId);
    private string? GetActiveJobKey() => _activeJobId != null ? GetJobKey(_activeJobId) : null;

    /// <summary>
    /// Atomically releases the in-memory active-job latch when an external
    /// actor (the API move endpoint, the boot-time stuck-folder sweep, a
    /// hand-edited folder move) takes the active job out of <c>3-progress</c>.
    /// Without this, the runner's <c>_activeJobId</c> stays pinned at a slug
    /// whose folder is gone or in another lane, every subsequent pickup tick
    /// short-circuits on <c>_activeJobId != null</c>, and the project is
    /// wedged until a backend restart.
    ///
    /// <para>
    /// Stops a live CLI process for that job first when one is recorded; the
    /// usual cli-finished callback then runs through and would clear the
    /// latch on its own, but we also clear synchronously so the next caller
    /// (orchestrator move side-effect, watcher reconciliation) sees a clean
    /// slate without waiting for the OS to reap the child.
    /// </para>
    /// </summary>
    /// <returns>True if the runner was holding this job and the latch was cleared.</returns>
    public bool ClearActiveJobIfMatches(string jobId, string reason)
        => ClearActiveJobIfMatches(jobId, reason, appendChatLog: true);

    private bool ClearActiveJobIfMatches(string jobId, string reason, bool appendChatLog)
    {
        if (string.IsNullOrEmpty(jobId)) return false;
        if (_activeJobId != jobId) return false;

        _logger.LogInformation(
            "Runner '{Project}' clearing active job '{JobId}': {Reason}",
            ProjectName, jobId, reason);

        // Before the process dies: a user follow-up this run never got to act
        // on must survive the kill (AGT-2747).
        PreserveUnconsumedFollowUp(jobId, reason);

        if (_activeCliType != null)
        {
            try
            {
                var jobKey = GetJobKey(jobId);
                _router.Get(_activeCliType).Stop(jobKey, RunStopReason.Cancelled);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ClearActiveJobIfMatches: cli.Stop failed for {JobId}", jobId);
            }
        }

        ReleaseRun(jobId);
        ApplyPendingModeIfAny(jobId);

        // Best-effort: drop a chat-log line on the moved folder so the
        // protocol pane shows why the latch was released. The job's folder
        // has already moved, so we look it up by id post-move; if the job
        // is gone (delete + folder-rm), we skip silently.
        try
        {
            if (appendChatLog)
            {
                var movedInfo = _scanner.FindJob(jobId, Entry.Path);
                if (movedInfo != null)
                {
                    _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision,
                        $"Runner active state cleared: {reason}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClearActiveJobIfMatches: chat-log append failed for {JobId}", jobId);
        }

        NotifyStatus();
        return true;
    }

    /// <summary>
    /// Writes a still-unconsumed user follow-up back to <c>pending-intent.json</c>
    /// before a stop path kills the run that carried it, so a steer can never be
    /// destroyed by a kill it did not cause (AGT-2747; three steers lost on
    /// 2026-09-07 when lane reconciliation shot down freshly started runs).
    ///
    /// <para>
    /// Only a run that still holds its execution slot is preserved: once the CLI
    /// has exited normally the agent has seen the follow-up, and the trailing
    /// post-processing lane move must not resurrect it as a fresh intent.
    /// </para>
    /// </summary>
    /// <returns>True when a follow-up was written back.</returns>
    private bool PreserveUnconsumedFollowUp(string jobId, string reason)
    {
        var run = _activeRuns.Get(jobId);
        if (run is null || !run.HoldsExecutionSlot) return false;
        if (string.IsNullOrWhiteSpace(run.Followup)) return false;

        var mode = ContinueModes.Normalize(run.FollowupMode);
        var savedReason = FollowUpQueueReasons.RunStopped(reason);
        PendingIntent? saved;
        try
        {
            saved = _mutations.SavePendingIntent(
                jobId, mode, run.Followup!,
                reason: savedReason,
                activeJobId: jobId,
                watchPath: Entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "follow-up-preserve-failed job={JobId} project={Project}", jobId, ProjectName);
            return false;
        }

        if (saved is null)
        {
            _logger.LogError(
                "follow-up-preserve-failed job={JobId} project={Project} reason={Reason}: the task could not be resolved",
                jobId, ProjectName, reason);
            return false;
        }

        _logger.LogWarning(
            "follow-up-preserved job={JobId} project={Project} mode={Mode} reason={Reason} chars={Chars}",
            jobId, ProjectName, mode, reason, run.Followup!.Length);

        var folder = run.JobFolder;
        try { folder = _scanner.FindJob(jobId, Entry.Path)?.FolderPath ?? folder; }
        catch (Exception ex) { _logger.LogDebug(ex, "PreserveUnconsumedFollowUp: FindJob threw for {JobId}", jobId); }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            _timeline?.Append(
                folder!,
                TimelineEventKinds.FollowUpPreserved,
                TimelineActors.System,
                summary: $"Follow-up preserved: the run was stopped ({reason}). It waits for the next run.",
                details: new Dictionary<string, string>
                {
                    ["mode"] = mode,
                    ["reason"] = reason,
                    ["savedReason"] = savedReason,
                });
        }

        // Advisory, not intervention: the operator has to learn that a steer they
        // believed was running is now waiting instead.
        try
        {
            _ = _bus?.EmitAdvisoryAsync(new SupervisorAdvisory(
                CreatedAt: DateTime.UtcNow,
                Project: ProjectName,
                Severity: SupervisorSeverity.Warn,
                Source: SupervisorSource.HardCheck,
                Topic: "follow-up-preserved",
                Message: $"The run for \"{jobId}\" was stopped ({reason}) while carrying an unconsumed {mode} follow-up. " +
                         "The follow-up was saved on the card and the next run will consume it.",
                JobId: jobId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Advisory for preserved follow-up failed for {JobId}", jobId);
        }

        return true;
    }

    /// <summary>
    /// Defensive watcher reconciliation: if the in-memory active-job latch
    /// points at a job whose folder is no longer in <c>3-progress</c>
    /// (deleted, moved by an external script, archived by the boot-time
    /// stuck-folder sweep), release the latch so the next pickup tick can
    /// choose freely. The admission path captures the concrete task folder,
    /// so this check does not enter the global task index.
    /// </summary>
    /// <returns>True if the latch was held and got cleared by this call.</returns>
    public bool ReconcileActiveJobAgainstDisk()
    {
        var jobId = _activeJobId;
        if (jobId == null) return false;
        if (_processing) return false;

        var folder = _activeRuns.Get(jobId)?.JobFolder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = FindLegacyActiveFolder(jobId);

        var physicalLane = ResolvePhysicalLane(folder);
        if (physicalLane == TaskStates.Progress) return false;

        var reason = physicalLane == null
            ? "active job folder no longer exists"
            : $"active job moved out of 3-progress (now in {physicalLane})";
        // Do not perform the best-effort chat lookup on a watcher callback:
        // FindJob would re-enter the just-invalidated global index.
        return ClearActiveJobIfMatches(jobId, reason, appendChatLog: false);
    }

    /// <summary>
    /// Finds the legacy lane folder without scanning any sibling task. Runtime
    /// admissions already carry <see cref="ActiveRun.JobFolder"/>; this fallback
    /// exists for test seams and old recovered in-memory records only.
    /// </summary>
    private string? FindLegacyActiveFolder(string jobId)
    {
        foreach (var state in TaskStates.All)
        {
            var candidate = Path.Combine(Entry.Path, state, jobId);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolvePhysicalLane(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var parent = Path.GetFileName(Path.GetDirectoryName(folder) ?? string.Empty);
        if (!string.IsNullOrEmpty(parent) && Array.IndexOf(TaskStates.All, parent) >= 0)
            return parent;

        try
        {
            var taskJson = Path.Combine(folder, "task.json");
            if (!File.Exists(taskJson)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(taskJson));
            return document.RootElement.TryGetProperty("state", out var state)
                   && state.ValueKind == JsonValueKind.String
                ? state.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Test seam: lets a unit test prime the active-job latch
    /// without spinning up a real CLI run.</summary>
    internal void SetActiveJobForTest(string jobId, string? cliType = null, string[]? predictedScope = null)
    {
        _activeRuns.TryClaim(new ActiveRun
        {
            JobId = jobId,
            CliType = cliType,
            JobFolder = FindLegacyActiveFolder(jobId),
            Parallelism = predictedScope == null ? TaskParallelism.Default : new TaskParallelism(false, predictedScope)
        });
    }

    private bool HasSuccessfulGradedRun(TaskInfo info)
    {
        try
        {
            var events = _sessions.ReadSessionEvents(info.Id, Entry.Path);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
            return HasSuccessfulGradedRun(info.Tags,
                RunTimelineBuilder.Build(events, lines, DateTime.UtcNow).Runs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve prior successful graded run for {JobId}", info.Id);
            return false;
        }
    }

    internal static bool HasSuccessfulGradedRun(
        IReadOnlyList<string> tags,
        IReadOnlyList<RunRecord> runs)
        => tags.Any(t => t.StartsWith(ConcernTagWriter.CodeReviewGradeTagPrefix, StringComparison.OrdinalIgnoreCase))
           && runs.Any(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase));

    private string BuildAuthoritativeAbortGitState(TaskInfo info, int currentCommits, string? currentHeadAfter)
    {
        var current = $"Failed attempt: {currentCommits} commit(s); HEAD={currentHeadAfter ?? "unknown"}.";
        try
        {
            var events = _sessions.ReadSessionEvents(info.Id, Entry.Path);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
            var successful = ReviewDecisionOrchestrator.SelectLastSuccessfulReviewRun(
                RunTimelineBuilder.Build(events, lines, DateTime.UtcNow).Runs);
            if (successful == null) return current;

            var commits = _git.GetCommitsInShaRange(
                info.Id, Entry.Path, successful.HeadShaBefore, successful.HeadShaAfter);
            var subjects = commits.Count == 0
                ? "no commits resolved"
                : string.Join("; ", commits.Select(c => $"{c.ShortSha} {c.Subject}"));
            return current +
                $" Authoritative last successful run diff: {successful.HeadShaBefore}..{successful.HeadShaAfter}; " +
                $"{commits.Count} commit(s): {subjects}. Do not judge the task from the failed attempt's empty diff.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve authoritative abort-review git state for {JobId}", info.Id);
            return current;
        }
    }

    /// <summary>Test seam for one slot completing without a real CLI callback.</summary>
    internal bool CompleteActiveJobForTest(string jobId)
    {
        if (ReleaseRun(jobId) == null) return false;
        ApplyPendingModeIfAny(jobId);
        return true;
    }

    private string RenderPrompt(
        RunPlan plan,
        TaskInfo info,
        string runWorkingDir,
        PromptEnrichmentPreparation? preparedPromptEnrichment = null)
    {
        var worktreeCheckout = _activeRuns.Get(info.Id)?.WorktreePath;
        if (string.IsNullOrWhiteSpace(worktreeCheckout) && IsWorktreePath(runWorkingDir))
            worktreeCheckout = runWorkingDir;

        var dossierTargets = !TaskModes.IsReportOnly(info.Mode) && !TaskModes.IsConcept(info.Mode)
            ? _dossierMaintenance?.ResolveTargets(ProjectName, info)
                ?? Array.Empty<DossierMaintenanceTarget>()
            : Array.Empty<DossierMaintenanceTarget>();
        var dossierFraming = dossierTargets.Count == 0
            ? string.Empty
            : _prompts.RenderDossierMaintenanceFraming(
                string.IsNullOrWhiteSpace(info.Key) ? info.Id : info.Key,
                DossierMaintenanceService.RenderTargetList(dossierTargets),
                new PromptCallContext(info.ProjectName, PipelineCatalogue.DossierMaintenanceStepId, info.Model));

        if (plan.PromptOverride != null)
        {
            var rewrittenOverride = RewriteMainCheckoutPathsForRun(
                plan.PromptOverride, runWorkingDir, worktreeCheckout);
            if (dossierFraming.Length > 0)
                rewrittenOverride = rewrittenOverride.TrimEnd() + "\n\n" + dossierFraming;
            return IsWorktreePath(runWorkingDir)
                ? BuildWorktreeContainmentNotice(runWorkingDir, worktreeCheckout) + rewrittenOverride
                : rewrittenOverride;
        }
        if (string.IsNullOrWhiteSpace(plan.PromptTemplate))
            throw new InvalidOperationException("Run plan has neither a prompt template nor a prompt override.");

        var promptPath = Path.Combine(info.FolderPath, "prompt.md");
        var repositoryPath = string.IsNullOrWhiteSpace(Entry.RepositoryPath) ? Entry.RootPath : Entry.RepositoryPath;
        var effectiveRepositoryPath = IsWorktreePath(runWorkingDir) ? worktreeCheckout : repositoryPath;
        var promptText = ReadPromptText(promptPath);
        if (ShouldForegroundIntakeEnrichment(plan) && preparedPromptEnrichment is null)
            promptText = PrependIntakeEnrichment(info.FolderPath, promptText);

        var modeFraming = PromptEnrichmentService.ComposeModeFraming(
            _prompts.RenderModeFraming(
                info.Mode,
                info.AllowWebAccess,
                new PromptCallContext(info.ProjectName, "core", info.Model))
                + dossierFraming,
            preparedPromptEnrichment?.ContextMarkdown);
        var values = new Dictionary<string, string?>(plan.PromptVariables)
        {
            ["prompt_path"] = promptPath,
            ["prompt_text"] = RewriteMainCheckoutPathsForRun(promptText, runWorkingDir, worktreeCheckout),
            ["job_folder"] = info.FolderPath,
            ["title"] = string.IsNullOrWhiteSpace(info.Title) ? "(untitled)" : info.Title,
            ["working_directory"] = runWorkingDir,
            ["repository_path"] = effectiveRepositoryPath,
            ["attachments_list"] = BuildAttachmentsList(info.FolderPath),
            ["mode_framing"] = modeFraming
        };
        var rendered = _prompts.Render(
            plan.PromptTemplate,
            values,
            new PromptCallContext(info.ProjectName, "core", info.Model));
        rendered = RewriteMainCheckoutPathsForRun(rendered, runWorkingDir, worktreeCheckout);
        return IsWorktreePath(runWorkingDir)
            ? BuildWorktreeContainmentNotice(runWorkingDir, worktreeCheckout) + rendered
            : rendered;
    }

    private static RunPlan BuildReissueChangePlan(
        RunPlan plan,
        ReissueOpenItemsPreCheck.PreCheckDecision decision,
        string projectName,
        string jobId)
    {
        var assignment = ReissuePromptExperiment.Assign(
            $"{projectName}/{jobId}",
            decision.PriorRunCount + 1,
            decision.PromptFamily,
            decision.ReissueCause,
            decision.OpenItems.Count);
        var treatment = assignment.Arm == ReissuePromptExperiment.TreatmentArm;
        var variables = new Dictionary<string, string?>(plan.PromptVariables)
        {
            ["reissue_findings"] = treatment
                ? ReissuePromptExperiment.BuildTreatmentFindings(
                    decision.OpenItems,
                    decision.Action == ReissueOpenItemsPreCheck.PreCheckAction.Escalate)
                : BuildReissueFindingsBlock(decision),
            ["reissue_followup"] = NormalizeReissueFollowUp(decision.FollowUpText),
            ["reissue_evidence"] = NormalizeReissueFollowUp(decision.FollowUpText),
        };

        return plan with
        {
            PromptTemplate = ReissuePromptExperiment.PromptTemplate(assignment),
            PromptVariables = variables,
            EventKind = "reissue",
            EventReason = decision.Note ?? "auto-review reissue",
            ReissuePromptAssignment = assignment,
        };
    }

    private static string BuildReissueFindingsBlock(ReissueOpenItemsPreCheck.PreCheckDecision decision)
    {
        if (decision.OpenItems.Count == 0)
            return "- [ ] Read the reissue context and resolve the auto-review findings.";

        var sb = new StringBuilder();
        if (decision.Action == ReissueOpenItemsPreCheck.PreCheckAction.Escalate)
        {
            sb.AppendLine(
                "This task has already been reissued multiple times. Resolve only these findings in this run, or stop with `[[TASK_BLOCKED:missing-dependency-xyz]]`, replacing the example reason with the actual short reason.");
            sb.AppendLine();
        }
        foreach (var item in decision.OpenItems)
            sb.AppendLine($"- [ ] {item}");
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeReissueFollowUp(string? followUpText)
    {
        var text = (followUpText ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? "(No orchestrator-follow-up.md content was captured; use the review findings above and inspect the job folder evidence.)"
            : text;
    }

    private bool IsWorktreePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !string.Equals(NormalizePath(path), NormalizePath(Entry.RootPath), StringComparison.OrdinalIgnoreCase);

    private string RewriteMainCheckoutPathsForRun(
        string text,
        string runWorkingDir,
        string? worktreeCheckout)
    {
        if (string.IsNullOrEmpty(text) || !IsWorktreePath(runWorkingDir)) return text;

        var result = text.Replace(Entry.RootPath, runWorkingDir, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Entry.RepositoryPath))
            result = result.Replace(
                Entry.RepositoryPath,
                string.IsNullOrWhiteSpace(worktreeCheckout) ? runWorkingDir : worktreeCheckout,
                StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private string BuildWorktreeContainmentNotice(string runWorkingDir, string? worktreeCheckout)
    {
        var mainCheckout = _git.ResolveRepositoryRoot(Entry)
                           ?? (!string.IsNullOrWhiteSpace(Entry.RepositoryPath)
                               ? Entry.RepositoryPath
                               : Entry.RootPath);
        var repository = string.IsNullOrWhiteSpace(worktreeCheckout) ? runWorkingDir : worktreeCheckout;
        return "## Worktree containment\n\n"
         + $"Your repository checkout for this run is `{repository}` and your working directory is `{runWorkingDir}`. "
         + "Do all file edits, reads, builds, tests, and git commands in that worktree. "
         + $"The main checkout `{mainCheckout}` is shared by other slots and is off limits for this run; do not edit it, build from it, test from it, or pass it to tools.\n\n";
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private static string ReadPromptText(string promptPath)
    {
        try
        {
            return File.Exists(promptPath) ? File.ReadAllText(promptPath).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ShouldForegroundIntakeEnrichment(RunPlan plan)
        => string.Equals(plan.PromptTemplate, RuntimePromptService.RunnerFreshStart, StringComparison.Ordinal);

    private static string PrependIntakeEnrichment(string jobFolder, string promptText)
    {
        var enrichment = ReadIntakeEnrichedContext(jobFolder);
        if (string.IsNullOrWhiteSpace(enrichment)) return promptText;
        if (string.IsNullOrWhiteSpace(promptText)) return enrichment.Trim();
        return enrichment.TrimEnd() + "\n\n---\n\n" + promptText;
    }

    private static string ReadIntakeEnrichedContext(string jobFolder)
    {
        try
        {
            var path = Path.Combine(
                jobFolder,
                IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string BuildAttachmentsList(string jobFolder)
    {
        try
        {
            var dir = Path.Combine(jobFolder, "attachments");
            if (!Directory.Exists(dir)) return "(none)";
            var files = Directory.EnumerateFiles(dir).OrderBy(p => p).ToList();
            if (files.Count == 0) return "(none)";
            return string.Join("\n", files.Select(f => $"- `{Path.GetFileName(f)}` → `{f}`"));
        }
        catch
        {
            return "(none)";
        }
    }

    /// <summary>
    /// Returns the oldest pickup-eligible job in <c>2-ready</c> for this
    /// project, or <c>null</c> when the lane is empty (or every entry is
    /// blocked by an active intake-running phase).
    /// </summary>
    /// <remarks>
    /// Filters strictly on <c>State == 2-ready</c>. Jobs sitting in
    /// <c>1-preparation</c>, <c>1a-orchestrator-prep</c>,
    /// <c>4-auto-review</c>, <c>5e-escalated</c>, or
    /// <c>5-human-review</c> have no influence here - those lanes are
    /// processed by their own background services in parallel with the
    /// runner. The single-state-machine rule (ADR-0001) is preserved by
    /// the active-job latch in <see cref="TickAsync"/>, not by lane
    /// coupling. Pinned by <c>ParallelLanesPickupTests</c>.
    /// </remarks>
    internal TaskInfo? GetNextReadyJob()
    {
        return ListReadyPickupCandidatesInDisplayedOrder().FirstOrDefault();
    }

    private static readonly string[] RunnerPickupLanesInDisplayedOrder =
    [
        TaskStates.Ready,
        TaskStates.Progress,
    ];

    private sealed record DisplayedPickupCandidate(string State, TaskInfo? Info, ProgressPickupCandidate? Progress);

    // AGT-2029: dedup waits-on cycle warnings. A dependency cycle is a
    // configuration error that persists until the operator fixes it, so it must
    // be reported (Issue) but only ONCE per card, not on every idle pickup tick.
    private readonly HashSet<string> _waitsOnCycleWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _waitsOnCycleGate = new();

    private List<TaskInfo> ListReadyPickupCandidatesInDisplayedOrder()
    {
        var settings = _projectSettings.Get(ProjectName);
        var intakeEnabled = settings.IntakeEnabled == true;
        var all = _scanner.ScanAllAutomationJobs();
        // AGT-2029 waits-on gate: resolve dependency fulfillment across ALL
        // projects and lanes. A dependency is satisfied once its target reaches
        // 6-completed OR 7-archive, and ScanAllJobs omits the archive lane, so
        // the gate index is built from the archive-inclusive snapshot. Built
        // once per candidate-list computation, not per card.
        var waitsOnIndex = TaskReferenceIndex.Build(_scanner.ScanAllAutomationJobsWithArchive());
        return LaneSortApplier.Sort(
                all.Where(j => j.ProjectName == ProjectName
                                && j.State == TaskStates.Ready
                                && IsReadyPickupCandidate(j, intakeEnabled, waitsOnIndex)),
                TaskStates.Ready,
                _ => settings)
            .ToList();
    }

    private bool IsReadyPickupCandidate(TaskInfo job, bool intakeEnabled, TaskReferenceIndex waitsOnIndex)
        => AgentTypes.IsAutoPickupEligible(job.Agent)
           && !job.Fixture
           // Epics are containers, not work items: their sub-tasks flow through
           // the pipeline, the epic card never code-executes. Skip it hard so it
           // can never be loaded into a slot, even if it has come to rest in a
           // pickup lane (old data / stray move).
           && !IsUnpickableEpic(job)
           // Hard safety net for the human-decision-needed marker:
           // never auto-run such a card even if the 2-ready->5e-escalated
           // relocation sweep failed to move it. Running it just NOOP-burns
           // a CLI and trips the breaker.
           && !TaskSlugs.IsHumanDecisionNeeded(job.Id)
           // Rapid-crash backoff: skip a task that just crashed fast until
           // its exponential cooldown elapses (RapidCrashBreaker).
           && !IsInRapidCrashBackoff(job.Id)
           && IsPickupAllowed(job, intakeEnabled)
           // AGT-2029: don't pull a card whose waits-on (dependsOn) targets are
           // not yet fulfilled, or that sits on a dependency cycle. The card
           // falls out of the candidate list and stays visibly "waiting" on the
           // board; the tick moves to the next candidate, so a blocked/cyclic
           // dependency never deadlocks the runner.
           && !IsBlockedByWaitsOn(job, waitsOnIndex);

    /// <summary>
    /// AGT-2029 waits-on pickup gate. Returns true when <paramref name="job"/>
    /// must not be auto-picked because a dependency it waits on is unfulfilled
    /// (its target has not reached 6-completed/7-archive, or the key is unknown
    /// / not yet created) or because its dependsOn edges form a cycle. A cycle
    /// is a configuration error that can never be satisfied: it is reported once
    /// per card (structured warning + the card's waits-on status drives an error
    /// chip in the UI) and the card is skipped rather than deadlocking the tick.
    /// Cross-project + archive-inclusive resolution comes from
    /// <paramref name="waitsOnIndex"/>.
    /// </summary>
    private bool IsBlockedByWaitsOn(TaskInfo job, TaskReferenceIndex waitsOnIndex)
    {
        if (job.References == null || job.References.DependsOn.Count == 0) return false;

        var status = waitsOnIndex.EvaluateWaitsOn(job);
        var cardKey = job.Key ?? job.Id;

        if (status.CycleDetected)
        {
            bool firstReport;
            lock (_waitsOnCycleGate) firstReport = _waitsOnCycleWarned.Add(cardKey);
            if (firstReport)
                _logger.LogWarning(
                    "[taskboard] waits-on cycle on {Job} ({Project}): its dependsOn chain forms a cycle and can never be fulfilled - skipping auto-pickup (configuration error). Fix the dependency chain via the task's references.",
                    cardKey, ProjectName);
            return true;
        }

        // Not cyclic anymore: allow a future warning if a cycle recurs.
        lock (_waitsOnCycleGate) _waitsOnCycleWarned.Remove(cardKey);

        if (status.Blocked)
        {
            var open = string.Join(", ", status.Items.Where(i => !i.Fulfilled).Select(i => i.Key));
            _logger.LogDebug(
                "[taskboard] waits-on hold on {Job} ({Project}): waiting on {Open} to reach completed/archive before auto-pickup",
                cardKey, ProjectName, open);
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when a card must never be auto-picked because it is an epic
    /// container rather than a runnable work item. Logs once per skip so an
    /// epic that has leaked into a work lane surfaces as a visible anomaly
    /// instead of a silent zombie. Manual decomposition (an operator starting
    /// the epic's planning run) routes through the start endpoint, not this
    /// auto-pickup gate, so it is unaffected.
    /// </summary>
    private bool IsUnpickableEpic(TaskInfo job)
    {
        if (!TaskKinds.IsEpic(job.Kind)) return false;
        _logger.LogWarning(
            "[taskboard] skipping epic card {Job} on {Project} in pickup lane {State}: epics are containers, not pickable work items",
            job.Id, ProjectName, job.State);
        return true;
    }

    private List<DisplayedPickupCandidate> ListPickupCandidatesInDisplayedOrder()
    {
        var result = new List<DisplayedPickupCandidate>();
        foreach (var lane in RunnerPickupLanesInDisplayedOrder)
        {
            if (lane == TaskStates.Ready)
            {
                result.AddRange(ListReadyPickupCandidatesInDisplayedOrder()
                    .Select(j => new DisplayedPickupCandidate(TaskStates.Ready, j, null)));
                continue;
            }

            result.AddRange(ListProgressPickupCandidatesInDisplayedOrder());
        }
        return result;
    }

    private List<DisplayedPickupCandidate> ListProgressPickupCandidatesInDisplayedOrder()
    {
        var settings = _projectSettings.Get(ProjectName);
        var orderedInfo = LaneSortApplier.Sort(
                _scanner.ScanAllAutomationJobs()
                    .Where(j => j.ProjectName == ProjectName && j.State == TaskStates.Progress),
                TaskStates.Progress,
                _ => settings)
            .ToList();
        var folderBySlug = ListProgressFoldersOldestFirst()
            .ToDictionary(c => c.Slug, StringComparer.OrdinalIgnoreCase);

        var result = new List<DisplayedPickupCandidate>();
        foreach (var info in orderedInfo)
        {
            if (!folderBySlug.TryGetValue(info.Id, out var progress))
                continue;
            folderBySlug.Remove(info.Id);
            var candidate = TryPrepareProgressCandidate(progress);
            if (candidate != null) result.Add(candidate);
        }

        // Orphans are not visible kanban cards. Process them after visible
        // progress cards so they cannot jump ahead of displayed work, but still
        // let the pickup tick clean them up when the visible queue is drained.
        foreach (var orphan in folderBySlug.Values)
        {
            var candidate = TryPrepareProgressCandidate(orphan);
            if (candidate != null) result.Add(candidate);
        }

        return result;
    }

    private DisplayedPickupCandidate? TryPrepareProgressCandidate(ProgressPickupCandidate candidate)
    {
        var slug = Path.GetFileName(candidate.FolderPath);
        if (candidate.Info == null)
        {
            HandleStaleProgressOrphan(candidate, slug);
            return null;
        }

        if (candidate.Info.Fixture)
            return null;

        if (!AgentTypes.IsAutoPickupEligible(candidate.Info.Agent))
            return null;

        // Epics are containers, not work items - never resume one into a slot
        // even if it has come to rest in 3-progress (old data / stray move).
        if (IsUnpickableEpic(candidate.Info))
            return null;

        // Rapid-crash backoff: a progress folder that just crashed fast is
        // skipped (not dead-lettered) until its cooldown elapses, so the picker
        // cannot pull it straight back into a tight loop. Other work is still
        // free to run this tick.
        if (IsInRapidCrashBackoff(candidate.Info.Id))
            return null;

        var attempts = GetPickupAttempts(slug);
        var pickupThreshold = GetPickupFailureThreshold(slug);
        if (attempts >= pickupThreshold)
        {
            RerouteOverBudgetFolder(candidate, slug, attempts, thresholdOverride: pickupThreshold);
            return null;
        }

        // Zombie guard. Resuming a session-less folder a couple of times is
        // useful for crash recovery, but once it exhausts that small budget the
        // folder leaves 3-progress so it cannot hide ahead of visible ready work
        // forever.
        if (!HasResumableSession(candidate.Info))
        {
            var resumeFailures = GetZombieResumeFailures(slug);
            if (resumeFailures >= ZombieResumeFailureThreshold)
            {
                var zombieReason =
                    $"Auto-pickup gave up resuming a session-less 3-progress folder after {resumeFailures} failed resume attempts " +
                    $"(budget {ZombieResumeFailureThreshold}): no active process and no resumable session id to continue.";
                RerouteOverBudgetFolder(
                    candidate, slug, resumeFailures,
                    thresholdOverride: ZombieResumeFailureThreshold,
                    reasonOverride: zombieReason);
                return null;
            }
        }

        return new DisplayedPickupCandidate(TaskStates.Progress, candidate.Info, candidate);
    }

    /// <summary>
    /// Relocate any <see cref="TaskSlugs.HumanDecisionNeededPrefix"/> card that
    /// has come to rest in <c>2-ready</c> into <c>5e-escalated</c> before the
    /// pickup selection runs. Such a card is a marker for a human call, not a
    /// unit of agent work: auto-picking it spawns a CLI run the agent correctly
    /// NOOPs (exit=1 after a few seconds), which burns tokens and trips the
    /// cross-slug infra circuit breaker into a manual demotion. The retired
    /// 1b-needs-human-review lane used to hold these; with that lane gone, the
    /// move goes through <see cref="HumanReviewEscalation"/> so the card lands
    /// in 5e-escalated with a verdict + status stub.
    /// </summary>
    private void RelocateStrayHumanDecisionCards()
    {
        List<TaskInfo> stray;
        try
        {
            stray = _scanner.ScanAllAutomationJobs()
                .Where(j => j.ProjectName == ProjectName
                            && j.State == TaskStates.Ready
                            && TaskSlugs.IsHumanDecisionNeeded(j.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[taskboard] human-decision-needed sweep: scan failed for {Project}", ProjectName);
            return;
        }

        foreach (var job in stray)
        {
            var reason = "Human-decision marker: this card exists for a person to decide, not for an agent to run.";
            var move = _humanReviewEscalation.Escalate(
                job.Id, Entry.Path, ProjectName,
                HumanReviewEscalationCategories.HumanDecisionNeeded, reason);
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "[taskboard] human-decision-needed card {JobId} on {Project} could not be moved 2-ready -> {Target}: {Status} {Message}; it stays parked and is skipped by pickup",
                    job.Id, ProjectName, TaskStates.Escalated, move.Status, move.Message);
                continue;
            }

            _logger.LogInformation(
                "[taskboard] routed human-decision-needed card {JobId} on {Project} from 2-ready -> {Target} (never auto-run: it is a human-decision marker)",
                job.Id, ProjectName, TaskStates.Escalated);

            try
            {
                var moved = _scanner.FindJob(job.Id, Entry.Path);
                if (moved != null)
                    _chatLog.AppendSupervisor(moved, "human-decision-routed",
                        "Routed to 5e-escalated: this card is a human-decision marker, so the runner will not spawn a CLI run for it.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[taskboard] human-decision-needed sweep: chat-log append failed for {JobId}", job.Id);
            }
        }
    }

    /// <summary>
    /// Intake gate. When intake is disabled (default), every 2-ready card is
    /// pickup-eligible. When intake is enabled per project, the runner waits
    /// for the orchestrator-intake hosted service to mark a card
    /// <see cref="LifecyclePhases.IntakePassed"/> before picking it up. Cards
    /// in <c>human-ready</c>, <c>intake-running</c>, or <c>intake-blocked</c>
    /// stay in 2-ready and the runner tick falls through to the next card.
    /// </summary>
    internal static bool IsPickupAllowed(TaskInfo job, bool intakeEnabled)
    {
        if (!intakeEnabled) return true;
        return job.Phase == LifecyclePhases.IntakePassed;
    }

    /// <summary>
    /// Returns the oldest 3-progress job for this project that carries a
    /// captured session id we can resume against. The auto-pickup tick
    /// prefers these over jobs in 2-ready so an interrupted in-flight run
    /// continues where it left off instead of being skipped while a fresh
    /// job is started. A job has resumable state when either
    /// <see cref="TaskInfo.SessionName"/> or any non-recovery-marker entry
    /// in <see cref="TaskInfo.SessionChain"/> is non-empty.
    /// </summary>
    /// <remarks>
    /// Retained because <see cref="AutoPickupCascadeTests"/> pins the
    /// <see cref="HasResumableSession"/> classifier as a public-shape
    /// invariant. The pickup tick itself no longer routes through this
    /// method; <see cref="TryPickProgressJobOrDeadLetter"/> picks ANY
    /// 3-progress folder regardless of session state (the "no log" case
    /// is the most-restartable, not the most-skippable).
    /// </remarks>
    private TaskInfo? GetNextResumableProgressJob()
    {
        return _scanner.ScanAllAutomationJobs()
            .Where(j => j.ProjectName == ProjectName
                        && j.State == TaskStates.Progress
                        && HasResumableSession(j))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
    }

    internal static bool HasResumableSession(TaskInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SessionName)) return true;
        if (info.SessionChain == null || info.SessionChain.Count == 0) return false;
        return info.SessionChain.Any(id => !string.IsNullOrWhiteSpace(id));
    }

    // Strict-iteration progress-first pickup (deliverables of the
    // pickup-loop-progress-first-strict-iteration task).
    //
    // Production observation: a 2-ready job had been picked up while a
    // 3-progress folder for the same project still existed, because the
    // older "GetNextResumableProgressJob" filter required a captured
    // session id. The folder in question lost its cli-output.log to a
    // race during a backend restart, so it carried no session id and was
    // skipped. The fix walks EVERY 3-progress folder oldest-first by
    // mtime before considering 2-ready, and dead-letters any folder that
    // has exhausted the retry budget without producing CLI output.

    /// <summary>Default retry budget before a 3-progress folder is rerouted off the pickup path.</summary>
    internal const int PickupFailureThreshold = 3;

    /// <summary>
    /// <see cref="PickupAttemptDiagnostic.ExecutionStatus"/> value recorded
    /// when a pickup attempt never started the CLI process (spawn failure).
    /// Used by the reroute classifier to tell "the CLI is unavailable"
    /// (requeue to 2-ready and pause) apart from "the CLI ran but produced
    /// nothing" (escalate to 5e-escalated with a typed reason).
    /// </summary>
    internal const string SpawnFailedExecutionStatus = "spawn-failed";
    internal const string WorktreeBlockedExecutionStatus = "worktree-blocked";
    internal const int WorktreeBlockedFailureThreshold = 5;
    /// <summary>
    /// Per-attempt deadline (seconds) within which the spawned CLI must
    /// produce at least one streamed output line for the attempt to count
    /// as healthy. Today the runner observes this passively at run-finish:
    /// a run that finishes with zero captured output lines is treated as
    /// a silent attempt regardless of duration. The constant is recorded
    /// in the dead-letter row so operators can correlate the verdict with
    /// the active configuration.
    /// </summary>
    internal const int PickupOutputDeadlineSeconds = 60;

    /// <summary>
    /// Small per-slug budget for resuming a <c>3-progress</c> folder that has
    /// no resumable session id (a "zombie": no active process and nothing to
    /// <c>--resume</c> against). The strict-iteration picker is progress-first
    /// by design, so a zombie that is re-picked every tick silently jumps
    /// ahead of the due 2-ready task forever. A zombie gets at most this many
    /// failed resume attempts - an auto-pickup run that neither reaches review
    /// nor captures a session id - before it is dead-lettered into
    /// <see cref="TaskStates.FailedPickup"/> so the queue can drain.
    /// Deliberately smaller than <see cref="PickupFailureThreshold"/>: a
    /// session-less folder has no real run to resume, so we give up sooner
    /// than for a folder that just streamed nothing on one attempt.
    /// </summary>
    internal const int ZombieResumeFailureThreshold = 2;

    // Per-slug consecutive-silent-attempt counter. In-memory only - a
    // backend restart resets the counter, which matches the wider runner
    // pattern (a restart is itself a recovery boundary). Bounded by
    // <see cref="PickupFailureThreshold"/>; the same dictionary is read
    // when picking and written when the failed run finishes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PickupAttemptState> _pickupAttempts = new();

    // Per-slug failed-resume counter for session-less 3-progress folders.
    // Unlike _pickupAttempts (which only counts FULLY SILENT runs and resets
    // on any streamed output line), this counter increments on every
    // auto-pickup resume that fails to make progress - one that neither
    // reaches review nor captures a resumable session id - even when the run
    // streamed an error line. That is the exact symptom behind the
    // "zombie keeps getting picked" bug: a resume that prints
    // "No conversation found" and exits non-zero used to reset the silent
    // counter and be resumed forever. In-memory only (a restart is a
    // recovery boundary). Reset on real progress.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _zombieResumeFailures = new();

    private sealed class PickupAttemptState
    {
        public int Count;
        public readonly Queue<PickupAttemptDiagnostic> History = new();
    }

    /// <summary>Test seam: lets a unit test prime the per-slug attempt counter
    /// without driving a real failed run. When <paramref name="executionStatus"/>
    /// is supplied, the attempt history is also primed with that status on every
    /// entry so the over-budget classifier can be exercised: pass
    /// <see cref="SpawnFailedExecutionStatus"/> to drive the spawn-failure ->
    /// 2-ready + pause path, or leave it null for the task-shaped ->
    /// 5e-escalated path. History is bounded at the threshold to mirror the
    /// real recorder.</summary>
    internal void SetPickupAttemptsForTest(string slug, int count, string? executionStatus = null, string? error = null)
    {
        var state = _pickupAttempts.GetOrAdd(slug, _ => new PickupAttemptState());
        state.Count = count;
        state.History.Clear();
        var threshold = string.Equals(executionStatus, WorktreeBlockedExecutionStatus, StringComparison.Ordinal)
            ? WorktreeBlockedFailureThreshold
            : PickupFailureThreshold;
        var entries = Math.Min(count, threshold);
        for (var i = 0; i < entries; i++)
        {
            state.History.Enqueue(new PickupAttemptDiagnostic
            {
                At = DateTime.UtcNow,
                DurationSeconds = 0,
                OutputLines = 0,
                ExecutionStatus = executionStatus,
                Error = error
            });
        }
    }

    private int PendingModeActiveTaskCount()
    {
        lock (_modeChangeGate) return _pendingMode == null ? 0 : _pendingModeDrainJobIds.Count;
    }

    private string? PendingModeActiveTaskTitle()
    {
        string? jobId;
        lock (_modeChangeGate)
        {
            if (_pendingMode == null || _pendingModeDrainJobIds.Count != 1) return null;
            jobId = _pendingModeDrainJobIds.First();
        }
        try { return _scanner.FindJob(jobId, Entry.Path)?.Title; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve pending-mode task title for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>Test seam: read the per-slug attempt counter.</summary>
    internal int GetPickupAttempts(string slug)
        => _pickupAttempts.TryGetValue(slug, out var s) ? s.Count : 0;

    internal int GetPickupFailureThreshold(string slug)
        => _pickupAttempts.TryGetValue(slug, out var state)
           && IsWorktreeBlockedFailure(state.History.LastOrDefault())
            ? WorktreeBlockedFailureThreshold
            : PickupFailureThreshold;

    private static bool IsWorktreeBlockedFailure(PickupAttemptDiagnostic? attempt)
        => string.Equals(
            attempt?.ExecutionStatus,
            WorktreeBlockedExecutionStatus,
            StringComparison.Ordinal);

    private void RecordWorktreePreparationFailure(string jobId, string? error)
    {
        var state = _pickupAttempts.GetOrAdd(jobId, _ => new PickupAttemptState());
        state.Count++;
        state.History.Enqueue(new PickupAttemptDiagnostic
        {
            At = DateTime.UtcNow,
            DurationSeconds = 0,
            OutputLines = 0,
            ExecutionStatus = WorktreeBlockedExecutionStatus,
            Error = error
        });
        while (state.History.Count > WorktreeBlockedFailureThreshold) state.History.Dequeue();
    }

    /// <summary>Test seam: number of distinct tasks the auto-failure breaker has
    /// parked in 5e-escalated without a success in between - the "3x3"
    /// cooldown counter that temporarily flips the project to manual at
    /// <see cref="AutoFailureDistinctTaskHaltThreshold"/>.</summary>
    internal int GetParkedFailedTaskCountForTest() => _parkedFailedJobIds.Count;

    /// <summary>
    /// Auto-failure handling for a finished auto-pickup run that did NOT reach
    /// review and was not a deliberate stop. Park-and-continue: a task that
    /// fails <see cref="AutoFailureHaltThreshold"/> times in a row is parked in
    /// 5e-escalated and auto-mode KEEPS running with the next task; only
    /// when <see cref="AutoFailureDistinctTaskHaltThreshold"/> DISTINCT tasks have
    /// each failed out without a success in between ("3x3") does the project
    /// enter a self-healing cooldown. <paramref name="activeInfo"/> may be null (e.g. in tests):
    /// the counting + halt decision still runs; the park-move + chat note are
    /// skipped.
    /// </summary>
    private void HandleAutoPickupFailure(string jobId, TaskInfo? activeInfo)
    {
        _consecutiveAutoFailureCount++;
        _recentAutoFailureJobIds.Enqueue(jobId);
        while (_recentAutoFailureJobIds.Count > _circuitBreakerOptions.PerTaskFailureThreshold)
            _recentAutoFailureJobIds.Dequeue();

        // A single task that fails AutoFailureHaltThreshold times in a row
        // without reaching review is the unambiguous offender.
        var sameJobRepeated = _recentAutoFailureJobIds.Count >= _circuitBreakerOptions.PerTaskFailureThreshold
            && _recentAutoFailureJobIds.All(id => string.Equals(id, jobId, StringComparison.Ordinal));

        if (sameJobRepeated && IsAutoMode(_mode))
        {
            // QUARANTINE-AND-CONTINUE (not a project-wide halt on one bad task).
            // Route the offender out of 3-progress into 5e-escalated through
            // the same escalation funnel as other system moves. Only a systemic
            // pattern trips the global cooldown below.
            if (activeInfo != null)
            {
                _mutations.AddJobTag(jobId, "auto-halted", activeInfo.WatchPath);
                var parkReason = $"auto-halted: {jobId} did not reach review after {_circuitBreakerOptions.PerTaskFailureThreshold} auto-pickup runs";
                _ = EscalateAutoFailureParkAsync(jobId, activeInfo.WatchPath, activeInfo.ProjectName, parkReason);
            }

            _parkedFailedJobIds.Add(jobId);
            // Reset the per-task window so the next task starts clean.
            _consecutiveAutoFailureCount = 0;
            _recentAutoFailureJobIds.Clear();

            if (_parkedFailedJobIds.Count >= AutoFailureDistinctTaskHaltThreshold)
            {
                var parked = string.Join(", ", _parkedFailedJobIds);
                if (activeInfo != null)
                    _chatLog.Append(activeInfo, OrchestratorMessageKind.Decision,
                        $"Auto-mode cooldown: {_parkedFailedJobIds.Count} distinct tasks each failed {_circuitBreakerOptions.PerTaskFailureThreshold}x without reaching review and were moved to 5e-escalated ({parked}). Looks systemic; the runner will resume automatically after cooldown.");
                _logger.LogWarning(
                    "Runner '{Project}' cooling down auto-mode: {Count} distinct tasks failed out (3x{Threshold}): {Parked}",
                    ProjectName, _parkedFailedJobIds.Count, _circuitBreakerOptions.PerTaskFailureThreshold, parked);
                ScheduleGlobalBreakerCooldown(
                    $"{_parkedFailedJobIds.Count} distinct tasks failed out; last '{jobId}'",
                    activeInfo);
                _parkedFailedJobIds.Clear();
            }
            else if (activeInfo != null)
            {
                _chatLog.Append(activeInfo, OrchestratorMessageKind.Decision,
                    $"Job '{jobId}' did not reach review after {_circuitBreakerOptions.PerTaskFailureThreshold} runs; moved to 5e-escalated with tag auto-halted. Auto-mode continues with the next task ({_parkedFailedJobIds.Count}/{AutoFailureDistinctTaskHaltThreshold} distinct tasks quarantined before a systemic cooldown).");
                _logger.LogWarning(
                    "Runner '{Project}' moved '{JobId}' to 5e-escalated after {N} failures; auto-mode continues ({Count}/{Halt} distinct quarantined).",
                    ProjectName, jobId, _circuitBreakerOptions.PerTaskFailureThreshold, _parkedFailedJobIds.Count, AutoFailureDistinctTaskHaltThreshold);
            }
        }
        else if (_consecutiveAutoFailureCount >= _circuitBreakerOptions.PerTaskFailureThreshold && IsAutoMode(_mode))
        {
            // Window full but no single repeated offender (mixed transient
            // failures across different jobs). Don't park or halt: reset the
            // window and let recovery proceed. A genuinely stuck job re-
            // accumulates as same-job-repeated above and gets parked then.
            _logger.LogInformation(
                "Runner '{Project}' saw {N} mixed auto-pickup failures (no single repeat); resetting window, auto-mode continues.",
                ProjectName, _consecutiveAutoFailureCount);
            _consecutiveAutoFailureCount = 0;
            _recentAutoFailureJobIds.Clear();
        }
    }

    private async Task EscalateAutoFailureParkAsync(string jobId, string watchPath, string projectName, string parkReason)
    {
        try
        {
            var outcome = await _humanReviewEscalation.EscalateAsync(
                jobId,
                watchPath,
                projectName,
                HumanReviewEscalationCategories.AutoFailurePark,
                parkReason,
                CancellationToken.None);
            if (outcome.Status == MoveJobStatus.Success && !string.IsNullOrWhiteSpace(outcome.NewFolderPath))
            {
                _timeline?.Append(
                    outcome.NewFolderPath!,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.Orchestrator,
                    parkReason,
                    details: new()
                    {
                        ["category"] = HumanReviewEscalationCategories.AutoFailurePark,
                        ["tag"] = "auto-halted",
                        ["threshold"] = _circuitBreakerOptions.PerTaskFailureThreshold.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-failure park escalation crashed for {Project}/{JobId}", ProjectName, jobId);
        }
    }

    private static bool IsRateLimitFailure(IReadOnlyList<CliOutputLine> output)
    {
        foreach (var line in output)
        {
            var text = line.Text ?? string.Empty;
            if (text.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("429", StringComparison.OrdinalIgnoreCase)
                || text.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || text.Contains("session limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ScheduleGlobalBreakerCooldown(string reason, TaskInfo? activeInfo)
    {
        if (!IsAutoMode(_mode)) return;

        _globalBreakerTripCount++;
        var minutes = Math.Min(
            _circuitBreakerOptions.GlobalCooldownMax.TotalMinutes,
            _circuitBreakerOptions.GlobalCooldownBase.TotalMinutes
                * Math.Pow(_circuitBreakerOptions.GlobalCooldownBackoffMultiplier, Math.Max(0, _globalBreakerTripCount - 1)));
        if (minutes <= 0) minutes = RunnerCircuitBreakerOptions.Default.GlobalCooldownBase.TotalMinutes;

        _globalBreakerReason = reason;
        _globalBreakerCooldownUntil = DateTime.UtcNow.AddMinutes(minutes);

        if (activeInfo != null)
        {
            _chatLog.Append(activeInfo, OrchestratorMessageKind.Decision,
                $"Auto-mode cooling down until {_globalBreakerCooldownUntil:O}: {reason}. The runner will resume automatically.");
        }

        _logger.LogWarning(
            "Runner '{Project}' global circuit breaker cooling down until {Until:o} after trip {TripCount}: {Reason}",
            ProjectName, _globalBreakerCooldownUntil, _globalBreakerTripCount, reason);
        SetMode("manual", $"auto-failure circuit-breaker cooldown: {reason}; resumes at {_globalBreakerCooldownUntil:O}");
    }

    private void TryAutoResumeGlobalBreaker()
    {
        if (_globalBreakerCooldownUntil == null) return;
        if (DateTime.UtcNow < _globalBreakerCooldownUntil.Value) return;
        if (!string.Equals(_mode, "manual", StringComparison.Ordinal)) return;

        var reason = _globalBreakerReason ?? "global circuit breaker cooldown elapsed";
        _logger.LogInformation(
            "Runner '{Project}' auto-resuming after global circuit breaker cooldown: {Reason}",
            ProjectName, reason);
        _globalBreakerCooldownUntil = null;
        _globalBreakerReason = null;
        SetMode("auto-continuous", $"auto-resume after circuit-breaker cooldown: {reason}");
    }

    /// <summary>Test seam: drive one auto-pickup failure through the breaker
    /// decision (park-and-continue / 3x3 halt) without a full run. Pass
    /// <paramref name="activeInfo"/> null to exercise the pure counting + mode
    /// decision.</summary>
    internal void RecordAutoPickupFailureForTest(string jobId, TaskInfo? activeInfo = null)
        => HandleAutoPickupFailure(jobId, activeInfo);

    internal void RecordRateLimitAutoPickupFailureForTest(string jobId, TaskInfo? activeInfo = null)
        => ScheduleGlobalBreakerCooldown($"rate-limit or transient CLI quota failure on '{jobId}'", activeInfo);

    internal void ForceGlobalBreakerCooldownElapsedForTest()
    {
        if (_globalBreakerCooldownUntil != null)
            _globalBreakerCooldownUntil = DateTime.UtcNow.AddSeconds(-1);
    }

    internal void RecordProviderLimitForTest(ProviderLimitStatus status)
        => _providerLimits.Record(status);

    internal bool ProviderClaimsAllowedForTest(string cliType, DateTime utcNow)
        => ActiveProviderLimit(cliType, utcNow) is null;

    /// <summary>Test seam: prime the per-slug zombie-resume-failure counter
    /// so a regression test can drive the picker's zombie dead-letter path
    /// without running a real failed resume end-to-end.</summary>
    internal void SetZombieResumeFailuresForTest(string slug, int count)
        => _zombieResumeFailures[slug] = count;

    /// <summary>Test seam: read the per-slug zombie-resume-failure counter so
    /// a regression test can prove it increments on each failed resume (the
    /// "keeps getting picked" symptom) and resets on real progress.</summary>
    internal int GetZombieResumeFailures(string slug)
        => _zombieResumeFailures.TryGetValue(slug, out var n) ? n : 0;

    /// <summary>
    /// Records one failed resume against a session-less <c>3-progress</c>
    /// folder. Called from both the spawn-failure path (no execution) and the
    /// run-finish path (resume produced no resumable session and did not reach
    /// review). Increments the per-slug counter the picker consults before
    /// resuming a zombie.
    /// </summary>
    private void RecordZombieResumeFailure(string slug)
    {
        var n = _zombieResumeFailures.AddOrUpdate(slug, 1, (_, prev) => prev + 1);
        _logger.LogInformation(
            "[taskboard] zombie resume failure {N}/{Budget} for {Slug} on {Project} (no resumable session, no progress)",
            n, ZombieResumeFailureThreshold, slug, ProjectName);
    }

    /// <summary>
    /// Settles the zombie-resume counter for a job whose auto-pickup run just
    /// finished WITHOUT reaching review (and was not a deliberate stop). The job
    /// is therefore still sitting in <c>3-progress</c>. If it now carries a
    /// resumable session id the run made real progress, so the counter is reset;
    /// otherwise the folder is a zombie that the progress-first picker will
    /// resume again next tick, so the failed resume is counted. Once the count
    /// reaches <see cref="ZombieResumeFailureThreshold"/> the picker stops
    /// resuming it and escalates it out of <c>3-progress</c> instead.
    /// Re-scans on-disk state because the pre-run <see cref="TaskInfo"/> snapshot
    /// predates any session id the finished run may have captured.
    /// </summary>
    internal void AccountZombieResumeOutcome(string jobId)
    {
        var info = _scanner.FindJob(jobId, Entry.Path);
        // A job that moved out of 3-progress (e.g. quarantined in
        // 5e-escalated by HandleAutoPickupFailure) is no longer a
        // resume candidate, so there is nothing to count.
        if (info == null || info.State != TaskStates.Progress) return;

        var slug = Path.GetFileName(info.FolderPath);
        if (HasResumableSession(info))
            _zombieResumeFailures.TryRemove(slug, out _);
        else
            RecordZombieResumeFailure(slug);
    }

    /// <summary>Test seam: read the consecutive auto-failure counter so
    /// regression tests can prove the counter actually resets after a
    /// successful auto-pickup, instead of inferring from "mode stayed
    /// auto" (which would still pass if the counter silently leaked).</summary>
    internal int GetConsecutiveAutoFailureCountForTest() => _consecutiveAutoFailureCount;

    /// <summary>Test seam: read the per-job consecutive capture-fail
    /// counter (and the job it is attributed to). Same motivation as
    /// <see cref="GetConsecutiveAutoFailureCountForTest"/>.</summary>
    internal (int Count, string? JobId) GetConsecutiveCaptureFailStateForTest()
        => (_consecutiveCaptureFailCount, _consecutiveCaptureFailJobId);

    /// <summary>Test seam: prime the consecutive auto-failure counter
    /// so regression tests can drive the reset path without first
    /// running three failed auto-pickups end-to-end.</summary>
    internal void SetConsecutiveAutoFailureCountForTest(int count, string? jobId = null)
    {
        _consecutiveAutoFailureCount = count;
        _recentAutoFailureJobIds.Clear();
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            for (var i = 0; i < count; i++) _recentAutoFailureJobIds.Enqueue(jobId);
        }
    }

    /// <summary>Test seam: prime the per-job consecutive capture-fail
    /// counter directly (same motivation as the auto-failure seam).</summary>
    internal void SetConsecutiveCaptureFailStateForTest(int count, string? jobId)
    {
        _consecutiveCaptureFailCount = count;
        _consecutiveCaptureFailJobId = jobId;
    }

    /// <summary>
    /// Strict-iteration progress-first picker. Walks every 3-progress folder
    /// for this project oldest-first by mtime, dead-letters folders past the
    /// retry budget, and returns the first remaining folder. Returns null
    /// only when 3-progress contains no folders (or all of them were
    /// dead-lettered in this call).
    /// </summary>
    private TaskInfo? TryPickProgressJobOrDeadLetter()
    {
        var folders = ListProgressFoldersOldestFirst();
        foreach (var candidate in folders)
        {
            var slug = Path.GetFileName(candidate.FolderPath);
            if (candidate.Info == null)
            {
                HandleStaleProgressOrphan(candidate, slug);
                continue;
            }

            if (!AgentTypes.IsAutoPickupEligible(candidate.Info.Agent))
                continue;

            // Rapid-crash backoff: a progress folder that just crashed fast is
            // skipped (not dead-lettered) until its cooldown elapses, so the
            // progress-first picker cannot pull it straight back into a tight
            // loop. Other progress/ready work is still free to run this tick.
            if (IsInRapidCrashBackoff(candidate.Info.Id))
                continue;

            var attempts = GetPickupAttempts(slug);
            var pickupThreshold = GetPickupFailureThreshold(slug);
            if (attempts >= pickupThreshold)
            {
                var pausedDuringReroute = RerouteOverBudgetFolder(
                    candidate, slug, attempts, thresholdOverride: pickupThreshold);
                // Spawn-failure pause (loop-inventory:
                // pickup.spawn-failure-budget-pause / cross-slug-infra-circuit-breaker).
                // If rerouting this over-budget folder just paused the runner
                // (its CLI could not be spawned), halt the iteration so the
                // remaining 3-progress folders are not touched this tick. The
                // mode flip also short-circuits the next TickAsync via the
                // "manual" gate at the head of the tick, so this guard is
                // mid-iteration only. A task-shaped or zombie escalation does
                // not pause: the folder leaves the loop (5e-escalated), so we
                // simply continue to the next candidate.
                if (pausedDuringReroute) return null;
                continue;
            }

            // Zombie guard. Progress-first resume is correct in principle -
            // don't abandon interrupted work - but a session-less folder has
            // no real run to resume: re-picking it every tick just starts a
            // fresh/recovery run that, when it also fails, leaves the folder
            // stranded in 3-progress and silently jumps ahead of the due
            // 2-ready task again. The project-wide pickup gate at the head of
            // TickAsync already guarantees no active process for this project,
            // so the only thing that distinguishes a resumable folder from a
            // zombie here is a captured session id. A folder with no resumable
            // session that has burned its small resume budget is dead-lettered
            // immediately rather than resumed indefinitely. A fresh session-
            // less folder (0-1 failures) is still resumed: the "no log" case
            // is the most-restartable, so we grant a couple of grace attempts
            // before giving up.
            if (!HasResumableSession(candidate.Info))
            {
                var resumeFailures = GetZombieResumeFailures(slug);
                if (resumeFailures >= ZombieResumeFailureThreshold)
                {
                    var zombieReason =
                        $"Auto-pickup gave up resuming a session-less 3-progress folder after {resumeFailures} failed resume attempts " +
                        $"(budget {ZombieResumeFailureThreshold}): no active process and no resumable session id to continue.";
                    var pausedDuringZombieReroute = RerouteOverBudgetFolder(
                        candidate, slug, resumeFailures,
                        thresholdOverride: ZombieResumeFailureThreshold,
                        reasonOverride: zombieReason);
                    if (pausedDuringZombieReroute) return null;
                    continue;
                }
            }

            return candidate.Info;
        }
        return null;
    }

    private void HandleStaleProgressOrphan(ProgressPickupCandidate candidate, string slug)
    {
        // Distinguish a genuine pickup orphan from post-move cleanup debris.
        //
        // Cleanup debris happens because the Windows + ASP.NET combination
        // sometimes leaves a skeleton 3-progress folder behind after the
        // job has already moved on. The race: while ProjectRunner finishes
        // a job and TaskStateMachine.MoveJob renames 3-progress/<slug> ->
        // <lane>/<slug>, another in-process writer (CliOutputLogStore on
        // cli-output.log, TaskSessionLog on session-events.jsonl) may still
        // hold a Read/Write file handle on a file inside that folder.
        // Those handles are opened with FileShare.ReadWrite but NOT
        // FileShare.Delete, which is exactly the share-flag that blocks
        // the Win32 directory-rename operation from completing for the
        // locked sub-file. Directory.Move() succeeds for the rest of the
        // tree (and returns success) but a stub folder containing just
        // the still-locked file or its parent <c>logs/</c> sub-folder is
        // left behind in 3-progress. The task.json is gone (it moved with
        // the rest), so the next pickup tick walks into this method.
        //
        // From the user's point of view, calling that "failed pickup" is
        // wrong: there was no CLI spawn, no missing prompt, no broken
        // config. The job moved on cleanly; only the empty shell remained.
        // Surfacing it as a pickup failure pollutes the 3a-failed-pickup
        // lane with entries that are not actionable and obscures genuine
        // CLI spawn failures.
        //
        // Decision rule: if any post-progress lane contains a folder with
        // this slug, treat the leftover 3-progress folder as cleanup debris
        // and best-effort delete it. The job is provably elsewhere; the
        // skeleton has no claim on the kanban. If the delete fails because
        // the locking handle is still open, leave the folder and retry
        // next tick — the slug-in-post-lane check stays true, so the next
        // tick will not mis-classify it either. No orphan entry is ever
        // written for cleanup debris.
        //
        // The genuine-orphan path (no post-progress twin) is below. A folder
        // with no task.json is not a runnable task: a user who manually created
        // an empty 3-progress/<slug>/ folder, or a hard backend crash that
        // lost task.json without moving the job on. failed-pickup-elimination
        // cause #5: this is debris, not a pickup failure, so it is archived to
        // 7-archive with its evidence (logs, status.md) intact rather than
        // parked in a dead-end failure lane the operator has to triage.
        if (TryFindSlugInPostProgressLane(slug, out var locatedLane))
        {
            // ADR-0024: skeleton delete routes through the typed layer.
            // Conflict (file lock) lets the next tick retry; Rejected
            // (access denied) is logged once for the operator.
            var deleteResult = _taskAccess.DeleteLaneFolder(Entry.Path, TaskStates.Progress, slug);
            if (deleteResult.Status == AgentStudio.TaskAccess.TaskMutationStatus.Applied)
            {
                _logger.LogInformation(
                    "[taskboard] cleaned up post-move skeleton for {Slug} on {Project} (real job lives in {Lane})",
                    slug, ProjectName, locatedLane);
            }
            else if (deleteResult.Status == AgentStudio.TaskAccess.TaskMutationStatus.Conflict)
            {
                _logger.LogDebug(
                    "[taskboard] post-move skeleton {Folder} for {Slug} still locked; will retry next tick ({Msg})",
                    candidate.FolderPath, slug, deleteResult.Message);
            }
            else if (deleteResult.Status == AgentStudio.TaskAccess.TaskMutationStatus.Rejected)
            {
                _logger.LogWarning(
                    "[taskboard] post-move skeleton {Folder} for {Slug} cannot be deleted (access denied); manual cleanup required ({Msg})",
                    candidate.FolderPath, slug, deleteResult.Message);
            }
            _pickupAttempts.TryRemove(slug, out _);
            return;
        }

        var now = DateTime.UtcNow;
        var destinationSlug = BuildProgressOrphanSlug(slug, now,
            existsInDestination: name => _taskAccess.SlugExistsInLane(Entry.Path, TaskStates.Archive, name));

        var moveResult = _taskAccess.ArchiveOrphanFolder(
            Entry.Path, TaskStates.Progress, slug, destinationSlug);
        if (moveResult.Status != AgentStudio.TaskAccess.TaskMutationStatus.Applied)
        {
            _logger.LogWarning(
                "Progress orphan archive refused for {Slug} on {Project}: {Status} {Message}",
                slug, ProjectName, moveResult.Status, moveResult.Message);
            return;
        }

        _pickupAttempts.TryRemove(slug, out _);
        _logger.LogInformation(
            "[taskboard] archived stale 3-progress orphan {Slug} on {Project} to {Destination} (no task.json, no downstream twin); auto-pickup will continue",
            slug, ProjectName, destinationSlug);
    }

    /// <summary>
    /// Returns true when a folder with the given slug exists in any of the
    /// lanes a job can move into after <c>3-progress</c>. Used to distinguish
    /// post-move cleanup debris from a genuine pickup orphan: if the job's
    /// real folder lives downstream, the empty shell that remained in
    /// <c>3-progress</c> is just a Windows file-handle race, not a failure
    /// the operator needs to see.
    /// </summary>
    internal bool TryFindSlugInPostProgressLane(string slug, out string lane)
    {
        foreach (var laneName in PostProgressLanes)
        {
            // ADR-0024: slug existence check goes through the typed
            // layer instead of building the lane path.
            if (_taskAccess.SlugExistsInLane(Entry.Path, laneName, slug))
            {
                lane = laneName;
                return true;
            }
        }
        lane = string.Empty;
        return false;
    }

    /// <summary>
    /// Every lane a job can land in after leaving <c>3-progress</c>. Used by
    /// <see cref="TryFindSlugInPostProgressLane"/> to decide whether a
    /// skeleton folder in <c>3-progress</c> represents post-move cleanup
    /// debris (real job is downstream) or a genuine orphan (no downstream
    /// twin). The intake / pre-progress lanes are deliberately excluded:
    /// they cannot be the move target of an in-flight job, so a slug
    /// match there does not indicate cleanup debris.
    /// </summary>
    internal static readonly string[] PostProgressLanes =
    [
        TaskStates.AutoReview,
        TaskStates.Escalated,
        TaskStates.HumanReview,
        TaskStates.Completed,
        TaskStates.Archive,
    ];

    internal static string BuildProgressOrphanSlug(string slug, DateTime utcNow, Func<string, bool> existsInDestination)
    {
        var baseSlug = $"orphan-{slug}-{utcNow:yyyy-MM-dd}";
        if (!existsInDestination(baseSlug)) return baseSlug;
        var i = 2;
        while (existsInDestination($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }

    /// <summary>
    /// Lists every folder under this project's <c>3-progress</c> lane,
    /// ordered oldest-first by mtime. mtime uses the same shape as
    /// <see cref="StaleProgressArchiver"/>: <c>logs/cli-output.log</c>
    /// when present, falling back to <c>task.json</c>, falling back to
    /// the directory itself; an empty folder lands at epoch 0 so it
    /// sorts to the head of the iteration.
    /// </summary>
    internal List<ProgressPickupCandidate> ListProgressFoldersOldestFirst()
    {
        // ADR-0024: enumerate 3-progress through the typed layer.
        // ListLaneFolders returns orphan folders (no task.json) too,
        // which is exactly the case the pickup loop is built around.
        // Keep fixture metadata attached while enumerating physical folders so
        // TryPrepareProgressCandidate can skip it explicitly instead of
        // misclassifying the folder as orphan debris.
        var byId = _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName && j.State == TaskStates.Progress)
            .ToDictionary(j => j.Id, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<ProgressPickupCandidate>();
        foreach (var laneFolder in _taskAccess.ListLaneFolders(Entry.Path, TaskStates.Progress))
        {
            byId.TryGetValue(laneFolder.Slug, out var info);
            candidates.Add(new ProgressPickupCandidate(
                FolderPath: laneFolder.FolderPath,
                Slug: laneFolder.Slug,
                Info: info,
                Mtime: MeasureProgressFolderMtime(laneFolder.FolderPath)));
        }

        return OrderProgressByMtime(candidates);
    }

    /// <summary>
    /// Pure helper: orders progress-folder candidates oldest-first by mtime.
    /// Ties are broken by slug for determinism (so test fixtures with mtime
    /// pinned to the same instant still sort predictably).
    /// </summary>
    internal static List<ProgressPickupCandidate> OrderProgressByMtime(IEnumerable<ProgressPickupCandidate> candidates)
        => candidates.OrderBy(c => c.Mtime).ThenBy(c => c.Slug, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// mtime measurement matching <see cref="StaleProgressArchiver.MeasureFolder"/>:
    /// max mtime across <c>task.json</c> and every file under <c>logs/</c>
    /// (<c>cli-output.log</c>, <c>tool-calls.jsonl</c>,
    /// <c>session-events.jsonl</c>, future log types). Falls back to the
    /// directory mtime when no files exist. Folders with nothing return
    /// <see cref="DateTime.MinValue"/> so they sort to the head of the
    /// oldest-first iteration. Reading any single file misses sessions that
    /// emit primarily tool-use events while <c>cli-output.log</c> stays quiet.
    /// </summary>
    internal static DateTime MeasureProgressFolderMtime(string folder)
    {
        try
        {
            var maxStamp = DateTime.MinValue.ToUniversalTime();
            var hasAny = false;

            var logsDir = Path.Combine(folder, "logs");
            if (Directory.Exists(logsDir))
            {
                foreach (var file in Directory.EnumerateFiles(logsDir))
                {
                    try
                    {
                        var stamp = File.GetLastWriteTimeUtc(file);
                        if (stamp > maxStamp) maxStamp = stamp;
                        hasAny = true;
                    }
                    catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: skip unreadable files"); /* skip unreadable files */ }
                }
            }

            var jobJson = Path.Combine(folder, "task.json");
            if (File.Exists(jobJson))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(jobJson);
                    if (stamp > maxStamp) maxStamp = stamp;
                    hasAny = true;
                }
                catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: skip"); /* skip */ }
            }

            if (hasAny) return maxStamp;
            if (Directory.Exists(folder)) return Directory.GetLastWriteTimeUtc(folder);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort: an unreadable folder sorts to the head"); /* best-effort: an unreadable folder sorts to the head */ }
        return DateTime.MinValue.ToUniversalTime();
    }

    /// <summary>
    /// Reroute a 3-progress folder whose autopickup attempts have exhausted
    /// the retry budget. failed-pickup-elimination doctrine: a folder that
    /// carries a <c>task.json</c> is a real task and is NEVER parked in a
    /// dead-end failure lane. The budget-exhaustion cause decides where it
    /// goes instead:
    ///
    /// <list type="bullet">
    ///   <item><b>Spawn failure</b> (every recorded attempt shows the CLI
    ///   process never started): the CLI is unavailable, not the task. The
    ///   task is returned to <see cref="TaskStates.Ready"/> and the runner is
    ///   paused so it does not spin against a dead CLI - a human fixes the CLI
    ///   and resumes. (failed-pickup-elimination cause #6)</item>
    ///   <item><b>Task-shaped / zombie</b> (the CLI did spawn but produced no
    ///   output, or a session-less folder exhausted its resume budget):
    ///   terminal, but the task still deserves a person, so it is escalated to
    ///   <see cref="TaskStates.Escalated"/>. The folder leaves 3-progress so
    ///   the loop ends without a dead-letter lane, and the runner continues to
    ///   the next candidate. (failed-pickup-elimination causes #7, #8)</item>
    /// </list>
    ///
    /// <para>Single-state-machine authority: the move goes through
    /// <see cref="TaskStateMachine.MoveJob"/> (the by-id move the rest of this
    /// class already uses), not direct file IO. A
    /// <see cref="PickupFailureRecord"/> row is appended to
    /// <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c> for forensics and
    /// the per-slug counters are cleared.</para>
    ///
    /// <para>Returns <c>true</c> when this reroute paused the runner (the
    /// caller halts the surrounding iteration); <c>false</c> when the runner
    /// is still auto and the caller should continue to the next candidate.</para>
    /// </summary>
    private bool RerouteOverBudgetFolder(
        ProgressPickupCandidate candidate, string slug, int attempts,
        int? thresholdOverride = null, string? reasonOverride = null)
    {
        var now = DateTime.UtcNow;
        var threshold = thresholdOverride ?? PickupFailureThreshold;

        var jobIdBeforeMove = candidate.Info?.Id ?? slug;
        var cliTypeBeforeMove = candidate.Info?.CliType;
        var historySnapshot = _pickupAttempts.TryGetValue(slug, out var state)
            ? state.History.ToList()
            : new List<PickupAttemptDiagnostic>();

        // Classify the budget-exhaustion cause. A zombie escalation (passed an
        // explicit reasonOverride) is always task-shaped: a session-less folder
        // that cannot be resumed goes to a human. A spawn failure is read from
        // the attempt history - every recorded attempt shows the CLI never
        // started. Anything else is task-shaped (the CLI ran but stayed silent).
        var isZombieEscalation = reasonOverride != null;
        var spawnFailure = !isZombieEscalation
            && historySnapshot.Count > 0
            && historySnapshot.All(h => string.Equals(h.ExecutionStatus, SpawnFailedExecutionStatus, StringComparison.Ordinal));

        // The latest failure determines both the retry budget and escalation
        // category. Earlier generic pickup failures must not erase a later
        // busy-worktree diagnosis and make the visible threshold disagree with
        // the five-attempt decision used by the picker.
        var worktreeBlocked = !isZombieEscalation
            && IsWorktreeBlockedFailure(historySnapshot.LastOrDefault());
        var targetState = spawnFailure ? TaskStates.Ready : TaskStates.Escalated;
        var worktreeError = worktreeBlocked
            ? historySnapshot.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Error))?.Error
            : null;

        // Computed up front so the zombie escalation can carry the reason into
        // the decision journal + status.md stub written by the funnel.
        var reason = reasonOverride
            ?? (spawnFailure
                ? $"Auto-pickup could not start the {cliTypeBeforeMove ?? "agent"} CLI for '{slug}': {attempts} consecutive attempts (budget {threshold}) failed to spawn a process. The task is unchanged and was returned to {TaskStates.Ready}; the runner paused so it does not spin against an unavailable CLI."
                : worktreeBlocked
                    ? $"Worktree preparation for '{slug}' remained blocked after {attempts} attempts (budget {threshold}). {worktreeError ?? "The orphan worktree directory is still busy."} Automatic retry is paused; release the process holding this path, then re-queue the task."
                    : $"Auto-pickup ran the CLI for '{slug}' on {attempts} consecutive attempts (budget {threshold}) but the run never produced a CLI output line within {PickupOutputDeadlineSeconds}s. The task was escalated for a person to decide.");

        // Spawn failure returns the unchanged task to 2-ready (no verdict - it
        // is re-picked once the CLI is fixed). A task-shaped / zombie folder is
        // terminal: route it through the escalation funnel so 5e-escalated
        // always carries an Escalate verdict + status.md stub.
        var moveResult = spawnFailure
            ? _states.MoveJob(
                jobIdBeforeMove, TaskStates.Ready, Entry.Path,
                transitionCause: LaneChangeCauses.RunnerRequeue, transitionDetail: "spawn-failure")
            : _humanReviewEscalation.Escalate(
                jobIdBeforeMove, Entry.Path, ProjectName,
                worktreeBlocked ? HumanReviewEscalationCategories.WorktreeBlocked : HumanReviewEscalationCategories.PickupZombie, reason);
        if (moveResult.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "[taskboard] reroute of over-budget 3-progress folder {Slug} on {Project} to {Target} refused: {Status} {Message}; leaving in place for the operator",
                slug, ProjectName, targetState, moveResult.Status, moveResult.Message);
            // Reset the counters so we don't loop on the same failed move every
            // tick. The folder stays in 3-progress; the operator can intervene.
            _pickupAttempts.TryRemove(slug, out _);
            _zombieResumeFailures.TryRemove(slug, out _);
            return false;
        }

        // The escalation summary turns checklist rows from
        // orchestrator-follow-up.md into visible gate items. A category in
        // status.md explains the terminal, but it is not actionable on its own.
        // Persist the busy path as one open worktree-blocked item so an operator
        // sees exactly what must be released before re-queuing the card.
        if (worktreeBlocked && !string.IsNullOrWhiteSpace(moveResult.NewFolderPath))
            WriteWorktreeBlockedGateItem(moveResult.NewFolderPath!, reason, jobIdBeforeMove);

        var record = new PickupFailureRecord
        {
            At = now,
            Kind = spawnFailure ? PickupFailureKinds.RequeuedReady : PickupFailureKinds.EscalatedHumanReview,
            ProjectName = ProjectName,
            Slug = slug,
            JobId = jobIdBeforeMove,
            DestinationSlug = slug,
            Attempts = attempts,
            Threshold = threshold,
            OutputDeadlineSeconds = PickupOutputDeadlineSeconds,
            AttemptHistory = historySnapshot.Count == 0 ? null : historySnapshot,
            Reason = reason
        };
        _pickupFailures.Append(record);
        _logger.LogWarning(
            "[taskboard] rerouted over-budget 3-progress folder {Slug} on {Project} after {Attempts} attempts (threshold {Threshold}) -> {Target}",
            slug, ProjectName, attempts, threshold, targetState);

        // Chat-log note on the moved folder so the protocol pane surfaces why
        // the lane returned to one-task-per-project. Best-effort.
        try
        {
            var moved = _scanner.FindJob(jobIdBeforeMove, Entry.Path);
            if (moved != null)
            {
                var tag = spawnFailure ? "pickup-requeued" : "pickup-escalated";
                _chatLog.AppendSupervisor(moved, tag, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PickupFailureLog: chat-log append failed for {Slug}", slug);
        }

        _pickupAttempts.TryRemove(slug, out _);
        _zombieResumeFailures.TryRemove(slug, out _);

        if (spawnFailure)
        {
            // CLI unavailable. Feed the cross-slug infra breaker so its
            // distinct-slug accounting and infra-halts.jsonl audit still fire,
            // then pause the runner regardless of the breaker threshold: even a
            // single task burning its budget against an unspawnable CLI must not
            // loop 2-ready -> 3-progress -> fail -> 2-ready. If the breaker
            // already flipped the mode, return its verdict; otherwise pause here.
            var trippedByBreaker = TripInfraBreakerIfNeeded(cliTypeBeforeMove, slug, jobIdBeforeMove, now);
            if (trippedByBreaker) return true;
            if (IsAutoMode(_mode))
            {
                SetMode("manual",
                    $"auto-pickup paused: the {cliTypeBeforeMove ?? "agent"} CLI for '{slug}' could not be started after {attempts} attempts; the task waits in {TaskStates.Ready} until the CLI is fixed");
                // Arm the CLI-recovery auto-resume AFTER SetMode (which clears
                // the marker): once this CLI spawns again, the tick restores
                // the operator's DesiredRunnerMode instead of leaving the
                // project parked on manual until a human notices.
                _autoResumeCliAfterPause = cliTypeBeforeMove ?? CliTypes.Claude;
                _autoResumeNextProbeUtc = DateTime.UtcNow.AddSeconds(60);
                return true;
            }
            return false;
        }

        // Task-shaped / zombie escalation. The folder left 3-progress, so the
        // loop is already broken; the runner continues to the next 3-progress
        // folder (then 2-ready) so one stuck task does not stall the queue.
        return false;
    }

    private void WriteWorktreeBlockedGateItem(string folderPath, string reason, string jobId)
    {
        var path = Path.Combine(folderPath, "orchestrator-follow-up.md");
        var item = $"- [ ] {HumanReviewEscalationCategories.WorktreeBlocked}: {reason}";
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (existing.Contains(item, StringComparison.Ordinal)) return;

            var prefix = string.IsNullOrWhiteSpace(existing)
                ? "# Orchestrator follow-up" + Environment.NewLine + Environment.NewLine
                : existing.TrimEnd() + Environment.NewLine + Environment.NewLine;
            File.WriteAllText(path, prefix + item + Environment.NewLine);
            _logger.LogWarning(
                "[taskboard] worktree-blocked gate item recorded for {JobId} at {FollowUpPath}",
                jobId, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[taskboard] failed to record worktree-blocked gate item for {JobId} at {FollowUpPath}",
                jobId, path);
        }
    }

    /// <summary>
    /// Feed one spawn-failed dead-letter into
    /// <see cref="CrossSlugInfraCircuitBreaker"/> and apply the trip
    /// side-effects on the call that crosses the threshold: SetMode("manual")
    /// + plain-text supervisor chat note on the moved job folder. Returns
    /// <c>true</c> when this call just tripped the breaker.
    /// </summary>
    private bool TripInfraBreakerIfNeeded(string? cliType, string slug, string jobIdBeforeMove, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        TripOutcome? trip;
        try
        {
            trip = _infraBreaker.RecordSpawnFailedDeadLetter(ProjectName, cliType, slug, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrossSlugInfraCircuitBreaker: record-trip failed for {Project}/{Slug}", ProjectName, slug);
            return false;
        }
        if (trip == null) return false;

        _logger.LogWarning(
            "[taskboard] cross-slug infra breaker tripped for {Project} on {Cli} ({Slugs}); switching mode to manual",
            ProjectName, cliType, string.Join(", ", trip.Slugs));

        // Plain-text supervisor chat note on the freshly-moved dead-letter so
        // the project chat surface shows one banner-shaped entry (not per-tick
        // spam). The supervisor stream tag is the same channel ChatNoteHosted
        // and the per-slug breaker use, so the activity-log renders it as a
        // separate participant.
        try
        {
            var moved = _scanner.FindJob(jobIdBeforeMove, Entry.Path);
            if (moved != null)
            {
                _chatLog.AppendSupervisor(moved, "infra-halt", trip.BuildSupervisorChatMessage());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrossSlugInfraCircuitBreaker: chat-log append failed for {Slug}", slug);
        }

        // Mode flip last so any throw above does not leave us paused
        // without the banner. SetMode routes through OnModePersist so the
        // backend-restart-safe persistence in ProjectSettingsService fires.
        //
        // Halt-iteration signal: only true when we actually transitioned
        // out of an auto mode. If the runner was already manual or paused
        // (e.g. test reflection invoking the picker against a paused
        // runner), the breaker still records its row and notes the chat,
        // but does not block the caller's iteration - the auto-pickup
        // cascade we're protecting against can only happen when auto
        // mode is what's driving the picker.
        if (IsAutoMode(_mode))
        {
            SetMode("manual",
                $"pickup paused: infra breaker, {trip.Slugs.Count} failures cliType={cliType} at {trip.TrippedAt:O}; tasks={string.Join(", ", trip.Slugs)}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Records a per-attempt diagnostic against the per-slug attempt counter
    /// for an autopickup against a 3-progress folder. A "silent" attempt
    /// (zero output lines) increments the counter; a productive attempt
    /// (any output streamed) resets the counter to zero so a flaky single
    /// run does not wedge a productive folder. Called from
    /// <see cref="OnCliFinishedAsync"/>.
    /// </summary>
    /// <summary>
    /// True when a run ended because something deliberately killed it rather
    /// than because the task failed: a host shutdown / backend restart, a user
    /// pause, a follow-up pause-and-send, the silence watchdog, or a run
    /// cancellation. <see cref="RunStatusClassifier"/> maps all of those to
    /// <see cref="RunStatuses.Stopped"/>. Such runs are interruptions, not
    /// failures, and must stay neutral for the auto-pickup circuit-breakers.
    /// </summary>
    private static bool WasDeliberatelyStopped(string? executionStatus)
        => string.Equals(executionStatus, RunStatuses.Stopped, StringComparison.OrdinalIgnoreCase);

    private void RecordPickupAttemptResult(string slug, int outputLines, double durationSeconds, string? executionStatus)
    {
        if (outputLines > 0)
        {
            // Productive attempt: drop the slug from the counter so the next
            // failure starts a fresh streak instead of inheriting an old one.
            _pickupAttempts.TryRemove(slug, out _);
            // Cross-slug infra breaker reset: ≥ 1 streamed CLI output line
            // means the infra is healthy again for this CLI. Clear the
            // distinct-slug counter so a future single bad job does not
            // ride on top of a stale cascade.
            try { _infraBreaker.OnProductivePickup(ProjectName, _activeCliType); }
            catch (Exception ex) { _logger.LogDebug(ex, "CrossSlugInfraCircuitBreaker reset failed for {Project}", ProjectName); }
            return;
        }

        // A deliberately-stopped run (host shutdown / backend restart, user
        // pause, watchdog, cancellation) produced no output only because it was
        // interrupted, not because the pickup is broken. Do NOT feed it to the
        // per-slug silent-attempt counter that dead-letters a folder after
        // PickupFailureThreshold silent runs - that would punish a job for a
        // restart it had no part in.
        if (WasDeliberatelyStopped(executionStatus))
        {
            _logger.LogInformation(
                "[taskboard] silent auto-pickup run for {Slug} was a deliberate stop ('{Status}'); not counting toward the per-slug dead-letter budget",
                slug, executionStatus);
            return;
        }

        var state = _pickupAttempts.GetOrAdd(slug, _ => new PickupAttemptState());
        state.Count++;
        state.History.Enqueue(new PickupAttemptDiagnostic
        {
            At = DateTime.UtcNow,
            DurationSeconds = durationSeconds,
            OutputLines = outputLines,
            ExecutionStatus = executionStatus
        });
        // Bound the history at the threshold so the JSONL row stays compact
        // and the in-memory state does not grow unboundedly when a move keeps
        // refusing.
        while (state.History.Count > PickupFailureThreshold) state.History.Dequeue();
    }

    /// <summary>
    /// Pick&lt;-&gt;Start atomicity (ASS-1655). A run that moved its task into
    /// <c>3-progress</c> but whose CLI process never started must not leave the
    /// task stranded there: a session-less folder in <c>3-progress</c> with no
    /// running run and a freed slot is exactly the zombie that lets the runner
    /// pick the next task and end up with two folders in <c>3-progress</c> at
    /// <c>maxParallelism=1</c>. This rolls the lane move straight back to
    /// <c>2-ready</c> the moment the start fails, instead of waiting for the
    /// 60-minute stale-progress sweep (<see cref="StaleProgressArchiver"/>) or the next
    /// pickup tick to notice.
    ///
    /// <para>Bounded by the existing per-slug spawn budget so a persistently
    /// un-spawnable task cannot tight-loop the requeue: once it has burned
    /// <see cref="PickupFailureThreshold"/> spawn attempts the over-budget
    /// reroute (<see cref="RerouteOverBudgetFolder"/>) returns it to
    /// <c>2-ready</c> AND pauses the runner so it stops spinning against an
    /// unavailable CLI. Below the budget the requeue is spaced with the same
    /// exponential <see cref="RapidCrashBreaker"/> backoff the rapid-crash
    /// finish path arms.</para>
    /// </summary>
    private void RevertFailedStartFromProgress(string jobId, TaskInfo info, RunIntent intent)
    {
        var attempts = GetPickupAttempts(jobId);

        // Over budget: the folder is still in 3-progress here, which is the
        // precondition the reroute expects. For a spawn failure it returns the
        // unchanged task to 2-ready and pauses the runner (CLI unavailable);
        // there is nothing more to do.
        var threshold = GetPickupFailureThreshold(jobId);
        if (intent == RunIntent.AutoPickup && attempts >= threshold)
        {
            var candidate = new ProgressPickupCandidate(info.FolderPath, jobId, info, DateTime.UtcNow);
            RerouteOverBudgetFolder(candidate, jobId, attempts, thresholdOverride: threshold);
            return;
        }

        var move = _states.MoveJob(
            jobId, TaskStates.Ready, Entry.Path,
            transitionCause: LaneChangeCauses.RunnerRequeue, transitionDetail: "pick-reverted-no-run");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "[taskboard] could not revert failed-start job {JobId} on {Project} from 3-progress to 2-ready: {Status} {Message}; folder left for the over-budget reroute / stale-progress sweep",
                jobId, ProjectName, move.Status, move.Message);
            return;
        }

        // Space the next pickup so a transient-but-repeating spawn failure cannot
        // tight-loop the requeue while it is still under the per-slug budget.
        if (intent == RunIntent.AutoPickup)
            _rapidCrashBackoffUntil[jobId] = DateTime.UtcNow + RapidCrashBreaker.Backoff(Math.Max(1, attempts));

        var now = DateTime.UtcNow;
        var logDecision = TakeRevertLogDecision(jobId, now);
        if (!logDecision.Emit) return;
        var suppressed = logDecision.Suppressed;

        _logger.LogInformation(
            "[taskboard] pick-reverted-no-run job={JobId} project={Project} attempts={Attempts} suppressedSinceLast={Suppressed} nextRetryNotBefore={NextRetryNotBefore}",
            jobId, ProjectName, attempts, suppressed,
            _rapidCrashBackoffUntil.TryGetValue(jobId, out var retryAt) ? retryAt : null);

        try
        {
            var moved = _scanner.FindJob(jobId, Entry.Path);
            if (moved != null)
                _chatLog.AppendSupervisor(
                    moved,
                    "pick-reverted-no-run",
                    "The task was picked into 3-progress but the agent process never started. " +
                    "It was returned to 2-ready immediately so it is not stranded as a zombie in 3-progress; " +
                    $"the orchestrator will retry it. Repeated identical notices suppressed since the prior notice: {suppressed}.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[taskboard] chat-log append failed for reverted pick {JobId}", jobId);
        }
    }

    private (bool Emit, int Suppressed) TakeRevertLogDecision(string jobId, DateTime nowUtc)
    {
        var logState = _revertLogStates.GetOrAdd(jobId, _ => new RevertLogState());
        lock (logState)
        {
            if (logState.LastEmittedUtc != default
                && nowUtc - logState.LastEmittedUtc < RevertLogInterval)
            {
                logState.Suppressed++;
                return (false, logState.Suppressed);
            }

            var suppressed = logState.Suppressed;
            logState.Suppressed = 0;
            logState.LastEmittedUtc = nowUtc;
            return (true, suppressed);
        }
    }

    /// <summary>Test seam for the ten-minute per-task revert notice limiter.</summary>
    internal (bool Emit, int Suppressed) TakeRevertLogDecisionForTest(string jobId, DateTime nowUtc)
        => TakeRevertLogDecision(jobId, nowUtc);

    /// <summary>
    /// Pure decision: should the just-finished run trigger a session-chain
    /// recovery marker? True when the run was a <c>--resume</c> attempt
    /// (planner produced a resume plan with a real session id) AND the
    /// CLI did not capture a usable session id back. Pulled out as a
    /// helper so the field-snapshot pattern protecting it from
    /// concurrency races (the prior bug class that drove the 31-run
    /// arhciv loop) is directly testable.
    /// </summary>
    internal static bool ShouldMarkSessionChainRecovery(RunPlan? planSnapshot) =>
        planSnapshot?.ResumeFlag == true
        && !string.IsNullOrWhiteSpace(planSnapshot.SessionToResume);

    private List<string> GetQueuedJobIds()
    {
        // This ordered list is the single queue-position source used by the
        // status API and TaskLiveStatusProjection. Reuse the pickup candidate
        // policy so a card held behind dependsOn, intake, crash-backoff, agent,
        // or task-kind gates never occupies a displayed runner-slot position.
        return ListReadyPickupCandidatesInDisplayedOrder()
            .Select(job => job.Id)
            .ToList();
    }

    private void NotifyStatus()
    {
        // Defense-in-depth: NotifyStatus is called from many points inside
        // the pickup tick. A throwing subscriber would escape the tick,
        // exit ExecuteAsync, and stop the host. The TaskRunnerService
        // wrapper around the subscriber chain is the primary guard; this
        // catch is the second line so any future direct subscriber added
        // to ProjectRunner.OnStatusChanged stays contained.
        try { OnStatusChanged?.Invoke(GetStatus()); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnStatusChanged subscriber threw for {Project}", ProjectName); }
    }

    /// <summary>
    /// HEAD-SHA capture wrapper. Swallows git failures - missing repo,
    /// missing tool, transient errors - so a flaky environment can't
    /// take down a run. The persisted SHA stays null on failure and the
    /// commits endpoint falls back to the wall-clock window. Worktree runs
    /// capture the isolated task branch here; successful integration later
    /// rewrites the event to the exact integration-branch range that landed.
    /// </summary>
    private string? SafeGetHeadSha(string jobId)
    {
        try
        {
            if (_activeRuns.Get(jobId) is { IsWorktreeRun: true, WorktreePath: { Length: > 0 } worktreePath })
                return _git.ReadHeadShaAt(worktreePath);
            return _git.GetHeadSha(jobId, Entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HEAD SHA capture failed for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Per-tick continuous decision review. Scans the active job's live
    /// CLI output buffer for the latest unresolved interruptive sentinel
    /// (<c>[[TASK_NEEDS_INPUT]]</c>, <c>[[TASK_BLOCKED]]</c>) and updates
    /// <see cref="_activePendingDecision"/>. Cheap: one regex pass over
    /// the buffer's tail. Same-state ticks are silent. See ADR-0027.
    /// </summary>
    private void TickPendingDecision()
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || cliType == null)
        {
            ClearPendingDecisionIfPresent();
            return;
        }

        ICliExecutionService cli;
        try { cli = _router.Get(cliType); }
        catch
        {
            ClearPendingDecisionIfPresent();
            return;
        }

        var jobKey = TaskIdentity.CreateKey(Entry.Path, jobId);
        List<CliOutputLine> output;
        try { output = cli.GetOutput(jobKey); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pending-decision scan: GetOutput failed for {JobId}", jobId);
            return;
        }

        var hit = PendingDecisionScanner.Scan(output);
        lock (_pendingDecisionLock)
        {
            if (hit == null)
            {
                if (_activePendingDecision != null)
                {
                    _logger.LogInformation(
                        "[taskboard] pending decision cleared for {JobId} on {Project}",
                        jobId, ProjectName);
                }
                _activePendingDecision = null;
                return;
            }

            // Same job, same line -> already known, nothing to log.
            if (_activePendingDecision != null
                && _activePendingDecision.JobId == jobId
                && _activePendingDecision.Decision.LineIndex == hit.LineIndex
                && _activePendingDecision.Decision.Kind == hit.Kind)
            {
                return;
            }

            string? title = null;
            try { title = _scanner.FindJob(jobId, Entry.Path)?.Title; } catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectRunner: best-effort"); /* best-effort */ }
            _activePendingDecision = new PendingDecisionEntry(jobId, title ?? jobId, hit);
            _logger.LogInformation(
                "[taskboard] pending decision detected for {JobId} on {Project}: kind={Kind} reason={Reason}",
                jobId, ProjectName, hit.Kind, hit.Reason ?? "<none>");
        }
    }

    private void ClearPendingDecisionIfPresent()
    {
        lock (_pendingDecisionLock)
        {
            if (_activePendingDecision == null) return;
            _activePendingDecision = null;
        }
    }

    /// <summary>
    /// Returns the active unresolved decision sentinel(s) for this project.
    /// At most one entry today (only one job runs in 3-progress per project,
    /// per ADR-0001), but the surface is shaped as a list so a future
    /// orchestrator advisory can join the same banner without an API break.
    /// </summary>
    public IReadOnlyList<PendingDecisionEntry> GetPendingDecisions()
    {
        lock (_pendingDecisionLock)
        {
            return _activePendingDecision == null
                ? Array.Empty<PendingDecisionEntry>()
                : new[] { _activePendingDecision };
        }
    }
}

/// <summary>
/// One pending decision currently surfaced on a project. The job-level
/// metadata (id, title) is captured at detection time so the read API can
/// shape a banner without a follow-up scanner call.
/// </summary>
public sealed record PendingDecisionEntry(
    string JobId,
    string Title,
    PendingDecision Decision);

/// <summary>
/// Outcome of <see cref="ProjectRunner.RequestModeChange"/>. <c>Applied</c>
/// means the live mode moved now; <c>Deferred</c> means the new mode is
/// queued and will land when the request-time active task set clears;
/// <c>Invalid</c> means the requested mode value was rejected before
/// it could be applied (the endpoint turns this into a 400).
/// </summary>
public enum ModeChangeOutcome
{
    Applied,
    Deferred,
    Invalid
}

/// <summary>
/// Typed return for <see cref="ProjectRunner.RequestModeChange"/>. The same
/// shape is also returned by <see cref="AgentStudio.Runner.TaskRunnerService.RequestModeChange"/>
/// so the endpoint can produce its response body from a single record.
/// <para>
/// <see cref="CurrentMode"/> is whatever <c>_mode</c> reads as <i>after</i>
/// the call: for <see cref="ModeChangeOutcome.Applied"/> this is the new mode;
/// for <see cref="ModeChangeOutcome.Deferred"/> this is the still-live previous
/// mode (the deferred value is in <see cref="PendingMode"/>).
/// </para>
/// </summary>
public sealed record ModeChangeResult(
    ModeChangeOutcome Outcome,
    string CurrentMode,
    string? PendingMode,
    string? WillApplyAfterJobId);
