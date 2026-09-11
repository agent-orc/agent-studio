using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Background loop that reads the <c>4-auto-review</c> lane (ADR-0025) and
/// acts on tasks that ended in <c>[[TASK_NEEDS_INPUT]]</c>,
/// <c>[[TASK_NOOP]]</c>, or <c>[[TASK_BLOCKED]]</c>. Reissue moves the
/// task to <c>2-ready</c> at order 0 (next pickup) so the runner picks
/// it ahead of fresh queued tasks but never displaces a currently
/// running job - the race where the runner saw an empty
/// <c>3-progress</c> mid-verdict and picked the next ready job is gone.
/// Accept-as-done moves the task forward to <c>5-human-review</c> (the
/// user always gets the final say on completion); escalate moves it to
/// <c>5e-escalated</c> with a <c>[supervisor]</c> chat-note explaining why
/// the orchestrator could not decide.
///
/// <para>
/// The user framing: when a job lands in 4-review with an unanswered
/// decision request, the orchestrator should read the task, the recent
/// activity, the roadmap, prior decisions, and either answer the
/// question (reissuing the task back to 3-progress with the orchestrator's
/// reply) or escalate to the user with a clear reason. A configurable
/// review-decision CLI call (<c>ReviewDecisionOrchestrator:Cli</c> /
/// <c>ReviewDecisionOrchestrator:Model</c>, Codex with <c>gpt-5.4-mini</c>
/// by default) handles unresolved questions and is expected to respond with
/// a single <c>[[ORCHESTRATOR_DECISION]]</c> sentinel. DONE reviews use
/// independently routed aspect reviewers followed by deterministic gates and
/// aggregation.
/// </para>
///
/// <para>
/// NOOP is treated as a recoverable signal, not a terminal state: a job
/// that ends in <c>[[TASK_NOOP]]</c> with a real prompt body is reissued
/// once with a sharpened framing (reusing
/// <see cref="RunOutcomePolicy.BuildReissueFollowupPrompt"/>); a NOOP on
/// an empty / placeholder prompt escalates to a human-decision intake;
/// repeated NOOPs past the shared reissue budget escalate too. The
/// branch is deterministic (no fast-model CLI call) so it does not
/// charge the per-hour rate budget.
/// </para>
///
/// <para>
/// BLOCKED is also handled deterministically: the agent has explicitly
/// declared that it cannot proceed, so the orchestrator does not retry
/// silently. Every BLOCKED in 4-review is escalated to a
/// <c>human-decision-needed-*</c> intake under <c>1-preparation</c> and
/// surfaced via the chat log + Agent Message Bus so the user sees one
/// "this card needs your attention" entry on the workspace banner.
/// </para>

/// <para>
/// On boot the service performs one explicit backfill sweep across every
/// watched project's 4-review lane before the recurring tick loop
/// starts. This catches jobs that landed in 4-review while the backend
/// was offline (or before this service shipped) and brings them into the
/// orchestrator-review pipeline immediately, rather than waiting for the
/// next scheduled tick.
/// </para>
///
/// <para>
/// Decisions are append-only: the source of truth for the chain is the
/// per-project journal at
/// <c>{workspace}/logs/decisions/{project}.jsonl</c>, plus the
/// orchestrator chat log written into the job's <c>cli-output.log</c>.
/// The single-state-machine rule still applies: lane transitions go
/// through <see cref="TaskTransitionService"/>.
/// </para>
///
/// <para>
/// Off by default. Enable via <c>ReviewDecisionOrchestrator:Enabled = true</c>.
/// Rate-limit reuses the same shape as
/// <see cref="AgentStudio.Supervisor.SoftReasoningHostedService"/>
/// (calls per hour, default 30).
/// </para>
/// </summary>
public sealed class ReviewDecisionOrchestrator : BackgroundService
{
    /// <summary>
    /// Maximum number of automatic reissues the review-decision tick will
    /// drive against a single job before escalating. Counts every Reissue
    /// record in the per-project decision journal for that job, so the
    /// budget is shared between NEEDS_INPUT-driven and NOOP-driven
    /// reissues (the agent only sees one stream of follow-ups).
    /// </summary>
    public const int MaxAutoReissueAttempts = 2;

    /// <summary>
    /// Default cap on concurrently-running DONE aspect-reviews in the read-only
    /// parallel pool (ADR-0052). Mirrors the in-aspect WhenAll cap of 4. Override
    /// with <c>ReviewDecisionOrchestrator:MaxParallelReviews</c>.
    /// </summary>
    public const int DefaultMaxParallelReviews = 4;

    /// <summary>
    /// Default per-hour budget for orchestrator LLM calls (decisions and DONE
    /// aspect reviews share it). The budget is a runaway backstop, not a pacing
    /// device: at ~5 calls per card, 30/h throttled healthy remote delivery
    /// waves to ~6 cards/h and became the binding constraint of the
    /// 4-auto-review drain. 120/h keeps the loop-protection while letting a
    /// full wave pass. Override with <c>ReviewDecisionOrchestrator:CallsPerHour</c>.
    /// </summary>
    public const int DefaultCallsPerHour = 120;

    /// <summary>
    /// A boot repair must never race a newly-created card. The repair also
    /// requires durable run provenance, but this age floor protects the short
    /// interval between lane placement and persistence of the first run event.
    /// </summary>
    internal static readonly TimeSpan VerdictlessBackfillMinimumAge = TimeSpan.FromMinutes(10);

    private readonly TaskScannerService _scanner;
    private readonly ITaskAccess _taskAccess;
    private readonly OrchestratorChatLog _chatLog;
    private readonly RuntimePromptService _prompts;
    private readonly AspectRunnerService _aspectRunner;
    private readonly AutoReviewStatusSnapshot _statusSnapshot;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewDecisionOrchestrator> _logger;
    private readonly TaskSessionLog? _sessions;
    private readonly GitService? _git;
    private readonly AttemptAuthorityService? _attemptAuthority;
    private readonly DossierMaintenanceService? _dossierMaintenance;

    /// <summary>
    /// Default aspect runner ids when <c>ReviewDecisionOrchestrator:AspectRunners</c>
    /// is not configured. ADR-0025 slice 1 ships these four; missing
    /// config falls back to default-on rather than no-aspects.
    /// </summary>
    private static readonly string[] DefaultAspectRunners =
    {
        "requirement-fit",
        "code-quality",
        "documentation-impact",
        "tests-and-evidence"
    };

    private readonly Queue<DateTime> _callTimestamps = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    // Guards _callTimestamps: the read-only review pool processes several DONE
    // tasks concurrently, each charging the per-hour rate budget, so the
    // sliding-window queue is mutated from multiple threads.
    private readonly object _callTimestampsLock = new();

    /// <summary>
    /// Serialises shared-checkout mutations - the lane folder moves that flow
    /// through <see cref="GuardedMoveJob"/> - so the bounded-parallel
    /// post-processing worker (<see cref="AutoReviewPostProcessingWorker"/>) and the
    /// backstop sweep never mutate the working tree at the same time. Git access on
    /// this path is otherwise read-only plumbing (diffs / SHA ranges), which is
    /// concurrency-safe, so only the moves take the gate. The build-test-gate keeps
    /// its own machine lock and is not funnelled through here.
    /// </summary>
    private readonly SemaphoreSlim _postProcessingGitGate = new(1, 1);

    /// <summary>
    /// Cards whose post-processing is currently in flight, keyed by job folder path.
    /// Both the bounded-parallel <see cref="AutoReviewPostProcessingWorker"/> and the
    /// backstop sweep consult this set so the same card is never post-processed twice
    /// concurrently ("gleiche Karte nie doppelt in Flug"). Card processings for
    /// distinct cards run in parallel; a card already in flight is skipped.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _cardsInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// CLI runner injection point. Tests substitute a deterministic stub.
    /// Args: cliBinary, model, prompt, timeout, ct → captured stdout/stderr.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;

    internal Func<int, TimeSpan> BuildTestGateRetryBackoff { get; set; }
        = PostProcessingOutcomeTaxonomy.RetryBackoff;

    private readonly AgentStudio.AdHoc.AdHocUsageRecorder? _usage;
    private readonly AgentStudio.Cli.CliOneShotRegistry? _oneShotRegistry;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly ILintScssRunner? _lintScssRunner;
    private readonly AgentStudio.Pipeline.IQualityAnalysisStepRunner? _qualityAnalysisRunner;
    private readonly IBuildTestGateRunner? _buildTestGateRunner;
    private readonly WikiMaintenancePostStepRunner? _wikiMaintenance;
    private readonly WikiLearningsPostStepRunner? _wikiLearnings;
    // The opt-in AGENTS.md <-> wiki designated-topics sync (AGT-1782). Optional so
    // the many stand-alone test constructors keep compiling; production DI supplies
    // the registered singleton.
    private readonly AgentsWikiSyncPostStepRunner? _agentsWikiSync;
    private readonly WikiTaskCrossReferenceService? _wikiTaskCrossReferences;
    private readonly RegressionRadarService? _regressionRadar;
    // The opt-in task-spawner post-step (AGT-2028). Optional so the many
    // stand-alone test constructors keep compiling; production DI supplies it.
    private readonly TaskSpawnerPostStepRunner? _taskSpawner;

    /// <summary>
    /// Regression-radar analysis injection point. Tests substitute a
    /// deterministic stub so the post-step can be exercised without git or
    /// a session timeline. Args: jobId, watchPath -> classification result.
    /// Null in production, where the post-step calls the injected
    /// <see cref="RegressionRadarService"/> directly.
    /// </summary>
    public Func<string, string?, RegressionRadarResult>? RegressionRadarAnalyzer { get; set; }
    private readonly TimelineLog? _timeline;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly PipelineStepEconomyAdvisor? _pipelineStepEconomy;
    private readonly WorkspaceArtifactCommitService? _workspaceArtifactCommits;
    // The automatic code-review quality-grade step (ASS-1657). Optional so the
    // many stand-alone test constructors that wire the orchestrator without it
    // keep compiling; production DI always supplies the registered singleton.
    private readonly AgentStudio.Review.CodeReviewStepService? _codeReviewStep;
    // The system-escalation funnel. Used here only for the boot-time repair of
    // legacy 5-human-review cards that carry no verdict (RecordVerdictAndStatus,
    // no move). Optional so test fixtures that do not exercise the backfill keep
    // their existing constructor; production DI always supplies it.
    private readonly HumanReviewEscalation? _humanReviewEscalation;
    private readonly FailureInterventionService? _failureInterventions;

    /// <summary>
    /// Stable prefix on the <c>Reason</c> field of every
    /// <see cref="ReviewDecisionRecord"/> that the lint-scss post-step
    /// emitted. Used for the infinite-spin guard (a prior reissue with
    /// this prefix means the agent already had one chance to clear the
    /// gate; the next failure escalates for operator intervention).
    /// </summary>
    internal const string LintScssReissueReasonPrefix = "lint-scss reissue: ";
    internal const string BuildTestGateReissueReasonPrefix = "build-test-gate reissue: ";
    internal const string BuildTestGateInfrastructureReasonPrefix = "build-test-gate review infrastructure: ";
    internal const string OperatorRequeueFreshAssessmentReason = "operator-requeue-fresh-assessment";

    /// <summary>
    /// Stable prefix on the <c>Reason</c> field of the <see cref="ReviewDecisionKind.Escalate"/>
    /// record the aspect-verdict InfraCrash path emits (AGT-2021). Lets a reader
    /// tell an environmental infra crash apart from a real work-quality escalation.
    /// </summary>
    internal const string AspectInfraCrashReasonPrefix = "aspect-verdict infra crash: ";

    public ReviewDecisionOrchestrator(
        TaskScannerService scanner,
        TaskStateMachine stateMachine,
        ITaskAccess taskAccess,
        OrchestratorChatLog chatLog,
        RuntimePromptService prompts,
        AspectRunnerService aspectRunner,
        AutoReviewStatusSnapshot statusSnapshot,
        IConfiguration configuration,
        ILogger<ReviewDecisionOrchestrator> logger,
        AgentStudio.AdHoc.AdHocUsageRecorder? usage = null,
        AgentStudio.Cli.CliOneShotRegistry? oneShotRegistry = null,
        TaskSessionLog? sessions = null,
        GitService? git = null,
        PipelineExecutionLog? pipelineLog = null,
        ILintScssRunner? lintScssRunner = null,
        IBuildTestGateRunner? buildTestGateRunner = null,
        WikiMaintenancePostStepRunner? wikiMaintenance = null,
        WikiLearningsPostStepRunner? wikiLearnings = null,
        TimelineLog? timeline = null,
        ProjectSettingsService? projectSettings = null,
        HumanReviewEscalation? humanReviewEscalation = null,
        RegressionRadarService? regressionRadar = null,
        WorkspaceArtifactCommitService? workspaceArtifactCommits = null,
        AgentStudio.Review.CodeReviewStepService? codeReviewStep = null,
        TaskSpawnerPostStepRunner? taskSpawner = null,
        WikiTaskCrossReferenceService? wikiTaskCrossReferences = null,
        AgentsWikiSyncPostStepRunner? agentsWikiSync = null,
        PipelineStepEconomyAdvisor? pipelineStepEconomy = null,
        AttemptAuthorityService? attemptAuthority = null,
        DossierMaintenanceService? dossierMaintenance = null,
        AgentStudio.Pipeline.IQualityAnalysisStepRunner? qualityAnalysisRunner = null,
        FailureInterventionService? failureInterventions = null)
    {
        _scanner = scanner;
        _taskAccess = taskAccess;
        _chatLog = chatLog;
        _prompts = prompts;
        _aspectRunner = aspectRunner;
        _statusSnapshot = statusSnapshot;
        _configuration = configuration;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _pipelineStepEconomy = pipelineStepEconomy;
        _sessions = sessions;
        _git = git;
        _pipelineLog = pipelineLog;
        _lintScssRunner = lintScssRunner;
        _buildTestGateRunner = buildTestGateRunner;
        _wikiMaintenance = wikiMaintenance;
        _wikiLearnings = wikiLearnings;
        _timeline = timeline;
        _projectSettings = projectSettings;
        _humanReviewEscalation = humanReviewEscalation;
        _regressionRadar = regressionRadar;
        _workspaceArtifactCommits = workspaceArtifactCommits;
        _codeReviewStep = codeReviewStep;
        _taskSpawner = taskSpawner;
        _wikiTaskCrossReferences = wikiTaskCrossReferences;
        _agentsWikiSync = agentsWikiSync;
        _attemptAuthority = attemptAuthority;
        _dossierMaintenance = dossierMaintenance;
        _qualityAnalysisRunner = qualityAnalysisRunner;
        _failureInterventions = failureInterventions;

        _statusSnapshot.ConfigureEscalationRateAlert(
            _configuration.GetValue(
                "ReviewDecisionOrchestrator:EscalationRateAlertThreshold",
                AutoReviewStatusSnapshot.DefaultEscalationRateAlertThreshold),
            _configuration.GetValue(
                "ReviewDecisionOrchestrator:EscalationRateMinimumDecisions",
                AutoReviewStatusSnapshot.DefaultEscalationRateMinimumDecisions));

        // Production calls route through ICliOneShot at the call site so the
        // task identity can accompany quota telemetry. CliRunner remains the
        // deterministic seam for tests that construct this service without DI.
    }

    private async Task<string> RunViaOneShotAsync(
        string cli,
        string model,
        string prompt,
        TimeSpan timeout,
        TaskInfo task,
        string project,
        CancellationToken ct)
    {
        var cliType = NormalizeReviewCliType(cli);
        var oneShot = _oneShotRegistry?.Get(cliType);
        if (oneShot == null) return await DefaultRunCliAsync(cli, model, prompt, timeout, ct);

        var result = await oneShot.RunAsync(new AgentStudio.Cli.CliOneShotRequest(
            CliType: cliType,
            Model: model,
            Prompt: prompt)
        {
            Timeout = timeout,
            Source = AgentStudio.Shared.AdHocUsageSources.ReviewDecision,
            RecordUsage = false,
            Project = project,
            JobId = task.Id,
            JobFolderPath = task.FolderPath,
            StepId = AgentStudio.Pipeline.PipelineCatalogue.OrchestratorDecisionStepId,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Review-decision CLI call failed: exit={ExitCode} duration={Duration}ms error={Error}",
                result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return AgentStudio.Cli.CliOneShotCompatibility.ToClaudeResultEnvelope(result, model);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; ReviewDecisionOrchestrator idle.");
            return;
        }

        var intervalSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:IntervalSeconds", 30);
        var bootDelaySeconds = _configuration.GetValue("ReviewDecisionOrchestrator:BootDelaySeconds", 5);

        // Brief warm-up so the scanner has indexed the lanes; then the
        // explicit boot sweep picks up anything that landed in 4-review
        // while the backend was offline (or before this service existed)
        // before the recurring loop kicks in.
        if (bootDelaySeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(bootDelaySeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        // Pure data-repair, run on EVERY boot regardless of the Enabled flag:
        // give an old 5-human-review card with durable run provenance but no
        // orchestrator verdict a retroactive Escalate verdict + status.md stub
        // so the board can explain it. Age, provenance, terminal-lane, and
        // latest-operator-move guards keep accepted or explicitly placed cards
        // out of this legacy migration.
        try { BackfillVerdictlessHumanReview(workspace!, stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator: verdict-less human-review backfill failed"); }

        if (_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
        {
            try
            {
                _logger.LogInformation("ReviewDecisionOrchestrator boot sweep starting (one-shot full backfill).");
                await TickOnceAsync(workspace!, stoppingToken);
                // One-shot migration for the "concern tags bleiben kleben" bug:
                // strip stale concern chips from already-parked accept cards in
                // 4-auto-review / 5-human-review that the merge-only path left
                // behind before the reconcile fix shipped. Idempotent.
                BackfillStaleConcernTags(workspace!, stoppingToken);
                // One-shot migration for the "Erfolg sieht aus wie classifier-unknown"
                // bug (ASS-775): clear a stale Warn-class outcome chip from already-
                // accepted 5-human-review cards whose accept note never reached the
                // log (6-completed cards are already reconciled by the scanner).
                BackfillStaleAcceptedOutcomeIssues(workspace!, stoppingToken);
                _logger.LogInformation("ReviewDecisionOrchestrator boot sweep complete; entering recurring tick loop.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator boot sweep failed"); }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            intervalSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:IntervalSeconds", 30);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
                    await TickOnceAsync(workspace!, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator tick failed"); }
        }
    }

    /// <summary>
    /// One tick: scan every watched project's 4-review lane, find jobs
    /// with an unresolved <c>[[TASK_NEEDS_INPUT]]</c> or
    /// <c>[[TASK_NOOP]]</c>, and route each through the decision flow.
    /// Public so tests can drive it directly against a temporary
    /// workspace.
    /// </summary>
    public async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct);
        try
        {
            await TickOnceCoreAsync(workspace, ct);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task TickOnceCoreAsync(string workspace, CancellationToken ct)
    {
        var maxPerHour = _configuration.GetValue("ReviewDecisionOrchestrator:CallsPerHour", DefaultCallsPerHour);
        var cliBinary = _configuration.GetValue("ReviewDecisionOrchestrator:Cli", CliTypes.Codex);
        var model = _configuration.GetValue("ReviewDecisionOrchestrator:Model", ModelIds.Gpt54Mini);
        var aspectModel = _configuration.GetValue("ReviewDecisionOrchestrator:AspectModel", model);
        var aspectTimeoutSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:AspectTimeoutSeconds", 60);
        var maxReissues = _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);
        var maxParallelReviews = ParallelSlotPolicy.ClampMax(
            _configuration.GetValue("ReviewDecisionOrchestrator:MaxParallelReviews", DefaultMaxParallelReviews));
        var aspects = ResolveAspectRunners();

        _statusSnapshot.BeginTick();
        try
        {
            // DONE aspect-reviews write no repo files, so they are not bound to
            // the sequential code-seat: collect them here and run them through a
            // bounded read-only parallel pool below (Req 1 / ADR-0052). Every
            // other kind stays sequential - they are cheap/deterministic and
            // some mutate shared lane state, so parallelising them buys nothing.
            var doneReviews = new List<(WatchPathEntry Entry, PendingDecision Pending)>();

            foreach (var entry in _scanner.GetWatchPaths())
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!Directory.Exists(entry.Path)) continue;

                foreach (var pending in EnumeratePending(workspace, entry))
                {
                    if (ct.IsCancellationRequested) return;
                    _statusSnapshot.RecordPending();

                    // The bounded-parallel post-processing worker may already be
                    // driving this card; skip it here so a card is never in flight
                    // twice at once. The worker holds the authoritative slot via
                    // _cardsInFlight; this is a best-effort skip on the backstop path.
                    if (_cardsInFlight.ContainsKey(pending.Job.FolderPath))
                        continue;

                    if (pending.Kind == ReviewSignalKind.Done)
                    {
                        if (TaskModes.IsReportOnly(pending.Job.Mode))
                        {
                            _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);
                            try
                            {
                                await ProcessReportOnlyDoneAsync(
                                    workspace, entry, pending, cliBinary, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "ReviewDecisionOrchestrator failed to process lightweight report {Project}/{JobId}",
                                    entry.Name, pending.Job.Id);
                            }
                            finally
                            {
                                _statusSnapshot.ClearCurrent(entry.Name, pending.Job.Id);
                            }
                            continue;
                        }

                        // Defer to the read-only parallel pool. The cheap
                        // enabled-checks happen here so a disabled/no-aspect
                        // project never occupies a slot.
                        if (aspects.Count == 0 ||
                            !_configuration.GetValue("ReviewDecisionOrchestrator:AspectsEnabled", true))
                        {
                            continue;
                        }
                        doneReviews.Add((entry, pending));
                        continue;
                    }

                    _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);

                    try
                    {
                        if (pending.Kind == ReviewSignalKind.StaleWithVerdict)
                        {
                            // Move-after-verdict backfill: a recorded verdict
                            // whose lane move never completed. Deterministic and
                            // move-lock-resilient - no fast-model call.
                            await ProcessStaleVerdictAsync(workspace, entry, pending, ct);
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.UnworkedNoCoreRun)
                        {
                            // Deterministic: a review-lane card with no core run
                            // is bounced to 2-ready. No fast-model call, no
                            // per-hour rate consumption.
                            ProcessUnworkedCard(workspace, entry, pending);
                            _statusSnapshot.RecordReissue();
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.NoOp)
                        {
                            // NOOP is fully deterministic: no fast-model call,
                            // no per-hour rate consumption.
                            await ProcessNoOpAsync(workspace, entry, pending, maxReissues, ct);
                            _statusSnapshot.RecordReissue();
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.NoCompletionSignal)
                        {
                            // No terminal sentinel arrived. Ask the
                            // review-decision model to classify the real
                            // reply first (ASS-684); fall back to the
                            // deterministic sentinel-demanding reissue only
                            // if the model is unavailable or malformed.
                            await ProcessNoCompletionSignalAsync(
                                workspace, entry, pending, cliBinary, model,
                                maxPerHour, maxReissues, ct);
                            _statusSnapshot.RecordReissue();
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.Blocked)
                        {
                            // BLOCKED is also deterministic: the agent has
                            // declared it cannot proceed, so we always
                            // escalate to a human-decision intake rather
                            // than spending a fast-model call to re-decide.
                            await ProcessBlockedAsync(workspace, entry, pending, ct);
                            _statusSnapshot.RecordEscalate();
                            continue;
                        }

                        if (!RateLimitOk(maxPerHour))
                        {
                            _logger.LogInformation(
                                "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {JobId}",
                                maxPerHour, pending.Job.Id);
                            return;
                        }

                        await ProcessNeedsInputAsync(workspace, entry, pending, cliBinary, model, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "ReviewDecisionOrchestrator failed to process {Project}/{JobId}",
                            entry.Name, pending.Job.Id);
                    }
                    finally
                    {
                        _statusSnapshot.ClearCurrent(entry.Name, pending.Job.Id);
                    }
                }
            }

            // Req 1: drain the collected DONE reviews through the read-only
            // parallel pool. Runs after the sequential pass so the deterministic
            // kinds above never wait behind a multi-aspect review.
            await RunReadOnlyReviewPoolAsync(
                workspace, doneReviews, aspects, cliBinary, aspectModel,
                TimeSpan.FromSeconds(aspectTimeoutSeconds), maxPerHour, maxParallelReviews, ct);
        }
        finally
        {
            _statusSnapshot.EndTick();
            var status = _statusSnapshot.Read();
            if (status.EscalationRateAlert)
            {
                _logger.LogWarning(
                    "Auto-review escalation-rate alert: rate={EscalationRate:P0} threshold={Threshold:P0} decisions={DecisionCount} accept={Accept} escalate={Escalate} reissue={Reissue}",
                    status.EscalationRate,
                    status.EscalationRateAlertThreshold,
                    status.EscalationRateDecisionCount,
                    status.Accept,
                    status.Escalate,
                    status.Reissue);
            }
        }
    }

    /// <summary>
    /// Lane move serialised through <see cref="_postProcessingGitGate"/>. Every
    /// orchestrator lane transition routes through here so concurrent post-processing
    /// - the bounded-parallel worker plus the backstop sweep - can never mutate the
    /// shared checkout working tree at the same time. The move itself is fast, so the
    /// gate costs little relative to the model/gate/aspect work that stays parallel.
    /// </summary>
    /// <param name="transitionCause">Ledger cause id of the move (why), one of <see cref="LaneChangeCauses"/>.</param>
    /// <param name="transitionDetail">Short qualifier: the verdict or gate id behind the move.</param>
    private MoveJobOutcome GuardedMoveJob(
        string jobId,
        string targetState,
        string? watchPath = null,
        string? cause = null,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        _postProcessingGitGate.Wait();
        try
        {
            var result = _taskAccess.TransitionLaneAsync(new TaskTransitionRequest
                {
                    JobId = jobId,
                    TargetLane = targetState,
                    WatchPath = watchPath,
                    Cause = cause,
                    TransitionCause = transitionCause,
                    TransitionDetail = transitionDetail,
                })
                .GetAwaiter()
                .GetResult();
            return result.Status switch
            {
                TaskMutationStatus.Applied => new MoveJobOutcome(
                    MoveJobStatus.Success,
                    result.Message,
                    result.Job?.FolderPath),
                TaskMutationStatus.NotFound => new MoveJobOutcome(
                    MoveJobStatus.NotFound,
                    result.Message),
                TaskMutationStatus.Conflict => new MoveJobOutcome(
                    MoveJobStatus.TargetFolderExists,
                    result.Message),
                _ => new MoveJobOutcome(
                    MoveJobStatus.Failure,
                    result.Message),
            };
        }
        finally { _postProcessingGitGate.Release(); }
    }

    /// <summary>
    /// Post-processes a SINGLE 4-auto-review card off the global tick gate so the
    /// bounded-parallel <see cref="AutoReviewPostProcessingWorker"/> can drive several
    /// cards' post-processing concurrently (Req: raise the parallelisation degree of
    /// the auto-review lane). Per card the step order stays sequential; only ACROSS
    /// cards is there parallelism. Safety: the same card is never in flight twice
    /// (<see cref="_cardsInFlight"/>, shared with the backstop sweep); shared-checkout
    /// lane moves are serialised (<see cref="GuardedMoveJob"/>); the build-test-gate
    /// keeps its machine lock; the rate-limiter and status snapshot are already
    /// lock-protected; a failure is isolated to the one card. The workspace-wide
    /// <see cref="TickOnceAsync"/> remains the boot/backstop/recovery safety net.
    /// </summary>
    public async Task<PostProcessingCardResult> ProcessCardAsync(string workspace, string projectName, string jobId, string watchPath, CancellationToken ct)
    {
        if (!_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
            return PostProcessingCardResult.Blocked("review-decision-orchestrator-disabled");

        var cliBinary = _configuration.GetValue("ReviewDecisionOrchestrator:Cli", CliTypes.Codex);
        var model = _configuration.GetValue("ReviewDecisionOrchestrator:Model", ModelIds.Gpt54Mini);
        var aspectModel = _configuration.GetValue("ReviewDecisionOrchestrator:AspectModel", model);
        var aspectTimeoutSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:AspectTimeoutSeconds", 60);
        var maxPerHour = _configuration.GetValue("ReviewDecisionOrchestrator:CallsPerHour", DefaultCallsPerHour);
        var maxReissues = _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);

        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.Path) &&
            (string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.Name, projectName, StringComparison.Ordinal)));
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path))
            return PostProcessingCardResult.Blocked("watch-path-unresolved");

        // Why the classifier passed this card over, if it did. Without it the
        // caller can only observe "nothing happened" and has to guess whether
        // the card is broken or simply owned by someone else.
        string? skipReason = null;
        void NoteSkip(string skippedJobId, string reason)
        {
            if (string.Equals(skippedJobId, jobId, StringComparison.Ordinal))
                skipReason = reason;
        }

        foreach (var pending in EnumeratePending(workspace, entry, NoteSkip))
        {
            if (ct.IsCancellationRequested)
                return PostProcessingCardResult.Deferred("cancelled");
            if (!string.Equals(pending.Job.Id, jobId, StringComparison.Ordinal)) continue;

            // In-flight guard: never post-process the same card concurrently (another
            // worker slot, a duplicate enqueue, or the backstop sweep). Distinct cards
            // are independent and proceed in parallel.
            var inflightKey = pending.Job.FolderPath;
            if (!_cardsInFlight.TryAdd(inflightKey, 0))
                return PostProcessingCardResult.Deferred("card-already-in-flight");

            _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);
            PostProcessingCardResult result;
            try
            {
                result = await DispatchPendingCardAsync(
                    workspace, entry, pending, cliBinary, model, aspectModel,
                    TimeSpan.FromSeconds(aspectTimeoutSeconds), maxPerHour, maxReissues, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate one card's failure from the rest of the parallel pool.
                _logger.LogWarning(ex,
                    "ReviewDecisionOrchestrator failed to post-process {Project}/{JobId}",
                    entry.Name, pending.Job.Id);
                result = PostProcessingCardResult.Blocked("dispatch-threw:" + ex.GetType().Name);
            }
            finally
            {
                _cardsInFlight.TryRemove(inflightKey, out _);
                _statusSnapshot.ClearCurrent(entry.Name, pending.Job.Id);
            }
            return result; // handled the target card
        }

        // The card was never yielded as pending. Either the classifier skipped
        // it for a known reason (then say which), or it is not an actionable
        // 4-auto-review card at all - a lane move that already took it out, or
        // a sentinel that a previous tick resolved. Both are "nothing to do
        // here", not a fault of this card.
        return skipReason is null
            ? PostProcessingCardResult.Deferred("card-not-pending-in-auto-review")
            : ClassifySkip(skipReason);
    }

    /// <summary>
    /// Maps a classifier skip reason onto a card status. Skips that another
    /// actor resolves (the canonical review executor, a later run) are
    /// deferrals; only a genuinely unusable card is blocked.
    /// </summary>
    private static PostProcessingCardResult ClassifySkip(string skipReason) => skipReason switch
    {
        PostProcessingCardResult.AwaitingCanonicalReviewExecutor or
        "no-unresolved-terminal-sentinel" or
        "fixture-card" => PostProcessingCardResult.Deferred(skipReason),
        _ => PostProcessingCardResult.Blocked(skipReason),
    };

    /// <summary>
    /// Dispatches one card's post-processing by signal kind. This mirrors the
    /// per-card branch of the sweep loop in <see cref="TickOnceCoreAsync"/>; the
    /// per-card path (<see cref="ProcessCardAsync"/>) runs each kind - including a
    /// DONE aspect review - inline for its single card, since the cross-card
    /// parallelism now lives in the worker rather than the in-tick DONE pool.
    /// </summary>
    private async Task<PostProcessingCardResult> DispatchPendingCardAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string cliBinary,
        string model,
        string aspectModel,
        TimeSpan perAspectTimeout,
        int maxPerHour,
        int maxReissues,
        CancellationToken ct)
    {
        switch (pending.Kind)
        {
            case ReviewSignalKind.StaleWithVerdict:
                await ProcessStaleVerdictAsync(workspace, entry, pending, ct);
                return PostProcessingCardResult.Decided("stale-with-verdict");

            case ReviewSignalKind.UnworkedNoCoreRun:
                ProcessUnworkedCard(workspace, entry, pending);
                _statusSnapshot.RecordReissue();
                return PostProcessingCardResult.Decided("unworked-no-core-run");

            case ReviewSignalKind.NoOp:
                await ProcessNoOpAsync(workspace, entry, pending, maxReissues, ct);
                _statusSnapshot.RecordReissue();
                return PostProcessingCardResult.Decided("noop");

            case ReviewSignalKind.NoCompletionSignal:
                await ProcessNoCompletionSignalAsync(
                    workspace, entry, pending, cliBinary, model, maxPerHour, maxReissues, ct);
                _statusSnapshot.RecordReissue();
                return PostProcessingCardResult.Decided("no-completion-signal");

            case ReviewSignalKind.Blocked:
                await ProcessBlockedAsync(workspace, entry, pending, ct);
                _statusSnapshot.RecordEscalate();
                return PostProcessingCardResult.Decided("blocked-sentinel");

            case ReviewSignalKind.Done:
                if (TaskModes.IsConcept(pending.Job.Mode))
                {
                    await ProcessConceptAsync(workspace, entry, pending, ct);
                    return PostProcessingCardResult.Decided("concept");
                }
                if (TaskModes.IsReportOnly(pending.Job.Mode))
                {
                    await ProcessReportOnlyDoneAsync(workspace, entry, pending, cliBinary, ct);
                    return PostProcessingCardResult.Decided("report-only");
                }
                if (!_configuration.GetValue("ReviewDecisionOrchestrator:AspectsEnabled", true))
                    return PostProcessingCardResult.Blocked("aspect-reviews-disabled");
                var aspects = ResolveAspectRunners();
                if (aspects.Count == 0)
                    return PostProcessingCardResult.Blocked("no-aspect-runners-resolved");
                await ProcessDoneAsync(
                    workspace, entry, pending, aspects, cliBinary, aspectModel, perAspectTimeout, ct);
                _statusSnapshot.RecordAspectsRun(aspects.Count);
                return PostProcessingCardResult.Decided("done-aspects");

            case ReviewSignalKind.NeedsInput:
                if (TaskModes.IsConcept(pending.Job.Mode))
                {
                    await ProcessConceptAsync(workspace, entry, pending, ct);
                    return PostProcessingCardResult.Decided("concept");
                }
                if (!RateLimitOk(maxPerHour))
                {
                    _logger.LogInformation(
                        "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {JobId} to next tick",
                        maxPerHour, pending.Job.Id);
                    // Budget exhaustion is a clock problem, not a card problem.
                    return PostProcessingCardResult.Deferred("rate-limit-reached");
                }
                await ProcessNeedsInputAsync(workspace, entry, pending, cliBinary, model, ct);
                return PostProcessingCardResult.Decided("needs-input");
        }

        return PostProcessingCardResult.Blocked("unhandled-signal-kind:" + pending.Kind);
    }

    /// <summary>
    /// Drains the DONE aspect-reviews collected in one tick through a bounded
    /// read-only parallel pool. ADR-0052: an aspect review writes no repo files,
    /// so each task is a <see cref="TaskParallelism.ReadOnlyTask"/> candidate the
    /// <see cref="ParallelSlotPolicy"/> admits without scope analysis, decoupled
    /// from the sequential code-seat (<c>ProjectRunner._activeJobId</c>). The pool
    /// is capped at <paramref name="maxParallel"/> concurrent reviews and shares
    /// the per-hour rate budget: each admitted review charges one call, and once
    /// the budget is spent the remaining reviews are left for the next tick.
    /// </summary>
    private async Task RunReadOnlyReviewPoolAsync(
        string workspace,
        IReadOnlyList<(WatchPathEntry Entry, PendingDecision Pending)> doneReviews,
        IReadOnlyList<string> aspects,
        string cliBinary,
        string aspectModel,
        TimeSpan perAspectTimeout,
        int maxPerHour,
        int maxParallel,
        CancellationToken ct)
    {
        if (doneReviews.Count == 0) return;
        var max = ParallelSlotPolicy.ClampMax(maxParallel);

        var running = new List<(Task Task, RunningTask Slot)>();
        try
        {
            foreach (var (entry, pending) in doneReviews)
            {
                if (ct.IsCancellationRequested) break;

                // Wait for a slot. For read-only candidates ParallelSlotPolicy
                // admits as soon as one of the `max` slots is free, so the loop
                // parks on WhenAny until a running review drains.
                while (true)
                {
                    running.RemoveAll(r => r.Task.IsCompleted);
                    var slots = running.Select(r => r.Slot).ToList();
                    var admission = ParallelSlotPolicy.Decide(
                        pending.Job.Id, TaskParallelism.ReadOnlyTask, slots, max);
                    if (admission.Admitted) break;
                    await Task.WhenAny(running.Select(r => r.Task));
                }

                // Share the per-hour budget across the whole pool: charge before
                // launching, and stop admitting (but let in-flight reviews finish)
                // once the budget is spent so we never exceed CallsPerHour.
                if (!RateLimitOk(maxPerHour))
                {
                    _logger.LogInformation(
                        "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {Count} DONE review(s) to next tick",
                        maxPerHour, doneReviews.Count - running.Count);
                    break;
                }
                RecordRateLimitedCall();

                var slot = new RunningTask(pending.Job.Id, TaskParallelism.ReadOnlyTask);
                var task = Task.Run(async () =>
                {
                    _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);
                    try
                    {
                        await ProcessDoneAsync(workspace, entry, pending, aspects, cliBinary,
                            aspectModel, perAspectTimeout, ct);
                        _statusSnapshot.RecordAspectsRun(aspects.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "ReviewDecisionOrchestrator failed to process {Project}/{JobId}",
                            entry.Name, pending.Job.Id);
                    }
                    finally
                    {
                        _statusSnapshot.ClearCurrent(entry.Name, pending.Job.Id);
                    }
                }, ct);
                running.Add((task, slot));
            }
        }
        finally
        {
            // Always await everything we launched, even on cancellation, so a
            // review never outlives the tick that owns its status-snapshot frame.
            await Task.WhenAll(running.Select(r => r.Task));
        }
    }

    /// <summary>
    /// One-shot boot-sweep backfill for the "concern tags bleiben kleben" bug.
    /// Scans <c>4-auto-review</c> and <c>5-human-review</c> and, for every card
    /// whose latest verdict is <c>accept</c> with no active runner-outcome
    /// issue, strips the aspect-concern tags the latest accept did not actually
    /// raise (accept-with-concerns cards keep exactly the concerns recorded on
    /// the decision). Deterministic, idempotent, and safe to run on every boot:
    /// once cleaned a card has no drift, so subsequent sweeps are no-ops. Public
    /// so tests can drive it against a temp workspace. See <see cref="TagDriftRule"/>.
    /// </summary>
    public void BackfillStaleConcernTags(string workspace, CancellationToken ct)
    {
        List<TaskInfo> jobs;
        try { jobs = _scanner.ScanAllAutomationJobs(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewDecisionOrchestrator: concern-tag backfill scan failed");
            return;
        }

        var decisionsByProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        var cleaned = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) return;
            if (job.State != TaskStates.AutoReview && job.State != TaskStates.HumanReview) continue;
            if (job.Tags.Count == 0 || !job.Tags.Any(TagDriftRule.IsAspectConcernTag)) continue;
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;

            if (!decisionsByProject.TryGetValue(job.ProjectName, out var records))
            {
                try { records = ReviewDecisionLog.ReadAll(workspace, job.ProjectName); }
                catch { records = Array.Empty<ReviewDecisionRecord>(); }
                decisionsByProject[job.ProjectName] = records;
            }

            ReviewDecisionRecord? latest = null;
            for (var i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].JobId == job.Id) { latest = records[i]; break; }
            }

            // AC2 gate: only touch cards the orchestrator accepted, with no
            // active outcome issue. accept-with-concerns is preserved because we
            // reconcile against the concern set the accept actually recorded.
            if (latest?.Kind != ReviewDecisionKind.AcceptAsDone) continue;
            if (job.OutcomeIssue != null) continue;

            var justified = TagDriftRule.ExtractConcernTagIds(latest.Reason);
            var drift = TagDriftRule.FindDriftingConcernTags(job.Tags, justified, "accept", hasOutcomeIssue: false);
            if (drift.Count == 0) continue;

            ConcernTagWriter.ReconcileConcernTags(job.FolderPath, justified, _logger);
            cleaned++;
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: tag-drift backfill stripped {Count} stale concern tag(s) from {Project}/{JobId} in {State}: [{Drift}] (kept [{Kept}])",
                drift.Count, job.ProjectName, job.Id, job.State, string.Join(", ", drift), string.Join(", ", justified));
        }

        if (cleaned > 0)
        {
            _logger.LogInformation("ReviewDecisionOrchestrator: tag-drift backfill cleaned {Cleaned} card(s).", cleaned);
        }
    }

    /// <summary>
    /// One-shot boot-sweep backfill for the "Erfolg sieht aus wie classifier-unknown"
    /// bug (ASS-775). Scans <c>5-human-review</c> and <c>6-completed</c> and, for every
    /// card the orchestrator <c>accept</c>ed that still derives a verdict-contradicting
    /// Warn-class outcome chip (<c>classifier-unknown</c> / <c>heuristic-done</c> /
    /// <c>missing-terminal-sentinel</c>), appends a typed reconcile note to the chat
    /// log. The note is itself an accept line, so the read-time derivation
    /// (<c>TaskScannerService.ResolveOutcomeIssue</c>) then suppresses the stale chip.
    /// 6-completed cards are already reconciled at read time, so they never need a
    /// write and their lane order is left untouched. Deterministic, idempotent (once a
    /// card carries the note it derives no contradicting issue and is skipped), and
    /// safe on every boot. Public so tests can drive it against a temp workspace. See
    /// <see cref="TaskOutcomeIssueReconciliation"/>.
    /// </summary>
    public void BackfillStaleAcceptedOutcomeIssues(string workspace, CancellationToken ct)
    {
        List<TaskInfo> jobs;
        try { jobs = _scanner.ScanAllAutomationJobs(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewDecisionOrchestrator: accepted-outcome backfill scan failed");
            return;
        }

        var decisionsByProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        var cleaned = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) return;
            if (job.State != TaskStates.HumanReview && job.State != TaskStates.Completed) continue;
            if (!TaskOutcomeIssueReconciliation.IsVerdictContradicting(job.OutcomeIssue)) continue;
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;

            // 6-completed is terminal-done = always accepted. For 5-human-review we
            // only touch cards whose latest verdict is an explicit accept (an
            // escalated card legitimately keeps its outcome chip).
            var accepted = job.State == TaskStates.Completed;
            if (!accepted)
            {
                if (!decisionsByProject.TryGetValue(job.ProjectName, out var records))
                {
                    try { records = ReviewDecisionLog.ReadAll(workspace, job.ProjectName); }
                    catch { records = Array.Empty<ReviewDecisionRecord>(); }
                    decisionsByProject[job.ProjectName] = records;
                }

                ReviewDecisionRecord? latest = null;
                for (var i = records.Count - 1; i >= 0; i--)
                {
                    if (records[i].JobId == job.Id) { latest = records[i]; break; }
                }
                accepted = latest?.Kind == ReviewDecisionKind.AcceptAsDone;
            }
            if (!accepted) continue;

            var kind = job.OutcomeIssue!.Kind;
            _chatLog.Append(job, OrchestratorMessageKind.Decision,
                $"Outcome reconciled on accept: cleared stale {kind} marker. The run was accepted and its final verdict supersedes the intermediate-cycle outcome.");
            cleaned++;
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: accepted-outcome backfill cleared stale {Kind} on {Project}/{JobId} in {State}.",
                kind, job.ProjectName, job.Id, job.State);
        }

        if (cleaned > 0)
        {
            _logger.LogInformation("ReviewDecisionOrchestrator: accepted-outcome backfill cleaned {Cleaned} card(s).", cleaned);
        }
    }

    /// <summary>
    /// One-shot boot repair for the bug
    /// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
    /// every old card parked in <c>5-human-review</c> whose per-project decision
    /// journal holds NO record for that job and whose task folder proves a prior
    /// agent run gets a retroactive
    /// <see cref="ReviewDecisionKind.Escalate"/> verdict (category
    /// <see cref="HumanReviewEscalationCategories.UnknownLegacy"/>) and a minimal
    /// <c>status.md</c> stub, written through <see cref="HumanReviewEscalation"/>
    /// while moving it to <c>5e-escalated</c>.
    /// These are the cards that reached the lane through the pre-funnel
    /// ProjectRunner paths, so the board showed them as done-but-blank with
    /// <c>orchestratorVerdict == null</c>. Idempotent: the gate is "no existing
    /// verdict record + old enough + durable run provenance", and the status
    /// stub is never written over a real summary, so re-running on later boots is
    /// a no-op. A freshly-created manual card without a run is never legacy
    /// evidence. An operator-authored latest lane transition is itself a verdict
    /// and always wins over missing legacy provenance. Completed and archived
    /// cards are acceptance terminals and never enter this repair. Public so
    /// tests can drive it.
    /// </summary>
    public void BackfillVerdictlessHumanReview(string workspace, CancellationToken ct)
    {
        if (_humanReviewEscalation == null)
        {
            _logger.LogDebug("ReviewDecisionOrchestrator: no HumanReviewEscalation funnel injected; skipping verdict-less backfill.");
            return;
        }

        List<TaskInfo> jobs;
        try { jobs = _scanner.ScanAllAutomationJobs(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewDecisionOrchestrator: verdict-less backfill scan failed");
            return;
        }

        var decisionsByProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        var repairedKeys = new HashSet<string>(StringComparer.Ordinal);
        var repaired = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) return;
            if (job.State is TaskStates.Completed or TaskStates.Archive) continue;
            if (job.State != TaskStates.HumanReview) continue;
            // An Epic planning completion is verdict-less by construction, not
            // by legacy: it owns no Result-SHA, so no automated review can ever
            // run for it and it will never gain a ReviewDecisionLog record.
            // Escalating it would move the dead end one lane over instead of
            // letting the operator accept or park the plan.
            if (TaskKinds.IsEpic(job.Kind)) continue;
            if (HasLatestOperatorLaneVerdict(job)) continue;
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;
            if (DateTime.UtcNow - job.CreatedAt < VerdictlessBackfillMinimumAge) continue;
            if (!HasRunProvenance(job)) continue;

            if (!decisionsByProject.TryGetValue(job.ProjectName, out var records))
            {
                try { records = ReviewDecisionLog.ReadAll(workspace, job.ProjectName); }
                catch { records = Array.Empty<ReviewDecisionRecord>(); }
                decisionsByProject[job.ProjectName] = records;
            }

            // Gate: a card that already has ANY verdict record is explained -
            // the endpoint-derived OrchestratorVerdict reads the latest of these.
            // Only verdict-less cards are the legacy ones this repairs. The
            // HashSet keeps a second folder for the same job id (rare, e.g. mid
            // crash-recovery) from being appended twice in one sweep.
            if (records.Any(r => r.JobId == job.Id)) continue;
            if (!repairedKeys.Add($"{job.ProjectName} {job.Id}")) continue;

            _humanReviewEscalation.Escalate(
                job.Id, job.WatchPath, job.ProjectName,
                HumanReviewEscalationCategories.UnknownLegacy,
                "Parked in human review before the escalation funnel existed; no automated review ran.");

            repaired++;
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: verdict-less backfill moved {Project}/{JobId} to {TargetState} with a retroactive escalate verdict (category={Category}).",
                job.ProjectName, job.Id, TaskStates.Escalated, HumanReviewEscalationCategories.UnknownLegacy);
        }

        if (repaired > 0)
            _logger.LogInformation("ReviewDecisionOrchestrator: verdict-less human-review backfill repaired {Repaired} card(s).", repaired);
    }

    private bool HasLatestOperatorLaneVerdict(TaskInfo job)
    {
        if (_timeline == null) return false;

        try
        {
            var latestLaneChange = _timeline.ReadAll(job.FolderPath)
                .LastOrDefault(evt => string.Equals(
                    evt.Kind,
                    TimelineEventKinds.LaneChanged,
                    StringComparison.Ordinal));
            if (latestLaneChange == null) return false;

            var actor = latestLaneChange.Actor?.Trim() ?? string.Empty;
            // Actor is also authoritative for legacy rows: before AGT-2345 the
            // Move API accepted a reason but dropped it before lane_changed was
            // written, so requiring Details["reason"] here would re-break the
            // operator corrections made immediately before the first fixed boot.
            return string.Equals(actor, TimelineActors.Human(""), StringComparison.OrdinalIgnoreCase)
                || actor.StartsWith("human:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "ReviewDecisionOrchestrator: operator lane-verdict read failed for {Project}/{JobId}.",
                job.ProjectName,
                job.Id);
            return false;
        }
    }

    private bool HasRunProvenance(TaskInfo job)
    {
        if (job.Commits.Count > 0
            || job.CodeActivityDetected
            || job.SessionChain.Count > 0
            || !string.IsNullOrWhiteSpace(job.SessionName))
        {
            return true;
        }

        try
        {
            if (_sessions?.ReadSessionEvents(job.Id, job.WatchPath).Count > 0)
                return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "ReviewDecisionOrchestrator: session provenance read failed for {Project}/{JobId}.",
                job.ProjectName,
                job.Id);
        }

        try
        {
            return _timeline?.ReadAll(job.FolderPath)
                .Any(evt => evt.Kind == TimelineEventKinds.AgentRunStarted) == true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "ReviewDecisionOrchestrator: timeline provenance read failed for {Project}/{JobId}.",
                job.ProjectName,
                job.Id);
            return false;
        }
    }

    private IReadOnlyList<string> ResolveAspectRunners()
    {
        var section = _configuration.GetSection("ReviewDecisionOrchestrator:AspectRunners");
        if (!section.Exists()) return DefaultAspectRunners;
        var configured = section.GetChildren()
            .Select(c => c.Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
        // Empty configured array is the explicit kill switch
        // (`AspectRunners: []`) - return empty rather than defaulting.
        return configured;
    }

    private async Task ProcessNeedsInputAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string cliBinary,
        string model,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(entry, pending, workspace);
        string response = string.Empty;
        try
        {
            var sw = AgentStudio.AdHoc.AdHocClaudeInvoker.StartTiming();
            var rawResponse = _oneShotRegistry is null
                ? await CliRunner(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct)
                : await RunViaOneShotAsync(
                    cliBinary,
                    model,
                    prompt,
                    TimeSpan.FromSeconds(120),
                    pending.Job,
                    entry.Name,
                    ct);
            sw.Stop();
            var (parsedText, callUsage) = AgentStudio.AdHoc.AdHocClaudeInvoker.ParseOrFallback(rawResponse, model);
            AgentStudio.AdHoc.AdHocClaudeInvoker.Record(
                _usage,
                AgentStudio.Shared.AdHocUsageSources.ReviewDecision,
                model,
                callUsage,
                sw.ElapsedMilliseconds,
                ok: true,
                project: entry.Name,
                jobId: pending.Job.Id);
            response = parsedText;
            RecordRateLimitedCall();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator CLI invocation failed for {Project}/{JobId}",
                entry.Name, pending.Job.Id);
            return;
        }

        var verdict = ReviewDecisionParsing.ParseDecision(response);
        if (verdict == null)
        {
            var fallback = new OrchestratorDecisionVerdict(
                OrchestratorDecisionAction.Escalate,
                "Review-decision model returned no parseable [[ORCHESTRATOR_DECISION]]; operator intervention required.");
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: no decision sentinel parsed for {Project}/{JobId}; escalating to 5e-escalated",
                entry.Name, pending.Job.Id);
            await HandleEscalateAsync(workspace, entry, pending, prompt, response, fallback, cliBinary, ct);
            return;
        }

        switch (verdict.Action)
        {
            case OrchestratorDecisionAction.Reissue:
                await HandleReissueAsync(workspace, entry, pending, prompt, response, verdict, ct);
                break;
            case OrchestratorDecisionAction.Escalate:
                await HandleEscalateAsync(workspace, entry, pending, prompt, response, verdict, cliBinary, ct);
                break;
            case OrchestratorDecisionAction.AcceptAsDone:
                HandleAcceptAsDone(workspace, entry, pending, prompt, response, verdict, cliBinary);
                break;
        }
    }

    /// <summary>
    /// Concept-specific post-processing. A complete, published Dossier is a
    /// successful outcome even when the core run ended in NeedsInput: the
    /// question is precisely what the sight-review gate exists to answer.
    /// Review is deterministic and document-focused; build/test and code aspects
    /// never run for this pipeline.
    /// </summary>
    private Task ProcessConceptAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        CancellationToken ct)
    {
        _ = ct;
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var repositoryRoot = string.IsNullOrWhiteSpace(entry.RepositoryPath)
            ? entry.RootPath
            : entry.RepositoryPath;
        var stored = ConceptWorkbenchStore.Read(current.FolderPath);
        ConceptWorkbenchReview review;
        if (stored is null)
        {
            review = new ConceptWorkbenchReview(
                false, null, null, null,
                ["Dossier placement did not publish a source document."]);
        }
        else if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            review = new ConceptWorkbenchReview(
                false, null, stored.RepoRelativeDirectory, null,
                ["Project repository is unavailable for concept review."]);
        }
        else
        {
            review = ConceptWorkbenchContract.ReviewDirectory(
                repositoryRoot,
                stored.RepoRelativeDirectory,
                current.Key ?? current.Id);
        }

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        _pipelineLog?.EnsureRun(
            current.FolderPath,
            ProjectPipelineOrder.Apply(PipelineCatalogue.Concept, settings),
            entry.Name,
            current.Id);
        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(current.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptReviewStepId,
            Kind = StepKind.Orchestrator,
            Status = review.IsComplete ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = review.IsComplete ? "concept-complete" : "concept-incomplete",
            VerdictSummary = review.Summary,
            Reason = review.IsComplete ? null : review.Summary,
        });

        if (!review.IsComplete)
        {
            _pipelineLog?.Complete(
                current.FolderPath,
                pendingStepReason: "Concept review failed: " + review.Summary);
            var reason = HumanReviewEscalation.FormatReason(
                HumanReviewEscalationCategories.AutoReviewEscalation,
                "Concept review failed: " + review.Summary);
            _chatLog.AppendSupervisor(
                current,
                "escalate",
                $"Concept review found an incomplete or uncontained Dossier. {review.Summary} Promoted to {TaskStates.Escalated}.");
            var failedMove = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "concept-review-failed");
            if (failedMove.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "Concept review could not move {Project}/{JobId} to Escalated: {Status} {Message}",
                    entry.Name, current.Id, failedMove.Status, failedMove.Message);
            }
            return Task.CompletedTask;
        }

        SteerPendingMarker.Write(current.FolderPath, new SteerPendingRecord
        {
            WaitStartedAt = now,
            Kind = SteerPendingKinds.ConceptSightReview,
            Question = "Review the concept Dossier, then promote its implementation cards or finish with no implementation.",
            CliType = current.CliType,
        }, _logger);
        _pipelineLog?.RecordStep(current.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptSightReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = now,
            Verdict = "awaiting-sight-review",
            VerdictSummary = "Concept delivered successfully; awaiting human sight review.",
        });
        _chatLog.Append(
            current,
            OrchestratorMessageKind.Decision,
            $"Concept delivered: `{stored!.RepoRelativeEntrypoint}`. Awaiting sight review; NeedsInput is expected and is not an escalation.");

        var move = GuardedMoveJob(
            current.Id, TaskStates.HumanReview, entry.Path,
            transitionCause: LaneChangeCauses.ReviewVerdict, transitionDetail: "concept-sight-review");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "Complete concept {Project}/{JobId} could not move to sight review: {Status} {Message}",
                entry.Name, current.Id, move.Status, move.Message);
        }
        else
        {
            _statusSnapshot.RecordAccept();
            _logger.LogInformation(
                "concept_delivered project={Project} job={JobId} path={Path} outcome=success-awaiting-sight-review",
                entry.Name, current.Id, stored.RepoRelativeEntrypoint);
        }
        return Task.CompletedTask;
    }

    private async Task ProcessNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        int maxReissues,
        CancellationToken ct)
    {
        var (taskTitle, taskBody) = LoadTaskTitleAndBody(pending);
        var promptUsable = IsPromptUsable(taskTitle, taskBody);

        // Branch 1: empty / placeholder prompt -> escalate. The agent's
        // NOOP is justified; the human needs to scope the task before any
        // run is worthwhile.
        if (!promptUsable)
        {
            var reason = "Task prompt is empty or placeholder; agent's [[TASK_NOOP]] is justified.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            return;
        }

        var prior = CountPriorReissues(workspace, entry.Name, pending.Job.Id);

        // Branch 2: a reissued Codex NOOP that came back as another
        // sentinel-only NOOP without any tool/file activity is not
        // recoverable by another sharpened prompt. Escalate it to
        // 5-human-review so it is not picked again automatically.
        var noProgressEvidence = InspectNoOpProgressSinceLastRecovery(pending);
        if (prior > 0
            && noProgressEvidence.SawNoOpRecoveryReissue
            && noProgressEvidence.ToolCalls == 0
            && noProgressEvidence.FileChanges == 0
            && noProgressEvidence.AgentSubstanceChars == 0)
        {
            var count = prior + 1;
            var reason =
                $"Escalated: {count} consecutive NOOPs without progress. " +
                "The latest retry emitted only [[TASK_NOOP]] after NOOP recovery, with 0 tool calls and 0 file changes.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            _logger.LogWarning(
                "ReviewDecisionOrchestrator escalated {Project}/{JobId} after {NoOpCount} consecutive NOOPs without progress: toolCalls={ToolCalls} fileChanges={FileChanges} agentSubstanceChars={AgentSubstanceChars}",
                entry.Name, pending.Job.Id, count, noProgressEvidence.ToolCalls,
                noProgressEvidence.FileChanges, noProgressEvidence.AgentSubstanceChars);
            return;
        }

        // Branch 3: budget exhausted -> escalate. We share the counter
        // with the existing NEEDS_INPUT-driven reissues so a job that has
        // already been retried multiple times by either path doesn't get
        // double-spent here.
        if (prior >= maxReissues)
        {
            var reason = $"NOOP after {prior} prior orchestrator reissue(s); user attention required.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            return;
        }

        // Branch 4: usable prompt + budget left -> reissue with the
        // sharpened framing from RunOutcomePolicy.
        var followUp = RunOutcomePolicy.BuildReissueFollowupPrompt(taskBody);
        await ReissueNoOpAsync(workspace, entry, pending, followUp, ct);
    }

    private async Task ReissueNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string followUp,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = "Agent emitted [[TASK_NOOP]] but the task description is real; reissuing with sharpened framing.";

        _chatLog.Append(current, OrchestratorMessageKind.Reissue,
            $"Decision: reissue (NOOP recovery). Reason: {reason}");

        var moved = MoveReissueToReadyTop(current, entry, "noop-recovery");
        if (moved != null)
        {
            var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);
            var steering = new SteeringContext("noop-recovery", "reissue", priorReissues, reason);
            followUp = await WriteFollowUpFileAsync(moved, followUp, ct, steering);
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop,
                "Reopened: NOOP recovery, reissued with sharpened framing.",
                BuildReopenDetails("noop-recovery", priorReissues, reason,
                    followUpPrompt: followUp, context: steering));
        }

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: reason,
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: followUp),
            current.FolderPath,
            moved?.FolderPath);
    }

    private Task EscalateNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string reason,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not auto-recover NOOP. Reason: {reason}. Promoted to {TaskStates.Escalated}.");

        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "noop-escalate");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to {TargetState} after NOOP escalate: {Status} {Message}",
                current.Id, TaskStates.Escalated, move.Status, move.Message);
        }

        // ADR-0049: escalation records the event on the original card's
        // timeline and leaves it in the intervention lane - no wrapper card.
        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("noop-escalate", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty),
            current.FolderPath,
            move.NewFolderPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic-completion contract (requirement 4): the run finished
    /// without emitting any terminal sentinel. Mirrors <see cref="ProcessNoOpAsync"/> -
    /// reissue with a sentinel-demanding follow-up while the shared reissue
    /// budget allows, otherwise escalate for operator intervention. The run is NEVER
    /// accepted as done here: a job counts as completed only when the
    /// deterministic signal is present.
    /// </summary>
    private async Task ProcessNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string cliBinary,
        string model,
        int maxPerHour,
        int maxReissues,
        CancellationToken ct)
    {
        var (taskTitle, taskBody) = LoadTaskTitleAndBody(pending);
        var promptUsable = IsPromptUsable(taskTitle, taskBody);

        // Empty / placeholder prompt: re-running cannot help because there is
        // nothing actionable to drive toward a sentinel. Hand to a human.
        if (!promptUsable)
        {
            var reason = "Run finished without a terminal sentinel and the task prompt is empty or placeholder; cannot auto-recover.";
            await EscalateNoCompletionSignalAsync(workspace, entry, pending, reason, ct);
            return;
        }

        var prior = CountPriorReissues(workspace, entry.Name, pending.Job.Id);

        // Budget exhausted: stop looping and hand to a human. Crucially we do
        // not fall back to "accept as done" - the deterministic completion
        // signal never arrived, so a human must judge the work.
        if (prior >= maxReissues)
        {
            var reason = $"Run finished without a terminal sentinel after {prior} prior orchestrator reissue(s); user attention required.";
            await EscalateNoCompletionSignalAsync(workspace, entry, pending, reason, ct);
            return;
        }

        if (RateLimitOk(maxPerHour))
        {
            var prompt = BuildNoCompletionSignalPrompt(entry, pending, workspace);
            try
            {
                var sw = AgentStudio.AdHoc.AdHocClaudeInvoker.StartTiming();
                var rawResponse = _oneShotRegistry is null
                    ? await CliRunner(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct)
                    : await RunViaOneShotAsync(
                        cliBinary,
                        model,
                        prompt,
                        TimeSpan.FromSeconds(120),
                        pending.Job,
                        entry.Name,
                        ct);
                sw.Stop();
                var (response, callUsage) = AgentStudio.AdHoc.AdHocClaudeInvoker.ParseOrFallback(rawResponse, model);
                AgentStudio.AdHoc.AdHocClaudeInvoker.Record(
                    _usage,
                    AgentStudio.Shared.AdHocUsageSources.ReviewDecision,
                    model,
                    callUsage,
                    sw.ElapsedMilliseconds,
                    ok: true,
                    project: entry.Name,
                    jobId: pending.Job.Id);
                RecordRateLimitedCall();

                var verdict = ReviewDecisionParsing.ParseDecision(response);
                if (verdict != null)
                {
                    switch (verdict.Action)
                    {
                        case OrchestratorDecisionAction.Reissue:
                            await HandleReissueAsync(workspace, entry, pending, prompt, response, verdict, ct);
                            return;
                        case OrchestratorDecisionAction.Escalate:
                            await HandleEscalateAsync(workspace, entry, pending, prompt, response, verdict, cliBinary, ct);
                            return;
                        case OrchestratorDecisionAction.AcceptAsDone:
                            _logger.LogInformation(
                                "ReviewDecisionOrchestrator: no-completion accept decision ignored for {Project}/{JobId}; falling back to deterministic reissue",
                                entry.Name, pending.Job.Id);
                            break;
                    }
                }

                _logger.LogInformation(
                    "ReviewDecisionOrchestrator: no no-completion decision sentinel parsed for {Project}/{JobId}; falling back to deterministic reissue",
                    entry.Name, pending.Job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ReviewDecisionOrchestrator no-completion CLI fallback failed for {Project}/{JobId}; falling back to deterministic reissue",
                    entry.Name, pending.Job.Id);
            }
        }
        else
        {
            _logger.LogInformation(
                "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); using deterministic no-completion reissue for {JobId}",
                maxPerHour, pending.Job.Id);
        }

        // Budget left: reissue, explicitly demanding a terminal sentinel on
        // close-out. A silent finish is a reviewable signal, not something to
        // ignore: run the same completion-gate scan over the run's own evidence
        // and, when it finds unfinished work (open items / build failures), append
        // those items so the reissue foregrounds them instead of only demanding a
        // sentinel.
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var (_, recentLog) = LoadTaskContext(pending);
        var findings = CompletionGate.ExtractFindings(LoadStatusSummary(current.FolderPath), recentLog);
        var priorCommits = RunOutcomePolicy.PriorCommitLines(current);

        var followUp = RunOutcomePolicy.BuildMissingSentinelInterventionPrompt(
            "the previous run ended without any [[TASK_DONE]] / [[TASK_BLOCKED]] / [[TASK_NEEDS_INPUT]] / [[TASK_NOOP]] sentinel",
            priorCommits);
        if (findings.Count > 0)
        {
            followUp += "\n\n" + CompletionGate.BuildFollowUp(findings);
        }
        await ReissueNoCompletionSignalAsync(workspace, entry, pending, followUp, findings.Count, ct);
    }

    private string BuildNoCompletionSignalPrompt(WatchPathEntry entry, PendingDecision pending, string workspace)
    {
        var (taskBody, recentLog) = LoadTaskContext(pending);
        var roadmapExcerpt = LoadRoadmap(entry.RootPath);
        var adrTitles = LoadAdrTitles(entry.RootPath);
        var prevDecisions = LoadPreviousDecisionsSummary(workspace, entry.Name, pending.Job.Id);

        return _prompts.Render("orchestrator-no-completion-signal.md", new Dictionary<string, string?>
        {
            ["project"] = entry.Name,
            ["job_id"] = pending.Job.Id,
            ["job_title"] = pending.Job.Title,
            ["task_body"] = taskBody,
            ["recent_log"] = recentLog,
            ["roadmap_excerpt"] = roadmapExcerpt,
            ["adr_titles"] = adrTitles,
            ["previous_decisions"] = prevDecisions,
        });
    }

    private async Task ReissueNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string followUp,
        int gateFindings,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = gateFindings > 0
            ? $"Run finished without a terminal sentinel and its own close-out lists {gateFindings} unfinished item(s); reissuing with them foregrounded."
            : "Run finished without a terminal sentinel; reissuing and demanding a deterministic close-out signal.";

        _chatLog.Append(current, OrchestratorMessageKind.Reissue,
            $"Decision: reissue (no completion signal). Reason: {reason}");

        var moved = MoveReissueToReadyTop(current, entry, "no-completion-signal");
        if (moved != null)
        {
            // Post-core Orchestrator-Review row: the silent-finish reissue is the
            // same completeness gate firing without a sentinel, so record it for
            // the Overview pipeline.
            RecordOrchestratorReviewStep(moved.FolderPath, PipelineStepStatus.Failed,
                DecisionVerdictReissue, reason);
            var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);
            var priorCommits = RunOutcomePolicy.PriorCommitLines(current);
            var steering = new SteeringContext("no-completion-signal", "reissue", priorReissues, reason,
                PriorCommits: priorCommits);
            followUp = await WriteFollowUpFileAsync(moved, followUp, ct, steering);
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop,
                "Reopened: run finished without a terminal sentinel, reissued demanding one.",
                BuildReopenDetails("no-completion-signal", priorReissues, reason,
                    followUpPrompt: followUp, context: steering));
        }

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: reason,
            Prompt: "(deterministic no-completion-signal branch)",
            Response: "(no fast-model call)",
            FollowUp: followUp),
            current.FolderPath,
            moved?.FolderPath);
    }

    private Task EscalateNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string reason,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        reason = HumanReviewEscalation.FormatReason(
            HumanReviewEscalationCategories.NoCompletionSignal,
            reason);

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not obtain a deterministic completion signal. Reason: {reason}. Promoted to {TaskStates.Escalated}.");

        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "no-completion-signal");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after no-completion-signal escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
        RecordOrchestratorReviewStep(escalatedFolder, PipelineStepStatus.Failed,
            DecisionVerdictEscalate, reason);

        EmitVerdictTimeline(escalatedFolder,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("no-completion-signal", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic no-completion-signal branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty),
            current.FolderPath,
            escalatedFolder);

        return Task.CompletedTask;
    }

    private Task ProcessBlockedAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var detail = string.IsNullOrWhiteSpace(pending.Reason)
            ? "Agent emitted [[TASK_BLOCKED]] without further detail; user attention required."
            : $"Agent emitted [[TASK_BLOCKED]]: {pending.Reason}";
        var reason = HumanReviewEscalation.FormatReason(
            HumanReviewEscalationCategories.AgentBlocked,
            detail);

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator escalated BLOCKED for human decision. Reason: {reason}. Promoted to {TaskStates.Escalated}.");

        // BLOCKED escalations move to the decision lane, not acceptance review.
        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "agent-blocked");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after BLOCKED: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        // ADR-0049: the lane move + this timeline event are the handover -
        // no wrapper card.
        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("agent-blocked", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic BLOCKED branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty),
            current.FolderPath,
            move.NewFolderPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Bounce an unworked card out of <c>4-auto-review</c> back to
    /// <c>2-ready</c> (ASS-693 / ASS-716). A card reaches the orchestrator's
    /// review lane with no <c>cli-output.log</c> only when it was mis-placed
    /// there without ever running a core agent run (a decomposition that
    /// targeted the review lane, a hand move). Auto-review presupposes a
    /// completed run; an unworked card has nothing to review and would
    /// otherwise be swept to <c>7-archive</c> unworked. Move it to
    /// <c>2-ready</c> (needs-work) so the pickup loop actually runs it, and
    /// never let it reach the archive. Deterministic and re-bill-safe: the move
    /// takes the card out of this lane, so the next tick no longer sees it; a
    /// move that fails is logged and retried next tick.
    /// </summary>
    private void ProcessUnworkedCard(string workspace, WatchPathEntry entry, PendingDecision pending)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var move = GuardedMoveJob(
            current.Id, TaskStates.Ready, entry.Path,
            transitionCause: LaneChangeCauses.QualityLoop, transitionDetail: UnworkedNoCoreRunCause);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to bounce unworked card {Project}/{JobId} from 4-auto-review to 2-ready: {Status} {Message}; leaving for next-tick retry",
                entry.Name, current.Id, move.Status, move.Message);
            return;
        }

        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.Ready };
        _scanner.InvalidateCache();

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.AppendSupervisor(moved, "requeued-unworked",
            $"\"{title}\" reached 4-auto-review with no core run (no run output, 0 commits). " +
            "Auto-review presupposes a completed run; bounced to 2-ready so the orchestrator runs it. " +
            "An unworked card is never archived.");

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            "Bounced: card reached 4-auto-review with no core run; sent to 2-ready to be worked.",
            new Dictionary<string, string>
            {
                ["cause"] = UnworkedNoCoreRunCause,
            });

        _logger.LogInformation(
            "ReviewDecisionOrchestrator: bounced unworked card {Project}/{JobId} from 4-auto-review to 2-ready (no core run)",
            entry.Name, current.Id);

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "Card reached 4-auto-review with no core run (0 commits, no run output); bounced to 2-ready.",
            Prompt: "(unworked-no-core-run sweep-guard)",
            Response: string.Empty,
            FollowUp: string.Empty),
            current.FolderPath,
            movedFolderPath);
    }

    /// <summary>
    /// Backfill the lane move for a card that already carries an orchestrator
    /// verdict but never left <c>4-auto-review</c> (the move failed after the
    /// verdict was recorded - a Move-Lock from open handles / orphan processes,
    /// vgl. ASS-759, or a backend restart between record and
    /// <see cref="TaskTransitionService.MoveAsync"/>). This is the move-retry the
    /// verdict paths lacked: they warned-but-continued on a failed move and left
    /// the card verdict-but-stuck. Performs ONLY the due move
    /// (<see cref="ReviewDecisionKind.Reissue"/> -> 2-ready,
    /// <see cref="ReviewDecisionKind.Escalate"/> -> 5e-escalated /
    /// <see cref="ReviewDecisionKind.AcceptAsDone"/> -> 5-human-review), appends
    /// NO new verdict record (the original is the source of truth), and writes
    /// the operator-facing chat-log / timeline line only AFTER the move sticks -
    /// so a still-failing move leaves the log mtime untouched and the next tick
    /// simply retries, move-lock-resilient and spam-free.
    /// </summary>
    private Task ProcessStaleVerdictAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var verdict = pending.StaleVerdict ?? ReviewDecisionKind.Skipped;

        if (verdict == ReviewDecisionKind.Reissue)
        {
            var moved = MoveReissueToReadyTop(current, entry, "stale-verdict-backfill");
            if (moved == null)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: stale-with-verdict backfill could not move {Project}/{JobId} (reissue) to 2-ready; leaving for next-tick retry",
                    entry.Name, current.Id);
                return Task.CompletedTask;
            }

            var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
            _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
                $"Auto-review backfill: a recorded reissue verdict for \"{title}\" never completed its lane move; nudged the card to 2-ready.");
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop,
                "Backfill: recorded reissue verdict had no lane move; sent to 2-ready.",
                BuildReopenDetails("stale-verdict-backfill",
                    CountPriorReissues(workspace, entry.Name, current.Id),
                    "Recorded reissue verdict never completed its lane move."));
            _statusSnapshot.RecordReissue();
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: stale-with-verdict backfill moved {Project}/{JobId} (reissue) to 2-ready",
                entry.Name, current.Id);
            return Task.CompletedTask;
        }

        var targetState = verdict == ReviewDecisionKind.AcceptAsDone
            ? TaskStates.HumanReview
            : TaskStates.Escalated;
        var move = GuardedMoveJob(
            current.Id, targetState, entry.Path,
            transitionCause: verdict == ReviewDecisionKind.AcceptAsDone
                ? LaneChangeCauses.ReviewVerdict
                : LaneChangeCauses.Escalated,
            transitionDetail: "stale-verdict-backfill");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: stale-with-verdict backfill could not move {Project}/{JobId} ({Verdict}) to {TargetState}: {Status} {Message}; leaving for next-tick retry",
                entry.Name, current.Id, verdict, targetState, move.Status, move.Message);
            return Task.CompletedTask;
        }

        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var movedInfo = current with { FolderPath = movedFolderPath, State = targetState };
        var titleH = string.IsNullOrWhiteSpace(movedInfo.Title) ? movedInfo.Id : movedInfo.Title;

        if (verdict == ReviewDecisionKind.AcceptAsDone)
        {
            // Provenance stamp mirrors the live accept path so the board can tell
            // an orchestrator-advanced card from a human-accepted one.
            ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);
            _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision,
                $"Auto-review backfill: a recorded accept verdict for \"{titleH}\" never completed its lane move; moved to 5-human-review for your approval.");
            EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
                TimelineActors.Orchestrator,
                "Backfill: recorded accept verdict had no lane move; moved to 5-human-review.",
                new Dictionary<string, string>
                {
                    ["verdict"] = "accept",
                    ["cause"] = "stale-verdict-backfill",
                });
            _statusSnapshot.RecordAccept();
        }
        else
        {
            _chatLog.AppendSupervisor(movedInfo, "escalate",
                $"Auto-review backfill: a recorded escalate verdict for \"{titleH}\" never completed its lane move; promoted to {TaskStates.Escalated}.");
            EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                TimelineActors.Orchestrator,
                "Backfill: recorded escalate verdict had no lane move; moved to 5e-escalated.",
                BuildEscalateDetails("stale-verdict-backfill",
                    "Recorded escalate verdict never completed its lane move.",
                    CountPriorReissues(workspace, entry.Name, current.Id)));
            _statusSnapshot.RecordEscalate();
        }

        _logger.LogInformation(
            "ReviewDecisionOrchestrator: stale-with-verdict backfill moved {Project}/{JobId} ({Verdict}) to {TargetState}",
            entry.Name, current.Id, verdict, targetState);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes a planning or research report without entering the code-review
    /// fan-out. Research has one strict primary artifact:
    /// <c>results/report.html</c>. Planning retains compatibility with existing
    /// result formats but still uses the same lightweight pipeline. A missing
    /// primary result enters the shared bounded completion-gate reissue path.
    /// </summary>
    private async Task ProcessReportOnlyDoneAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string cliBinary,
        CancellationToken ct)
    {
        _statusSnapshot.SetCurrentStep(
            entry.Name, pending.Job.Id, AutoReviewActivitySteps.Gate);
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var settings = PipelineTypeSettings.ForTask(
            _projectSettings?.Get(entry.Name),
            current);
        var pipelineRecord = _pipelineLog?.EnsureRun(
            current.FolderPath,
            ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(current), settings),
            entry.Name,
            current.Id);
        using var pipelineAttempt = pipelineRecord == null
            ? null
            : _pipelineLog!.EnterAttempt(current.FolderPath, pipelineRecord.Attempt);

        var isResearch = TaskModes.Normalize(current.Mode) == TaskModes.Research;
        var primaryReport = Path.Combine(current.FolderPath, "results", "report.html");
        var hasPrimaryResult = isResearch
            ? HasPrimaryResearchHtml(primaryReport)
            : HasResultsArtifacts(current.FolderPath);

        if (!hasPrimaryResult)
        {
            var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);
            var maxReissues = ConfiguredMaxReissues();
            var requiredArtifact = isResearch
                ? "a valid HTML document at results/report.html"
                : "a primary artifact under results/";
            var gate = new CompletionGate.Decision
            {
                Action = priorReissues >= maxReissues
                    ? CompletionGate.CompletionGateAction.Escalate
                    : CompletionGate.CompletionGateAction.Reissue,
                Findings = [$"Required report artifact is missing: {requiredArtifact}."],
                Reason = priorReissues >= maxReissues
                    ? $"The lightweight report pipeline still has no primary result after {priorReissues} prior orchestrator reissue(s)."
                    : $"The lightweight report pipeline requires {requiredArtifact} before handoff.",
            };
            await HandleCompletionGateAsync(workspace, entry, pending, current, gate, ct);
            return;
        }

        var resultRef = isResearch ? "results/report.html" : "results/";
        var reason = $"Primary report is ready at {resultRef}; code gates are not applicable.";
        RecordOrchestratorReviewStep(
            current.FolderPath, PipelineStepStatus.Passed, ReviewVerdictComplete, reason);
        RecordOrchestratorDecisionStep(
            current.FolderPath, PipelineStepStatus.Passed, DecisionVerdictAccept, reason);
        _pipelineLog?.Complete(
            current.FolderPath,
            pendingStepReason: "Not applicable to a lightweight report run.");

        HandleAcceptAsDone(
            workspace,
            entry,
            pending,
            "(deterministic lightweight report validation)",
            resultRef,
            new OrchestratorDecisionVerdict(OrchestratorDecisionAction.AcceptAsDone, reason),
            cliBinary);
        _statusSnapshot.RecordAccept();
        await Task.CompletedTask;
    }

    private static bool HasPrimaryResearchHtml(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var content = File.ReadAllText(path);
            return content.Contains("<html", StringComparison.OrdinalIgnoreCase)
                && content.Contains("</html>", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessDoneAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        IReadOnlyList<string> aspects,
        string cliBinary,
        string aspectModel,
        TimeSpan perAspectTimeout,
        CancellationToken ct)
    {
        _statusSnapshot.SetCurrentStep(
            entry.Name, pending.Job.Id, AutoReviewActivitySteps.Gate);
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var (taskBody, recentLog) = LoadTaskContext(pending);
        // An operator-owned epoch reassesses the current subject, not the old
        // escalation narrative. Rotation normally removes status.md, but the
        // freshness boundary must remain true even if that best-effort file move
        // failed. A later status written by a fresh core run is admitted again.
        var statusPath = Path.Combine(current.FolderPath, "status.md");
        var statusSummary = OperatorReviewRequeueService.IsArtifactFresh(
            current.FolderPath,
            statusPath)
                ? LoadStatusSummary(current.FolderPath)
                : string.Empty;
        var diffSummary = LoadDiffSummary(entry, current);
        var resultsInventory = ResultsInventory.Render(current.FolderPath);
        var cardMode = ReviewCardMode.Describe(current.Mode);
        WritePostProcessingOutcome(current, PostProcessingOutcomes.FindingsAdded,
            summary: "Orchestrator post-processing started.",
            performerCliType: NormalizeReviewCliType(cliBinary),
            stepId: PipelineCatalogue.OrchestratorReviewStepId,
            evidenceRef: "pipeline-execution.json");

        var inputs = new AspectRunInputs(
            Project: entry.Name,
            JobId: current.Id,
            JobTitle: current.Title ?? current.Id,
            JobFolderPath: current.FolderPath,
            TaskBody: taskBody,
            RecentLog: recentLog,
            DiffSummary: diffSummary,
            StatusSummary: statusSummary)
        {
            ResultsInventory = resultsInventory,
            CardMode = cardMode,
            AcceptanceScope = RequirementAcceptanceScope.Describe(
                current.AcceptanceScope,
                taskBody),
        };

        // Bracket the aspect run with a pipeline-execution record so the
        // Overview pipeline view can show "ran 4 aspects in N ms, used X
        // tokens" without having to reconstruct it from cli-output.log.
        // The aspect runner records each step's outcome inside RunAsync;
        // we own the complete mark. Stand-alone tests that wire the
        // orchestrator without a PipelineExecutionLog skip this entirely
        // (the recorder is fully optional).
        //
        // EnsureRun (not Begin): the core agent run already opened this
        // record in ProjectRunner and stamped the CORE "Agent execution"
        // step with its real duration/outcome. Begin would overwrite the
        // file and reset CORE to Pending - the bug where CORE showed "- -"
        // forever while the aspect rows below it completed. EnsureRun
        // resumes the in-flight record so CORE survives, and only begins a
        // fresh one when no run record exists yet (legacy / hand-moved job).
        var projectSettings = PipelineTypeSettings.ForTask(
            _projectSettings?.Get(entry.Name),
            current);
        var pipelineRecord = _pipelineLog?.EnsureRun(
            current.FolderPath,
            ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(current), projectSettings),
            entry.Name,
            current.Id);
        using var pipelineAttempt = pipelineRecord == null
            ? null
            : _pipelineLog!.EnterAttempt(current.FolderPath, pipelineRecord.Attempt);

        // Post-core completeness gate. Persist the exact requirement source,
        // machine evidence, blockers, and lifecycle states before review. Never
        // infer open work from bullets or narrative in status.md: AGT-2149 was
        // reopened on four historical/external-access prose lines despite a
        // TASK_DONE, exit-0, verified run.
        var acceptance = CompletionAcceptanceRecord.Capture(
            taskBody,
            recentLog,
            CompletionGate.ExtractLatestExitCode(recentLog),
            CompletionGate.ExtractLatestRunCompleted(recentLog),
            HasResultsArtifacts(current.FolderPath),
            parsedDoneSignal: pending.Kind == ReviewSignalKind.Done);
        CompletionAcceptanceRecord.Write(current.FolderPath, acceptance, _logger);
        var gate = CompletionGate.EvaluateStructured(
            acceptance,
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues());
        if (gate.IsIncomplete)
        {
            await HandleCompletionGateAsync(workspace, entry, pending, current, gate, ct);
            return;
        }

        var dossierReview = RunDossierMaintenancePostStep(entry, current);
        if (dossierReview is { Required: true, IsComplete: false })
        {
            var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);
            var dossierGate = new CompletionGate.Decision
            {
                Action = priorReissues >= ConfiguredMaxReissues()
                    ? CompletionGate.CompletionGateAction.Escalate
                    : CompletionGate.CompletionGateAction.Reissue,
                Findings = dossierReview.Findings.ToList(),
                Reason = "The referenced Dossier was not updated through its append-only implementation log.",
            };
            await HandleCompletionGateAsync(workspace, entry, pending, current, dossierGate, ct);
            return;
        }

        // Gate clean: record the post-core Orchestrator-Review row as a passed
        // completeness check so the Overview pipeline shows the gate ran ahead of
        // the aspect verdicts, then fall through to the normal aspect review and
        // the final Orchestrator-Review decision below.
        RecordOrchestratorReviewStep(current.FolderPath, PipelineStepStatus.Passed,
            ReviewVerdictComplete, gate.Reason);

        _statusSnapshot.SetCurrentStep(
            entry.Name, current.Id, AutoReviewActivitySteps.Gate);
        var buildGateResult = await RunBuildTestGatePostStepAsync(workspace, entry, current, ct);
        if (buildGateResult?.Verdict == BuildTestGateVerdict.Fail)
        {
            var intervention = await TryRaiseBuildGateInterventionAsync(current, entry, buildGateResult, ct);
            if (intervention is not null)
            {
                await HandleFailureInterventionAsync(workspace, entry, current, buildGateResult, intervention, ct);
                return;
            }
            if (buildGateResult.IsInfrastructureFailure)
            {
                await HandleBuildTestGateInfrastructureFailureAsync(
                    workspace, entry, current, buildGateResult);
                return;
            }
            // The quality grade is reporting evidence, not a success gate. A
            // red deterministic build must stay loud and still receive that
            // evidence before the task is reissued / escalated. The aspect pool
            // is intentionally bypassed on this terminal branch.
            _statusSnapshot.SetCurrentStep(
                entry.Name, current.Id, AutoReviewActivitySteps.Grade);
            var failedBuildGrade = await RunCodeReviewGradePostStepAsync(
                entry, current, taskBody, buildGateResult, ct);
            await HandleBuildTestGateFailureAsync(
                workspace, entry, pending, current, buildGateResult, failedBuildGrade, ct);
            return;
        }

        var qualityAnalysis = await RunQualityAngularRulesPostStepAsync(
            entry, current, pipelineRecord?.Attempt, ct);
        var qualityGate = qualityAnalysis is null
            ? null
            : BuildQualityAnalysisGateDecision(
                qualityAnalysis,
                CountPriorReissues(workspace, entry.Name, current.Id),
                ConfiguredMaxReissues());
        if (qualityGate is not null)
        {
            await HandleCompletionGateAsync(workspace, entry, pending, current, qualityGate, ct);
            return;
        }

        // Per-project pipeline config: drop aspects the project disabled and
        // route each remaining aspect's CLI call to its configured model
        // (falling back to the run-wide aspectModel). The resolver keys on
        // the catalogue step id (aspect-{id}); see PipelineStepConfigResolver.
        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var conditionContext = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = false,
            TaskType = current.TaskType,
            Tags = current.Tags,
        };
        var enabledAspects = aspects
            .Where(id => PipelineStepConfigResolver.ShouldRun(settings, $"aspect-{id}", conditionContext))
            .ToList();
        var economyRecommendations = new Dictionary<string, PipelineStepEconomyRecommendation>(StringComparer.OrdinalIgnoreCase);
        if (_pipelineStepEconomy is not null)
        {
            foreach (var aspectId in enabledAspects)
            {
                var recommendation = await _pipelineStepEconomy.SuggestModelAsync(
                    settings, $"aspect-{aspectId}", ct);
                if (recommendation is not null) economyRecommendations[aspectId] = recommendation;
            }
        }
        Func<string, string>? modelForAspect = settings is null
            ? null
            : aspectId => economyRecommendations.TryGetValue(aspectId, out var recommendation)
                ? recommendation.Model
                : PipelineStepConfigResolver.ResolveModel(settings, $"aspect-{aspectId}", aspectModel);
        Func<string, string?>? cliForAspect = settings is null
            ? null
            : aspectId => economyRecommendations.TryGetValue(aspectId, out var recommendation)
                ? recommendation.CliType
                : PipelineStepConfigResolver.ResolveCliType(settings, $"aspect-{aspectId}") ?? NormalizeReviewCliType(cliBinary);
        Func<string, string?>? thinkingLevelForAspect = settings is null
            ? null
            : aspectId =>
            {
                if (economyRecommendations.TryGetValue(aspectId, out var recommendation))
                    return recommendation.ThinkingLevel;
                var resolvedModel = modelForAspect?.Invoke(aspectId) ?? aspectModel;
                return PipelineStepConfigResolver.ResolveThinkingLevel(
                    settings,
                    $"aspect-{aspectId}",
                    cliForAspect?.Invoke(aspectId) ?? NormalizeReviewCliType(cliBinary),
                    resolvedModel);
            };
        Func<string, string?>? promptForAspect = settings is null
            ? null
            : aspectId => PipelineStepConfigResolver.ResolvePrompt(settings, $"aspect-{aspectId}");

        _statusSnapshot.SetCurrentStep(
            entry.Name, current.Id, AutoReviewActivitySteps.Aspects);
        var report = await _aspectRunner.RunAsync(inputs, enabledAspects, cliBinary, aspectModel, perAspectTimeout, ct,
            modelForAspect, thinkingLevelForAspect, promptForAspect, cliForAspect);

        // Grade the settled change set before any aspect-infrastructure
        // short-circuit. The grade supplies council findings; a dead aspect
        // reviewer must not silently erase that review artifact or its explicit
        // reaction. Keeping the call here also preserves the normal ordering
        // (after aspects) without running twice.
        _statusSnapshot.SetCurrentStep(
            entry.Name, current.Id, AutoReviewActivitySteps.Grade);
        var gradeReport = await RunCodeReviewGradePostStepAsync(
            entry, current, taskBody, buildGateResult, ct);
        _statusSnapshot.SetCurrentStep(
            entry.Name, current.Id, AutoReviewActivitySteps.Decision);

        // Aspect-verdict infra crash (AGT-2021): one or more aspects produced no
        // verdict because the reviewing CLI died - even after the aspect runner's
        // single environmental retry. This is an INFRASTRUCTURE fault (the backend
        // cut that killed the runner mid-run, AGT-1996), NOT the card's unfinished
        // work. Short-circuit BEFORE the block/evidence/solution-quality routing
        // and the remaining reporting-only post-steps: the card must not be
        // accepted, reissued, or counted as a work deficit. Record an InfraCrash
        // flagged environmental and hand to human review WITHOUT burning the
        // reissue budget (an Escalate decision, which resets the attempt chain).
        if (report.HasInfraFailure)
        {
            AgentStudio.Review.CouncilReviewReaction? infraReaction = null;
            if (gradeReport is not null)
            {
                infraReaction = DeriveCouncilReviewReaction(workspace, entry, current, gradeReport);
                infraReaction = AgentStudio.Review.CouncilReviewPolicy.EscalateBecause(
                    infraReaction,
                    "aspect review infrastructure failed, so no safe automatic follow-up round can be started");
                AgentStudio.Review.CouncilReviewReactionStore.Write(current.FolderPath, infraReaction);
            }
            _pipelineLog?.Complete(
                current.FolderPath,
                pendingStepReason: "Not run because aspect review infrastructure failed after its retry budget was exhausted.");
            await HandleAspectInfraCrashAsync(workspace, entry, pending, current, report, infraReaction, ct);
            return;
        }

        // ASS-563: run the lint-scss post-step BEFORE the pipeline Complete
        // mark so its step record lands in pipeline-execution.json while
        // the file is still in its in-flight state. Skipped/Ok/Warn just
        // record; Fail short-circuits the move-to-review path with a
        // reissue (or, if we've already reissued once, an escalation).
        var lintResult = await RunLintScssPostStepAsync(workspace, entry, current, ct);

        // Regression radar post-step: a deterministic spec-change classification
        // recorded alongside lint so the Overview pipeline lists it with a
        // status + duration. Reporting only - the verdict never gates the move
        // to review (unlike lint Fail above), so it sits between lint and the
        // Complete mark and feeds nothing into the decision branches below.
        RunRegressionRadarPostStep(entry, current);

        // Wiki maintenance post-step: opt-in project-scoped knowledge upkeep.
        // It dedupes recurring problem entries by slug, records this task as
        // occurrence evidence, and regenerates the project wiki index. It is
        // reporting-only and never changes the task lane decision.
        RunWikiMaintenancePostStep(entry, current);

        // Wiki learnings post-step: opt-in project-scoped knowledge distillation.
        // It folds the derived verdict, the per-aspect review findings, the
        // agent's close-out notes, and the typed outcome stumbling block into a
        // per-task page under docs/learnings and regenerates that index. It
        // is reporting-only and never changes the task lane decision.
        RunWikiLearningsPostStep(entry, current, report, statusSummary, diffSummary);

        // AGENTS/wiki-sync post-step (AGT-1782): opt-in project-scoped upkeep that
        // keeps the AGENTS.md -> wiki pointers for the designated topics consistent
        // (no dead/missing link) and collects each designated topic's current state
        // from the task's own change set, so agents stop re-discovering the same
        // ground. Reporting-only and never changes the task lane decision.
        RunAgentsWikiSyncPostStep(entry, current);

        // AGT-2053: append bidirectional task/wiki associations after the wiki
        // producers have settled. This is reporting-only and deliberately does
        // not clean stale targets: missing pages/tasks remain useful history.
        RunWikiTaskCrossReferenceStep(entry, current);

        // Task-spawner post-step (AGT-2028): opt-in, quality-first relevance
        // judgment that, on a conservative yes, spawns a follow-up card in a
        // configured target project (e.g. "we changed X -> update the website").
        // Reporting-only and deduped; it never gates the source task's decision.
        // Runs after the aspects settle and before the Complete mark so its step
        // record lands in the in-flight pipeline-execution.json.
        await RunTaskSpawnerPostStepAsync(entry, current, report, taskBody, statusSummary, diffSummary, resultsInventory, ct);

        // Council pattern: the quality-grade reviewer is advisory, while this
        // orchestrator owns the explicit per-finding ruling. A named deficiency
        // cannot disappear behind a passing grade or an unrelated aspect pass.
        // Reissue carries only the concrete finding sentences into the next run.
        if (gradeReport is not null
            && await HandleCouncilReviewReactionAsync(workspace, entry, current, gradeReport, ct))
        {
            return;
        }

        _pipelineLog?.Complete(
            current.FolderPath,
            pendingStepReason: "Not run in this completed pipeline attempt.");

        if (report.Overall == AspectStatus.Block)
        {
            // Reissue-loop breaker (ASS-794). The aspect-block path is the one
            // reissue branch that did not enforce the shared reissue budget, so a
            // finished task whose re-run produced an empty follow-up diff (nothing
            // left to do) could be BLOCKed on the unchanged/+0-0 diff and reissued
            // forever. The completion gate already passed above, so the close-out
            // is acceptable here; pass that plus the empty-diff probe to the pure
            // breaker, which accepts an empty clean re-run and otherwise escalates
            // once the budget is spent rather than penduluming 2-ready <-> run.
            var reviewRecords = ReviewDecisionLog.ReadAll(workspace, entry.Name);
            var repeatedBlock = RepeatedAspectBlockPolicy.Diagnose(
                report,
                reviewRecords,
                current.Id,
                OperatorReviewRequeueService.ReadEpoch(current.FolderPath),
                ConfiguredIdenticalBlockRounds());
            var loopBreak = ReissueLoopBreaker.Evaluate(
                CountReissuesInCurrentChain(reviewRecords, current.Id),
                ConfiguredMaxReissues(),
                emptyFollowupDiff: IsLatestRunEmptyDiff(current, entry.Path),
                stateAcceptable: true,
                repeatedBlock);
            switch (loopBreak.Action)
            {
                case ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff:
                    await AcceptOnLoopBreakAsync(workspace, entry, current, report, loopBreak, ct);
                    return;
                case ReissueLoopBreaker.LoopBreakAction.Escalate:
                    EscalateOnLoopBreak(
                        workspace, entry, current, report, loopBreak, repeatedBlock);
                    return;
            }

            await ReissueOnBlockAsync(
                workspace, entry, pending, current, report, repeatedBlock, ct);
            return;
        }

        if (lintResult?.Verdict == LintScssVerdict.Fail)
        {
            // The aspect verdict was pass/concerns but lint-scss broke; in
            // fail mode the post-step routes to its own reissue / escalate
            // path and we stop here. In warn mode the verdict was Warn,
            // not Fail, and we fall through to the normal move-to-review.
            await HandleLintScssFailureAsync(workspace, entry, pending, current, lintResult, ct);
            return;
        }

        // Evidence gate (ASS-764): a bare success claim is not acceptance. For a
        // UI/bug task that left no visual proof, or when the tests-and-evidence
        // aspect is not clean (failing build/tests, missing evidence, +0/-0
        // "test" commit), demand verification instead of accepting with concerns:
        // reissue with a screenshot/e2e + green-build demand while the shared
        // reissue budget allows, otherwise escalate to 5e-escalated.
        // Only demand a screenshot when the change-set proves the task actually
        // touched the frontend UI surface: a backend bug or a planning/doc task
        // cannot produce one, and the tests-and-evidence aspect governs its proof
        // instead. A null change-set (unknown diff / remote run) falls back to
        // the heuristic so protection is never silently dropped.
        var evidenceGateChangedFiles = ResolveLatestRunChangedFiles(current, entry.Path);
        var evidenceGate = EvidenceGate.Evaluate(
            EvidenceGate.RequiresVisualEvidence(current.TaskType, current.Tags, current.Title, evidenceGateChangedFiles),
            EvidenceGate.HasVisualEvidence(current.FolderPath),
            report,
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues());
        if (evidenceGate.IsBlocking)
        {
            await HandleEvidenceGateAsync(workspace, entry, pending, current, report, evidenceGate, ct);
            return;
        }

        var solutionQualityGate = SolutionQualityGate.Evaluate(
            report,
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues());
        if (solutionQualityGate.IsBlocking)
        {
            await HandleSolutionQualityGateAsync(workspace, entry, pending, current, report, solutionQualityGate, ct);
            return;
        }

        // Reconcile (not merge-only): set the concern tags to exactly this
        // pass's set. A follow-up pass that now passes cleanly - or raises
        // fewer concerns than before - must STRIP the stale concern chips an
        // earlier pass left behind, not leave them stuck. Runs even when the
        // current set is empty. See the "concern tags bleiben kleben" bug.
        ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

        // Promote to 5-human-review with or without concern tags. ADR-0025:
        // accept-as-done routes to human-review, never directly to completed.
        var move = GuardedMoveJob(
            current.Id, TaskStates.HumanReview, entry.Path,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: report.Overall == AspectStatus.Concerns ? "accept-with-concerns" : "accept");
        if (move.Status != MoveJobStatus.Success)
        {
            // Move failed -> do NOT fire the operator-facing "accepted as
            // done" banner: it would claim the task moved while the folder
            // is still in 4-auto-review. The aspect work is sunk; the next
            // tick will re-attempt the move.
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after multi-aspect accept: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        // Re-merge tags after the lane move so the tags array on disk
        // reflects the post-move folder. The state machine moves the
        // folder atomically, so the tags written before the move travel
        // with it; this re-merge is a defensive idempotent reapplication
        // on the authoritative post-move path returned by MoveJob.
        // Using move.NewFolderPath rather than re-scanning avoids the
        // stale-cache race that previously fell back to the source folder
        // and left orphan logs/cli-output.log skeletons in 4-auto-review.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var movedInfo = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        CompletionAcceptanceRecord.MarkAccepted(
            movedFolderPath,
            "pipeline-execution.json",
            report.Overall == AspectStatus.Concerns
                ? $"Structured aspect review accepted with concerns: {FormatConcernCount(report)}"
                : "Structured aspect review accepted all requirements.",
            _logger);
        if (!string.Equals(movedFolderPath, current.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ConcernTagWriter.ReconcileConcernTags(movedFolderPath, report.ConcernTagIds, _logger);
        }

        // Final verdict step: the orchestrator's single ruling after the
        // parallel aspects. Recorded on the post-move folder (the pipeline
        // record travelled with the lane move) so the Overview pipeline shows
        // it as the distinct "Auto-review decision" row below the aspects.
        RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Passed,
            report.Overall == AspectStatus.Concerns
                ? DecisionVerdictAcceptWithConcerns
                : DecisionVerdictAccept,
            report.Overall == AspectStatus.Concerns
                ? FormatConcernCount(report)
                : "all aspects pass");
        if (report.Overall == AspectStatus.Concerns)
        {
            WritePostProcessingOutcome(movedInfo, PostProcessingOutcomes.FindingsAdded,
                summary: FormatConcernCount(report),
                stepId: PipelineCatalogue.OrchestratorDecisionStepId,
                evidenceRef: "pipeline-execution.json",
                findingRefs: report.Verdicts
                    .Where(v => v.Status == AspectStatus.Concerns)
                    .Select(v => $"aspect-{v.Aspect}.md")
                    .ToList(),
                performer: PostProcessingPerformers.Orchestrator);
        }
        WritePostProcessingOutcome(movedInfo, PostProcessingOutcomes.PassToHumanReview,
            summary: report.Overall == AspectStatus.Concerns
                ? $"Accepted with concerns: {FormatConcernCount(report)}"
                : "Accepted as done after post-processing.",
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json",
            performer: PostProcessingPerformers.Orchestrator);

        // Provenance: the orchestrator (not a human) advanced this task
        // toward Completed. Stamp on the authoritative post-move path.
        ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);

        // Append the operator-facing chat-log line ONLY after the lane move
        // has succeeded, so the banner cannot fire while the task is still
        // in 4-auto-review. F29: keep the headline short (one sentence +
        // count) so the workspace banner / notification toast stays
        // readable. The full per-aspect verdict list lives in the
        // aspect-*.md files inside the job folder and on the decision
        // record below.
        var titleAccept = string.IsNullOrWhiteSpace(movedInfo.Title) ? movedInfo.Id : movedInfo.Title;
        var verdictNote = report.Overall == AspectStatus.Concerns
            ? $"Auto-review accepted \"{titleAccept}\" with concerns ({FormatConcernCount(report)}). Moved to 5-human-review for your approval."
            : $"Auto-review accepted \"{titleAccept}\" as done. Moved to 5-human-review for your approval.";
        _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision, verdictNote);

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
            TimelineActors.Orchestrator, verdictNote, new Dictionary<string, string>
            {
                ["verdict"] = "accept",
                ["aspectOutcome"] = report.Overall == AspectStatus.Concerns ? "concerns" : "pass",
                ["aspects"] = AspectSummaryLine(report),
            });

        if (report.Overall == AspectStatus.Concerns)
        {
            _statusSnapshot.RecordAccept();
        }
        else
        {
            _statusSnapshot.RecordAccept();
        }

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: report.Overall == AspectStatus.Concerns
                ? $"Multi-aspect: accept with concerns ({string.Join(", ", report.ConcernTagIds)})"
                : "Multi-aspect: all aspects pass",
            Prompt: "(multi-aspect run; per-aspect prompts written to aspect-*.md)",
            Response: AspectSummaryLine(report),
            FollowUp: string.Empty),
            current.FolderPath,
            movedFolderPath);
    }

    /// <summary>
    /// Persist and apply the council reaction for one quality-grade artifact.
    /// Returns true when the reaction is terminal for this post-processing pass
    /// (reissue or escalation); accept reactions stay attached to the review and
    /// allow the remaining deterministic/aspect gates to rule on their evidence.
    /// </summary>
    private async Task<bool> HandleCouncilReviewReactionAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        AgentStudio.Review.CodeReviewStepReport gradeReport,
        CancellationToken ct)
    {
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);
        var reaction = DeriveCouncilReviewReaction(workspace, entry, current, gradeReport);
        AgentStudio.Review.CouncilReviewReactionStore.Write(current.FolderPath, reaction);

        if (reaction.Disposition == AgentStudio.Review.CouncilReactionDisposition.Accept)
        {
            return false;
        }

        _pipelineLog?.Complete(
            current.FolderPath,
            pendingStepReason: "Not run because the council reaction concluded this review pass.");

        if (reaction.Disposition == AgentStudio.Review.CouncilReactionDisposition.Reissue)
        {
            var followUp = AgentStudio.Review.CouncilReviewPolicy.BuildTargetedFollowUp(reaction);
            var moved = MoveReissueToReadyTop(current, entry, "code-review-council");
            if (moved is null) return true;

            AgentStudio.Review.CouncilReviewReactionStore.Write(moved.FolderPath, reaction);
            RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
                DecisionVerdictReissue, reaction.Summary);
            WritePostProcessingOutcome(moved, PostProcessingOutcomes.NeedsFollowUpTask,
                summary: reaction.Summary,
                stepId: PipelineCatalogue.OrchestratorDecisionStepId,
                evidenceRef: gradeReport.FileName,
                findingRefs: new[] { gradeReport.FileName },
                performer: PostProcessingPerformers.Orchestrator);
            followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

            _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
                $"Council reaction reopened \"{(moved.Title ?? moved.Id)}\" for {reaction.Assessments.Count} named review finding(s). Next round: {moved.Id} attempt {reaction.TargetRunAttempt}.");
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop, reaction.Summary,
                BuildReopenDetails("code-review-council", priorReissues, followUp));
            _statusSnapshot.RecordReissue();

            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: reaction.CreatedAt,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Reissue,
                Reason: reaction.Summary,
                Prompt: $"(council reaction to {gradeReport.FileName})",
                Response: string.Join("\n", reaction.Assessments.Select(a => $"{a.Action}: {a.Finding}")),
                FollowUp: followUp)
            {
                CouncilReaction = reaction,
            }, current.FolderPath, moved.FolderPath);
            return true;
        }

        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "code-review-council");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to escalate {JobId} after council reaction: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return true;
        }

        var escalatedPath = move.NewFolderPath ?? current.FolderPath;
        var escalated = current with { FolderPath = escalatedPath, State = TaskStates.Escalated };
        AgentStudio.Review.CouncilReviewReactionStore.Write(escalatedPath, reaction);
        RecordOrchestratorDecisionStep(escalatedPath, PipelineStepStatus.Failed,
            DecisionVerdictEscalate, reaction.Summary);
        WritePostProcessingOutcome(escalated, PostProcessingOutcomes.NeedsHumanInput,
            summary: reaction.Summary,
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: gradeReport.FileName,
            findingRefs: new[] { gradeReport.FileName },
            performer: PostProcessingPerformers.Orchestrator);
        _chatLog.Append(escalated, OrchestratorMessageKind.Decision,
            $"Council reaction escalated \"{(escalated.Title ?? escalated.Id)}\": {reaction.Summary}");
        _statusSnapshot.RecordEscalate();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: reaction.CreatedAt,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reaction.Summary,
            Prompt: $"(council reaction to {gradeReport.FileName})",
            Response: string.Join("\n", reaction.Assessments.Select(a => $"{a.Action}: {a.Finding}")),
            FollowUp: string.Empty)
        {
            CouncilReaction = reaction,
        }, current.FolderPath, escalatedPath);
        return true;
    }

    private AgentStudio.Review.CouncilReviewReaction DeriveCouncilReviewReaction(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        AgentStudio.Review.CodeReviewStepReport gradeReport)
        => AgentStudio.Review.CouncilReviewPolicy.Derive(
            gradeReport.FileName,
            gradeReport.Grade,
            gradeReport.Findings ?? Array.Empty<string>(),
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues(),
            current.Id,
            targetRunAttempt: (_pipelineLog?.Read(current.FolderPath)?.Attempt
                ?? CountPriorReissues(workspace, entry.Name, current.Id) + 1) + 1,
            executionError: gradeReport.ExecutionError);

    private async Task ReissueOnBlockAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        RepeatedAspectBlockDiagnosis? repeatedBlock,
        CancellationToken ct)
    {
        var followUp =
            "Auto-review found one or more blocking aspect verdicts. Address each item below, " +
            "then re-run the task and end with [[TASK_DONE]]:\n\n" +
            report.FollowUpSummary;

        var moved = MoveReissueToReadyTop(current, entry, "multi-aspect-block");
        if (moved == null)
        {
            // Move failed -> no operator-facing "sent back to ready" banner.
            // The aspect work is sunk; the next tick will retry.
            return;
        }

        // Final verdict step: reissue. Recorded on the post-move folder so the
        // Overview pipeline shows the orchestrator's ruling distinctly from the
        // parallel aspect rows that drove it.
        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, "Multi-aspect block: " + AspectSummaryLine(report));
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.NeedsFollowUpTask,
            summary: "Blocking aspect verdicts require follow-up work before human review.",
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json",
            findingRefs: report.Verdicts
                .Where(v => v.Status == AspectStatus.Block)
                .Select(v => $"aspect-{v.Aspect}.md")
                .ToList(),
            performer: PostProcessingPerformers.Orchestrator);

        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        // F29: keep the operator-facing reissue note short. The full
        // per-aspect verdict list is in the decision journal record below
        // and in the aspect-*.md files written by the runner.
        var titleReissue = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var blockCount = report.Verdicts.Count(v => v.Status == AspectStatus.Block);
        var blockNoun = blockCount == 1 ? "aspect" : "aspects";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{titleReissue}\" back to 2-ready ({blockCount} blocking {blockNoun}).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: {blockCount} blocking {blockNoun} from auto-review.",
            BuildReopenDetails("multi-aspect-block",
                CountPriorReissues(workspace, entry.Name, current.Id),
                report.FollowUpSummary,
                report.Verdicts));

        _statusSnapshot.RecordReissue();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "Multi-aspect block: " + AspectSummaryLine(report),
            Prompt: "(multi-aspect run; per-aspect prompts written to aspect-*.md)",
            Response: AspectSummaryLine(report),
            FollowUp: followUp)
        {
            FailureKind = RepeatedAspectBlockPolicy.FailureKind,
            FailureFingerprint = repeatedBlock?.Fingerprint,
            FailureFingerprints = repeatedBlock?.CurrentFingerprints,
        },
            current.FolderPath,
            moved.FolderPath);
    }

    /// <summary>
    /// Handle an aspect-verdict INFRA crash (AGT-2021): one or more aspects
    /// produced no verdict because the reviewing CLI died, and the aspect runner's
    /// single environmental retry did not recover it. This is an infrastructure
    /// fault, never the card's unfinished work, so:
    /// <list type="bullet">
    ///   <item>the card is moved to <c>5e-escalated</c> flagged <c>environmental</c>
    ///   (an honest human-review terminal - a human re-queues the infra blip),</item>
    ///   <item>the decision is recorded as an <see cref="ReviewDecisionKind.Escalate"/>,
    ///   which is a chain boundary: it does NOT append a Reissue record and it
    ///   resets the attempt chain, so the card's reissue budget is not burned
    ///   (<see cref="CountReissuesInCurrentChain"/>),</item>
    ///   <item>the outcome / timeline carry <c>InfraCrash</c> + <c>environmental</c>
    ///   so a reviewer reads it as an infra blip, not a failed change.</item>
    /// </list>
    /// </summary>
    private async Task HandleAspectInfraCrashAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        AgentStudio.Review.CouncilReviewReaction? councilReaction,
        CancellationToken ct)
    {
        var crashedAspects = report.InfraFailures.Select(v => v.Aspect).ToList();
        var aspectList = crashedAspects.Count == 0 ? "aspect review" : string.Join(", ", crashedAspects);
        var noun = crashedAspects.Count == 1 ? "aspect" : "aspects";
        var reason = AspectInfraCrashReasonPrefix +
            $"the {aspectList} {noun} produced no verdict after an environmental retry (reviewing CLI died). " +
            "Classified environmental (InfraCrash); the card's work is unaffected and its reissue budget is not charged.";

        // Strip any stale concern chips a prior pass left behind; the infra-failure
        // verdicts hang no concern tag of their own.
        ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

        _chatLog.AppendSupervisor(current, "escalate",
            $"Auto-review could not obtain an aspect verdict: the reviewing CLI died even after an environmental retry. " +
            $"This is an infrastructure crash (InfraCrash), not a problem with the change. Promoted to {TaskStates.Escalated} flagged environmental.");

        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "aspect-verdict-infra-crash");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after aspect infra-crash: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
        var escalated = current with { FolderPath = escalatedFolder, State = TaskStates.Escalated };
        if (councilReaction is not null)
        {
            AgentStudio.Review.CouncilReviewReactionStore.Write(escalatedFolder, councilReaction);
        }

        RecordOrchestratorDecisionStep(escalatedFolder, PipelineStepStatus.Failed,
            "environmental", reason);
        WritePostProcessingOutcome(escalated, PostProcessingOutcomes.FailedPostProcessing,
            summary: reason,
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json",
            findingRefs: report.InfraFailures.Select(v => $"aspect-{v.Aspect}.md").ToList(),
            performer: PostProcessingPerformers.Orchestrator);

        EmitVerdictTimeline(escalatedFolder,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildInfraCrashDetails(crashedAspects, reason));

        // Escalate, NOT Reissue: the environmental infra crash adds no spend to
        // the current attempt epoch. The existing ceiling is retained until an
        // operator explicitly requeues the card.
        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(multi-aspect run; reviewing CLI produced no verdict after environmental retry)",
            Response: AspectSummaryLine(report),
            FollowUp: string.Empty)
        {
            CouncilReaction = councilReaction,
        },
            current.FolderPath,
            escalatedFolder);

        _statusSnapshot.RecordEscalate();

        _ = ct; // no async CLI work on this deterministic path; keep the signature uniform
        await Task.CompletedTask;
    }

    /// <summary>
    /// Timeline details for an aspect-verdict InfraCrash escalation (AGT-2021).
    /// Carries the <c>environmental</c> + <c>InfraCrash</c> flags so the frontend
    /// and any reviewer read it as an infra blip, not a failed change.
    /// </summary>
    private static Dictionary<string, string> BuildInfraCrashDetails(IReadOnlyList<string> crashedAspects, string reason)
        => new()
        {
            ["cause"] = "aspect-verdict-infra-crash",
            ["environmental"] = "true",
            ["issueKind"] = RunIssueKind.InfraCrash.ToString(),
            ["aspects"] = string.Join(", ", crashedAspects),
            ["reason"] = Truncate(reason, 600),
        };

    /// <summary>
    /// True when the LATEST run for this task produced no new commit - HEAD was
    /// unchanged across the run (<see cref="RunRecord.HeadShaBefore"/> equals
    /// <see cref="RunRecord.HeadShaAfter"/>). That is the "empty follow-up diff"
    /// signal the <see cref="ReissueLoopBreaker"/> reads: a re-issued run that
    /// found nothing left to do and committed nothing. Conservative by design -
    /// when the timeline, session events, or the before/after SHAs are missing or
    /// unresolvable, it returns <c>false</c> so the empty-diff accept never fires
    /// on a guess; the budget rule still breaks the loop.
    /// </summary>
    private bool IsLatestRunEmptyDiff(TaskInfo job, string? watchPath)
    {
        if (_sessions == null) return false;
        try
        {
            var events = _sessions.ReadSessionEvents(job.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(job.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            if (timeline.Runs.Count == 0) return false;
            var last = timeline.Runs[^1];
            return !string.IsNullOrWhiteSpace(last.HeadShaBefore)
                && !string.IsNullOrWhiteSpace(last.HeadShaAfter)
                && string.Equals(last.HeadShaBefore, last.HeadShaAfter, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: empty-diff probe failed for {JobId}; treating as non-empty",
                job.Id);
            return false;
        }
    }

    /// <summary>
    /// Loop-break accept (ASS-794): a re-issued card came back with an empty
    /// follow-up diff while its close-out was already clean, so the aspect block
    /// is the +0/-0 diff-attribution false negative, not real missing work.
    /// Accept it to <c>5-human-review</c> (ADR-0025 - the human still confirms),
    /// reconciling the aspect-concern chips and recording the final
    /// Orchestrator-Review decision row so the Overview pipeline shows the ruling.
    /// </summary>
    private async Task AcceptOnLoopBreakAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        AspectRunReport report,
        ReissueLoopBreaker.Decision loopBreak,
        CancellationToken ct)
    {
        await Task.CompletedTask;

        ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

        var move = GuardedMoveJob(
            current.Id, TaskStates.HumanReview, entry.Path,
            transitionCause: LaneChangeCauses.ReviewVerdict, transitionDetail: "loop-break-accept");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after loop-break accept: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var movedInfo = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        CompletionAcceptanceRecord.MarkAccepted(
            movedFolderPath, "pipeline-execution.json", loopBreak.Reason, _logger);
        if (!string.Equals(movedFolderPath, current.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ConcernTagWriter.ReconcileConcernTags(movedFolderPath, report.ConcernTagIds, _logger);
        }

        RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Passed,
            DecisionVerdictAccept, loopBreak.Reason);

        // Provenance: the orchestrator (not a human) advanced this card.
        ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);

        var title = string.IsNullOrWhiteSpace(movedInfo.Title) ? movedInfo.Id : movedInfo.Title;
        var note =
            $"Auto-review accepted \"{title}\" as done (loop-break: empty follow-up diff on a clean re-run). " +
            "Moved to 5-human-review for your approval.";
        _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision, note);

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
            TimelineActors.Orchestrator, note, new Dictionary<string, string>
            {
                ["verdict"] = "accept",
                ["loopBreak"] = "empty-diff",
                ["reason"] = Truncate(loopBreak.Reason, 600),
            });

        _statusSnapshot.RecordAccept();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: loopBreak.Reason,
            Prompt: "(loop-break: empty follow-up diff on clean re-run)",
            Response: AspectSummaryLine(report),
            FollowUp: string.Empty),
            current.FolderPath,
            movedFolderPath);
    }

    /// <summary>
    /// Loop-break escalate (ASS-794): the shared reissue budget is spent, so the
    /// card must not loop back to <c>2-ready</c> again. Hand it to
    /// <c>5e-escalated</c> with the blocking aspect concerns surfaced and record
    /// the final Orchestrator-Review escalate row.
    /// </summary>
    private void EscalateOnLoopBreak(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        AspectRunReport report,
        ReissueLoopBreaker.Decision loopBreak,
        RepeatedAspectBlockDiagnosis? repeatedBlock)
    {
        ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

        _chatLog.AppendSupervisor(current, "escalate",
            $"Auto-review stopped its reissue loop. Reason: {loopBreak.Reason}. Promoted to {TaskStates.Escalated}.");

        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: loopBreak.Cause);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after loop-break escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
        RecordOrchestratorDecisionStep(escalatedFolder, PipelineStepStatus.Failed,
            DecisionVerdictEscalate, loopBreak.Reason);

        EmitVerdictTimeline(escalatedFolder, TimelineEventKinds.OrchestratorEscalated,
            TimelineActors.Orchestrator, loopBreak.Reason,
            BuildEscalateDetails(loopBreak.Cause, loopBreak.Reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        _statusSnapshot.RecordEscalate();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: loopBreak.Reason,
            Prompt: $"(loop-break: {loopBreak.Cause})",
            Response: AspectSummaryLine(report),
            FollowUp: string.Empty)
        {
            FailureKind = RepeatedAspectBlockPolicy.FailureKind,
            FailureFingerprint = repeatedBlock?.Fingerprint,
            FailureFingerprints = repeatedBlock?.CurrentFingerprints,
        },
            current.FolderPath,
            escalatedFolder);
    }

    /// <summary>
    /// Drive a blocking evidence-gate decision (ASS-764) to a conclusion. The
    /// aspects passed (or only raised non-blocking concerns), but the run is
    /// unverified: a UI/bug task with no visual proof, or an unclean
    /// tests-and-evidence aspect. Reissue (budget left) sends the card back to
    /// 2-ready with a verification demand foregrounded; escalate (budget spent)
    /// hands it to 5e-escalated. Records the final
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> row so the
    /// Overview pipeline shows the ruling. The in-flight pipeline run was already
    /// completed by the caller, so the record travels with the lane move.
    /// </summary>
    private async Task HandleEvidenceGateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        EvidenceGate.Decision gate,
        CancellationToken ct)
    {
        var findingsBlock = string.Join("; ", gate.Findings.Take(EvidenceGate.MaxFindings));
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);

        if (gate.Action == EvidenceGate.EvidenceGateAction.Escalate)
        {
            // Reconcile the aspect-concern chips to this pass's set before
            // handing to a human, same as the accept path, so the review starts
            // from a current tag set rather than stale chips.
            ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

            _chatLog.AppendSupervisor(current, "escalate",
                $"Auto-review could not verify this task's result. Reason: {gate.Reason}. Promoted to {TaskStates.Escalated}.");

            var move = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "evidence-gate");
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after evidence-gate escalate: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }

            var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
            RecordOrchestratorDecisionStep(escalatedFolder, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, gate.Reason);

            EmitVerdictTimeline(escalatedFolder,
                TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, gate.Reason,
                BuildEscalateDetails("evidence-gate", gate.Reason, priorReissues));

            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: gate.Reason,
                Prompt: "(evidence-gate static check)",
                Response: findingsBlock,
                FollowUp: string.Empty),
                current.FolderPath,
                escalatedFolder);

            _statusSnapshot.RecordEscalate();
            return;
        }

        // Reissue: foreground the verification demand so the next run proves the
        // result instead of re-asserting it.
        var followUp = EvidenceGate.BuildFollowUp(gate);
        var moved = MoveReissueToReadyTop(current, entry, "evidence-gate");
        if (moved == null)
        {
            // Move failed -> no operator-facing banner; the DONE stays unresolved
            // and the next tick retries.
            return;
        }

        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, gate.Reason);

        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var count = gate.Findings.Count;
        var noun = count == 1 ? "item" : "items";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready ({count} unverified {noun}; evidence required).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: evidence gate requires verification for {count} {noun}.",
            BuildReopenDetails("evidence-gate",
                CountPriorReissues(workspace, entry.Name, current.Id), findingsBlock));

        _statusSnapshot.RecordReissue();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: gate.Reason,
            Prompt: "(evidence-gate static check)",
            Response: findingsBlock,
            FollowUp: followUp),
            current.FolderPath,
            moved.FolderPath);
    }

    /// <summary>
    /// Drive a solution-quality gate decision to a conclusion. The aspect pass
    /// did not produce a hard BLOCK, but requirement-fit / code-quality raised a
    /// narrow non-shippable concern such as "goal not met", redundant work, or a
    /// half-finished implementation. Those signals should not be advanced as
    /// ordinary accept-with-concerns; they reuse the same bounded reissue /
    /// escalate path as the other auto-review gates.
    /// </summary>
    private async Task HandleSolutionQualityGateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        SolutionQualityGate.Decision gate,
        CancellationToken ct)
    {
        var findingsBlock = string.Join("; ", gate.Findings.Take(SolutionQualityGate.MaxFindings));
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);

        if (gate.Action == SolutionQualityGate.SolutionQualityGateAction.Escalate)
        {
            ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

            _chatLog.AppendSupervisor(current, "escalate",
                $"Auto-review could not clear solution-quality concerns. Reason: {gate.Reason}. Promoted to {TaskStates.Escalated}.");

            var move = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "solution-quality-gate");
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after solution-quality-gate escalate: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }

            var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
            var escalated = current with { FolderPath = escalatedFolder, State = TaskStates.Escalated };
            RecordOrchestratorDecisionStep(escalatedFolder, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, gate.Reason);
            WritePostProcessingOutcome(escalated, PostProcessingOutcomes.NeedsHumanInput,
                summary: gate.Reason,
                stepId: PipelineCatalogue.OrchestratorDecisionStepId,
                evidenceRef: "pipeline-execution.json",
                findingRefs: report.Verdicts
                    .Where(v => v.Status == AspectStatus.Concerns &&
                                (string.Equals(v.Aspect, SolutionQualityGate.RequirementFitAspectId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(v.Aspect, SolutionQualityGate.CodeQualityAspectId, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => $"aspect-{v.Aspect}.md")
                    .ToList(),
                performer: PostProcessingPerformers.Orchestrator);

            EmitVerdictTimeline(escalatedFolder,
                TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, gate.Reason,
                BuildEscalateDetails("solution-quality-gate", gate.Reason, priorReissues));

            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: gate.Reason,
                Prompt: "(solution-quality-gate static check)",
                Response: findingsBlock,
                FollowUp: string.Empty),
                current.FolderPath,
                escalatedFolder);

            _statusSnapshot.RecordEscalate();
            return;
        }

        var followUp = SolutionQualityGate.BuildFollowUp(gate);
        var moved = MoveReissueToReadyTop(current, entry, "solution-quality-gate");
        if (moved == null)
        {
            // Move failed -> no operator-facing banner; the DONE stays unresolved
            // and the next tick retries.
            return;
        }

        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, gate.Reason);
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.NeedsFollowUpTask,
            summary: gate.Reason,
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json",
            findingRefs: report.Verdicts
                .Where(v => v.Status == AspectStatus.Concerns &&
                            (string.Equals(v.Aspect, SolutionQualityGate.RequirementFitAspectId, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(v.Aspect, SolutionQualityGate.CodeQualityAspectId, StringComparison.OrdinalIgnoreCase)))
                .Select(v => $"aspect-{v.Aspect}.md")
                .ToList(),
            performer: PostProcessingPerformers.Orchestrator);

        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var count = gate.Findings.Count;
        var noun = count == 1 ? "concern" : "concerns";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready ({count} blocking solution-quality {noun}).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: solution-quality gate requires follow-up for {count} {noun}.",
            BuildReopenDetails("solution-quality-gate",
                CountPriorReissues(workspace, entry.Name, current.Id), findingsBlock));

        _statusSnapshot.RecordReissue();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: gate.Reason,
            Prompt: "(solution-quality-gate static check)",
            Response: findingsBlock,
            FollowUp: followUp),
            current.FolderPath,
            moved.FolderPath);
    }

    private async Task<AgentStudio.Pipeline.QualityAnalysisStepResult?>
        RunQualityAngularRulesPostStepAsync(
            WatchPathEntry entry,
            TaskInfo current,
            int? runIndex,
            CancellationToken ct)
    {
        if (_qualityAnalysisRunner is null) return null;
        var repositoryPath = string.IsNullOrWhiteSpace(entry.RepositoryPath)
            ? entry.RootPath
            : entry.RepositoryPath;
        AgentStudio.Pipeline.QualityAnalysisStepResult result;
        try
        {
            result = await _qualityAnalysisRunner.RunAngularRulesAsync(
                repositoryPath,
                current.FolderPath,
                ResolveLatestRunChangedFiles(current, entry.Path),
                runIndex,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Quality Studio Angular analysis failed for {Project}/{JobId}",
                entry.Name, current.Id);
            result = new AgentStudio.Pipeline.QualityAnalysisStepResult(
                PipelineCatalogue.QualityAngularRulesStepId,
                AgentStudio.Pipeline.QualityAnalysisStepVerdict.Unavailable,
                0,
                ex.Message,
                null,
                [],
                []);
        }

        if (_pipelineLog is not null)
        {
            var status = result.Verdict switch
            {
                AgentStudio.Pipeline.QualityAnalysisStepVerdict.Passed => PipelineStepStatus.Passed,
                AgentStudio.Pipeline.QualityAnalysisStepVerdict.Findings
                    when result.BlockingFindings.Count > 0 => PipelineStepStatus.Failed,
                AgentStudio.Pipeline.QualityAnalysisStepVerdict.Findings => PipelineStepStatus.Passed,
                AgentStudio.Pipeline.QualityAnalysisStepVerdict.NotApplicable => PipelineStepStatus.NotApplicable,
                _ => PipelineStepStatus.Skipped,
            };
            var now = DateTime.UtcNow;
            _pipelineLog.RecordStep(current.FolderPath, new PipelineStepExecution
            {
                StepId = result.StepId,
                Kind = StepKind.Analysis,
                Status = status,
                StartedAt = now - TimeSpan.FromMilliseconds(result.DurationMs),
                CompletedAt = now,
                DurationMs = result.DurationMs,
                Verdict = result.Verdict.ToString().ToLowerInvariant(),
                Reason = result.Reason,
                EvidenceRef = result.EvidencePath,
            });
        }
        return result;
    }

    internal static CompletionGate.Decision? BuildQualityAnalysisGateDecision(
        AgentStudio.Pipeline.QualityAnalysisStepResult result,
        int priorReissues,
        int maxReissues)
    {
        if (result.BlockingFindings.Count == 0) return null;

        return new CompletionGate.Decision
        {
            Action = priorReissues >= maxReissues
                ? CompletionGate.CompletionGateAction.Escalate
                : CompletionGate.CompletionGateAction.Reissue,
            Findings = result.BlockingFindings.Select(finding =>
            {
                var location = finding.Locations.FirstOrDefault();
                var reference = location is null
                    ? finding.RuleId
                    : $"{finding.RuleId} {location.Path}:{location.StartLine ?? 1}";
                return $"{reference}: {finding.Title}";
            }).ToList(),
            Reason = "Quality Studio named-rule findings require a steered retry.",
        };
    }

    /// <summary>
    /// Drive the lint-scss post-step: resolve mode through
    /// <see cref="PostStepConfigResolver"/>, invoke the runner, record
    /// the verdict on <c>pipeline-execution.json</c>, and write the
    /// per-run log file under <c>post-steps/</c>. Returns null when the
    /// runner is not wired (test path) or when mode = off so the caller
    /// can short-circuit. Never throws: a broken local stylelint
    /// toolchain falls through to a <see cref="LintScssVerdict.Skipped"/>
    /// verdict rather than crash the orchestrator tick.
    /// </summary>
    private async Task<LintScssResult?> RunLintScssPostStepAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        CancellationToken ct)
    {
        if (_lintScssRunner == null) return null;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var lintStep = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.LintScssStepId, StringComparison.OrdinalIgnoreCase));
        if (lintStep is not null
            && !PipelineStepConfigResolver.ShouldRun(settings, lintStep, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = 0,
                AnyAspectFailed = false,
                TaskType = current.TaskType,
                Tags = current.Tags,
            }))
        {
            RecordLintScssStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "condition",
                reason: "pipeline condition did not match");
            return new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "condition");
        }

        var legacyMode = PostStepConfigResolver.Resolve(
            _configuration, current.FolderPath, PipelineCatalogue.LintScssStepId);
        var mode = PipelineStepConfigResolver.ResolveMode(settings, PipelineCatalogue.LintScssStepId, legacyMode);

        if (mode == PostStepMode.Off)
        {
            RecordLintScssStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off",
                reason: "post-step disabled by config");
            return new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "mode=off");
        }

        var repoPath = string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;
        var timeoutSeconds = _configuration.GetValue($"PostSteps:{PipelineCatalogue.LintScssStepId}:TimeoutSeconds", 120);

        LintScssResult result;
        try
        {
            result = await _lintScssRunner.RunAsync(repoPath, mode, TimeSpan.FromSeconds(timeoutSeconds), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: lint-scss post-step threw for {Project}/{JobId}; treating as skipped",
                entry.Name, current.Id);
            RecordLintScssStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "error", reason: ex.Message);
            return null;
        }

        var status = result.Verdict switch
        {
            LintScssVerdict.Ok      => PipelineStepStatus.Passed,
            LintScssVerdict.Warn    => PipelineStepStatus.Passed, // ran, no reissue
            LintScssVerdict.Fail    => PipelineStepStatus.Failed,
            LintScssVerdict.Skipped => PipelineStepStatus.Skipped,
            _ => PipelineStepStatus.Skipped,
        };
        var verdictToken = result.Verdict switch
        {
            LintScssVerdict.Ok      => "ok",
            LintScssVerdict.Warn    => "warn",
            LintScssVerdict.Fail    => "fail",
            LintScssVerdict.Skipped => "skipped",
            _ => "skipped",
        };
        RecordLintScssStep(current.FolderPath, status, result.DurationMs, verdictToken, result.Reason);
        WriteLintScssLog(current.FolderPath, result);
        return result;
    }

    private static string ResolveBuildTestGateRepositoryPath(WatchPathEntry entry)
        => string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;

    private BuildTestGateSubject ResolveBuildTestGateSubject(TaskInfo current, string? watchPath)
    {
        var remote = ReviewSubjectStore.Read(current.FolderPath);
        RunRecord? local = null;
        if (_sessions is not null)
        {
            var events = _sessions.ReadSessionEvents(current.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(current.FolderPath));
            local = SelectLastSuccessfulReviewRun(
                RunTimelineBuilder.Build(events, lines, DateTime.UtcNow).Runs);
        }

        // A Remote completion is authoritative for its fenced attempt. Local
        // session events are considered only when they describe a later local
        // completion, never as a way to infer a missing Remote subject.
        if (remote is not null
            && (local?.EndedAt is null || remote.CompletedAtUtc.UtcDateTime >= local.EndedAt.Value))
        {
            return new BuildTestGateSubject(
                remote.ResultSha,
                remote.AttemptChainId,
                remote.Executor,
                remote.ResultRef,
                IsRemote: true);
        }

        if (local is null)
        {
            return new BuildTestGateSubject(
                null, null, $"{Environment.MachineName}:{Environment.ProcessId}", null, IsRemote: false);
        }

        var attemptChainId = local.CapturedSessionId
            ?? local.InputSessionId
            ?? $"local:{local.StartedAt:O}:{local.Index}";
        return new BuildTestGateSubject(
            local.HeadShaAfter,
            attemptChainId,
            $"{Environment.MachineName}:{Environment.ProcessId}",
            null,
            IsRemote: false);
    }

    private sealed record BuildTestGateSubject(
        string? Sha,
        string? AttemptChainId,
        string Executor,
        string? SubjectRef,
        bool IsRemote);

    private void RecordLintScssStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.LintScssStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    private async Task<BuildTestGateResult?> RunBuildTestGatePostStepAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        CancellationToken ct)
    {
        if (_buildTestGateRunner == null) return null;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var step = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.BuildTestGateStepId, StringComparison.OrdinalIgnoreCase));
        if (step is not null
            && !PipelineStepConfigResolver.ShouldRun(settings, step, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = 0,
                AnyAspectFailed = false,
                TaskType = current.TaskType,
                Tags = current.Tags,
            }))
        {
            RecordBuildTestGateStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "condition",
                reason: "pipeline condition did not match");
            return new BuildTestGateResult(BuildTestGateVerdict.Skipped, null, 0, "",
                "condition", false, false);
        }

        var projectMode = PostStepConfigResolver.ParseMode(
            _configuration[$"PostSteps:{PipelineCatalogue.BuildTestGateStepId}:DefaultMode"])
            ?? PostStepMode.Fail;
        var legacyMode = PostStepConfigResolver.Resolve(
            current.FolderPath,
            PipelineCatalogue.BuildTestGateStepId,
            projectMode: projectMode);
        var mode = PipelineStepConfigResolver.ResolveMode(settings, PipelineCatalogue.BuildTestGateStepId, legacyMode);

        if (mode == PostStepMode.Off)
        {
            RecordBuildTestGateStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off",
                reason: "post-step disabled by config");
            return new BuildTestGateResult(BuildTestGateVerdict.Skipped, null, 0, "",
                "mode=off", false, false);
        }

        var repoPath = ResolveBuildTestGateRepositoryPath(entry);
        var subject = ResolveBuildTestGateSubject(current, entry.Path);
        var timeoutSeconds = _configuration.GetValue($"PostSteps:{PipelineCatalogue.BuildTestGateStepId}:TimeoutSeconds", 300);
        var infrastructureTimeoutSeconds = _configuration.GetValue(
            $"PostSteps:{PipelineCatalogue.BuildTestGateStepId}:InfrastructureTimeoutSeconds", 120);
        // The machine gate is held for a whole build+test run, so a card queued
        // behind a running gate must be willing to wait roughly one full run - not
        // the short infra-op SLA. Default to run-timeout + infra-timeout so waiting
        // behind a legitimate 15-25 min gate never escalates as "Timeout persisted"
        // (AGT-2182, 21.07.); still overridable for a saturated host.
        var queueWaitTimeoutSeconds = _configuration.GetValue(
            $"PostSteps:{PipelineCatalogue.BuildTestGateStepId}:QueueWaitTimeoutSeconds",
            timeoutSeconds + infrastructureTimeoutSeconds);
        var changedFiles = ResolveLatestRunChangedFiles(current, entry.Path);

        var request = new BuildTestGateRequest(
            repoPath,
            subject.Sha,
            subject.Executor,
            RequireExactSubject: true)
        {
            AttemptChainId = subject.AttemptChainId,
            SubjectRef = subject.SubjectRef,
            Project = entry.Name,
            WatchPath = entry.Path,
            JobId = current.Id,
            Lane = current.State,
            TestExecution = settings?.TestExecution,
            JobFolderPath = current.FolderPath,
            InfrastructureTimeout = TimeSpan.FromSeconds(Math.Max(1, infrastructureTimeoutSeconds)),
            QueueWaitTimeout = TimeSpan.FromSeconds(Math.Max(1, queueWaitTimeoutSeconds)),
            OnMachineGateWaiting = () => _statusSnapshot.SetCurrentStep(
                entry.Name, current.Id, AutoReviewActivitySteps.GateQueued),
            OnMachineGateAcquired = () => _statusSnapshot.SetCurrentStep(
                entry.Name, current.Id, AutoReviewActivitySteps.Gate),
        };

        BuildTestGateResult? result = null;
        for (var infrastructureRetry = 0;
             infrastructureRetry <= PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries;
             infrastructureRetry++)
        {
            try
            {
                result = await _buildTestGateRunner.RunAsync(
                    request, changedFiles, settings?.BuildProfile, mode,
                    TimeSpan.FromSeconds(timeoutSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ReviewDecisionOrchestrator: build-test gate threw for {Project}/{JobId}; classifying as review infrastructure",
                    entry.Name, current.Id);
                result = new BuildTestGateResult(
                    BuildTestGateVerdict.Fail, null, 0, ex.ToString(),
                    "build-test gate runner threw", false, false)
                {
                    GateId = request.GateId,
                    Repository = repoPath,
                    ExpectedSha = subject.Sha,
                    AttemptChainId = subject.AttemptChainId,
                    Executor = subject.Executor,
                    FailureKind = BuildTestGateFailureKind.ProcessLaunch,
                    FailureFingerprint = BuildTestGateRunner.Fingerprint(
                        BuildTestGateFailureKind.ProcessLaunch, ex.ToString()),
                    TerminationSignal = "processlaunch",
                };
            }

            var normalizedKind = result.Verdict == BuildTestGateVerdict.Fail
                                 && result.FailureKind == BuildTestGateFailureKind.None
                ? BuildTestGateRunner.ClassifyFailure(result.Output + "\n" + result.Reason)
                : result.FailureKind;
            if (result.Verdict == BuildTestGateVerdict.Fail
                && normalizedKind == BuildTestGateFailureKind.None)
                normalizedKind = BuildTestGateFailureKind.Code;
            result = result with
            {
                GateId = request.GateId,
                Repository = result.Repository ?? repoPath,
                ExpectedSha = result.ExpectedSha ?? subject.Sha,
                AttemptChainId = result.AttemptChainId ?? subject.AttemptChainId,
                Executor = result.Executor ?? subject.Executor,
                FailureKind = normalizedKind,
                FailureFingerprint = result.FailureFingerprint
                    ?? (normalizedKind == BuildTestGateFailureKind.None
                        ? null
                        : BuildTestGateRunner.Fingerprint(
                            normalizedKind, result.Reason + "\n" + result.Output)),
            };

            WriteBuildTestGateLog(current.FolderPath, result, changedFiles);
            if (!result.IsInfrastructureFailure
                || infrastructureRetry >= PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries)
                break;

            var retryNumber = infrastructureRetry + 1;
            var backoff = BuildTestGateRetryBackoff(retryNumber);
            _logger.LogWarning(
                "build_test_gate_infrastructure_retry project={Project} job_id={JobId} expected_sha={ExpectedSha} attempt_chain_id={AttemptChainId} gate_id={GateId} failure_kind={FailureKind} failure_fingerprint={FailureFingerprint} retry={Retry} backoff_ms={BackoffMs}",
                entry.Name, current.Id, subject.Sha ?? "missing", subject.AttemptChainId ?? "missing",
                result.GateId, result.FailureKind, result.FailureFingerprint ?? "missing",
                retryNumber, backoff.TotalMilliseconds);
            if (backoff > TimeSpan.Zero) await Task.Delay(backoff, ct);
        }

        if (result is null) return null;

        var status = result.Verdict switch
        {
            BuildTestGateVerdict.Ok => PipelineStepStatus.Passed,
            BuildTestGateVerdict.Warn => PipelineStepStatus.Passed,
            BuildTestGateVerdict.Fail => PipelineStepStatus.Failed,
            BuildTestGateVerdict.Skipped => PipelineStepStatus.Skipped,
            BuildTestGateVerdict.NotApplicable => PipelineStepStatus.NotApplicable,
            _ => PipelineStepStatus.Skipped,
        };
        var verdictToken = result.Verdict switch
        {
            BuildTestGateVerdict.Ok => "ok",
            BuildTestGateVerdict.Warn => "warn",
            BuildTestGateVerdict.Fail => "fail",
            BuildTestGateVerdict.Skipped => "skipped",
            BuildTestGateVerdict.NotApplicable => "not-applicable",
            _ => "skipped",
        };
        RecordBuildTestGateStep(current.FolderPath, status, result.DurationMs, verdictToken, result.Reason);

        _logger.LogInformation(
            "ReviewDecisionOrchestrator: build-test gate {Verdict} for {Project}/{JobId} in {DurationMs}ms (backend={Backend} frontend={Frontend} changedFiles={ChangedFiles})",
            result.Verdict, entry.Name, current.Id, result.DurationMs,
            result.RanBackendBuild, result.RanFrontendBuild,
            changedFiles?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");

        return result;
    }

    private IReadOnlyList<string>? ResolveLatestRunChangedFiles(TaskInfo job, string? watchPath)
    {
        if (_sessions == null || _git == null) return null;
        try
        {
            var events = _sessions.ReadSessionEvents(job.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(job.FolderPath));
            var latest = SelectLastSuccessfulReviewRun(
                RunTimelineBuilder.Build(events, lines, DateTime.UtcNow).Runs);
            if (latest == null) return null;
            return _git.GetFilesChangedInShaRange(job.Id, watchPath, latest.HeadShaBefore, latest.HeadShaAfter)
                .Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: changed-file probe failed for {JobId}; build-test gate will run conservatively",
                job.Id);
            return null;
        }
    }

    private void RunWikiTaskCrossReferenceStep(WatchPathEntry entry, TaskInfo current)
    {
        if (_wikiTaskCrossReferences == null) return;
        var root = string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            _wikiTaskCrossReferences.LinkAuto(root!, current,
                ResolveLatestRunChangedFiles(current, entry.Path) ?? Array.Empty<string>());
            _scanner.InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: wiki-task cross-reference step failed for {Project}/{JobId}",
                entry.Name, current.Id);
        }
    }

    private void RecordBuildTestGateStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    private void WriteBuildTestGateLog(
        string jobFolderPath,
        BuildTestGateResult result,
        IReadOnlyList<string>? changedFiles)
    {
        try
        {
            var dir = Path.Combine(jobFolderPath, "post-steps");
            Directory.CreateDirectory(dir);
            var index = Directory.EnumerateFiles(dir, "build-test-gate-*.log").Count() + 1;
            var path = Path.Combine(dir, $"build-test-gate-{index}.log");
            var processEvidence = JsonSerializer.Serialize(result.Processes, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            var selectionEvidence = JsonSerializer.Serialize(result.TestSelection, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            var body = $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} signal={result.TerminationSignal ?? "n/a"} durationMs={result.DurationMs}\n" +
                       $"gateId={result.GateId} failureKind={result.FailureKind} failureFingerprint={result.FailureFingerprint ?? "n/a"}\n" +
                       $"gateRunId={result.GateRunId ?? "n/a"} startedAtUtc={result.GateStartedAtUtc?.ToString("O") ?? "n/a"} completedAtUtc={result.GateCompletedAtUtc?.ToString("O") ?? "n/a"}\n" +
                       $"collision={result.GateCollisionDetected} queueWaitMs={result.GateQueueWaitMs}\n" +
                       $"repository={result.Repository ?? "n/a"} expectedSha={result.ExpectedSha ?? "n/a"} testedSha={result.TestedSha ?? "n/a"}\n" +
                       $"attemptChainId={result.AttemptChainId ?? "n/a"} executor={result.Executor ?? "n/a"} workspace={result.Workspace ?? "n/a"}\n" +
                       $"reason={result.Reason}\n" +
                       $"backend={result.RanBackendBuild} frontend={result.RanFrontendBuild}\n" +
                       $"changedFiles={(changedFiles == null ? "unknown" : string.Join(", ", changedFiles.Take(50)))}\n" +
                       "--- test-selection.json ---\n" +
                       selectionEvidence + "\n" +
                       "--- process-evidence.json ---\n" +
                       processEvidence + "\n" +
                       "--- last-300-lines ---\n" +
                       result.Output;
            File.WriteAllText(path, body);
            if (result.Findings.Count > 0)
            {
                var findingPath = Path.Combine(dir, $"test-findings-{index}.json");
                File.WriteAllText(findingPath, JsonSerializer.Serialize(new
                {
                    gateRunId = result.GateRunId,
                    testLevel = result.TestSelection?.Level,
                    blocking = false,
                    findings = result.Findings,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to persist build-test gate log under {TaskFolder}",
                jobFolderPath);
        }
    }

    /// <summary>
    /// Verdict tokens stamped on the
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> step. This is
    /// the orchestrator's single final ruling, aggregated from the parallel
    /// aspect verdicts (plus the lint gate): <c>accept</c> when every aspect
    /// passed, <c>accept-with-concerns</c> when it advances despite non-blocking
    /// concerns, <c>reissue</c> when it sends the task back to 2-ready, and
    /// <c>escalate</c> when it hands the task to a human. The FE Overview
    /// pipeline renders the token on the "Auto-review decision" row's pill.
    /// </summary>
    internal const string DecisionVerdictAccept = "accept";
    internal const string DecisionVerdictAcceptWithConcerns = "accept-with-concerns";
    internal const string DecisionVerdictReissue = "reissue";
    internal const string DecisionVerdictEscalate = "escalate";

    /// <summary>
    /// Verdict token for a clean post-core
    /// <see cref="PipelineCatalogue.OrchestratorReviewStepId"/> completeness
    /// check: the run's own close-out carried no unfinished-work evidence, so the
    /// gate let the task proceed to the aspect review. A non-clean gate stamps
    /// <see cref="DecisionVerdictReissue"/> or <see cref="DecisionVerdictEscalate"/>
    /// instead, mirroring the final decision row's vocabulary.
    /// </summary>
    internal const string ReviewVerdictComplete = "complete";

    /// <summary>
    /// Drive a non-clean completion-gate decision to a conclusion so the task can
    /// never park in 4-auto-review without a verdict. Reissue (budget left) sends
    /// the card back to 2-ready with the gate's findings foregrounded into a
    /// follow-up; escalate (budget spent) hands it to 5e-escalated. Both record
    /// the post-core <see cref="PipelineCatalogue.OrchestratorReviewStepId"/> row
    /// so the gate's ruling is visible in the Overview pipeline. The in-flight
    /// pipeline run is completed before the lane move so the record travels with
    /// the folder.
    /// </summary>
    private async Task HandleCompletionGateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        CompletionGate.Decision gate,
        CancellationToken ct)
    {
        _pipelineLog?.Complete(
            current.FolderPath,
            pendingStepReason: "Not run because the completion gate stopped this pipeline attempt: " + gate.Reason);

        var findingsBlock = string.Join("; ", gate.Findings.Take(CompletionGate.MaxFindings));
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);

        if (gate.Action == CompletionGate.CompletionGateAction.Escalate)
        {
            var escalationReason = HumanReviewEscalation.FormatReason(
                HumanReviewEscalationCategories.CompletionGateUnresolved,
                gate.Reason);
            _chatLog.AppendSupervisor(current, "escalate",
                $"Auto-review completion gate could not clear unfinished-work evidence. Reason: {escalationReason}. Promoted to {TaskStates.Escalated}.");

            var move = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "completion-gate");
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after completion-gate escalate: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }

            var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
            var escalated = current with { FolderPath = escalatedFolder, State = TaskStates.Escalated };
            RecordOrchestratorReviewStep(escalatedFolder, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, escalationReason);
            WritePostProcessingOutcome(escalated, PostProcessingOutcomes.NeedsHumanInput,
                summary: escalationReason,
                stepId: PipelineCatalogue.OrchestratorReviewStepId,
                evidenceRef: "pipeline-execution.json",
                findingRefs: gate.Findings.Take(CompletionGate.MaxFindings).ToList(),
                performer: PostProcessingPerformers.Orchestrator);

            EmitVerdictTimeline(escalatedFolder,
                TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, escalationReason,
                BuildEscalateDetails("completion-gate", escalationReason, priorReissues));

            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: escalationReason,
                Prompt: "(completion-gate static scan)",
                Response: findingsBlock,
                FollowUp: string.Empty),
                current.FolderPath,
                escalatedFolder);

            _statusSnapshot.RecordEscalate();
            return;
        }

        // Reissue: foreground the gate's findings so the next run finishes the
        // open work instead of restarting blind.
        var followUp = CompletionGate.BuildFollowUp(gate.Findings);
        var moved = MoveReissueToReadyTop(current, entry, "completion-gate");
        if (moved == null)
        {
            // Move failed -> no operator-facing banner; the DONE stays unresolved
            // and the next tick retries.
            return;
        }

        RecordOrchestratorReviewStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, gate.Reason);
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.NeedsFollowUpTask,
            summary: gate.Reason,
            stepId: PipelineCatalogue.OrchestratorReviewStepId,
            evidenceRef: "pipeline-execution.json",
            findingRefs: gate.Findings.Take(CompletionGate.MaxFindings).ToList(),
            performer: PostProcessingPerformers.Orchestrator);

        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready. {gate.Reason} Evidence: {findingsBlock}");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: {gate.Reason}",
            BuildReopenDetails("completion-gate",
                CountPriorReissues(workspace, entry.Name, current.Id), findingsBlock));

        _statusSnapshot.RecordReissue();

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: gate.Reason,
            Prompt: "(completion-gate static scan)",
            Response: findingsBlock,
            FollowUp: followUp),
            current.FolderPath,
            moved.FolderPath);
    }

    /// <summary>
    /// Record the post-core <see cref="PipelineCatalogue.OrchestratorReviewStepId"/>
    /// completeness-check row. This is the FIRST of the two "Orchestrator-Review"
    /// rows the Overview pipeline shows (the second is the final
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> decision). No-op
    /// when the pipeline log is not wired (stand-alone test path) or no run record
    /// exists yet.
    /// </summary>
    private void RecordOrchestratorReviewStep(
        string jobFolderPath,
        PipelineStepStatus status,
        string verdict,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorReviewStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = verdict,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Run the automatic post-CORE quality-grade code-review step (ASS-1657)
    /// and record it on the pipeline. It assigns an A/B/C/D grade and structured
    /// findings to the task's full change set with a quality-first model
    /// (<c>CodeReviewStep:DefaultModel</c>, the live Codex flagship by default)
    /// and hangs a
    /// <c>code-review:grade-*</c> tag on the card so the grade shows in the
    /// Overview and as a card badge. The caller applies the council policy to
    /// every returned report. Best-effort: any runtime failure is logged and
    /// returned as unavailable evidence, so it never invents a work deficiency.
    /// An unavailable service, explicit disable, or condition
    /// mismatch records an honest skipped row rather than remaining Pending.
    /// </summary>
    private async Task<AgentStudio.Review.CodeReviewStepReport?> RunCodeReviewGradePostStepAsync(
        WatchPathEntry entry,
        TaskInfo job,
        string taskBody,
        BuildTestGateResult? buildGateResult,
        CancellationToken ct)
    {
        var stepId = PipelineCatalogue.CodeReviewGradeStepId;
        if (_codeReviewStep == null)
        {
            RecordCodeReviewGradeTerminal(
                job.FolderPath,
                PipelineStepStatus.Skipped,
                "Quality-grade service is unavailable in this runtime.");
            return null;
        }

        // Opt-out switch; default on so every pipelined task carries a grade.
        if (!_configuration.GetValue("CodeReviewStep:AutoGrade", true))
        {
            RecordCodeReviewGradeTerminal(
                job.FolderPath,
                PipelineStepStatus.Skipped,
                "Quality grade disabled by CodeReviewStep:AutoGrade=false.");
            return null;
        }

        var projectSettings = PipelineTypeSettings.ForTask(
            _projectSettings?.Get(entry.Name),
            job);
        var catalogueStep = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (catalogueStep is not null
            && !PipelineStepConfigResolver.ShouldRun(projectSettings, catalogueStep, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = 0,
                AnyAspectFailed = false,
                TaskType = job.TaskType,
                Tags = job.Tags,
            }))
        {
            RecordCodeReviewGradeTerminal(
                job.FolderPath,
                PipelineStepStatus.Skipped,
                "Quality-grade pipeline condition did not match this task.");
            return null;
        }

        var startedAt = DateTime.UtcNow;
        string? selectedModel = null;
        string? selectedThinkingLevel = null;
        try
        {
            // Quality over cost: the grade pass defaults to the live Codex
            // flagship, while the bounded aspect reviews use the support model.
            // Configurable so a deployment can dial the grade model without
            // touching the aspects.
            var (defaultModel, defaultCli) = AgentStudio.Review.CodeReviewGradeModelSelector.Resolve(
                _configuration["CodeReviewStep:DefaultModel"],
                _configuration["CodeReviewStep:DefaultCli"]);
            var model = catalogueStep is null
                ? defaultModel
                : PipelineStepConfigResolver.ResolveModel(projectSettings, catalogueStep, defaultModel);
            var cli = PipelineStepConfigResolver.ResolveCliType(projectSettings, stepId) ?? defaultCli;
            var thinkingLevel = catalogueStep is null
                ? null
                : PipelineStepConfigResolver.ResolveThinkingLevel(projectSettings, catalogueStep, cli, model);
            selectedModel = model;
            selectedThinkingLevel = thinkingLevel;

            // Persist dispatch before the one-shot begins. A slow or interrupted
            // reviewer now reads Running instead of the misleading Pending state.
            _pipelineLog?.RecordStep(job.FolderPath, new PipelineStepExecution
            {
                StepId = stepId,
                Kind = StepKind.Orchestrator,
                Status = PipelineStepStatus.Running,
                StartedAt = startedAt,
                Model = selectedModel,
                ThinkingLevel = selectedThinkingLevel,
            });

            var (diff, commitLabel) = BuildGradeDiff(entry, job, buildGateResult);

            var request = new AgentStudio.Review.CodeReviewStepRequest(
                Project: entry.Name,
                JobId: job.Id,
                JobTitle: job.Title ?? job.Id,
                JobFolderPath: job.FolderPath,
                TaskBody: taskBody,
                Diff: diff,
                CliType: cli!,
                Model: model!)
            {
                Mode = AgentStudio.Review.CodeReviewMode.Grade,
                Commit = commitLabel,
                ThinkingLevel = thinkingLevel,
                ResultsInventory = ResultsInventory.Render(job.FolderPath),
                CardMode = ReviewCardMode.Describe(job.Mode),
            };

            var report = await _codeReviewStep.RunAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(report.ExecutionError))
            {
                _logger.LogWarning(
                    "code-review-grade: reviewer failed for {Project}/{JobId}: {Error}",
                    entry.Name, job.Id, report.ExecutionError);
                RecordCodeReviewGradeTerminal(
                    job.FolderPath,
                    PipelineStepStatus.Failed,
                    "Quality-grade step error: " + report.ExecutionError,
                    startedAt,
                    report.Model,
                    report.ThinkingLevel);
                return report;
            }

            var gradeToken = report.Grade is null
                ? "?"
                : AgentStudio.Review.CodeReviewGradeParsing.GradeToken(report.Grade.Value);
            // A grade is reporting evidence, never a lane gate: a D records as a
            // Failed row so it stands out in the Overview; A-C record Passed.
            var status = report.Grade == AgentStudio.Review.CodeReviewGrade.D
                ? PipelineStepStatus.Failed
                : PipelineStepStatus.Passed;

            _pipelineLog?.RecordStep(job.FolderPath, new PipelineStepExecution
            {
                StepId = stepId,
                Kind = StepKind.Orchestrator,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = report.DurationMs,
                Model = report.Model,
                ThinkingLevel = report.ThinkingLevel,
                Verdict = gradeToken,
                VerdictSummary = string.IsNullOrWhiteSpace(report.Summary) ? null : report.Summary,
            });

            WritePostProcessingOutcome(job, PostProcessingOutcomes.FindingsAdded,
                summary: $"Quality grade {gradeToken}: {report.Summary}",
                performerCliType: cli,
                stepId: stepId,
                evidenceRef: report.FileName);

            _logger.LogInformation(
                "code-review-grade: project={Project} job={JobId} grade={Grade} model={Model} file={File}",
                entry.Name, job.Id, gradeToken, report.Model, report.FileName);
            return report;
        }
        catch (OperationCanceledException)
        {
            RecordCodeReviewGradeTerminal(
                job.FolderPath,
                PipelineStepStatus.Failed,
                "Quality-grade step was cancelled before completion.",
                startedAt,
                selectedModel,
                selectedThinkingLevel);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-grade: post-step failed for {Project}/{JobId}; recording a failed row",
                entry.Name, job.Id);
            RecordCodeReviewGradeTerminal(
                job.FolderPath,
                PipelineStepStatus.Failed,
                "Quality-grade step error: " + ex.Message,
                startedAt,
                selectedModel,
                selectedThinkingLevel);
            return null;
        }
    }

    private void RecordCodeReviewGradeTerminal(
        string jobFolderPath,
        PipelineStepStatus status,
        string reason,
        DateTime? startedAt = null,
        string? model = null,
        string? thinkingLevel = null)
    {
        if (status is PipelineStepStatus.Failed or PipelineStepStatus.Skipped)
        {
            ConcernTagWriter.ClearCodeReviewGradeTags(jobFolderPath, _logger);
        }
        if (_pipelineLog == null) return;
        var completedAt = DateTime.UtcNow;
        var started = startedAt ?? completedAt;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CodeReviewGradeStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = started,
            CompletedAt = completedAt,
            DurationMs = Math.Max(0L, (long)(completedAt - started).TotalMilliseconds),
            Model = model,
            ThinkingLevel = thinkingLevel,
            Reason = reason,
        });
    }

    /// <summary>
    /// Drive the opt-in task-spawner post-step (AGT-2028). Resolves the
    /// per-project enable flag + spawn config, asks the best available model
    /// whether the settled change is relevant to the configured target project,
    /// and - on a conservative yes that is not already deduped - creates a
    /// follow-up card there. Reporting-only: it records a pipeline step + a
    /// timeline entry on the SOURCE task and NEVER gates the source lane
    /// decision. Never throws into the tick (except cancellation); any failure
    /// records a skipped / failed row.
    /// </summary>
    private async Task RunTaskSpawnerPostStepAsync(
        WatchPathEntry entry,
        TaskInfo current,
        AspectRunReport report,
        string taskBody,
        string statusSummary,
        string diffSummary,
        string resultsInventory,
        CancellationToken ct)
    {
        if (_taskSpawner == null) return;

        var stepId = PipelineCatalogue.TaskSpawnerStepId;
        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var catalogueStep = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase));
        var conditionCtx = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = report.Overall == AspectStatus.Block,
            TaskType = current.TaskType,
            Tags = current.Tags,
        };
        var shouldRun = catalogueStep is null
            ? PipelineStepConfigResolver.ShouldRun(settings, stepId, conditionCtx)
            : PipelineStepConfigResolver.ShouldRun(settings, catalogueStep, conditionCtx);
        if (!shouldRun)
        {
            RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config or condition");
            return;
        }

        var config = settings?.TaskSpawner;
        if (config == null || string.IsNullOrWhiteSpace(config.TargetProject))
        {
            RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "no-target", reason: "no TaskSpawner target project configured");
            return;
        }

        // Only spawn from a run that is settling into accept / accept-with-concerns.
        // A Block run is about to be reissued, so a follow-up would be premature;
        // the dedup ledger will still let a later good run spawn exactly once.
        if (report.Overall == AspectStatus.Block)
        {
            RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "source-blocked", reason: "source run blocked/reissued; not spawning");
            return;
        }

        var startedAt = DateTime.UtcNow;
        try
        {
            // Quality-first: the spawn evaluation defaults to the best available
            // model at max effort, layered under any per-project override.
            var (defaultModel, defaultCli, defaultThinking) = TaskSpawnerModelSelector.Resolve(
                _configuration["TaskSpawnerStep:DefaultModel"],
                _configuration["TaskSpawnerStep:DefaultCli"],
                _configuration["TaskSpawnerStep:DefaultThinkingLevel"]);
            var model = catalogueStep is null
                ? defaultModel
                : PipelineStepConfigResolver.ResolveModel(settings, catalogueStep, defaultModel);
            var cli = PipelineStepConfigResolver.ResolveCliType(settings, stepId) ?? defaultCli;
            var thinking = catalogueStep is null
                ? defaultThinking
                : PipelineStepConfigResolver.ResolveThinkingLevel(settings, catalogueStep, cli, model, defaultThinking);

            var runCtx = new TaskSpawnerRunContext
            {
                Source = current,
                SourceProjectName = entry.Name,
                TargetProject = config.TargetProject!.Trim(),
                RelevanceQuestion = config.RelevanceQuestion,
                SpawnLane = string.IsNullOrWhiteSpace(config.SpawnLane) ? TaskStates.Backlog : config.SpawnLane!.Trim(),
                MaxPerSourceTask = config.MaxPerSourceTask is > 0 ? config.MaxPerSourceTask.Value : 1,
                TaskBody = taskBody,
                StatusSummary = statusSummary,
                DiffSummary = diffSummary,
                ResultsInventory = resultsInventory,
                Model = model,
                Cli = cli,
                ThinkingLevel = thinking,
            };

            var result = await _taskSpawner.RunAsync(runCtx, ct);
            var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            switch (result.Verdict)
            {
                case TaskSpawnerVerdict.Spawned:
                    RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Passed, durationMs,
                        "spawned", result.Reason, result.Model,
                        verdictSummary: $"{result.TargetKey} in {result.TargetProjectName}");
                    WritePostProcessingOutcome(current, PostProcessingOutcomes.NeedsFollowUpTask,
                        summary: $"Spawned {result.TargetKey} in {result.TargetProjectName}: {result.Reason}",
                        performerCliType: cli,
                        stepId: stepId,
                        followUpTaskIds: string.IsNullOrWhiteSpace(result.TargetJobId)
                            ? null
                            : new[] { result.TargetJobId! });
                    EmitVerdictTimeline(current.FolderPath, TimelineEventKinds.TaskSpawned, TimelineActors.Orchestrator,
                        $"Spawned {result.TargetKey} in {result.TargetProjectName}",
                        new Dictionary<string, string>
                        {
                            ["targetProject"] = result.TargetProjectName ?? config.TargetProject!,
                            ["targetKey"] = result.TargetKey ?? string.Empty,
                            ["targetJobId"] = result.TargetJobId ?? string.Empty,
                            ["reason"] = result.Reason,
                        });
                    _logger.LogInformation(
                        "task-spawner: project={Project} job={JobId} spawned={TargetKey} target={TargetProject} model={Model}",
                        entry.Name, current.Id, result.TargetKey, result.TargetProjectName, result.Model);
                    break;
                case TaskSpawnerVerdict.NotRelevant:
                    RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped, durationMs,
                        "not-relevant", result.Reason, result.Model);
                    break;
                case TaskSpawnerVerdict.Deduped:
                    RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped, durationMs,
                        "deduped", result.Reason, result.Model);
                    break;
                case TaskSpawnerVerdict.Error:
                    RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Failed, durationMs,
                        "error", result.Reason, result.Model);
                    break;
                default:
                    RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped, durationMs,
                        "skipped", result.Reason, result.Model);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "task-spawner: post-step failed for {Project}/{JobId}; recording a skipped row",
                entry.Name, current.Id);
            RecordTaskSpawnerStep(current.FolderPath, PipelineStepStatus.Skipped,
                (long)(DateTime.UtcNow - startedAt).TotalMilliseconds, "error",
                "task-spawner step error: " + ex.Message);
        }
    }

    private void RecordTaskSpawnerStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason,
        string? model = null,
        string? verdictSummary = null)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.TaskSpawnerStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Model = model,
            Verdict = verdictToken,
            VerdictSummary = string.IsNullOrWhiteSpace(verdictSummary) ? null : verdictSummary,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Build the diff text the quality-grade pass reviews. A fenced remote
    /// completion's persisted result SHA is authoritative, matching the
    /// build/test gate subject. Its review diff spans the merge base with the
    /// canonical task/integration branch through that result SHA, so a multi-
    /// commit remote task is reviewed as one change set rather than as only its
    /// final commit. Local runs use the aggregate diff of every commit the task
    /// owns, then fall back to HEAD and the live working tree.
    /// Best-effort: returns an empty diff with a "(no diff resolved)" label
    /// when git is not wired or resolution throws.
    /// </summary>
    private (string Diff, string? CommitLabel) BuildGradeDiff(
        WatchPathEntry entry,
        TaskInfo job,
        BuildTestGateResult? buildGateResult)
    {
        var project = entry.Name;
        var watchPath = entry.Path;
        if (_git == null) return (string.Empty, null);
        try
        {
            var remoteSubject = ResolveRemoteGradeSubject(job, watchPath, buildGateResult);
            if (remoteSubject is not null)
            {
                var remoteRange = TryBuildRemoteGradeDiff(entry, job, remoteSubject.ResultSha);
                if (remoteRange is not null) return remoteRange.Value;

                // A missing canonical base must not make the grade fall back to
                // an unrelated local HEAD. The exact fenced result remains the
                // safe last-resort subject, even though it can only show the tip
                // commit when the repository has no resolvable fork point.
                return (
                    _git.GetCommitDiff(job.Id, watchPath, remoteSubject.ResultSha, path: null),
                    remoteSubject.ResultSha[..8] + " (remote result; base unresolved)");
            }

            IReadOnlyList<string> taskShas = Array.Empty<string>();
            if (_sessions != null)
            {
                var events = _sessions.ReadSessionEvents(job.Id, watchPath);
                var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(job.FolderPath));
                var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
                var aggregate = TaskCommitsAggregator.Aggregate(job, SelectAuthoritativeReviewRuns(timeline.Runs),
                    (before, after) => _git!.GetCommitsInShaRange(job.Id, watchPath, before, after));
                taskShas = aggregate.Commits
                    .Select(c => c.Sha)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var scope = AgentStudio.Review.CodeReviewScopeResolver.Resolve(
                overrideCommit: null,
                taskShas,
                _git.GetHeadSha(job.Id, watchPath));

            var diff = scope.Mode switch
            {
                AgentStudio.Review.CodeReviewScopeMode.WorkingTree =>
                    "(no commit resolved; reviewing working-tree diff)\n\n" +
                    _git.GetDiff(job.Id, watchPath, path: null),
                AgentStudio.Review.CodeReviewScopeMode.AggregateCommits =>
                    _git.GetAggregateCommitDiff(job.Id, watchPath, scope.Shas, path: null),
                _ => _git.GetCommitDiff(job.Id, watchPath, scope.Shas[0], path: null),
            };

            // Post-squash/merge or steer follow-up: the run-window scope resolves
            // no commits and the working tree is clean, so the grade would review
            // an empty diff and mis-grade a real, landed change. Fall back to the
            // task-branch-vs-base range so the grade sees the actual change set.
            // The branch range is resolved lazily so a normal run (non-empty diff)
            // never touches git for a fallback it does not need.
            return SelectGradeDiff(diff, scope.Label, () => TryBuildBranchDiffSummary(entry, job));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-grade: diff resolution failed for {Project}/{JobId}; grading an empty diff",
                project, job.Id);
            return (string.Empty, null);
        }
    }

    private ReviewSubjectRecord? ResolveRemoteGradeSubject(
        TaskInfo job,
        string? watchPath,
        BuildTestGateResult? buildTestGateResult)
    {
        var remote = ReviewSubjectStore.Read(job.FolderPath);
        if (remote is null) return null;

        // Once the build/test gate has selected an exact subject, carry that
        // same fenced SHA through the later aspect and grade steps. Re-resolving
        // from mutable session history after a long gate can otherwise switch
        // the grade to a different local HEAD.
        if (ReviewSubjectStore.IsValidResultSha(buildTestGateResult?.ExpectedSha))
        {
            return string.Equals(
                buildTestGateResult!.ExpectedSha,
                remote.ResultSha,
                StringComparison.OrdinalIgnoreCase)
                ? remote
                : null;
        }

        var resolved = ResolveBuildTestGateSubject(job, watchPath);
        return resolved.IsRemote
               && string.Equals(resolved.Sha, remote.ResultSha, StringComparison.OrdinalIgnoreCase)
            ? remote
            : null;
    }

    private (string Diff, string CommitLabel)? TryBuildRemoteGradeDiff(
        WatchPathEntry entry,
        TaskInfo job,
        string resultSha)
    {
        if (_git == null) return null;
        var repoRoot = !string.IsNullOrWhiteSpace(entry.RepositoryPath)
            ? entry.RepositoryPath
            : entry.RootPath;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return null;

        var configuredBase = _projectSettings?.Get(entry.Name)?.IntegrationBranch;
        var integrationBranch = _git.ResolveIntegrationBranch(repoRoot, configuredBase);
        var candidateBases = new[]
        {
            job.Provenance?.Branch,
            $"task/{job.Id}",
            integrationBranch,
        }
        .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
        .Select(candidate => candidate!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidateBase in candidateBases)
        {
            var mergeBase = _git.GetMergeBase(repoRoot, candidateBase, resultSha);
            if (!ReviewSubjectStore.IsValidResultSha(mergeBase)) continue;

            var diff = _git.GetDiffInShaRange(
                job.Id, entry.Path, mergeBase, resultSha, path: null);
            if (string.IsNullOrWhiteSpace(diff)) continue;

            return (
                diff,
                $"{mergeBase![..8]}..{resultSha[..8]} (remote task range vs {candidateBase})");
        }

        return null;
    }

    /// <summary>
    /// Pure selection for the code-review grade diff (AGT-2022): grade the
    /// run-window <paramref name="workingDiff"/> whenever it is non-empty, but
    /// fall back to the task-branch-vs-base range when it is blank - the
    /// post-squash/merge or steer-follow-up case where the working tree is clean
    /// yet the branch still carries the real, landed change set. The branch range
    /// is produced by <paramref name="branchDiffFactory"/> and consulted ONLY on
    /// the empty-working-diff path, so a normal run is graded on exactly what it
    /// changed and never pays for a git range it does not need. When the fallback
    /// fires, the label is annotated <c>(branch range vs base)</c> so the grade
    /// record makes the source of the diff explicit. A genuinely empty
    /// deliverable (blank working diff AND no branch range) stays empty here - the
    /// results/ inventory and card-mode framing carry the read-only case in the
    /// prompt, not this diff selection.
    ///
    /// <para>
    /// Pure aside from the injected factory so a unit test can pin all three
    /// branches (fallback fires / working diff wins / no branch available)
    /// without a live repository.
    /// </para>
    /// </summary>
    internal static (string Diff, string? CommitLabel) SelectGradeDiff(
        string workingDiff, string? scopeLabel, Func<string?> branchDiffFactory)
    {
        if (string.IsNullOrWhiteSpace(workingDiff))
        {
            var branchSummary = branchDiffFactory();
            if (!string.IsNullOrWhiteSpace(branchSummary))
                return (branchSummary!, string.IsNullOrWhiteSpace(scopeLabel)
                    ? "(branch range vs base)"
                    : $"{scopeLabel} (branch range vs base)");
        }
        return (workingDiff, scopeLabel);
    }

    private void WritePostProcessingOutcome(
        TaskInfo job,
        string outcome,
        string? summary,
        string? performerCliType = null,
        string? stepId = null,
        string? evidenceRef = null,
        IReadOnlyList<string>? findingRefs = null,
        IReadOnlyList<string>? followUpTaskIds = null,
        string performer = PostProcessingPerformers.SupportingAgent)
    {
        if (!PostProcessingOutcomes.All.Contains(outcome, StringComparer.Ordinal))
        {
            outcome = PostProcessingOutcomes.FailedPostProcessing;
        }

        PostProcessingOutcomeLog.Append(job.FolderPath, new PostProcessingOutcomeRecord
        {
            At = DateTime.UtcNow,
            JobId = job.Id,
            Project = job.ProjectName,
            Outcome = outcome,
            Performer = performer,
            PerformerCliType = performerCliType,
            StepId = stepId,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
            EvidenceRef = evidenceRef,
            FindingRefs = findingRefs?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList() ?? [],
            FollowUpTaskIds = followUpTaskIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList() ?? [],
        }, _logger);

        if (!string.Equals(outcome, PostProcessingOutcomes.FindingsAdded, StringComparison.Ordinal))
        {
            var failed = !string.Equals(
                outcome,
                PostProcessingOutcomes.PassToHumanReview,
                StringComparison.Ordinal);
            PostProcessingLifecycleStore.Terminalize(
                job.FolderPath,
                DateTime.UtcNow,
                failed,
                string.IsNullOrWhiteSpace(summary)
                    ? $"Post Processing reached terminal outcome {outcome}."
                    : summary!,
                _logger,
                onlyWhenActive: true);
        }

        _logger.LogInformation(
            "post-processing-outcome project={Project} job={JobId} outcome={Outcome} performer={Performer} cli={CliType} step={StepId}",
            job.ProjectName, job.Id, outcome, performer, performerCliType ?? "", stepId ?? "");
    }

    /// <summary>
    /// Record the <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/>
    /// step with the orchestrator's single aggregated final verdict. This is
    /// the step the Overview pipeline shows as the distinct "Auto-review
    /// decision" row beneath the parallel aspect rows - the catalogue defines
    /// it (<see cref="StepKind.Orchestrator"/>, depends on every aspect) but
    /// nothing recorded an outcome before, so it stayed <c>Pending</c> forever.
    /// Recorded on the job's post-move folder so the record lands where the
    /// pipeline-execution.json now lives. No-op when the pipeline log is not
    /// wired (stand-alone test path).
    /// </summary>
    private void RecordOrchestratorDecisionStep(
        string jobFolderPath,
        PipelineStepStatus status,
        string verdict,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = verdict,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Persist the truncated stylelint output for the FE timeline to
    /// render on click-to-expand. One file per run-index so the user can
    /// see previous-run output side-by-side with the current attempt;
    /// the index is derived from how many <c>lint-scss-*.log</c> files
    /// already exist in <c>post-steps/</c>.
    /// </summary>
    private void WriteLintScssLog(string jobFolderPath, LintScssResult result)
    {
        try
        {
            var dir = Path.Combine(jobFolderPath, "post-steps");
            Directory.CreateDirectory(dir);
            var index = Directory.EnumerateFiles(dir, "lint-scss-*.log").Count() + 1;
            var path = Path.Combine(dir, $"lint-scss-{index}.log");
            var body = $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs}\n" +
                       $"reason={result.Reason}\n" +
                       "---\n" +
                       result.Output;
            File.WriteAllText(path, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to persist lint-scss log under {TaskFolder}",
                jobFolderPath);
        }
    }

    /// <summary>
    /// Drive the regression-radar post-step: resolve the per-project enable
    /// flag, run the deterministic spec-change analysis, and record the
    /// verdict + duration on <c>pipeline-execution.json</c>. Reporting only -
    /// returns void because the outcome never gates the move to review.
    /// Skips silently when neither a test analyzer seam nor the injected
    /// <see cref="RegressionRadarService"/> is wired (stand-alone test path).
    /// Never throws: a broken analysis falls through to a
    /// <see cref="PipelineStepStatus.Skipped"/> record rather than crash the
    /// orchestrator tick.
    /// </summary>
    private void RunRegressionRadarPostStep(WatchPathEntry entry, TaskInfo current)
    {
        var analyze = RegressionRadarAnalyzer
            ?? (_regressionRadar != null
                ? _regressionRadar.Analyze
                : (Func<string, string?, RegressionRadarResult>?)null);
        if (analyze == null) return;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var radarStep = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.RegressionRadarStepId, StringComparison.OrdinalIgnoreCase));
        var shouldRun = radarStep is null
            ? PipelineStepConfigResolver.ShouldRun(settings, PipelineCatalogue.RegressionRadarStepId, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = 0,
                AnyAspectFailed = false,
                TaskType = current.TaskType,
                Tags = current.Tags,
            })
            : PipelineStepConfigResolver.ShouldRun(settings, radarStep, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = 0,
                AnyAspectFailed = false,
                TaskType = current.TaskType,
                Tags = current.Tags,
            });
        if (!shouldRun)
        {
            RecordRegressionRadarStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config or condition");
            return;
        }

        var sw = Stopwatch.StartNew();
        RegressionRadarResult result;
        try
        {
            result = analyze(current.Id, entry.Path);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: regression-radar post-step threw for {Project}/{JobId}; treating as skipped",
                entry.Name, current.Id);
            RecordRegressionRadarStep(current.FolderPath, PipelineStepStatus.Skipped,
                sw.ElapsedMilliseconds, "error", ex.Message);
            return;
        }
        sw.Stop();

        var (status, verdict, reason) = MapRegressionRadarOutcome(result);
        RecordRegressionRadarStep(current.FolderPath, status, sw.ElapsedMilliseconds, verdict, reason);
    }

    private void RecordRegressionRadarStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.RegressionRadarStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Validates the Dossier update promised in the CORE prompt and records the
    /// result as a first-class pipeline timeline row. A missing required entry
    /// gates delivery; an unreferenced card records NotApplicable.
    /// </summary>
    private DossierMaintenanceReview? RunDossierMaintenancePostStep(
        WatchPathEntry entry,
        TaskInfo current)
    {
        if (_dossierMaintenance == null) return null;
        var startedAt = DateTime.UtcNow;
        DossierMaintenanceReview review;
        try
        {
            var repositoryRoot = string.IsNullOrWhiteSpace(entry.RepositoryPath)
                ? entry.RootPath
                : entry.RepositoryPath;
            review = _dossierMaintenance.Review(entry.Name, repositoryRoot, current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dossier maintenance review failed for {Project}/{JobId}",
                entry.Name,
                current.Id);
            review = new DossierMaintenanceReview(
                true,
                false,
                Array.Empty<DossierMaintenanceTarget>(),
                new[] { "Dossier maintenance review failed: " + ex.Message });
        }

        var completedAt = DateTime.UtcNow;
        _pipelineLog?.RecordStep(
            current.FolderPath,
            DossierMaintenanceStepPolicy.ToExecution(review, startedAt, completedAt));
        return review;
    }

    /// <summary>
    /// Run the opt-in project wiki maintenance post-step. It is deliberately
    /// non-gating: any failure records a failed/skipped step and the review
    /// decision continues from the task's actual aspect/evidence verdict.
    /// </summary>
    private void RunWikiMaintenancePostStep(WatchPathEntry entry, TaskInfo current)
    {
        if (_wikiMaintenance == null) return;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var wikiStep = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.WikiMaintenanceStepId, StringComparison.OrdinalIgnoreCase));
        var ctx = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = false,
            TaskType = current.TaskType,
            Tags = current.Tags,
        };
        var shouldRun = wikiStep is null
            ? PipelineStepConfigResolver.ShouldRun(settings, PipelineCatalogue.WikiMaintenanceStepId, ctx)
            : PipelineStepConfigResolver.ShouldRun(settings, wikiStep, ctx);
        if (!shouldRun)
        {
            RecordWikiMaintenanceStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config or condition");
            return;
        }

        var sw = Stopwatch.StartNew();
        WikiMaintenanceResult result;
        try
        {
            result = _wikiMaintenance.Run(current, entry);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: wiki-maintenance post-step threw for {Project}/{JobId}; recording failure",
                entry.Name, current.Id);
            RecordWikiMaintenanceStep(current.FolderPath, PipelineStepStatus.Failed,
                sw.ElapsedMilliseconds, "error", ex.Message);
            return;
        }
        sw.Stop();

        var status = result.Verdict == WikiMaintenanceVerdict.Error
            ? PipelineStepStatus.Failed
            : result.Verdict == WikiMaintenanceVerdict.Skipped
                ? PipelineStepStatus.Skipped
                : PipelineStepStatus.Passed;
        var verdict = result.Verdict.ToString().ToLowerInvariant();
        var reason = result.Slug == null ? result.Reason : $"{result.Reason}: {result.Slug}";
        RecordWikiMaintenanceStep(current.FolderPath, status, sw.ElapsedMilliseconds, verdict, reason);
    }

    private void RecordWikiMaintenanceStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.WikiMaintenanceStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Opt-in wiki-learnings post-step (<c>post-wiki-learnings</c>). Distills the
    /// settled review into a per-task page under <c>docs/learnings</c> via
    /// the injected <see cref="WikiLearningsPostStepRunner"/>. Disabled by default
    /// and gated by per-project config (same switch the wiki-maintenance step
    /// uses). Reporting-only and fully non-gating: any failure records a
    /// failed / skipped step and the review decision continues unchanged.
    /// </summary>
    private void RunWikiLearningsPostStep(
        WatchPathEntry entry,
        TaskInfo current,
        AspectRunReport report,
        string statusSummary,
        string diffSummary)
    {
        if (_wikiLearnings == null) return;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var step = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.WikiLearningsStepId, StringComparison.OrdinalIgnoreCase));
        var ctx = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = report.Overall == AspectStatus.Block,
            TaskType = current.TaskType,
            Tags = current.Tags,
        };
        var shouldRun = step is null
            ? PipelineStepConfigResolver.ShouldRun(settings, PipelineCatalogue.WikiLearningsStepId, ctx)
            : PipelineStepConfigResolver.ShouldRun(settings, step, ctx);
        if (!shouldRun)
        {
            RecordWikiLearningsStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config or condition");
            return;
        }

        var sw = Stopwatch.StartNew();
        WikiLearningsResult result;
        try
        {
            var run = BuildWikiLearningsRun(report, current, statusSummary, diffSummary);
            result = _wikiLearnings.Run(current, entry, run);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: wiki-learnings post-step threw for {Project}/{JobId}; recording failure",
                entry.Name, current.Id);
            RecordWikiLearningsStep(current.FolderPath, PipelineStepStatus.Failed,
                sw.ElapsedMilliseconds, "error", ex.Message);
            return;
        }
        sw.Stop();

        var status = result.Verdict == WikiLearningsVerdict.Error
            ? PipelineStepStatus.Failed
            : result.Verdict == WikiLearningsVerdict.Skipped
                ? PipelineStepStatus.Skipped
                : PipelineStepStatus.Passed;
        var verdict = result.Verdict.ToString().ToLowerInvariant();
        var reason = result.Slug == null ? result.Reason : $"{result.Reason}: {result.Slug}";
        RecordWikiLearningsStep(current.FolderPath, status, sw.ElapsedMilliseconds, verdict, reason);
    }

    /// <summary>
    /// Pure mapping from the settled aspect report plus the run's evidence
    /// strings into the structured <see cref="WikiLearningsRun"/> the runner
    /// distills. Static + internal so the mapping (verdict derivation, finding
    /// projection, evidence trimming) is unit-testable without the orchestrator.
    /// </summary>
    internal static WikiLearningsRun BuildWikiLearningsRun(
        AspectRunReport report,
        TaskInfo current,
        string statusSummary,
        string diffSummary)
    {
        var verdict = report.Overall switch
        {
            AspectStatus.Block => "reissue",
            AspectStatus.Concerns => "accept-with-concerns",
            _ => "accept",
        };

        var findings = report.Verdicts
            .Select(v => new WikiLearningFinding(
                v.Aspect,
                AspectVerdictParsing.StatusToken(v.Status),
                v.Summary ?? string.Empty))
            .ToList();

        var verdictReason = string.IsNullOrWhiteSpace(report.FollowUpSummary)
            ? null
            : report.FollowUpSummary;

        var stumblingBlock = current.OutcomeIssue is { } issue
            ? string.IsNullOrWhiteSpace(issue.Summary)
                ? issue.Label
                : $"{issue.Label}: {issue.Summary}"
            : null;

        return new WikiLearningsRun(
            Verdict: verdict,
            VerdictReason: verdictReason,
            Findings: findings,
            AgentNotes: DistillStatusNotes(statusSummary),
            StumblingBlock: stumblingBlock,
            ChangedSummary: BuildChangedSummary(current, diffSummary));
    }

    /// <summary>
    /// Reduce the full status.md text to a short, single-paragraph note for the
    /// learnings page: drop headings, HTML comments, and blank lines, then take
    /// the first few meaningful lines capped at a readable length. Null when the
    /// status carries nothing distillable.
    /// </summary>
    private static string? DistillStatusNotes(string statusSummary)
    {
        if (string.IsNullOrWhiteSpace(statusSummary)) return null;
        var lines = statusSummary
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0
                && !l.StartsWith('#')
                && !l.StartsWith("---", StringComparison.Ordinal)
                && !l.StartsWith("<!--", StringComparison.Ordinal))
            .Take(3)
            .ToList();
        if (lines.Count == 0) return null;
        var joined = string.Join(" ", lines).Trim();
        return joined.Length <= 360 ? joined : joined[..359].TrimEnd() + "...";
    }

    /// <summary>
    /// Build a one-line "what changed" headline from the task's attributed
    /// commit chain (newest subject + commit count). Falls back to the diff
    /// summary's first non-empty line when no commit is attributed, and null
    /// when neither is available so the runner records "no commit recorded".
    /// </summary>
    private static string? BuildChangedSummary(TaskInfo current, string diffSummary)
    {
        if (current.Commits.Count > 0)
        {
            var newest = current.Commits[^1];
            var subject = (newest.Message ?? string.Empty).Split('\n', 2)[0].Trim();
            if (subject.Length == 0) subject = "(no subject)";
            var count = current.Commits.Count;
            var plural = count == 1 ? "commit" : "commits";
            return $"{count} {plural}; latest {newest.ShortSha}: {subject}";
        }

        if (!string.IsNullOrWhiteSpace(diffSummary))
        {
            var firstLine = diffSummary
                .Replace("\r", string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine;
        }

        return null;
    }

    private void RecordWikiLearningsStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.WikiLearningsStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Opt-in AGENTS/wiki-sync post-step (<c>post-agents-wiki-sync</c>). Keeps the
    /// designated-topic pointers in the project's AGENTS.md consistent and collects
    /// each designated topic's current state via the injected
    /// <see cref="AgentsWikiSyncPostStepRunner"/>. Disabled by default and gated by
    /// the same per-project switch the sibling wiki steps use. Reporting-only and
    /// fully non-gating: any failure records a failed / skipped step and the review
    /// decision continues unchanged.
    /// </summary>
    private void RunAgentsWikiSyncPostStep(WatchPathEntry entry, TaskInfo current)
    {
        if (_agentsWikiSync == null) return;

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(entry.Name), current);
        var step = PipelineCatalogue.Standard.Post.FirstOrDefault(s =>
            string.Equals(s.Id, PipelineCatalogue.AgentsWikiSyncStepId, StringComparison.OrdinalIgnoreCase));
        var ctx = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = false,
            TaskType = current.TaskType,
            Tags = current.Tags,
        };
        var shouldRun = step is null
            ? PipelineStepConfigResolver.ShouldRun(settings, PipelineCatalogue.AgentsWikiSyncStepId, ctx)
            : PipelineStepConfigResolver.ShouldRun(settings, step, ctx);
        if (!shouldRun)
        {
            RecordAgentsWikiSyncStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config or condition");
            return;
        }

        var sw = Stopwatch.StartNew();
        AgentsWikiSyncResult result;
        try
        {
            var changedFiles = ResolveLatestRunChangedFiles(current, entry.Path);
            result = _agentsWikiSync.Run(current, entry, changedFiles);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: agents-wiki-sync post-step threw for {Project}/{JobId}; recording failure",
                entry.Name, current.Id);
            RecordAgentsWikiSyncStep(current.FolderPath, PipelineStepStatus.Failed,
                sw.ElapsedMilliseconds, "error", ex.Message);
            return;
        }
        sw.Stop();

        var status = result.Verdict == AgentsWikiSyncVerdict.Error
            ? PipelineStepStatus.Failed
            : result.Verdict == AgentsWikiSyncVerdict.Skipped
                ? PipelineStepStatus.Skipped
                : PipelineStepStatus.Passed;
        var verdict = result.Verdict.ToString().ToLowerInvariant();
        RecordAgentsWikiSyncStep(current.FolderPath, status, sw.ElapsedMilliseconds, verdict, result.Reason);
    }

    private void RecordAgentsWikiSyncStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.AgentsWikiSyncStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Pure mapping from a <see cref="RegressionRadarResult"/> to the
    /// recorded step status + verdict token + reason. The radar never blocks,
    /// so every successful analysis records as
    /// <see cref="PipelineStepStatus.Passed"/> with the spec-change category
    /// carried in the verdict token (clean / intended / at-risk / drift); an
    /// analysis that could not run records as
    /// <see cref="PipelineStepStatus.Skipped"/>. Static + internal so unit
    /// tests can assert the mapping without the orchestrator.
    /// </summary>
    internal static (PipelineStepStatus Status, string Verdict, string Reason) MapRegressionRadarOutcome(
        RegressionRadarResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
            return (PipelineStepStatus.Skipped, "n/a", result.Error!);

        if (result.TotalSpecChanges == 0)
            return (PipelineStepStatus.Passed, "clean", "No spec changes in attributed commits");

        var counts = $"{result.TotalSpecChanges} spec change(s): "
            + $"{result.IntendedCount} intended, {result.AtRiskCount} at-risk, {result.DriftCount} drift";

        return result.OverallStatus switch
        {
            SpecChangeCategory.Drift  => (PipelineStepStatus.Passed, "drift", counts),
            SpecChangeCategory.AtRisk => (PipelineStepStatus.Passed, "at-risk", counts),
            _                         => (PipelineStepStatus.Passed, "intended", counts),
        };
    }

    private Task HandleBuildTestGateInfrastructureFailureAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        BuildTestGateResult result)
    {
        _pipelineLog?.Complete(current.FolderPath);
        var reason = BuildTestGateInfrastructureReasonPrefix
            + $"{result.FailureKind} persisted for exact subject {result.ExpectedSha ?? "missing"} "
            + $"in attempt chain {result.AttemptChainId ?? "missing"} after "
            + $"{PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries} retries "
            + $"(fingerprint {result.FailureFingerprint ?? "missing"}); coding reissue budget was not consumed.";
        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "build-test-gate-infrastructure");
        if (move.Status == MoveJobStatus.Success)
        {
            var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
            var escalated = current with { FolderPath = movedFolderPath, State = TaskStates.Escalated };
            RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, reason);
            WritePostProcessingOutcome(escalated, PostProcessingOutcomes.FailedPostProcessing,
                summary: reason,
                performer: PostProcessingPerformers.Tool,
                stepId: PipelineCatalogue.BuildTestGateStepId,
                evidenceRef: "post-steps/");
            _chatLog.AppendSupervisor(escalated, "escalate",
                $"Build/test review infrastructure failed for exact subject `{result.ExpectedSha ?? "missing"}` "
                + "after bounded retries. The coding work was not reissued. "
                + $"Failure: {result.FailureKind} ({result.FailureFingerprint ?? "missing"}).");
            EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                TimelineActors.Orchestrator, reason,
                BuildBuildTestGateInfrastructureDetails(result, reason));
        }
        else
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to escalate {JobId} after build-test review infrastructure failure: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        _statusSnapshot.RecordEscalate();
        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic exact-subject build-test gate post-step)",
            Response: result.Output,
            FollowUp: string.Empty)
        {
            AttemptChainId = result.AttemptChainId,
            GateId = result.GateId,
            SubjectSha = result.ExpectedSha,
            FailureFingerprint = result.FailureFingerprint,
            FailureKind = result.FailureKind.ToString(),
        }, current.FolderPath, move.NewFolderPath);
        return Task.CompletedTask;
    }

    private async Task<FailureInterventionResult?> TryRaiseBuildGateInterventionAsync(
        TaskInfo current,
        WatchPathEntry entry,
        BuildTestGateResult result,
        CancellationToken ct)
    {
        if (_failureInterventions is null || _projectSettings is null) return null;
        var settings = PipelineTypeSettings.ForTask(_projectSettings.Get(entry.Name), current);
        var step = PipelineCatalogue.FindStep(PipelineCatalogue.FailureInterventionStepId)!;
        if (!PipelineStepConfigResolver.IsEnabled(settings, step)) return null;
        var failureCode = result.FailureKind == BuildTestGateFailureKind.MissingSource
            ? "MissingSource"
            : "build-gate-failed";
        return await _failureInterventions.RaiseAsync(current, new FailureCommandEvidence(
            failureCode,
            "GateFailure",
            result.ExitCode,
            result.DurationMs,
            result.Output,
            result.Reason,
            PipelineCatalogue.BuildTestGateStepId,
            ["post-steps/build-test-gate-*.log"],
            result.GateStartedAtUtc?.UtcDateTime), ct);
    }

    private async Task HandleFailureInterventionAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        BuildTestGateResult result,
        FailureInterventionResult intervention,
        CancellationToken ct)
    {
        var move = _humanReviewEscalation is not null
            ? await _humanReviewEscalation.EscalateAsync(
                current.Id,
                current.WatchPath,
                current.ProjectName,
                HumanReviewEscalationCategories.FailureIntervention,
                intervention.WaitReason,
                ct)
            : GuardedMoveJob(current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated,
                transitionDetail: HumanReviewEscalationCategories.FailureIntervention);
        var movedFolder = move.NewFolderPath ?? current.FolderPath;
        RecordOrchestratorDecisionStep(movedFolder, PipelineStepStatus.Failed,
            DecisionVerdictEscalate,
            $"intervention raised: {intervention.Intervention.FollowUpKey} "
            + $"({intervention.Intervention.FailureClass}, {intervention.Intervention.Fingerprint}); "
            + intervention.WaitReason);
        _pipelineLog?.Complete(movedFolder);
        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: intervention.WaitReason,
            Prompt: "(deterministic failure-intervention post-step)",
            Response: result.Output,
            FollowUp: intervention.Intervention.FollowUpKey)
        {
            GateId = result.GateId,
            FailureFingerprint = intervention.Intervention.Fingerprint,
            FailureKind = intervention.Intervention.FailureClass,
        }, current.FolderPath, move.NewFolderPath);
    }

    private static Dictionary<string, string> BuildBuildTestGateInfrastructureDetails(
        BuildTestGateResult result,
        string reason)
        => new()
        {
            ["cause"] = "build-test-gate-infrastructure",
            ["environmental"] = "true",
            ["issueKind"] = RunIssueKind.InfraCrash.ToString(),
            ["failureKind"] = result.FailureKind.ToString(),
            ["fingerprint"] = result.FailureFingerprint ?? "missing",
            ["subjectSha"] = result.ExpectedSha ?? "missing",
            ["attemptChainId"] = result.AttemptChainId ?? "missing",
            ["reason"] = Truncate(reason, 600),
        };

    private async Task HandleBuildTestGateFailureAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        BuildTestGateResult result,
        AgentStudio.Review.CodeReviewStepReport? gradeReport,
        CancellationToken ct)
    {
        var councilReaction = gradeReport is null
            ? null
            : DeriveCouncilReviewReaction(workspace, entry, current, gradeReport);
        if (councilReaction is not null)
        {
            AgentStudio.Review.CouncilReviewReactionStore.Write(current.FolderPath, councilReaction);
        }

        _pipelineLog?.Complete(
            current.FolderPath,
            pendingStepReason: "Not run because the build/test gate stopped this pipeline attempt: " + result.Reason);

        var decisions = ReviewDecisionLog.ReadAll(workspace, entry.Name);
        var currentEpoch = OperatorReviewRequeueService.ReadEpoch(current.FolderPath);
        var priorBuildGateReissues = decisions.Count(r =>
            r.JobId == current.Id
            && r.Kind == ReviewDecisionKind.Reissue
            && IsInAttemptEpoch(r, currentEpoch)
            && string.Equals(r.AttemptChainId, result.AttemptChainId, StringComparison.Ordinal)
            && string.Equals(r.GateId, result.GateId, StringComparison.Ordinal)
            && string.Equals(r.SubjectSha, result.ExpectedSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.FailureFingerprint, result.FailureFingerprint, StringComparison.Ordinal));
        var taskReissueCeilingReached = CountPriorReissues(workspace, entry.Name, current.Id)
            >= ConfiguredMaxReissues();
        var failureIdentity = BuildTestGateFailureIdentity(result);

        if (priorBuildGateReissues >= 1 || taskReissueCeilingReached)
        {
            var reason = priorBuildGateReissues >= 1
                ? $"build-test gate failed twice for the same product retry identity ({failureIdentity}; {result.Reason}); escalating per post-step loop guard."
                : $"build-test gate failed and the task-level anti-churn ceiling was reached ({failureIdentity}; {result.Reason}).";
            if (councilReaction?.Disposition == AgentStudio.Review.CouncilReactionDisposition.Reissue)
            {
                councilReaction = AgentStudio.Review.CouncilReviewPolicy.EscalateBecause(
                    councilReaction,
                    "the build/test gate ended this attempt at its loop guard, so no further automatic round can start");
            }
            var move = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "build-test-gate-double-fail");
            if (move.Status == MoveJobStatus.Success)
            {
                var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
                var escalated = current with { FolderPath = movedFolderPath, State = TaskStates.Escalated };
                if (councilReaction is not null)
                {
                    AgentStudio.Review.CouncilReviewReactionStore.Write(movedFolderPath, councilReaction);
                }
                RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Failed,
                    DecisionVerdictEscalate, reason);
                WritePostProcessingOutcome(escalated, PostProcessingOutcomes.FailedPostProcessing,
                    summary: reason,
                    performer: PostProcessingPerformers.Tool,
                    stepId: PipelineCatalogue.BuildTestGateStepId,
                    evidenceRef: "post-steps/build-test-gate.log");
                _chatLog.AppendSupervisor(escalated, "escalate",
                    $"Build/test gate failed twice in a row. Promoted to {TaskStates.Escalated}. Output:\n{result.Output}");
                EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.Orchestrator, reason,
                    BuildEscalateDetails("build-test-gate-double-fail", reason,
                        CountPriorReissues(workspace, entry.Name, current.Id)));
            }
            else
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to escalate {JobId} after build-test gate double-fail: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }
            _statusSnapshot.RecordEscalate();
            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: reason,
                Prompt: "(deterministic build-test gate post-step)",
                Response: result.Output,
                FollowUp: string.Empty)
            {
                AttemptChainId = result.AttemptChainId,
                GateId = result.GateId,
                SubjectSha = result.ExpectedSha,
                FailureFingerprint = result.FailureFingerprint,
                FailureKind = result.FailureKind.ToString(),
                CouncilReaction = councilReaction,
            },
                current.FolderPath,
                move.NewFolderPath);
            return;
        }

        var moved = MoveReissueToReadyTop(current, entry, BuildTestGateReopenCause);
        if (moved == null) return;

        if (councilReaction is not null)
        {
            AgentStudio.Review.CouncilReviewReactionStore.Write(moved.FolderPath, councilReaction);
        }
        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, BuildTestGateReissueReasonPrefix + failureIdentity + "; " + result.Reason);
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.FailedPostProcessing,
            summary: BuildTestGateReissueReasonPrefix + failureIdentity + "; " + result.Reason,
            performer: PostProcessingPerformers.Tool,
            stepId: PipelineCatalogue.BuildTestGateStepId,
            evidenceRef: "post-steps/build-test-gate.log");

        var followUp = BuildBuildTestGateFollowUp(result, councilReaction);
        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var councilSuffix = councilReaction?.Disposition == AgentStudio.Review.CouncilReactionDisposition.Reissue
            ? $" The same round also carries {councilReaction.Assessments.Count} named council finding(s)."
            : string.Empty;
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready: build/test gate failed ({result.Reason}).{councilSuffix}");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: build/test gate failed ({result.Reason}).",
            BuildReopenDetails(BuildTestGateReopenCause,
                CountPriorReissues(workspace, entry.Name, current.Id),
                followUp));

        _statusSnapshot.RecordReissue();
        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: BuildTestGateReissueReasonPrefix + failureIdentity + "; " + result.Reason,
            Prompt: "(deterministic build-test gate post-step)",
            Response: result.Output,
            FollowUp: followUp)
        {
            AttemptChainId = result.AttemptChainId,
            GateId = result.GateId,
            SubjectSha = result.ExpectedSha,
            FailureFingerprint = result.FailureFingerprint,
            FailureKind = result.FailureKind.ToString(),
            CouncilReaction = councilReaction,
        },
            current.FolderPath,
            moved.FolderPath);
    }

    internal static string BuildTestGateFailureIdentity(BuildTestGateResult result)
        => $"attempt={result.AttemptChainId ?? "missing"};subject={result.ExpectedSha ?? "missing"};gate={result.GateId};fingerprint={result.FailureFingerprint ?? "missing"}";

    private static string BuildBuildTestGateFollowUp(
        BuildTestGateResult result,
        AgentStudio.Review.CouncilReviewReaction? councilReaction = null)
    {
        var commands = result.RanFrontendBuild
            ? "`dotnet build backend/OrchestratorApi.csproj` and `npm run build` from `frontend/`"
            : "`dotnet build backend/OrchestratorApi.csproj`";
        var buildFollowUp = "Auto-review re-opened this task because the deterministic build/test gate failed. " +
            "Do not rely on the previous self-reported Success. Fix only the current task diff, " +
            $"run {commands}, and end with [[TASK_DONE]] once the gate is green.\n\n" +
            "Truncated build/test output:\n" +
            "```\n" +
            result.Output + "\n" +
            "```";
        if (councilReaction?.Disposition != AgentStudio.Review.CouncilReactionDisposition.Reissue)
        {
            return buildFollowUp;
        }

        return buildFollowUp + "\n\n" +
            AgentStudio.Review.CouncilReviewPolicy.BuildTargetedFollowUp(councilReaction);
    }

    /// <summary>
    /// Resolve a Fail verdict from the lint-scss post-step. If the job has
    /// no prior lint-scss reissue, send it back to <c>2-ready</c> with a
    /// follow-up that includes the truncated stylelint output. If a prior
    /// reissue already exists in the decision journal, the budget is
    /// spent and the job escalates to <c>5e-escalated</c> instead - the
    /// spec's infinite-spin guard.
    /// </summary>
    private async Task HandleLintScssFailureAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        LintScssResult result,
        CancellationToken ct)
    {
        var currentEpoch = OperatorReviewRequeueService.ReadEpoch(current.FolderPath);
        var priorLintReissues = ReviewDecisionLog.ReadAll(workspace, entry.Name)
            .Count(r => r.JobId == current.Id
                        && r.Kind == ReviewDecisionKind.Reissue
                        && IsInAttemptEpoch(r, currentEpoch)
                        && r.Reason != null
                        && r.Reason.StartsWith(LintScssReissueReasonPrefix, StringComparison.Ordinal));

        if (priorLintReissues >= 1)
        {
            var reason = $"lint-scss failed twice in a row (exit {result.ExitCode}); escalating per ASS-46 infinite-spin guard.";
            var move = GuardedMoveJob(
                current.Id, TaskStates.Escalated, entry.Path,
                transitionCause: LaneChangeCauses.Escalated, transitionDetail: "lint-scss-double-fail");
            if (move.Status == MoveJobStatus.Success)
            {
                var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
                var moved = current with { FolderPath = movedFolderPath, State = TaskStates.Escalated };
                // Final verdict step: escalate (lint double-fail infinite-spin guard).
                RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Failed,
                    DecisionVerdictEscalate, reason);
                _chatLog.AppendSupervisor(moved, "escalate",
                    $"Lint-scss post-step failed twice in a row. Promoted to {TaskStates.Escalated}. Output:\n{result.Output}");
                // ADR-0049: timeline event on the original card, no wrapper card.
                EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.Orchestrator, reason,
                    BuildEscalateDetails("lint-scss-double-fail", reason,
                        CountPriorReissues(workspace, entry.Name, current.Id)));
            }
            else
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to escalate {JobId} after lint-scss double-fail: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }
            _statusSnapshot.RecordEscalate();
            AppendReviewDecision(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: reason,
                Prompt: "(deterministic lint-scss post-step)",
                Response: result.Output,
                FollowUp: string.Empty),
                current.FolderPath,
                move.NewFolderPath);
            return;
        }

        var moved2 = MoveReissueToReadyTop(current, entry, "lint-scss-fail");
        if (moved2 == null) return;

        // Final verdict step: reissue (lint-scss gate failed once).
        RecordOrchestratorDecisionStep(moved2.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, LintScssReissueReasonPrefix + $"stylelint exit {result.ExitCode}");

        var followUp = BuildLintScssFollowUp(result);
        followUp = await WriteFollowUpFileAsync(moved2, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved2.Title) ? moved2.Id : moved2.Title;
        _chatLog.Append(moved2, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready: lint-scss failed (exit {result.ExitCode}).");

        EmitVerdictTimeline(moved2.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: lint-scss post-step failed (exit {result.ExitCode}).",
            BuildReopenDetails("lint-scss-fail",
                CountPriorReissues(workspace, entry.Name, current.Id),
                result.Output));

        _statusSnapshot.RecordReissue();
        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: LintScssReissueReasonPrefix + $"stylelint exit {result.ExitCode}",
            Prompt: "(deterministic lint-scss post-step)",
            Response: result.Output,
            FollowUp: followUp),
            current.FolderPath,
            moved2.FolderPath);
    }

    private static string BuildLintScssFollowUp(LintScssResult result) =>
        "Auto-review re-opened this task because the lint-scss post-step found stylelint errors. " +
        "Run `npm run lint:scss` from the frontend tree, address every error (warnings can stay), " +
        "and end with [[TASK_DONE]] once the gate is green again.\n\n" +
        "Truncated stylelint output (first 50 lines):\n" +
        "```\n" +
        result.Output + "\n" +
        "```";

    private static string AspectSummaryLine(AspectRunReport report)
    {
        if (report.Verdicts.Count == 0) return "(no aspects ran)";
        return string.Join(", ", report.Verdicts.Select(v =>
            $"{v.Aspect}={AspectVerdictParsing.StatusToken(v.Status)}"));
    }

    /// <summary>
    /// F29: produce a compact aspect-count phrase for the operator-facing
    /// verdict toast ("2 of 4 aspects flagged"). Keeps the headline a
    /// single sentence; the full per-aspect list stays on
    /// <see cref="AspectSummaryLine"/> for the decision journal.
    /// </summary>
    private static string FormatConcernCount(AspectRunReport report)
    {
        if (report.Verdicts.Count == 0) return "no aspects ran";
        var concerns = report.Verdicts.Count(v => v.Status == AspectStatus.Concerns);
        if (concerns == 0) return $"0 of {report.Verdicts.Count} aspects flagged";
        var noun = concerns == 1 ? "aspect" : "aspects";
        return $"{concerns} of {report.Verdicts.Count} {noun} flagged";
    }

    private static string LoadStatusSummary(string folderPath)
    {
        var statusPath = Path.Combine(folderPath, "status.md");
        if (!File.Exists(statusPath)) return string.Empty;
        try { return Truncate(File.ReadAllText(statusPath), 4_000); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Build the diff summary fed to the per-aspect prompts. Aggregates
    /// every commit attributed to the job across all of its runs (via
    /// session-events SHA ranges) plus the auto-commit on
    /// <see cref="TaskInfo.Commit"/>. Same aggregation pipeline that
    /// powers <c>/api/tasks/{id}/commits</c>, so the aspect reviewer sees
    /// the same picture the human reviewer sees.
    ///
    /// <para>
    /// Why not just <c>job.Commit</c>: a crash-recovery commit lands as a
    /// near-empty fixup on top of the real work, then becomes
    /// <see cref="TaskInfo.Commit"/>. Reading that alone gives "0 files
    /// changed" and false-positive blocks the verdict (2026-05-11
    /// incident). Walking the full run range is the only source of truth
    /// for the actual change set.
    /// </para>
    ///
    /// <para>
    /// Falls back to the legacy single-commit summary only when neither
    /// dependency is wired - that path exists for tests that construct
    /// the orchestrator without the full graph; production always has
    /// both services from DI.
    /// </para>
    /// </summary>
    private string LoadDiffSummary(WatchPathEntry entry, TaskInfo job)
    {
        var project = entry.Name;
        var watchPath = entry.Path;
        if (_sessions == null || _git == null)
        {
            return AppendBranchDiffSummary(BuildDiffSummary(EmptyAggregate, job.Commit), entry, job);
        }
        try
        {
            var events = _sessions.ReadSessionEvents(job.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(job.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            var aggregate = TaskCommitsAggregator.Aggregate(job, SelectAuthoritativeReviewRuns(timeline.Runs),
                (before, after) => _git!.GetCommitsInShaRange(job.Id, watchPath, before, after));
            // Commits surfaced from the persisted chain (task.json) carry only a
            // file count - their +/- line stats are hardcoded 0 because
            // TaskCommitInfo never stored them. Re-derive the real +N/-M per
            // SHA from git so the aspect reviewer never sees "N files, +0/-0"
            // (read as "corrupted / no work") for a genuine multi-line commit.
            aggregate = EnrichLineStats(aggregate,
                sha => _git!.GetCommitStat(job.Id, watchPath, sha));
            return AppendBranchDiffSummary(BuildDiffSummary(aggregate, job.Commit), entry, job);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: full-range diff summary failed for {Project}/{JobId}; falling back to single-commit view",
                project, job.Id);
            return AppendBranchDiffSummary(BuildDiffSummary(EmptyAggregate, job.Commit), entry, job);
        }
    }

    /// <summary>
    /// Append the task-branch-vs-base commit range to the run-window diff summary.
    /// The run-window aggregate can be empty even though the task changed the tree
    /// - after a squash/merge or a steer follow-up run whose window produced no new
    /// commits, the current run's working diff is empty. The branch range
    /// (<c>base..task/&lt;id&gt;</c>) is the authoritative "what did this task change"
    /// signal that survives those cases, so the aspect / review reviewers always
    /// see the real change set (AGT-2022 / AGT-1915). Best-effort: returns the base
    /// summary unchanged when git is unwired, the task branch is absent (sequential
    /// runs never create one), or the range is empty.
    /// </summary>
    private string AppendBranchDiffSummary(string baseSummary, WatchPathEntry entry, TaskInfo job)
        => ComposeAspectDiffSummary(baseSummary, TryBuildBranchDiffSummary(entry, job));

    /// <summary>
    /// Pure composition of the aspect diff summary (AGT-2022): the run-window
    /// summary always carries the task-branch-vs-base range appended when one is
    /// available, so an empty run-window diff never reads as "deliverables
    /// missing" while the branch holds the real commits (squash/merge or steer
    /// follow-up). Returns <paramref name="baseSummary"/> unchanged when no branch
    /// range is available (git unwired, no task branch, or an empty range). Pure
    /// so a unit test can pin the append / passthrough shapes without a live repo.
    /// </summary>
    internal static string ComposeAspectDiffSummary(string baseSummary, string? branchSummary)
        => string.IsNullOrWhiteSpace(branchSummary) ? baseSummary : baseSummary + "\n\n" + branchSummary;

    private string? TryBuildBranchDiffSummary(WatchPathEntry entry, TaskInfo job)
    {
        if (_git == null) return null;
        var repoRoot = !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath : entry.RootPath;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;

        try
        {
            var taskBranch = $"task/{job.Id}";
            if (!_git.BranchExists(repoRoot, taskBranch)) return null;

            var configuredBase = _projectSettings?.Get(entry.Name)?.IntegrationBranch;
            var baseBranch = _git.ResolveIntegrationBranch(repoRoot, configuredBase);
            var commits = _git.GetCommitsInRangeAtRoot(repoRoot, baseBranch, taskBranch);
            if (commits.Count == 0) return null;

            return BuildBranchDiffSummary(baseBranch, taskBranch, commits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: task-branch diff summary failed for {Project}/{JobId}",
                entry.Name, job.Id);
            return null;
        }
    }

    /// <summary>
    /// Pure renderer for the task-branch-vs-base commit range. Pure so a unit test
    /// can pin the "steer follow-up: empty working diff but branch commits" shape
    /// without a live repo.
    /// </summary>
    internal static string BuildBranchDiffSummary(string baseBranch, string taskBranch, IReadOnlyList<GitCommitInfo> commits)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task branch `{taskBranch}` vs base `{baseBranch}`: {commits.Count} commit(s) ahead.");
        sb.AppendLine($"Total files changed: {commits.Sum(c => c.FilesChanged)}; lines +{commits.Sum(c => c.Added)}/-{commits.Sum(c => c.Removed)}.");
        sb.AppendLine();
        sb.AppendLine("Per commit (newest first):");
        foreach (var c in commits)
        {
            var subject = string.IsNullOrWhiteSpace(c.Subject) ? "(no subject)" : c.Subject;
            sb.AppendLine($"- {c.ShortSha} {subject} ({c.FilesChanged} files, +{c.Added}, -{c.Removed})");
        }
        sb.AppendLine();
        sb.AppendLine("These branch commits are attributed to the task even when the current run's working diff is empty (post-squash/merge or steer follow-up). Do NOT treat an empty working diff as missing work when this range is non-empty.");
        return sb.ToString().TrimEnd();
    }

    private static readonly TaskCommitsAggregate EmptyAggregate = new() { Count = 0, Commits = [] };

    /// <summary>
    /// Backfill genuine +added/-removed line stats onto aggregate commits that
    /// arrived without them. The aggregator folds persisted-chain and legacy
    /// auto-commit entries with <c>Added = Removed = 0</c> (the
    /// <see cref="TaskCommitInfo"/> chain only caches a file count), so a task
    /// whose run-window SHA range produced no commits - and therefore surfaces
    /// only via that chain - renders as "N files changed, +0/-0". An aspect
    /// reviewer reads that as corrupted / empty work and false-BLOCKs a real,
    /// tested change (ASS-770). For each such commit we ask <paramref name="statLookup"/>
    /// for the real per-SHA stat and recompute the totals.
    ///
    /// <para>
    /// Pure aside from the injected lookup so it can be pinned by a unit test
    /// with a fake stat source. A commit that already has line data (it came
    /// from the SHA-range path, which carries +/-) is left untouched; a lookup
    /// that returns all-zero (truly empty commit, or repo unresolvable) leaves
    /// the record as-is so we never invent numbers.
    /// </para>
    /// </summary>
    internal static TaskCommitsAggregate EnrichLineStats(
        TaskCommitsAggregate aggregate,
        Func<string, (int FilesChanged, int Added, int Removed)> statLookup)
    {
        if (aggregate.Count == 0) return aggregate;
        if (!aggregate.Commits.Any(c => c.Added == 0 && c.Removed == 0 && !string.IsNullOrWhiteSpace(c.Sha)))
            return aggregate;

        var enriched = new List<TaskCommitRecord>(aggregate.Commits.Count);
        foreach (var c in aggregate.Commits)
        {
            if (c.Added == 0 && c.Removed == 0 && !string.IsNullOrWhiteSpace(c.Sha))
            {
                (int FilesChanged, int Added, int Removed) stat;
                try { stat = statLookup(c.Sha); }
                catch { stat = (0, 0, 0); }
                if (stat.Added != 0 || stat.Removed != 0 || stat.FilesChanged != 0)
                {
                    enriched.Add(c with
                    {
                        Added = stat.Added,
                        Removed = stat.Removed,
                        FilesChanged = stat.FilesChanged > 0 ? stat.FilesChanged : c.FilesChanged
                    });
                    continue;
                }
            }
            enriched.Add(c);
        }

        return aggregate with
        {
            Commits = enriched,
            TotalAdded = enriched.Sum(x => x.Added),
            TotalRemoved = enriched.Sum(x => x.Removed),
            TotalFilesChanged = enriched.Sum(x => x.FilesChanged)
        };
    }

    /// <summary>
    /// Pure renderer: turn an aggregate plus the legacy auto-commit into the
    /// diff-summary string handed to the aspect prompts. Pure so it can
    /// be pinned by unit tests against a synthetic commit stack
    /// (including the empty-HEAD-recovery-commit + non-empty prior
    /// commits scenario that triggered the 2026-05-11 false positive).
    /// </summary>
    internal static string BuildDiffSummary(TaskCommitsAggregate aggregate, TaskCommitInfo? legacyAutoCommit)
    {
        if (aggregate.Count == 0)
        {
            if (legacyAutoCommit == null)
            {
                return "No commits attributed to this task (no run-window SHA range produced commits and no auto-commit was recorded).";
            }
            // Aggregator could not be run (test path / missing deps). Fall
            // back to the legacy single-commit view so the prompt still has
            // something to chew on.
            var c = legacyAutoCommit;
            var sb0 = new StringBuilder();
            sb0.AppendLine($"Commit: {c.ShortSha}");
            if (!string.IsNullOrWhiteSpace(c.Message))
            {
                var firstLine = c.Message.Split('\n', 2)[0].Trim();
                if (firstLine.Length > 0) sb0.AppendLine($"Subject: {firstLine}");
            }
            sb0.AppendLine($"Files changed: {c.FilesChanged}");
            return sb0.ToString();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Commits attributed to this task: {aggregate.Count}");
        sb.AppendLine($"Total files changed (sum across commits; a file touched twice counts twice): {aggregate.TotalFilesChanged}");
        sb.AppendLine($"Total lines added: {aggregate.TotalAdded}");
        sb.AppendLine($"Total lines removed: {aggregate.TotalRemoved}");
        sb.AppendLine();
        sb.AppendLine("Per commit (newest first):");
        foreach (var c in aggregate.Commits)
        {
            var subject = string.IsNullOrWhiteSpace(c.Subject) ? "(no subject)" : c.Subject;
            sb.AppendLine($"- {c.ShortSha} {subject} ({c.FilesChanged} files, +{c.Added}, -{c.Removed})");
        }

        // Defensive: commits exist and touch files, but no +/- line counts
        // could be derived (line stats were not cached on the attributed
        // commits and git could not re-derive them - e.g. an unresolvable
        // worktree). The file counts above are the authoritative signal that
        // work landed; spell that out so a reviewer does NOT read the zero
        // line totals as "no work", "empty", or "corrupted data" and BLOCK a
        // real change (ASS-770).
        if (aggregate.TotalAdded == 0 && aggregate.TotalRemoved == 0 && aggregate.TotalFilesChanged > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Note: per-line +/- counts could not be computed for these commits (line stats were not cached and git could not re-derive them). The file counts above are authoritative and confirm real changes - do NOT treat the zero line totals as missing, empty, or corrupted work.");
        }
        return sb.ToString();
    }

    private static int CountPriorReissues(string workspace, string project, string jobId)
        => CountReissuesInCurrentChain(ReviewDecisionLog.ReadAll(workspace, project), jobId);

    /// <summary>
    /// Count the reissues in the job's current operator-owned attempt epoch.
    /// The epoch changes only when an explicit
    /// <see cref="ReviewDecisionKind.OperatorRequeue"/> boundary is appended.
    /// Automatic escalate, accept, and lane moves stay in the same epoch and
    /// therefore retain the anti-churn budget.
    ///
    /// <para>
    /// Legacy records without <see cref="ReviewDecisionRecord.AttemptEpoch"/>
    /// belong to epoch 0. Once an operator requeue opens epoch N, only reissues
    /// stamped with N count. Old rows remain readable history. This deliberately
    /// does not infer a reset from an Escalate or AcceptAsDone verdict: an
    /// automated move must never replenish an agent loop's budget.
    /// </para>
    ///
    /// <para><see cref="ReviewDecisionKind.Skipped"/> is neither a count nor a
    /// boundary. Records are consumed in append order, the order
    /// <see cref="ReviewDecisionLog.ReadAll"/> returns them.</para>
    /// </summary>
    internal static int CountReissuesInCurrentChain(IEnumerable<ReviewDecisionRecord> records, string jobId)
    {
        var jobRecords = records.Where(r => r.JobId == jobId).ToList();
        var currentEpoch = jobRecords.Count == 0
            ? 0
            : jobRecords.Max(r => Math.Max(0, r.AttemptEpoch ?? 0));
        return jobRecords.Count(r =>
            r.Kind == ReviewDecisionKind.Reissue
            && IsInAttemptEpoch(r, currentEpoch));
    }

    internal static bool IsInAttemptEpoch(ReviewDecisionRecord record, int epoch)
        => Math.Max(0, record.AttemptEpoch ?? 0) == Math.Max(0, epoch);

    /// <summary>
    /// No-verdict guard (requirement 7): true when a 4-auto-review card has sat
    /// past the grace window with NO recorded orchestrator verdict at all. This
    /// rescues a card whose terminal sentinel was resolved on a prior tick but
    /// whose decision never landed - e.g. a lane move that silently failed to
    /// stick - so it is driven to a conclusion instead of hanging in
    /// 4-auto-review without a verdict.
    ///
    /// Two cheap preconditions, mtime first so the common (fresh) case never
    /// touches the decision journal: the CLI log must be older than the
    /// configurable grace window
    /// (<c>ReviewDecisionOrchestrator:NoVerdictGraceMinutes</c>, default 15),
    /// and the journal must hold no record for this job. The no-verdict
    /// precondition makes force-processing re-bill-safe: the first force-process
    /// appends a decision record, after which this returns false.
    /// </summary>
    private bool IsStaleWithoutVerdict(string workspace, string project, TaskInfo info, string logPath)
    {
        var graceMinutes = _configuration.GetValue("ReviewDecisionOrchestrator:NoVerdictGraceMinutes", 15);
        if (graceMinutes <= 0) return false;

        DateTime lastWriteUtc;
        try { lastWriteUtc = File.GetLastWriteTimeUtc(logPath); }
        catch { return false; }

        if (DateTime.UtcNow - lastWriteUtc < TimeSpan.FromMinutes(graceMinutes))
            return false;

        try
        {
            var currentEpoch = OperatorReviewRequeueService.ReadEpoch(info.FolderPath);
            return !ReviewDecisionLog.ReadAll(workspace, project).Any(r =>
                r.JobId == info.Id
                && IsInAttemptEpoch(r, currentEpoch));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Operator authority (AGT-2260): the current review-cycle epoch is the
    /// card's most recent recorded transition INTO <c>4-auto-review</c> (written
    /// by the single recording hook in <c>TaskTransitionService.MoveAsync</c>).
    /// Verdicts older than it belong to a closed cycle - typically one the
    /// operator ended by requeueing the escalated card - and must neither be
    /// backfilled as "due moves" nor count as this cycle's verdict. Cards
    /// without recorded transitions (legacy folders, unit-test fixtures) keep
    /// the pre-epoch behavior.
    /// </summary>
    private static DateTime? CurrentReviewCycleEpoch(TaskInfo info)
    {
        var transitions = info.Provenance?.Transitions;
        if (transitions is not { Count: > 0 }) return null;
        for (var i = transitions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(transitions[i].Lane, TaskStates.AutoReview, StringComparison.Ordinal))
                return transitions[i].AtUtc;
        }
        return null;
    }

    /// <summary>
    /// Stale-WITH-verdict guard (the move-after-verdict failure mode this fixes):
    /// a card still parked in <c>4-auto-review</c> past the grace window whose
    /// latest decision-journal record already names a verdict that implies a
    /// lane move which never completed. The deterministic verdict paths
    /// (NOOP/BLOCKED/gate reissue + escalate) write their
    /// <c>[orchestrator]</c>/<c>[supervisor]</c> follow-up line FIRST - which
    /// resolves the agent sentinel - then only warn-but-continue if
    /// <see cref="TaskTransitionService.MoveAsync"/> fails (a Move-Lock from open log
    /// handles / orphan processes - vgl. ASS-759 - or a backend restart between
    /// recording and moving), yet still append the verdict record. The card is
    /// then invisible to every other path: the sentinel is resolved, and
    /// <see cref="IsStaleWithoutVerdict"/> only covers the no-verdict case.
    ///
    /// <para>
    /// Returns the verdict whose move is due so the tick can backfill it
    /// idempotently - <see cref="ReviewDecisionKind.Reissue"/> -> 2-ready,
    /// <see cref="ReviewDecisionKind.Escalate"/> /
    /// <see cref="ReviewDecisionKind.AcceptAsDone"/> -> 5-human-review - or
    /// <c>null</c> when no move is due. <see cref="ReviewDecisionKind.Skipped"/>
    /// never resolves the sentinel (it leaves the card for the normal sentinel
    /// path), so it is deliberately excluded.
    /// </para>
    /// </summary>
    private ReviewDecisionKind? GetStaleVerdictNeedingMove(string workspace, string project, TaskInfo info, string logPath)
    {
        var graceMinutes = _configuration.GetValue("ReviewDecisionOrchestrator:StaleVerdictGraceMinutes", 15);
        if (graceMinutes <= 0) return null;

        DateTime lastWriteUtc;
        try { lastWriteUtc = File.GetLastWriteTimeUtc(logPath); }
        catch { return null; }

        if (DateTime.UtcNow - lastWriteUtc < TimeSpan.FromMinutes(graceMinutes))
            return null;

        IReadOnlyList<ReviewDecisionRecord> records;
        try { records = ReviewDecisionLog.ReadAll(workspace, project); }
        catch { return null; }

        ReviewDecisionRecord? latest = null;
        var currentEpoch = OperatorReviewRequeueService.ReadEpoch(info.FolderPath);
        for (var i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].JobId == info.Id
                && IsInAttemptEpoch(records[i], currentEpoch))
            {
                latest = records[i];
                break;
            }
        }
        if (latest == null) return null;

        // Operator authority (AGT-2260): a verdict recorded BEFORE the card's
        // current review-cycle epoch has already had its move - and someone
        // (typically the operator, requeueing an escalated card) deliberately
        // moved the card back afterwards. Backfilling that old move would
        // override the operator and re-escalate from stale artifacts without
        // any new evidence (the requeue ping-pong of 23.07.). Only a verdict
        // YOUNGER than the current lane entry is a genuinely un-executed move.
        if (CurrentReviewCycleEpoch(info) is { } epoch && latest.CreatedAt < epoch)
            return null;

        return latest.Kind switch
        {
            ReviewDecisionKind.Reissue => ReviewDecisionKind.Reissue,
            ReviewDecisionKind.Escalate => ReviewDecisionKind.Escalate,
            ReviewDecisionKind.AcceptAsDone => ReviewDecisionKind.AcceptAsDone,
            _ => null,
        };
    }

    /// <summary>
    /// Configured reissue budget (shared by NEEDS_INPUT / NOOP / aspect /
    /// lint-scss reissues). Defaults to <see cref="MaxAutoReissueAttempts"/>.
    /// Drives the <c>maxAttempts</c> detail on the completion-loop timeline
    /// events so the FE can render "Attempt N of M" without re-reading
    /// config itself.
    /// </summary>
    private int ConfiguredMaxReissues() =>
        _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);

    /// <summary>
    /// Consecutive rounds allowed to return the exact same aspect/reason pair.
    /// This is narrower than the shared reissue budget: changing findings may
    /// still use that budget, while an unchanged semantic block stops early.
    /// </summary>
    private int ConfiguredIdenticalBlockRounds() =>
        Math.Clamp(_configuration.GetValue(
            "ReviewDecisionOrchestrator:MaxIdenticalAspectBlockRounds",
            ReissueLoopBreaker.DefaultIdenticalBlockRounds), 1, 20);

    /// <summary>
    /// Tee one orchestrator verdict (accept / reopen / escalate) into the
    /// unified per-task timeline ledger (ADR-0049) so the Overview/Timeline
    /// surfaces can show the completion-loop cycle (ASS-566) without
    /// re-deriving it from the decision journal and chat log. Best-effort:
    /// the timeline is observability, never a state-machine input, so a
    /// missing writer (test path) or a write failure cannot affect the
    /// verdict. Always pass the POST-MOVE folder so the event lands in the
    /// lane the card actually moved to, mirroring the chat-log firing-order
    /// rule.
    /// </summary>
    private void EmitVerdictTimeline(
        string? folderPath,
        string kind,
        string actor,
        string summary,
        Dictionary<string, string>? details = null)
    {
        if (_timeline == null || string.IsNullOrWhiteSpace(folderPath)) return;
        _timeline.Append(folderPath!, kind, actor, summary, details: details);
    }

    /// <summary>
    /// Build the <c>Details</c> bag shared by every "go again" timeline
    /// event. <paramref name="priorReissues"/> is the count BEFORE this
    /// reopen (the decision-journal record is appended after the emit), so
    /// the upcoming attempt is <c>priorReissues + 2</c> (initial run = 1).
    /// </summary>
    private Dictionary<string, string> BuildReopenDetails(
        string cause, int priorReissues, string? gap = null,
        IReadOnlyList<AspectVerdict>? verdicts = null,
        string? followUpPrompt = null,
        SteeringContext? context = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var details = new Dictionary<string, string>
        {
            ["cause"] = cause,
            ["attempt"] = (priorReissues + 2).ToString(inv),
            ["maxAttempts"] = (ConfiguredMaxReissues() + 1).ToString(inv),
        };
        // Traceability (ASS-734): carry the exact steering prompt the agent
        // received so the FE timeline/protocol pane can show "Prompt + Context"
        // per steering step instead of only a verdict label.
        if (!string.IsNullOrWhiteSpace(followUpPrompt))
        {
            details["followUpPrompt"] = Truncate(followUpPrompt!.Trim(), 4000);
        }
        // Epic ASS-776: when the steering context is known, also carry the
        // resume-vs-fresh mode, the resumed session, the considered commits, and
        // the prior re-issue counter so the FE renders a structured "Context"
        // block alongside the prompt. All optional and forward-compatible: an
        // older ledger row (or a branch that does not build a context) simply
        // omits these keys and the FE shows whatever is present.
        if (context != null)
        {
            details["priorReissues"] = context.PriorReissues.ToString(inv);
            details["mode"] = context.ResumeSessionId != null ? "resume" : "fresh-run";
            if (!string.IsNullOrWhiteSpace(context.ResumeSessionId))
            {
                details["resumeSessionId"] = context.ResumeSessionId!;
            }
            if (context.PriorCommits is { Count: > 0 })
            {
                details["priorCommits"] = Truncate(
                    string.Join("\n", context.PriorCommits.Take(20)), 1200);
            }
        }
        if (!string.IsNullOrWhiteSpace(gap))
        {
            details["gap"] = Truncate(gap!.Trim(), 600);
        }
        // Option A: when the reopen was driven by per-aspect verdicts, carry
        // the structured findings alongside the legacy `gap` blob so the FE
        // renders a list of toned chips instead of raw `**`/`[]` markdown. We
        // mirror FollowUpSummary and emit only the non-pass verdicts that
        // actually triggered the reissue. The `gap` string stays for
        // backwards-compat (alt-clients / old ledger rows parse it instead).
        if (verdicts != null)
        {
            var flagged = verdicts.Where(v => v.Status != AspectStatus.Pass).ToList();
            if (flagged.Count > 0)
            {
                details["findings"] = AspectVerdictParsing.SerializeFindings(flagged);
            }
        }
        return details;
    }

    /// <summary>
    /// Build the <c>Details</c> bag shared by every "hand to a human"
    /// timeline event. The run that just finished is attempt
    /// <c>priorReissues + 1</c> (initial run = 1); recording it next to the
    /// budget makes a budget-exhaustion escalation legible on the timeline.
    /// </summary>
    private Dictionary<string, string> BuildEscalateDetails(string cause, string reason, int priorReissues)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new Dictionary<string, string>
        {
            ["cause"] = cause,
            ["reason"] = Truncate(reason ?? string.Empty, 600),
            ["attempt"] = (priorReissues + 1).ToString(inv),
            ["maxAttempts"] = (ConfiguredMaxReissues() + 1).ToString(inv),
        };
    }

    private static NoOpProgressEvidence InspectNoOpProgressSinceLastRecovery(PendingDecision pending)
    {
        var logPath = TaskPaths.CliOutputLog(pending.Job.FolderPath);
        if (!File.Exists(logPath)) return NoOpProgressEvidence.None;

        string log;
        try { log = File.ReadAllText(logPath); }
        catch { return NoOpProgressEvidence.None; }

        if (string.IsNullOrWhiteSpace(log)) return NoOpProgressEvidence.None;
        var lines = log.Split('\n');
        var noopIndex = Math.Clamp(pending.LineNumber - 1, 0, Math.Max(lines.Length - 1, 0));

        var reissueIndex = -1;
        for (var i = noopIndex - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("[orchestrator]", StringComparison.Ordinal)
                && line.Contains("[reissue]", StringComparison.Ordinal)
                && line.Contains("NOOP recovery", StringComparison.OrdinalIgnoreCase))
            {
                reissueIndex = i;
                break;
            }
        }

        if (reissueIndex < 0) return NoOpProgressEvidence.None;

        var toolCalls = 0;
        var fileChanges = 0;
        var agentSubstanceChars = 0;

        for (var i = reissueIndex + 1; i <= noopIndex && i < lines.Length; i++)
        {
            InspectProgressLine(lines[i], ref toolCalls, ref fileChanges, ref agentSubstanceChars);
        }

        return new NoOpProgressEvidence(
            SawNoOpRecoveryReissue: true,
            ToolCalls: toolCalls,
            FileChanges: fileChanges,
            AgentSubstanceChars: agentSubstanceChars);
    }

    private static void InspectProgressLine(
        string line,
        ref int toolCalls,
        ref int fileChanges,
        ref int agentSubstanceChars)
    {
        var jsonStart = line.IndexOf('{');
        if (jsonStart >= 0)
        {
            var json = line[jsonStart..].Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (!string.Equals(type, "item.started", StringComparison.Ordinal)
                    && !string.Equals(type, "item.completed", StringComparison.Ordinal))
                {
                    return;
                }

                if (!root.TryGetProperty("item", out var item)
                    || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemTypeProp))
                {
                    return;
                }

                var itemType = itemTypeProp.GetString();
                if (string.Equals(itemType, "agent_message", StringComparison.Ordinal))
                {
                    var text = item.TryGetProperty("text", out var textProp)
                        ? textProp.GetString()
                        : null;
                    agentSubstanceChars += CountAgentSubstanceChars(text);
                    return;
                }

                if (string.Equals(itemType, "file_change", StringComparison.Ordinal))
                {
                    fileChanges++;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(itemType))
                {
                    toolCalls++;
                }
                return;
            }
            catch (JsonException __ex)
            {
                SilentCatch.Note(__ex, "ReviewDecisionOrchestrator: Fall through and treat it as ordinary output text.");
                // Fall through and treat it as ordinary output text.
            }
        }

        var payloadText = ExtractStreamPayload(line);
        agentSubstanceChars += CountAgentSubstanceChars(payloadText);
    }

    private static string ExtractStreamPayload(string line)
    {
        var stdoutMarker = "] [stdout] ";
        var stderrMarker = "] [stderr] ";
        var stdoutAt = line.IndexOf(stdoutMarker, StringComparison.Ordinal);
        if (stdoutAt >= 0) return line[(stdoutAt + stdoutMarker.Length)..];
        var stderrAt = line.IndexOf(stderrMarker, StringComparison.Ordinal);
        if (stderrAt >= 0) return line[(stderrAt + stderrMarker.Length)..];
        return line;
    }

    private static int CountAgentSubstanceChars(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[[TASK_NOOP", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith("]]", StringComparison.Ordinal))
        {
            return 0;
        }

        var withoutNoOp = text
            .Replace("[[TASK_NOOP]]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return withoutNoOp.Length;
    }

    private static (string Title, string Body) LoadTaskTitleAndBody(PendingDecision pending)
    {
        var title = pending.Job.Title ?? string.Empty;
        var promptPath = Path.Combine(pending.Job.FolderPath, "prompt.md");
        var body = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        return (title, body);
    }

    /// <summary>
    /// Heuristic: a prompt is "usable" if it has a non-empty title, a
    /// non-empty body, and the body has at least 20 characters of
    /// non-heading content. Catches the obvious placeholder cases
    /// (untouched template, single heading, "TODO: fill in") without
    /// trying to score real prose.
    /// </summary>
    internal static bool IsPromptUsable(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var trimmedTitle = title.Trim();
        if (LooksLikePlaceholder(trimmedTitle)) return false;
        if (string.IsNullOrWhiteSpace(body)) return false;

        var contentChars = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue; // markdown heading
            if (LooksLikePlaceholder(line)) continue;
            contentChars += line.Length;
        }
        return contentChars >= 20;
    }

    private static bool LooksLikePlaceholder(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var lower = s.ToLowerInvariant();
        return lower.Contains("{title}")
            || lower.Contains("<title>")
            || lower.Contains("placeholder")
            || lower == "todo"
            || lower.StartsWith("todo:")
            || lower.StartsWith("tbd")
            || lower == "(empty)";
    }

    private async Task HandleReissueAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        CancellationToken ct)
    {
        var followUp = BuildReissueFollowUp(verdict);
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        // Move first so the operator-visible "sent back to ready" notification
        // only fires once the folder has actually left 4-auto-review. A failed
        // move must not produce a banner that claims the task moved.
        var moved = MoveReissueToReadyTop(current, entry, "needs-input");
        if (moved == null)
        {
            return;
        }
        followUp = await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready for another attempt. Reason: {verdict.Reason}. Follow-up: {followUp}");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: orchestrator answered NEEDS_INPUT. {verdict.Reason}",
            BuildReopenDetails("needs-input",
                CountPriorReissues(workspace, entry.Name, current.Id),
                verdict.Reason));

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: verdict.Reason,
            Prompt: prompt,
            Response: response,
            FollowUp: followUp),
            current.FolderPath,
            moved.FolderPath);
    }

    private Task HandleEscalateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        string cliBinary,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var escalationReason = HumanReviewEscalation.FormatReason(
            HumanReviewEscalationCategories.NeedsHumanInput,
            verdict.Reason);

        // ADR-0049: the orchestrator could not decide this 4-auto-review
        // task unattended. It flips the *original* card to 5e-escalated
        // (a genuine "a human must decide" case) and records one
        // orchestrator_escalated event on that card's timeline - the timeline
        // is the explanation. No sibling human-decision-needed-<slug> card is
        // spawned: the wrapper-card pattern (ASS-30) is the bug this ADR ends.
        var move = GuardedMoveJob(
            current.Id, TaskStates.Escalated, entry.Path,
            transitionCause: LaneChangeCauses.Escalated, transitionDetail: "needs-input-escalate");
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to escalated after escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return Task.CompletedTask;
        }

        // Pin the chat-log line to the post-move folder via MoveJob's
        // authoritative path. FindJob can briefly return null or the
        // pre-move snapshot when the cache has not refreshed yet, and
        // the chat-log auto-creates its parent folder on write — so a
        // stale path resurrects the source lane as a one-line skeleton.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.Escalated };
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.NeedsHumanInput,
            summary: escalationReason,
            performerCliType: NormalizeReviewCliType(cliBinary),
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json");
        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.AppendSupervisor(moved, "escalate",
            $"Auto-review escalated \"{title}\" to {TaskStates.Escalated} for human attention. Reason: {escalationReason}.");

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
            TimelineActors.Orchestrator, escalationReason,
            BuildEscalateDetails("needs-input-escalate", escalationReason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: escalationReason,
            Prompt: prompt,
            Response: response,
            FollowUp: string.Empty),
            current.FolderPath,
            movedFolderPath);

        return Task.CompletedTask;
    }

    private void HandleAcceptAsDone(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        string cliBinary)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = verdict.Reason ?? string.Empty;

        // ADR-0025: accept-as-done routes to 5-human-review, NOT directly
        // to 6-completed. The user always gets the final say on whether a
        // task is done; the orchestrator's accept signal is "the agent's
        // answer looks complete to me, please confirm."
        var move = GuardedMoveJob(
            current.Id, TaskStates.HumanReview, entry.Path,
            transitionCause: LaneChangeCauses.ReviewVerdict, transitionDetail: "accept");
        if (move.Status != MoveJobStatus.Success)
        {
            // Move failed -> do NOT write the operator-facing "accepted as
            // done" line: the banner would then claim the task moved while
            // it is still sitting in 4-auto-review.
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after accept: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        // Pin the chat-log line to the post-move folder via MoveJob's
        // authoritative path; see HandleEscalate for the rationale. The
        // chat-log auto-creates its parent folder, so a stale path would
        // resurrect the source lane as a one-line skeleton.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        CompletionAcceptanceRecord.MarkAccepted(
            movedFolderPath, "pipeline-execution.json", reason, _logger);
        WritePostProcessingOutcome(moved, PostProcessingOutcomes.PassToHumanReview,
            summary: reason,
            performerCliType: NormalizeReviewCliType(cliBinary),
            stepId: PipelineCatalogue.OrchestratorDecisionStepId,
            evidenceRef: "pipeline-execution.json");

        // Provenance: the orchestrator (not a human) advanced this task
        // toward Completed. Stamp on the authoritative post-move path.
        ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.Append(moved, OrchestratorMessageKind.Decision,
            $"Auto-review accepted \"{title}\" as done. Moved to 5-human-review for your approval. Reason: {reason}");

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
            TimelineActors.Orchestrator,
            $"Accepted as done. {reason}", new Dictionary<string, string>
            {
                ["verdict"] = "accept",
                ["reason"] = Truncate(reason, 600),
            });

        AppendReviewDecision(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: reason,
            Prompt: prompt,
            Response: response,
            FollowUp: string.Empty),
            current.FolderPath,
            movedFolderPath);
    }

    private string BuildPrompt(WatchPathEntry entry, PendingDecision pending, string workspace)
    {
        var (taskBody, recentLog) = LoadTaskContext(pending);
        var roadmapExcerpt = LoadRoadmap(entry.RootPath);
        var adrTitles = LoadAdrTitles(entry.RootPath);
        var prevDecisions = LoadPreviousDecisionsSummary(workspace, entry.Name, pending.Job.Id);

        var values = new Dictionary<string, string?>
        {
            ["project"] = entry.Name,
            ["job_id"] = pending.Job.Id,
            ["job_title"] = pending.Job.Title,
            ["needs_input_reason"] = pending.Reason ?? "(none provided)",
            ["task_body"] = taskBody,
            ["recent_log"] = recentLog,
            ["roadmap_excerpt"] = roadmapExcerpt,
            ["adr_titles"] = adrTitles,
            ["previous_decisions"] = prevDecisions,
        };
        try
        {
            return _prompts.Render("orchestrator-review-decision.md", values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to the review-decision fallback template");
            return _prompts.Render("orchestrator-review-decision-fallback.md", values);
        }
    }

    private void AppendReviewDecision(
        string workspace,
        ReviewDecisionRecord record,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath)
    {
        var epochFolder = !string.IsNullOrWhiteSpace(afterMoveFolderPath)
            ? afterMoveFolderPath
            : beforeMoveFolderPath;
        record = record with
        {
            AttemptEpoch = record.AttemptEpoch
                ?? OperatorReviewRequeueService.ReadEpoch(epochFolder),
        };
        ReviewDecisionLog.Append(workspace, record);
        var result = _workspaceArtifactCommits?.TryCommitRunBoundary(
            workspace,
            record.JobId,
            beforeMoveFolderPath,
            afterMoveFolderPath,
            record.Kind);
        if (result is { Success: false })
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: workspace artifact commit failed for {Project}/{JobId}: {Error}",
                record.Project, record.JobId, result.Error);
        }
    }

    private static (string TaskBody, string RecentLog) LoadTaskContext(PendingDecision pending)
    {
        var folder = pending.Job.FolderPath;
        var promptPath = Path.Combine(folder, "prompt.md");
        var task = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        var logPath = TaskPaths.CliOutputLog(folder);
        var recent = ReadActiveEpochLogTail(folder, logPath);
        // The terminal sentinel, final verification summary, and CLI exit are
        // at the end of the log. Head-truncating a verbose 200-line tail kept
        // early pre-restore failures while discarding their later successful
        // restore/re-run, which made the completion gate reissue clean tasks.
        return (Truncate(task, 4_000), TruncateTail(recent, 6_000));
    }

    private static string ReadActiveEpochLogTail(string folder, string logPath)
    {
        if (!File.Exists(logPath)) return string.Empty;
        var lines = File.ReadAllLines(logPath);
        var boundary = OperatorReviewRequeueService.ReadCliLogLineBoundary(folder);
        if (boundary is >= 0)
        {
            // A shorter file was replaced/truncated after the boundary, so all
            // of its current content is fresh. Otherwise skip the consumed
            // prefix retained by the append-only log.
            var skip = boundary.Value <= lines.Length ? boundary.Value : 0;
            lines = lines.Skip(skip).ToArray();
        }
        return TailLines(string.Join('\n', lines), 200);
    }

    /// <summary>
    /// Review post-steps use the last completed agent run as their run-window
    /// truth. A later launch-only failure can append an empty range, but it must
    /// never replace the successful diff that already earned review evidence.
    /// Legacy timelines without a completed run retain the old all-runs fallback.
    /// </summary>
    internal static IReadOnlyList<RunRecord> SelectAuthoritativeReviewRuns(IReadOnlyList<RunRecord> runs)
    {
        var last = SelectLastSuccessfulReviewRun(runs);
        return last == null ? runs : new[] { last };
    }

    internal static RunRecord? SelectLastSuccessfulReviewRun(IReadOnlyList<RunRecord> runs)
        => runs.LastOrDefault(r =>
            string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(r.HeadShaBefore)
            && !string.IsNullOrWhiteSpace(r.HeadShaAfter));

    private static bool HasResultsArtifacts(string jobFolderPath)
        => ResultsInventory.HasActiveArtifacts(jobFolderPath);

    private static string LoadRoadmap(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return string.Empty;
        var path = Path.Combine(rootPath, "ROADMAP.md");
        if (!File.Exists(path)) return string.Empty;
        try { return Truncate(File.ReadAllText(path), 1_500); }
        catch { return string.Empty; }
    }

    private static string LoadAdrTitles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return string.Empty;
        var path = Path.Combine(rootPath, "docs", "system", "architecture", "decisions", "adr-archive.md");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            var titles = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("## ADR", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("## ", StringComparison.Ordinal) && line.Contains("ADR", StringComparison.OrdinalIgnoreCase))
                {
                    titles.Add(line.TrimStart('#').Trim());
                    if (titles.Count >= 30) break;
                }
            }
            return string.Join('\n', titles);
        }
        catch { return string.Empty; }
    }

    private static string LoadPreviousDecisionsSummary(string workspace, string project, string jobId)
    {
        var records = ReviewDecisionLog.ReadAll(workspace, project)
            .Where(r => r.JobId == jobId)
            .TakeLast(5)
            .ToList();
        if (records.Count == 0) return "(none)";
        return string.Join('\n', records.Select(r => $"- {r.CreatedAt:u} [{r.Kind}] {r.Reason}"));
    }

    /// <param name="onSkipped">
    /// Optional probe invoked as <c>(jobId, reason)</c> whenever a card in the
    /// lane is passed over. The workspace sweep ignores it; the per-card path
    /// uses it so a card that produced no decision can name why.
    /// </param>
    private IEnumerable<PendingDecision> EnumeratePending(
        string workspace,
        WatchPathEntry entry,
        Action<string, string>? onSkipped = null)
    {
        // ADR-0024: list 4-auto-review through the typed layer
        // instead of walking the lane directory by hand. The cache
        // dominates the cost, so iterating typed records is also
        // faster than the original folder-walk + ScanAllJobs FirstOrDefault.
        foreach (var info in _taskAccess.ListByLaneInWorkspace(entry.Path, TaskStates.AutoReview))
        {
            if (info.Fixture)
            {
                onSkipped?.Invoke(info.Id, "fixture-card");
                continue;
            }

            // Canonical ReviewAttempts belong to the remote review data plane.
            // Until that executor claims the attempt, leaving the card in Auto
            // Review is the fail-closed state. The legacy Task Server review
            // loop must not infer a subject from session-events or inspect its
            // own project checkout for a Remote result.
            //
            // Report-only modes (planning / research) are the exception: their
            // deliverable is a document in the task folder on THIS server, no
            // ReviewAttempt is minted for them at completion, and the
            // lightweight report pipeline validates them deterministically
            // right here - so they must not wait for a code-review executor
            // that will never claim them.
            var authorityKey = _attemptAuthority is null || TaskModes.IsReportOnly(info.Mode)
                ? null
                : new[] { info.Key, info.TaskKey, info.Id }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(value => !_attemptAuthority.GetTaskProjection(value!).LegacyTask);
            if (authorityKey is not null)
            {
                _logger.LogDebug(
                    "ReviewDecisionOrchestrator skipped canonical remote review {TaskKey}; awaiting fenced ReviewAttempt executor.",
                    authorityKey);
                onSkipped?.Invoke(info.Id, PostProcessingCardResult.AwaitingCanonicalReviewExecutor);
                continue;
            }

            // An operator requeue is an explicit request to reassess the existing
            // completed run from fresh evidence. Its old terminal sentinel is
            // normally already resolved by the prior escalation follow-up, so
            // log scanning alone would yield no work and the queued card would
            // be skipped. While OperatorRequeue is the newest record in the
            // active epoch, force the full DONE gate + aspect path exactly as if
            // the completed run had just arrived. The first verdict appended by
            // that path supersedes the boundary and makes this trigger false.
            var pendingOperatorAssessment = false;
            try
            {
                var epoch = OperatorReviewRequeueService.ReadEpoch(info.FolderPath);
                ReviewDecisionLog.ReadLatestByJob(workspace, entry.Name)
                    .TryGetValue(info.Id, out var latest);
                pendingOperatorAssessment = IsPendingOperatorRequeueAssessment(latest, epoch);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "operator-requeue: fresh-assessment journal probe failed");
            }
            if (pendingOperatorAssessment)
            {
                yield return new PendingDecision(
                    info,
                    ReviewSignalKind.Done,
                    LineNumber: -1,
                    Reason: OperatorRequeueFreshAssessmentReason,
                    NeedsInput: null);
                continue;
            }

            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath))
            {
                // Sweep-guard (ASS-693 / ASS-716): a card in 4-auto-review with
                // no cli-output.log never had a core run - auto-review has
                // nothing to evaluate and the card would otherwise linger until
                // a sweep wiped it to 7-archive unworked. Bounce it to 2-ready
                // instead of silently skipping. Re-bill-safe: the move takes the
                // card out of this lane, so the next tick no longer sees it.
                yield return new PendingDecision(info, ReviewSignalKind.UnworkedNoCoreRun, LineNumber: -1, Reason: "no-core-run", NeedsInput: null);
                continue;
            }

            string log;
            try { log = File.ReadAllText(logPath); }
            catch
            {
                onSkipped?.Invoke(info.Id, "cli-log-unreadable");
                continue;
            }

            // The agent contract guarantees only one terminal sentinel per
            // run, but a job folder may have been continued and accumulated
            // multiple kinds across runs. Pick the latest unresolved one so
            // we act on the most recent state.
            var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);
            var noop = ReviewDecisionParsing.FindUnresolvedNoOp(log);
            var blocked = ReviewDecisionParsing.FindUnresolvedBlocked(log);
            var done = ReviewDecisionParsing.FindUnresolvedDone(log);

            if (needs == null && noop == null && blocked == null && done == null)
            {
                // Deterministic-completion contract (requirement 4): a job can
                // reach 4-auto-review with NO terminal sentinel at all - the
                // run exited with only heuristic "done"-ish prose, or the
                // terminal classifier force-routed an Unknown / committed-
                // partial outcome here. Such a run must never be silently
                // accepted as completed; it has to keep looping (reissue
                // demanding a sentinel) until the shared reissue budget is
                // spent, then escalate for operator intervention.
                //
                // LacksTerminalSentinelInLatestRun separates that from a
                // sentinel that was already resolved on a prior tick (the
                // orchestrator wrote a follow-up line and nothing ran since):
                // the latter returns false and is left untouched, so we never
                // re-bill an already-handled card.
                if (ReviewDecisionParsing.LacksTerminalSentinelInLatestRun(log))
                {
                    yield return new PendingDecision(info, ReviewSignalKind.NoCompletionSignal, LineNumber: -1, Reason: null, NeedsInput: null);
                }
                else if (IsStaleWithoutVerdict(workspace, entry.Name, info, logPath))
                {
                    // No-verdict guard: a card that has sat in 4-auto-review past
                    // the grace window with no recorded orchestrator verdict at
                    // all (its sentinel was resolved on a prior tick but no
                    // decision ever landed - e.g. a move that silently failed to
                    // stick). Force-process it as a no-completion-signal so it is
                    // driven to a conclusion rather than hanging without a verdict.
                    // The no-verdict precondition makes this re-bill-safe: the
                    // first force-process appends a decision record, after which
                    // the guard no longer fires.
                    yield return new PendingDecision(info, ReviewSignalKind.NoCompletionSignal, LineNumber: -1, Reason: "no-verdict-timeout", NeedsInput: null);
                }
                else if (GetStaleVerdictNeedingMove(workspace, entry.Name, info, logPath) is { } dueVerdict)
                {
                    // Stale-with-verdict guard: a recorded verdict whose lane
                    // move never completed (the verdict path resolved the
                    // sentinel via its follow-up line, then warned-but-continued
                    // on a failed MoveJob - ASS-759 move-lock / backend restart).
                    // Backfill the due move; idempotent and re-bill-safe because
                    // a successful move takes the card out of this lane.
                    yield return new PendingDecision(info, ReviewSignalKind.StaleWithVerdict, LineNumber: -1, Reason: "stale-with-verdict", NeedsInput: null, StaleVerdict: dueVerdict);
                }
                else
                {
                    // A sentinel that an earlier tick already resolved, with a
                    // verdict on record: nothing left to decide here.
                    onSkipped?.Invoke(info.Id, "no-unresolved-terminal-sentinel");
                }
                continue;
            }

            int needsLine = needs?.LineNumber ?? -1;
            int noopLine = noop?.LineNumber ?? -1;
            int blockedLine = blocked?.LineNumber ?? -1;
            int doneLine = done?.LineNumber ?? -1;

            // Priority is "latest unresolved sentinel wins". The terminal
            // markers (BLOCKED, NOOP, DONE) all live at the same level;
            // the latest one in the log is the one to act on. NEEDS_INPUT
            // keeps the existing handling.
            var maxTerminal = Math.Max(Math.Max(blockedLine, noopLine), doneLine);

            if (maxTerminal >= needsLine && blockedLine == maxTerminal && blocked != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.Blocked, blocked.LineNumber, blocked.Reason, NeedsInput: null);
            }
            else if (maxTerminal >= needsLine && noopLine == maxTerminal && noop != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.NoOp, noop.LineNumber, noop.Reason, NeedsInput: null);
            }
            else if (maxTerminal >= needsLine && doneLine == maxTerminal && done != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.Done, done.LineNumber, Reason: null, NeedsInput: null);
            }
            else if (needs != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.NeedsInput, needs.LineNumber, needs.Reason, NeedsInput: needs);
            }
        }
    }

    private string BuildReissueFollowUp(OrchestratorDecisionVerdict verdict) =>
        _prompts.Render("orchestrator-reissue-followup.md", new Dictionary<string, string?>
        {
            ["decision"] = verdict.Reason,
        }).TrimEnd('\r', '\n');

    internal static bool IsPendingOperatorRequeueAssessment(
        ReviewDecisionRecord? latest,
        int epoch)
    {
        epoch = Math.Max(0, epoch);
        if (epoch == 0)
        {
            return latest?.Kind == ReviewDecisionKind.OperatorRequeue
                && IsInAttemptEpoch(latest, epoch);
        }

        // The epoch sidecar is authoritative. If the best-effort journal
        // boundary failed after the lane move, the latest row still belongs to
        // an older epoch and must not resurrect its verdict. Keep forcing the
        // full gate/aspect path until a decision from this epoch is durable.
        if (latest is null || !IsInAttemptEpoch(latest, epoch))
            return true;

        return latest.Kind == ReviewDecisionKind.OperatorRequeue;
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text) || n <= 0) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text;
        return string.Join('\n', lines[^n..]);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..max] + "\n... (truncated)";
    }

    internal static string TruncateTail(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return "... (truncated)\n" + s[^max..];
    }

    private bool RateLimitOk(int maxPerHour)
    {
        lock (_callTimestampsLock)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
            return _callTimestamps.Count < maxPerHour;
        }
    }

    /// <summary>
    /// Charge one call against the per-hour rate budget. Thread-safe: the
    /// read-only review pool records calls from several review threads at once.
    /// </summary>
    private void RecordRateLimitedCall()
    {
        lock (_callTimestampsLock)
        {
            _callTimestamps.Enqueue(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Legacy fallback for tests that construct the orchestrator without
    /// a <see cref="AgentStudio.Cli.CliOneShotRegistry"/>.
    /// Stdin-piped — replaces the previous <c>-p &lt;prompt&gt;</c> argv path
    /// that caused the 2026-05-11 empty-reply incident on Windows.
    /// </summary>
    internal static string NormalizeReviewCliType(string? cli)
    {
        var value = cli?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return CliTypes.Codex;

        // Config can be shared across Windows and Linux runners. Normalize both
        // separator styles before asking the current platform for the file name.
        var fileName = Path.GetFileNameWithoutExtension(value.Replace('\\', '/'));
        if (fileName.Equals(CliTypes.Codex, StringComparison.OrdinalIgnoreCase)) return CliTypes.Codex;
        if (fileName.Equals(CliTypes.Gemini, StringComparison.OrdinalIgnoreCase)) return CliTypes.Gemini;
        if (fileName.Equals(CliTypes.Claude, StringComparison.OrdinalIgnoreCase)) return CliTypes.Claude;
        return value.ToLowerInvariant();
    }

    private static async Task<string> DefaultRunCliAsync(string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ReviewDecisionOrchestrator: stdin may already be closed by CLI"); /* stdin may already be closed by CLI */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return await stdoutTask;
        }
        catch (OperationCanceledException)
        {
            AgentStudio.Diagnostics.CliKillAudit.Trace(p, "ReviewDecisionOrchestrator:4661 (entireProcessTree)");
            try { p.Kill(true); } catch (Exception __ex) { SilentCatch.Note(__ex, "ReviewDecisionOrchestrator:4650"); }
            return string.Empty;
        }
    }

    /// <summary>
    /// Reissue tag id stamped on the job's tags array when an auto-review
    /// reissue parks it in <c>2-ready</c>. UI-only signal so the kanban
    /// can render the card distinctly from a fresh queued task; the
    /// runtime priority is carried by order 0, not by this tag.
    /// </summary>
    internal const string ReissueTagId = "reissue:autoreview";

    /// <summary>
    /// Provenance tag stamped on a task when the orchestrator advances it
    /// toward Completed via accept-as-done (both the multi-aspect and the
    /// single-verdict paths route to <c>5-human-review</c> per ADR-0025).
    /// Distinguishes an orchestrator-advanced task from one a human
    /// accepted by hand: human acceptance happens in the UI and never
    /// stamps this tag. Registered in the workspace tag registry (see
    /// <c>TagRegistryService</c>) so the kanban renders it with a label
    /// and colour. Unlike <see cref="ReissueTagId"/> this id uses the
    /// plain registry grammar (no colon).
    /// </summary>
    internal const string OrchestratorMovedTagId = "orchestrator-moved";

    /// <summary>
    /// Lane-target for every auto-review reissue path (NEEDS_INPUT,
    /// NOOP recovery, multi-aspect block). Routes the task to
    /// <c>2-ready</c> at order 0, stamps the reissue tag for UI
    /// highlighting, and returns the moved <see cref="TaskInfo"/> so the
    /// caller can write follow-up evidence next to the prompt. Returning
    /// <c>null</c> means the move did not complete (logged and recorded
    /// in the decision journal upstream); the caller then skips the
    /// follow-up file write.
    ///
    /// <para>
    /// Why not 3-progress: the pre-fix path moved straight to
    /// <c>3-progress</c> while the runner-pickup tick observed an empty
    /// lane and grabbed the next queued job (the 2026-05-11 incident).
    /// Routing to <c>2-ready</c> instead keeps reissues out of the
    /// "currently running" bucket; order 0 guarantees the runner picks
    /// the reissue as the very next task without displacing the active
    /// one. See ADR-0025 and the lane-write-3-progress-forbidden drift
    /// rule.
    /// </para>
    /// </summary>
    /// <param name="reopenCause">
    /// The quality-loop cause id the matching <c>quality_loop_reopened</c> row
    /// carries (<c>build-test-gate-fail</c>, <c>multi-aspect-block</c>, ...). It
    /// is also stamped onto the lane row: a build/test gate failure is its own
    /// ledger cause, every other reopen is a quality-loop reopen.
    /// </param>
    private TaskInfo? MoveReissueToReadyTop(TaskInfo current, WatchPathEntry entry, string reopenCause)
    {
        var move = GuardedMoveJob(
            current.Id, TaskStates.Ready, entry.Path,
            transitionCause: ReopenLaneChangeCause(reopenCause),
            transitionDetail: reopenCause);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to ready (reissue after {Cause}): {Status} {Message}",
                current.Id, reopenCause, move.Status, move.Message);
            return null;
        }

        // Use the authoritative post-move path from MoveJob rather than
        // a re-scan: the cache may not yet reflect the move, and writes
        // through OrchestratorChatLog auto-create their parent, so a
        // stale path would resurrect 4-auto-review as a one-line skeleton
        // (see HandleAcceptAsDone for the same fix).
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.Ready };

        // Order 0 lifts the reissue ahead of any fresh ready job (which
        // typically uses order >= 10) without rewriting their orders.
        // The runner picks by OrderBy(j => j.Order).
        TaskJsonFile.UpdateOrder(moved.FolderPath, 0, _logger);
        // UI hint only; the kanban shows the reissue tag distinctly.
        // Routed through ConcernTagWriter because the tag id uses the
        // namespace:value grammar that TaskMutationService.NormalizeTagId
        // would strip the colon from.
        ConcernTagWriter.MergeConcernTags(moved.FolderPath, new[] { ReissueTagId }, _logger);
        _scanner.InvalidateCache();
        return moved;
    }

    /// <summary>Quality-loop reopen cause id of the ledger row to the lane-change cause of the same row.</summary>
    internal static string ReopenLaneChangeCause(string reopenCause)
        => string.Equals(reopenCause, BuildTestGateReopenCause, StringComparison.Ordinal)
            ? LaneChangeCauses.GateFailure
            : LaneChangeCauses.QualityLoop;

    /// <summary>The <c>quality_loop_reopened.details.cause</c> of a failed build/test gate.</summary>
    internal const string BuildTestGateReopenCause = "build-test-gate-fail";

    /// <summary>The <c>quality_loop_reopened.details.cause</c> of an unworked card bounced back to Ready.</summary>
    private const string UnworkedNoCoreRunCause = "unworked-no-core-run";

    /// <summary>
    /// Persist the exact steering prompt that the orchestrator handed the agent,
    /// both as the canonical <c>orchestrator-follow-up.md</c> (read by the pickup
    /// pre-check) AND as an append-only, timestamped copy under
    /// <c>orchestrator-follow-up-history/</c>. The history copy is never
    /// overwritten, so an operator can reconstruct, per steering step, the exact
    /// prompt and the context it was given (resume vs fresh, prior attempt count,
    /// reason). This is the traceability half of the diff-steering fix
    /// (ASS-734): without it the only record of "what did the orchestrator tell
    /// the agent" was a single file the next reissue clobbered.
    /// </summary>
    private const string SteeringPromptHistoryHeading = "## Steering prompt (verbatim)";
    private const string ReissueRepeatGuardHeading = "## Reissue repeat guard: diagnosis first";

    private async Task<string> WriteFollowUpFileAsync(
        TaskInfo moved, string followUp, CancellationToken ct, SteeringContext? context = null)
    {
        var guarded = GuardRepeatedReissuePrompt(moved.FolderPath, followUp);
        await WriteFollowUpFilesAsync(
            moved.FolderPath, guarded.Prompt, context, moved.Id, _logger, ct);

        if (guarded.Intervened)
        {
            EmitVerdictTimeline(
                moved.FolderPath,
                TimelineEventKinds.OrchestratorSteered,
                TimelineActors.QualityLoop,
                "Repeated reissue prompt detected; diagnosis-first instructions were added before sending.",
                new Dictionary<string, string>
                {
                    ["cause"] = "reissue-prompt-repeat-guard",
                    ["action"] = "diagnosis-first-enrichment",
                    ["normalizedMatch"] = "true",
                    ["matchingPriorPrompts"] = guarded.MatchingPriorPrompts.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["matchedHistoryFile"] = guarded.MatchedHistoryFile ?? "unknown",
                    ["followUpPrompt"] = Truncate(guarded.Prompt, 4000),
                });
        }

        return guarded.Prompt;
    }

    /// <summary>
    /// Compare a proposed automatic reissue against the append-only steering
    /// history. Whitespace and casing differences are ignored. When the same
    /// base instruction has already been sent, replace any older guard block
    /// with a diagnosis-first block whose intervention count increases on each
    /// recurrence. This prevents the guard itself from becoming another
    /// byte-identical repeated prompt.
    /// </summary>
    internal static ReissuePromptGuardResult GuardRepeatedReissuePrompt(
        string folderPath, string followUp)
    {
        var basePrompt = StripReissueRepeatGuard(followUp);
        var normalizedCandidate = NormalizeReissuePrompt(basePrompt);
        var historyDir = Path.Combine(folderPath, "orchestrator-follow-up-history");
        if (normalizedCandidate.Length == 0 || !Directory.Exists(historyDir))
        {
            return new ReissuePromptGuardResult(followUp, false, 0, null);
        }

        var matchingFiles = new List<string>();
        try
        {
            foreach (var historyPath in Directory
                         .EnumerateFiles(historyDir, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var priorPrompt = ExtractSteeringPrompt(File.ReadAllText(historyPath));
                if (priorPrompt is null) continue;
                var normalizedPrior = NormalizeReissuePrompt(
                    StripReissueRepeatGuard(priorPrompt));
                if (string.Equals(
                        normalizedCandidate, normalizedPrior,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchingFiles.Add(historyPath);
                }
            }
        }
        catch (Exception)
        {
            // History is an observability sidecar. If it cannot be read, keep
            // the existing reissue path available rather than stranding a card.
            return new ReissuePromptGuardResult(followUp, false, 0, null);
        }

        if (matchingFiles.Count == 0)
        {
            return new ReissuePromptGuardResult(followUp, false, 0, null);
        }

        var times = matchingFiles.Count == 1
            ? "1 time"
            : $"{matchingFiles.Count} times";
        var enriched = basePrompt.TrimEnd() + "\n\n" +
            ReissueRepeatGuardHeading + "\n\n" +
            $"The normalized instruction above has already been sent {times}. " +
            "Repeating the previous approach unchanged did not converge. Before editing again:\n\n" +
            "1. Diagnose first from the latest run output and the newest relevant evidence file. " +
            "Name the exact failed check, blocking aspect, or missing evidence.\n" +
            "2. Name the target artifact and verification that will close that diagnosed gap.\n" +
            "3. Continue from the existing diff. Do not restart, redesign, or broaden the task.\n" +
            "4. Address only the diagnosed gap, run the named verification, and then emit the honest terminal sentinel.\n";

        return new ReissuePromptGuardResult(
            enriched,
            true,
            matchingFiles.Count,
            Path.GetFileName(matchingFiles[^1]));
    }

    internal static string NormalizeReissuePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;

        var normalized = new StringBuilder(prompt.Length);
        var pendingWhitespace = false;
        foreach (var ch in prompt)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = normalized.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                normalized.Append(' ');
                pendingWhitespace = false;
            }
            normalized.Append(char.ToUpperInvariant(ch));
        }
        return normalized.ToString();
    }

    private static string StripReissueRepeatGuard(string prompt)
    {
        var normalizedNewlines = (prompt ?? string.Empty).Replace("\r\n", "\n");
        var guardIndex = normalizedNewlines.IndexOf(
            ReissueRepeatGuardHeading, StringComparison.Ordinal);
        return guardIndex < 0
            ? normalizedNewlines.Trim()
            : normalizedNewlines[..guardIndex].Trim();
    }

    private static string? ExtractSteeringPrompt(string historyText)
    {
        var headingIndex = historyText.IndexOf(
            SteeringPromptHistoryHeading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0) return null;
        var prompt = historyText[(headingIndex + SteeringPromptHistoryHeading.Length)..].Trim();
        return prompt.Length == 0 ? null : prompt;
    }

    /// <summary>
    /// Pure-IO core of <see cref="WriteFollowUpFileAsync"/>: write the canonical
    /// follow-up file and the append-only versioned history copy into
    /// <paramref name="folderPath"/>. Extracted as an internal static so the
    /// audit-persistence behaviour (history file is created, carries the verbatim
    /// prompt + context, and never clobbers a prior step's copy) is testable
    /// against a temp folder without standing up the whole orchestrator. Returns
    /// the path of the history file written (null when that write failed) so the
    /// test can assert append-only-ness across calls.
    /// </summary>
    internal static async Task<string?> WriteFollowUpFilesAsync(
        string folderPath, string followUp, SteeringContext? context,
        string jobId, ILogger logger, CancellationToken ct)
    {
        var canonical = $"# Orchestrator follow-up\n\n{followUp}\n";
        try
        {
            var followUpPath = Path.Combine(folderPath, "orchestrator-follow-up.md");
            await File.WriteAllTextAsync(followUpPath, canonical, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to write follow-up file for {JobId}",
                jobId);
        }

        try
        {
            var historyDir = Path.Combine(folderPath, "orchestrator-follow-up-history");
            Directory.CreateDirectory(historyDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            var cause = SanitizeForFileName(context?.Cause ?? "reissue");
            // Append-only: two steers within the same millisecond must not
            // clobber each other, so disambiguate with a suffix when needed.
            var historyPath = Path.Combine(historyDir, $"{stamp}-{cause}.md");
            for (var n = 2; File.Exists(historyPath); n++)
                historyPath = Path.Combine(historyDir, $"{stamp}-{cause}-{n}.md");
            await File.WriteAllTextAsync(historyPath, RenderSteeringHistory(context, followUp), ct);
            return historyPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to write versioned follow-up history for {JobId}",
                jobId);
            return null;
        }
    }

    /// <summary>
    /// Render one versioned steering-history entry: a context header (timestamp,
    /// cause/verdict, prior attempt count, resume-vs-fresh, reason) followed by
    /// the verbatim steering prompt the agent received. Public-shaped string so
    /// the FE protocol/timeline pane can show "Prompt + Context" per step.
    /// </summary>
    internal static string RenderSteeringHistory(SteeringContext? context, string followUp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Orchestrator steering step");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"- timestamp: {DateTime.UtcNow:O}");
        if (context != null)
        {
            sb.AppendLine($"- cause: {context.Cause}");
            sb.AppendLine($"- verdict: {context.Verdict}");
            sb.AppendLine($"- priorReissues: {context.PriorReissues}");
            sb.AppendLine($"- mode: {(context.ResumeSessionId != null ? "resume" : "fresh-run")}");
            if (context.ResumeSessionId != null)
                sb.AppendLine($"- resumeSessionId: {context.ResumeSessionId}");
            if (!string.IsNullOrWhiteSpace(context.Reason))
                sb.AppendLine($"- reason: {context.Reason!.Replace("\r", " ").Replace("\n", " ").Trim()}");
            if (context.PriorCommits is { Count: > 0 })
            {
                sb.AppendLine("- priorCommits:");
                foreach (var commit in context.PriorCommits.Take(20))
                    sb.AppendLine($"  - {commit.Replace("\r", " ").Replace("\n", " ").Trim()}");
            }
        }
        sb.AppendLine();
        sb.AppendLine(SteeringPromptHistoryHeading);
        sb.AppendLine();
        sb.AppendLine(followUp);
        return sb.ToString();
    }

    private static string SanitizeForFileName(string value)
    {
        var trimmed = (value ?? "reissue").Trim();
        if (trimmed.Length == 0) trimmed = "reissue";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// The orchestrator context recorded alongside a single steering step so the
    /// operator can see exactly what the agent was told and why. Optional on the
    /// write path: deterministic branches that carry the data pass it; the rest
    /// still get the verbatim prompt versioned without the structured header.
    /// </summary>
    internal sealed record SteeringContext(
        string Cause,
        string Verdict,
        int PriorReissues,
        string? Reason = null,
        string? ResumeSessionId = null,
        IReadOnlyList<string>? PriorCommits = null);

    internal sealed record ReissuePromptGuardResult(
        string Prompt,
        bool Intervened,
        int MatchingPriorPrompts,
        string? MatchedHistoryFile);

    private enum ReviewSignalKind
    {
        NeedsInput,
        NoOp,
        Blocked,
        Done,
        /// <summary>
        /// The run landed in 4-auto-review with no terminal sentinel at all
        /// (heuristic "done"-ish prose, an Unknown/committed-partial outcome
        /// force-routed here, etc.). The deterministic-completion contract
        /// forbids treating this as completed: the loop reissues demanding a
        /// sentinel until the shared reissue budget is spent, then escalates
        /// to human review. Detected via
        /// <see cref="ReviewDecisionParsing.LacksTerminalSentinelInLatestRun"/>.
        /// </summary>
        NoCompletionSignal,

        /// <summary>
        /// A card parked in 4-auto-review past the grace window whose latest
        /// decision-journal verdict already implies a lane move that never
        /// completed (the move failed after the verdict was recorded - a
        /// Move-Lock or a backend restart between record and MoveJob). The tick
        /// backfills the due move idempotently: reissue -> 2-ready, escalate /
        /// accept-as-done -> 5-human-review. Detected via
        /// <see cref="GetStaleVerdictNeedingMove"/>; the due verdict travels on
        /// <see cref="PendingDecision.StaleVerdict"/>.
        /// </summary>
        StaleWithVerdict,

        /// <summary>
        /// A card sits in 4-auto-review with no core run behind it at all: no
        /// <c>cli-output.log</c> exists, so no agent run ever streamed output
        /// (0 commits, no run). Auto-review presupposes a completed core run;
        /// such a card was mis-placed here (a decomposition that targeted the
        /// review lane, a hand move) and must never be silently swept to
        /// 7-archive unworked - the ASS-693 / ASS-716 incident. It is bounced
        /// back to 2-ready (needs-work) so the pickup loop actually runs it.
        /// </summary>
        UnworkedNoCoreRun
    }

    private sealed record PendingDecision(
        TaskInfo Job,
        ReviewSignalKind Kind,
        int LineNumber,
        string? Reason,
        NeedsInputState? NeedsInput,
        ReviewDecisionKind? StaleVerdict = null);

    private sealed record NoOpProgressEvidence(
        bool SawNoOpRecoveryReissue,
        int ToolCalls,
        int FileChanges,
        int AgentSubstanceChars)
    {
        public static readonly NoOpProgressEvidence None = new(false, 0, 0, 0);
    }
}
