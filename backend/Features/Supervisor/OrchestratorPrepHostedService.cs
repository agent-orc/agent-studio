

namespace AgentStudio.Supervisor;

/// <summary>
/// ADR-0026 orchestrator-prep loop, reshaped into the optional
/// <see cref="PipelineCatalogue.PreOrchestratorPrepStepId"/> pre-coding step.
/// Per project, it runs the pure-rule engine in
/// <see cref="OrchestratorPrepRules"/> in-place on the head
/// <c>1-preparation</c> card and then either admits it to <c>2-ready</c>
/// (accept or bounce - the retired 1b-needs-human-review lane is gone), or
/// leaves it in <c>1-preparation</c> with an iteration counter incremented.
/// The standalone <c>1a-orchestrator-prep</c> backlog lane is gone: prep is no
/// longer a lane the operator sees, it is the <c>pre-orchestrator-prep</c>
/// pipeline step that runs in the active flow before the coding run.
///
/// <para>Off by default. The global kill switch stays
/// <c>Orchestrator:PrepEnabled = true</c>; on top of that the step is opt-in
/// per project via the <c>pre-orchestrator-prep</c>
/// <see cref="ProjectSettings.PipelineSteps"/> override (the step's
/// <see cref="PipelineStep.DefaultEnabled"/> is false), resolved by
/// <see cref="PipelineStepConfigResolver"/>. Rate-limited via
/// <c>Orchestrator:PrepCallsPerHour</c> (default 30).</para>
///
/// <para>The loop runs decoupled from the runner's pickup tick and never
/// holds the coding latch, so it does not block throughput
/// (<see cref="StepRunMode.Parallel"/>). Preparation is its own pipeline phase
/// and does not start a coding CLI; it only reads jobs in
/// <c>1-preparation</c> and writes verdicts into <c>2-ready</c>.
/// ADR-0001's boundary (one coding CLI per
/// project at a time) is unchanged - that invariant is enforced inside
/// <see cref="AgentStudio.Runner.ProjectRunner.TickAsync"/> via
/// the active-job latch, not here. The runner consumes from <c>2-ready</c> on
/// its own tick, so state mutations written by this service are picked up
/// without explicit coordination.</para>
///
/// <para>Every decision is recorded as a <see cref="StepKind.Module"/> step in
/// the job's <c>pipeline-execution.json</c> with the resolved per-project
/// model and a verdict, so the prep pass surfaces in the pipeline table with
/// status + duration. This slice is heuristic-only (no LLM calls); the clarity
/// score in <see cref="OrchestratorPrepRules.ScoreClarity"/> is auditable and
/// cheap, and a fast-model upgrade is a follow-up slice that does not change
/// the step shape or the autonomy gating.</para>
/// </summary>
public sealed class OrchestratorPrepHostedService : BackgroundService
{
    /// <summary>
    /// Last-resort model recorded when neither the per-step override nor the
    /// project <see cref="ProjectSettings.OrchestratorModel"/> sets one. Prep
    /// is currently heuristic-only, but its pipeline telemetry follows the
    /// same bounded Codex support-model default as the other review steps.
    /// The project's selection still wins via
    /// <see cref="PipelineStepConfigResolver.ResolveModel(ProjectSettings?, PipelineStep, string)"/>.
    /// </summary>
    public static string PrepFallbackModel => PipelineStepModelDefaults.SupportModel;

    /// <summary>The catalogue prep step, resolved once for config + recording.</summary>
    private static readonly PipelineStep PrepStep =
        PipelineCatalogue.Standard.Pre.First(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);

    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly ProjectSettingsService _settings;
    private readonly AgentStudio.Registry.OrchestratorDefaultsProvider _orchestratorDefaults;
    private readonly OrchestratorChatLog _chatLog;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrchestratorPrepHostedService> _logger;

    private readonly Queue<DateTime> _callTimestamps = new();
    private bool _migratedStrayLane;

    public OrchestratorPrepHostedService(
        TaskScannerService scanner,
        TaskStateMachine states,
        ProjectSettingsService settings,
        AgentStudio.Registry.OrchestratorDefaultsProvider orchestratorDefaults,
        OrchestratorChatLog chatLog,
        PipelineExecutionLog pipelineLog,
        IConfiguration configuration,
        ILogger<OrchestratorPrepHostedService> logger)
    {
        _scanner = scanner;
        _states = states;
        _settings = settings;
        _orchestratorDefaults = orchestratorDefaults;
        _chatLog = chatLog;
        _pipelineLog = pipelineLog;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue("Orchestrator:PrepTickSeconds", 60);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        // One-shot rescue of any card still parked in the retired
        // 1a-orchestrator-prep lane back to 1-preparation, so removing the lane
        // from the board never orphans an in-flight card. Runs regardless of
        // PrepEnabled - the lane is gone whether or not prep is turned on.
        MigrateStrayPrepLaneCards();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue("Orchestrator:PrepEnabled", false))
                    await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "OrchestratorPrep tick failed"); }

            intervalSeconds = _configuration.GetValue("Orchestrator:PrepTickSeconds", 60);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs one pass over every watched project. Public for tests.
    /// </summary>
    public Task TickOnceAsync(CancellationToken ct)
    {
        var maxPerHour = _configuration.GetValue("Orchestrator:PrepCallsPerHour", 30);
        var maxIterations = _configuration.GetValue("Orchestrator:MaxPrepIterations", 3);
        var queueFloor = _configuration.GetValue("Orchestrator:QueueFloor", 2);

        var entries = _scanner.GetWatchPaths();
        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                ProcessProject(entry.Name, entry.Path, maxIterations, queueFloor, maxPerHour);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OrchestratorPrep failed for project {Project}", entry.Name);
            }
        }
        return Task.CompletedTask;
    }

    private void ProcessProject(string projectName, string watchPath, int maxIterations, int queueFloor, int maxPerHour)
    {
        var settings = _settings.Get(projectName);
        // AGT-1812: autonomy resolves project override -> workspace default ->
        // platform default (2). Falls through to the project-only value when no
        // workspace default is set, so behaviour is unchanged until one is.
        var level = _orchestratorDefaults.ResolveAutonomyLevel(projectName);
        if (level == 0) return; // manual: never moves a task forward

        var allJobs = _scanner.ScanAllAutomationJobs().Where(j => j.WatchPath == watchPath).ToList();
        var prep = allJobs.Where(j => j.State == TaskStates.Preparation).OrderBy(j => j.Order).ToList();
        var ready = allJobs.Where(j => j.State == TaskStates.Ready).OrderBy(j => j.Order).ToList();

        // Backpressure: only feed the active flow when 2-ready is below the
        // floor, and only the head 1-preparation card per tick. Prep runs
        // in-place (no 1a-orchestrator-prep hop) - accept and bounce both admit
        // to 2-ready (the retired 1b-needs-human-review lane is gone), iterate
        // leaves the card at the head of 1-preparation to be re-evaluated next
        // tick. This keeps prep before the coding run without ever blocking
        // pickup throughput.
        if (ready.Count >= queueFloor || prep.Count == 0) return;
        if (!RateLimitOk(maxPerHour))
        {
            _logger.LogInformation(
                "OrchestratorPrep rate limit reached ({MaxPerHour}/h); skipping this tick", maxPerHour);
            return;
        }

        var job = prep.First();
        var pipelineSettings = PipelineTypeSettings.ForTask(settings, job)!;

        // Optional per project: prep is the opt-in pre-orchestrator-prep
        // pipeline step (DefaultEnabled = false). Evaluate the full per-step
        // condition against the head preparation card so task-type / tag gates
        // apply here like they do for post-run pipeline steps.
        if (!PipelineStepConfigResolver.ShouldRun(pipelineSettings, PrepStep, new PipelineStepConditionContext
            {
                Aborted = false,
                ExitCode = null,
                AnyAspectFailed = false,
                TaskType = job.TaskType,
                Tags = job.Tags,
            }))
        {
            return;
        }

        var promptText = ReadPromptText(job);
        var prevText = "";
        var nextText = "";
        // Heuristic neighbour-context: previous = last ready, next = next ready.
        // Both are best-effort; missing files don't break the score.
        try
        {
            var prevJob = ready.LastOrDefault();
            if (prevJob != null) prevText = ReadPromptText(prevJob);
            var nextJob = ready.FirstOrDefault();
            if (nextJob != null) nextText = ReadPromptText(nextJob);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "OrchestratorPrepHostedService: best-effort"); /* best-effort */ }

        var iteration = ReadIteration(job);
        var input = new OrchestratorPrepRules.PrepInput
        {
            PromptText = promptText,
            Slug = job.Id,
            Iteration = iteration,
            MaxIterations = level == 1 ? 1 : maxIterations,
            AutonomyLevel = level,
            PrevPromptText = prevText,
            NextPromptText = nextText,
        };

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decision = OrchestratorPrepRules.Decide(input);
        sw.Stop();
        _callTimestamps.Enqueue(DateTime.UtcNow);

        RecordPrepStep(job, pipelineSettings, decision, startedAt, sw.Elapsed);
        ApplyDecision(job, decision, iteration);
    }

    /// <summary>
    /// Record the prep decision as the <c>pre-orchestrator-prep</c> step in the
    /// job's pipeline-execution log: status from the verdict, the resolved
    /// per-project model, and the wall-clock duration. Surfaces prep in the
    /// pipeline table. Hold takes no action on the card, so it is not recorded
    /// (avoids rewriting the file every tick on a held card).
    /// </summary>
    private void RecordPrepStep(
        TaskInfo job,
        ProjectSettings settings,
        OrchestratorPrepRules.PrepDecision decision,
        DateTime startedAt,
        TimeSpan elapsed)
    {
        if (decision.Verdict == OrchestratorPrepRules.Verdict.Hold) return;

        var status = decision.Verdict switch
        {
            OrchestratorPrepRules.Verdict.Accept => PipelineStepStatus.Passed,
            OrchestratorPrepRules.Verdict.Bounce => PipelineStepStatus.Failed,
            OrchestratorPrepRules.Verdict.Iterate => PipelineStepStatus.Running,
            _ => PipelineStepStatus.Pending,
        };
        var model = PipelineStepConfigResolver.ResolveModel(settings, PrepStep, PrepFallbackModel);

        try
        {
            var pipeline = ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(job), settings);
            // Attach to the in-flight run when the core / aspect stages already
            // created one; otherwise begin a fresh record so the step is not a
            // silent no-op while the card is still in preparation.
            var pipelineRecord = _pipelineLog.EnsureRun(
                job.FolderPath, pipeline, job.ProjectName, job.Id);
            using var pipelineAttempt = _pipelineLog.EnterAttempt(
                job.FolderPath, pipelineRecord.Attempt);
            _pipelineLog.RecordStep(job.FolderPath, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.PreOrchestratorPrepStepId,
                Kind = StepKind.Module,
                Model = model,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = startedAt + elapsed,
                DurationMs = (long)elapsed.TotalMilliseconds,
                Verdict = decision.Verdict.ToString().ToLowerInvariant(),
                VerdictSummary = decision.Note
                    ?? (decision.Verdict == OrchestratorPrepRules.Verdict.Bounce
                        ? $"bounce: {decision.BounceReason}"
                        : $"clarity {decision.Clarity:F2}"),
                Reason = decision.Verdict == OrchestratorPrepRules.Verdict.Bounce
                    ? decision.BounceReason.ToString()
                    : null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OrchestratorPrep failed to record pipeline step for {JobId}", job.Id);
        }
    }

    /// <summary>
    /// One-shot rescue: move any card left in the retired
    /// <c>1a-orchestrator-prep</c> lane back to <c>1-preparation</c> so the
    /// removed lane never strands an in-flight card. Idempotent - once a card
    /// is moved out there is nothing left for the next boot to do. Public for
    /// tests; guarded so it runs at most once per process.
    /// </summary>
    public void MigrateStrayPrepLaneCards()
    {
        if (_migratedStrayLane) return;
        _migratedStrayLane = true;

        try
        {
            var stray = _scanner.ScanAllAutomationJobs()
                .Where(j => j.State == TaskStates.OrchestratorPrep)
                .ToList();
            foreach (var job in stray)
            {
                var moved = _states.MoveJob(
                    job.Id, TaskStates.Preparation, job.WatchPath,
                    transitionCause: LaneChangeCauses.SystemMove, transitionDetail: "stray-prep-lane-migration");
                if (moved.Status == MoveJobStatus.Success)
                {
                    _logger.LogInformation(
                        "OrchestratorPrep migrated stray card {JobId} from {OldLane} back to {NewLane} (lane retired)",
                        job.Id, TaskStates.OrchestratorPrep, TaskStates.Preparation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OrchestratorPrep stray-lane migration failed");
        }
    }

    private void ApplyDecision(TaskInfo job, OrchestratorPrepRules.PrepDecision decision, int iteration)
    {
        switch (decision.Verdict)
        {
            case OrchestratorPrepRules.Verdict.Accept:
                _states.MoveJob(
                    job.Id, TaskStates.Ready, job.WatchPath,
                    transitionCause: LaneChangeCauses.Promoted, transitionDetail: "orchestrator-prep-accept");
                _chatLog.Append(job, OrchestratorMessageKind.Decision,
                    decision.Note ?? $"orchestrator-prep: accept (clarity {decision.Clarity:F2}); -> {TaskStates.Ready}");
                break;

            case OrchestratorPrepRules.Verdict.Bounce:
                WriteBounceMetadata(job, decision);
                // The 1b-needs-human-review bounce lane has been retired. Admit
                // the card to 2-ready instead: a normally-unclear task gets an
                // agent attempt (it can still emit NEEDS_INPUT), and an explicit
                // human-decision-needed marker is herded onward to 5-human-review
                // by the runner's pickup sweep (RelocateStrayHumanDecisionCards).
                _states.MoveJob(
                    job.Id, TaskStates.Ready, job.WatchPath,
                    transitionCause: LaneChangeCauses.Promoted, transitionDetail: "orchestrator-prep-bounce");
                _chatLog.Append(job, OrchestratorMessageKind.GiveUp,
                    $"orchestrator-prep: bounce ({decision.BounceReason}); clarity {decision.Clarity:F2}; -> {TaskStates.Ready}");
                break;

            case OrchestratorPrepRules.Verdict.Iterate:
                WriteIterationMetadata(job, iteration + 1, decision);
                _chatLog.Append(job, OrchestratorMessageKind.Decision,
                    $"orchestrator-prep: iterate {iteration + 1} (clarity {decision.Clarity:F2})");
                break;

            case OrchestratorPrepRules.Verdict.Hold:
                // No move. Level 0 stays in 1-preparation; nothing logged so the
                // orchestrator does not spam the chat at every tick.
                break;
        }
    }

    private static string ReadPromptText(TaskInfo job)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "prompt.md");
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch { return ""; }
    }

    private int ReadIteration(TaskInfo job)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            if (!File.Exists(p)) return 0;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("iteration", out var it) ? it.GetInt32() : 0;
        }
        catch { return 0; }
    }

    private void WriteIterationMetadata(TaskInfo job, int newIteration, OrchestratorPrepRules.PrepDecision decision)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            var payload = new
            {
                iteration = newIteration,
                lastVerdict = decision.Verdict.ToString().ToLowerInvariant(),
                lastClarity = decision.Clarity,
                updatedAt = DateTime.UtcNow,
            };
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write orchestrator-prep.json for {JobId}", job.Id);
        }
    }

    private void WriteBounceMetadata(TaskInfo job, OrchestratorPrepRules.PrepDecision decision)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            var payload = new
            {
                iteration = 0,
                lastVerdict = "bounce",
                lastClarity = decision.Clarity,
                bounceReason = decision.BounceReason.ToString(),
                bouncedAt = DateTime.UtcNow,
            };
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write orchestrator-prep.json bounce metadata for {JobId}", job.Id);
        }
    }

    private bool RateLimitOk(int maxPerHour)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
        return _callTimestamps.Count < maxPerHour;
    }
}
