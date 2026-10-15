

namespace AgentStudio.Runner;

/// <summary>
/// Background loop that runs the orchestrator intake check on every
/// 2-ready card sitting in <c>phase = human-ready</c> (or with no phase
/// at all) inside a project that has <c>IntakeEnabled = true</c>.
///
/// <para>
/// Intake is opt-in per project (<see cref="ProjectSettings.IntakeEnabled"/>)
/// so the broader migration risk stays bounded. When the flag is off, this
/// loop is a no-op for that project; the runner pickup gate stays open and
/// behavior matches the pre-intake board.
/// </para>
///
/// <para>
/// Intake runs decoupled from the coding runner: per the open question in
/// <c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c> section 13,
/// intake is not a coding run, so it can run while the project's coding
/// runner is busy on a different job. The single-active-run boundary
/// applies to <c>3-progress</c>, not to intake. Because intake holds no
/// code seat, it is parallel-safe: a tick drains every awaiting card in the
/// project (up to <see cref="MaxIntakePerProjectPerTick"/>) rather than one
/// at a time, so a batch of newly-ready cards is prepared together instead
/// of one-per-20s. The cap bounds work on a very large board.
/// </para>
/// </summary>
public sealed class IntakeHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper bound on intake runs per project per tick. Intake is cheap and
    /// deterministic, but a single tick should not fan out unboundedly on a
    /// board with hundreds of freshly-ready cards; the remainder is picked up
    /// on the next tick.
    /// </summary>
    internal const int MaxIntakePerProjectPerTick = 16;

    private readonly IServiceProvider _services;
    private readonly ILogger<IntakeHostedService> _logger;

    public IntakeHostedService(IServiceProvider services, ILogger<IntakeHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so a backend restart does not race the watcher's
        // initial scan and try to intake jobs before the project list resolves.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ProjectSettingsService>();
                var scanner = scope.ServiceProvider.GetRequiredService<TaskScannerService>();
                var intake = scope.ServiceProvider.GetRequiredService<IntakeRunner>();
                var orchestratorDefaults = scope.ServiceProvider
                    .GetRequiredService<AgentStudio.Registry.OrchestratorDefaultsProvider>();

                var escalation = scope.ServiceProvider.GetRequiredService<HumanReviewEscalation>();
                var decisions = scope.ServiceProvider.GetService<AiOperatorDecisionGateway>()
                    ?? AiOperatorDecisionGateway.ForTestHost(scope.ServiceProvider.GetRequiredService<IConfiguration>());

                var allSettings = settings.GetAll();
                // AGT-1812: autonomy resolves project override -> workspace
                // default -> platform default (2) so a workspace-level manual
                // setting also suppresses background intake.
                var enabledProjects = scanner.GetWatchPaths()
                    .Where(p => allSettings.TryGetValue(p.Name, out var s)
                                && ShouldAutoRunIntake(s, orchestratorDefaults.ResolveAutonomyLevel(p.Name)))
                    .ToList();

                if (enabledProjects.Count == 0)
                {
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in enabledProjects)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    ProcessProject(scanner, intake, escalation, decisions, entry, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ProcessProject(TaskScannerService scanner, IntakeRunner intake, HumanReviewEscalation escalation,
        AiOperatorDecisionGateway decisions, WatchPathEntry entry, CancellationToken stoppingToken)
    {
        // Drain every 2-ready job that has not yet been intaked, oldest first,
        // up to the per-tick cap. A null phase counts as human-ready under the
        // compatibility contract; an explicit human-ready also qualifies.
        // intake-running, intake-passed, and intake-blocked are skipped: they
        // are the runner's signal that the verdict already exists. Intake holds
        // no code seat, so processing several cards in one tick is safe — it is
        // the parallel-prep contract, not a coding run.
        var candidates = SelectCandidates(scanner.ScanAllAutomationJobs(), entry.Path, MaxIntakePerProjectPerTick);

        foreach (var candidate in candidates)
        {
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                var verdict = intake.RunForJob(candidate.Id, entry.Path);

                // Done-precheck routing (prompt requirement 5): a card the prompt
                // declares already finished is not executed. Route it to
                // 5-human-review for a person to confirm-and-complete instead of
                // leaving it parked in intake-blocked. AGT-3060: the routing is
                // the AI Operator decider intake-routing; on the Engine switch
                // IntakeRoutingEffect applies the receipt.
                var facts = new IntakeOperatorFacts(verdict.Outcome.ToString(), verdict.Reason);
                var submission = decisions.Submit(new AiOperatorDecisionRequest(entry.Name, candidate.Id,
                    candidate.State, new AiOperatorDecisionPayload(AiOperatorDeciders.IntakeRouting,
                        IntakeRoutingEffect.SubjectKey(candidate.Id, facts), IntakeRouting: facts), Period: null));
                if (submission.MonolithDecision?.Effect == AiOperatorEffects.Escalate)
                    RouteAlreadyDone(escalation, candidate.Id, entry.Path, entry.Name, verdict.Reason, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake run for {JobId} in {Project} failed", candidate.Id, entry.Name);
            }
        }
    }

    /// <summary>
    /// Done-precheck routing for an already-done card. The orchestrator never
    /// auto-completes (the human owns the final <c>6-completed</c> move), so the
    /// card is routed to <c>5-human-review</c> through the
    /// <see cref="HumanReviewEscalation"/> funnel under the
    /// <see cref="HumanReviewEscalationCategories.HumanDecisionNeeded"/> category —
    /// it exists for a person to confirm-and-complete, never for an agent to run.
    /// Going through the funnel (rather than a raw state move) guarantees the move
    /// records an orchestrator verdict + status stub, which the
    /// <c>HumanReviewVerdictDriftTest</c> mechanically enforces. Best-effort: a
    /// failed move leaves the card in intake-blocked (the pickup gate already
    /// keeps the runner off it). Extracted and internal so the routing is
    /// unit-testable without the BackgroundService loop.
    /// </summary>
    internal static void RouteAlreadyDone(
        HumanReviewEscalation escalation, string jobId, string watchPath, string project, string reason, ILogger logger)
    {
        try
        {
            var outcome = escalation.Escalate(
                jobId, watchPath, project,
                HumanReviewEscalationCategories.HumanDecisionNeeded,
                reason);
            if (outcome.Status == MoveJobStatus.Success)
                logger.LogInformation(
                    "Intake done-precheck: routed already-done card {JobId} in {Project} to 5-human-review for confirm-and-complete.",
                    jobId, project);
            else
                logger.LogWarning(
                    "Intake done-precheck: routing {JobId} in {Project} to 5-human-review did not complete: {Status} {Message}",
                    jobId, project, outcome.Status, outcome.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Intake done-precheck: routing {JobId} in {Project} to 5-human-review threw", jobId, project);
        }
    }

    /// <summary>
    /// Pure selection surface: the awaiting 2-ready cards for one project,
    /// oldest first, capped at <paramref name="cap"/>. Cards already stamped
    /// intake-running / intake-passed / intake-blocked are excluded so a tick
    /// never re-runs a settled verdict. Extracted so the parallel-prep contract
    /// (drain several awaiting cards per tick, bounded) is unit-testable without
    /// the BackgroundService loop.
    /// </summary>
    internal static IReadOnlyList<TaskInfo> SelectCandidates(IEnumerable<TaskInfo> jobs, string watchPath, int cap)
    {
        return jobs
            .Where(j => !j.Fixture
                        && string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)
                        && j.State == TaskStates.Ready
                        && IsAwaitingIntake(j.Phase))
            .OrderBy(j => j.Order)
            .ThenBy(j => j.CreatedAt)
            .Take(cap)
            .ToList();
    }

    private static bool IsAwaitingIntake(string? phase)
    {
        return string.IsNullOrWhiteSpace(phase)
            || string.Equals(phase, LifecyclePhases.HumanReady, StringComparison.Ordinal);
    }

    /// <summary>
    /// Background intake is automatic preparation, so the manual autonomy level
    /// suppresses it even when intake is enabled. A user can still trigger
    /// intake explicitly through the manual endpoint.
    /// </summary>
    internal static bool ShouldAutoRunIntake(ProjectSettings settings)
        => ShouldAutoRunIntake(settings, settings.AutonomyLevel ?? 2);

    /// <summary>
    /// Overload taking an already-resolved autonomy level (AGT-1812: project
    /// override -> workspace default -> platform default). The single-argument
    /// form uses the project-only value.
    /// </summary>
    internal static bool ShouldAutoRunIntake(ProjectSettings settings, int autonomyLevel)
        => settings.IntakeEnabled is true && autonomyLevel > 0;
}
