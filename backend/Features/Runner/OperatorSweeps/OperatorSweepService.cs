using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>Configuration of the operator sweep service (section <c>OperatorSweeps</c>).</summary>
public sealed record OperatorSweepOptions(bool Enabled, TimeSpan TickInterval, TimeSpan InitialDelay, int MaxRoundsPerCard)
{
    /// <summary>The cadence the night-shift <c>sweeper.sh</c> loop used.</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMinutes(10);

    public static OperatorSweepOptions FromConfiguration(IConfiguration? configuration)
    {
        var section = configuration?.GetSection("OperatorSweeps");
        var tickSeconds = section?.GetValue("TickSeconds", (int)DefaultTickInterval.TotalSeconds)
                          ?? (int)DefaultTickInterval.TotalSeconds;
        var delaySeconds = section?.GetValue("InitialDelaySeconds", 60) ?? 60;
        return new OperatorSweepOptions(
            section?.GetValue("Enabled", true) ?? true,
            TimeSpan.FromSeconds(Math.Max(1, tickSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, delaySeconds)),
            CardRoundBudget.ResolveAllowed(configuration));
    }
}

/// <summary>Seam for the hosted loop, so a failing tick can be simulated.</summary>
public interface IOperatorSweepRunner
{
    OperatorSweepOptions Options { get; }

    Task<OperatorSweepTickReport> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>Counters of one tick, per sweep.</summary>
public sealed record OperatorSweepTickReport(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int Cards,
    int Acted,
    int Held,
    int WaitingForPerson,
    int Failed,
    string? Error = null);

/// <summary>
/// The three night-shift sweeps as one supervised product service (AGT-3011):
/// <c>fix-rounds</c> (ProductFailure to fix round), <c>gate-triage</c> (merge
/// gate failure classified, product failure to fix round), and <c>salvage</c>
/// (timed-out run continued from its salvage commit).
/// <para>
/// Flow per card, in the backend guide's order: read the facts once
/// (lane, attempt authority, timeline, decision journal, settings), let
/// <see cref="OperatorSweepTriggers"/> name the exact failure per sweep, let
/// <see cref="OperatorSweepPolicy"/> decide, then apply at most one bounded
/// side effect through <see cref="IOperatorSweepActions"/> and write its
/// receipts: a <see cref="ReviewDecisionKind.Reissue"/> journal record (which
/// charges the shared <see cref="CardRoundBudget"/>) and an
/// <see cref="TimelineEventKinds.OperatorSweepRoundStarted"/> timeline entry in
/// the task folder (which makes the subject idempotent).
/// </para>
/// <para>
/// The service owns no state file. Pause state lives in project settings; the
/// per-card budget and receipts live in the decision journal and the task
/// folder; the last tick's per-card reasons live in memory for the projection
/// and are rebuilt by the next tick after a restart.
/// </para>
/// </summary>
public sealed class OperatorSweepService : IOperatorSweepRunner
{
    private const int RecentActionLimit = 20;

    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly ProjectSettingsService _settings;
    private readonly TimelineLog _timeline;
    private readonly IOperatorSweepGateFacts _gateFacts;
    private readonly IOperatorSweepActions _actions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatorSweepService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private readonly ConcurrentDictionary<string, OperatorSweepRunState> _runs =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectTickState> _projects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _startedAtUtc;

    public OperatorSweepService(
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        ProjectSettingsService settings,
        TimelineLog timeline,
        IOperatorSweepGateFacts gateFacts,
        IOperatorSweepActions actions,
        IConfiguration configuration,
        ILogger<OperatorSweepService> logger,
        TimeProvider? time = null)
    {
        _scanner = scanner;
        _authority = authority;
        _settings = settings;
        _timeline = timeline;
        _gateFacts = gateFacts;
        _actions = actions;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _startedAtUtc = _time.GetUtcNow().UtcDateTime;
    }

    public OperatorSweepOptions Options => OperatorSweepOptions.FromConfiguration(_configuration);

    private string? Workspace => _configuration["TaskRepository"];

    /// <summary>
    /// One tick over every card in Human Review and Escalated. Never throws for
    /// a single card: one unreadable card must not stop the others. A failure
    /// outside the card loop is recorded on every sweep's run state and
    /// returned, and the hosted loop simply runs the next tick.
    /// </summary>
    public async Task<OperatorSweepTickReport> RunOnceAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunTickAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task<OperatorSweepTickReport> RunTickAsync(CancellationToken ct)
    {
        var started = Now();
        foreach (var sweep in OperatorSweepKinds.All)
            _runs.AddOrUpdate(sweep, _ => new OperatorSweepRunState { LastStartedAtUtc = started },
                (_, state) => state with { LastStartedAtUtc = started });

        var acted = 0;
        var held = 0;
        var waiting = 0;
        var failed = 0;
        var cards = 0;
        var perSweep = OperatorSweepKinds.All.ToDictionary(kind => kind, _ => new SweepCounters(), StringComparer.Ordinal);
        try
        {
            var options = Options;
            var jobs = _scanner.ScanAllAutomationJobs()
                .Where(job => !job.Fixture && job.State is TaskStates.HumanReview or TaskStates.Escalated)
                .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(job => job.EnteredLaneAt)
                .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var workspace = Workspace;

            foreach (var projectJobs in jobs.GroupBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var project = projectJobs.Key;
                var settings = _settings.Get(project);
                var paused = (settings.OperatorSweepPauses ?? [])
                    .Select(item => item.Sweep)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var journal = ReadJournal(workspace, project);
                var cardStates = new List<OperatorSweepCardState>();
                var projectState = _projects.GetOrAdd(project, _ => new ProjectTickState());

                foreach (var job in projectJobs)
                {
                    ct.ThrowIfCancellationRequested();
                    cards++;
                    OperatorSweepCardState card;
                    try
                    {
                        card = await EvaluateCardAsync(
                            job, settings, paused, journal, workspace, options, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex,
                            "operator-sweep-card-failed project={Project} job={JobId}",
                            job.ProjectName, job.Id);
                        card = new OperatorSweepCardState(
                            job.Key ?? job.Id, job.Id, job.Title, job.State, 0, options.MaxRoundsPerCard,
                            [new OperatorSweepCardDecision(
                                "all", OperatorSweepAction.Hold.ToString(), OperatorSweepReasons.EvaluationFailed,
                                $"{ex.GetType().Name}: {ex.Message}", null)]);
                    }

                    foreach (var decision in card.Decisions)
                    {
                        if (!perSweep.TryGetValue(decision.Sweep, out var counters)) continue;
                        switch (decision.Action)
                        {
                            case nameof(OperatorSweepAction.Act):
                                counters.Acted++;
                                acted++;
                                projectState.RecordAction(decision.Sweep, new OperatorSweepRecentAction(
                                    Now(), card.TaskKey, decision.Reason, decision.Detail ?? string.Empty),
                                    RecentActionLimit);
                                break;
                            case nameof(OperatorSweepAction.WaitForPerson):
                                counters.Waiting++;
                                waiting++;
                                break;
                            default:
                                counters.Held++;
                                held++;
                                break;
                        }
                    }
                    cardStates.Add(card);
                }

                projectState.Cards = cardStates;
                projectState.CapturedAtUtc = Now();
            }

            // A project with no card left in a decision lane must not keep
            // showing the previous tick's cards.
            var seen = jobs.Select(job => job.ProjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (project, state) in _projects)
            {
                if (seen.Contains(project)) continue;
                state.Cards = [];
                state.CapturedAtUtc = Now();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var finishedWithError = Now();
            var error = $"{ex.GetType().Name}: {ex.Message}";
            foreach (var sweep in OperatorSweepKinds.All)
                _runs.AddOrUpdate(sweep,
                    _ => new OperatorSweepRunState { LastFinishedAtUtc = finishedWithError, LastError = error },
                    (_, state) => state with { LastFinishedAtUtc = finishedWithError, LastError = error });
            _logger.LogWarning(ex, "operator-sweep-tick-failed");
            return new OperatorSweepTickReport(started, finishedWithError, cards, acted, held, waiting, failed, error);
        }

        var finished = Now();
        foreach (var sweep in OperatorSweepKinds.All)
        {
            var counters = perSweep[sweep];
            _runs.AddOrUpdate(sweep,
                _ => new OperatorSweepRunState(),
                (_, state) => state with
                {
                    LastFinishedAtUtc = finished,
                    LastError = null,
                    LastActed = counters.Acted,
                    LastHeld = counters.Held,
                    LastWaitingForPerson = counters.Waiting,
                });
        }
        _logger.LogInformation(
            "operator-sweep-tick cards={Cards} acted={Acted} held={Held} waiting={Waiting} failed={Failed} durationMs={DurationMs}",
            cards, acted, held, waiting, failed, (long)(finished - started).TotalMilliseconds);
        return new OperatorSweepTickReport(started, finished, cards, acted, held, waiting, failed);
    }

    private async Task<OperatorSweepCardState> EvaluateCardAsync(
        TaskInfo job,
        ProjectSettings settings,
        IReadOnlySet<string> paused,
        IReadOnlyList<ReviewDecisionRecord> journal,
        string? workspace,
        OperatorSweepOptions options,
        CancellationToken ct)
    {
        var taskKey = job.Key ?? job.TaskKey;
        var timeline = _timeline.ReadAll(job.FolderPath);
        var projection = _authority.GetTaskProjection(taskKey);
        var budget = CardRoundBudget.Evaluate(journal, timeline, job.Id, options.MaxRoundsPerCard);
        var hasActiveReview = projection.ReviewAttempts.Any(IsActive)
                              || projection.CurrentReviewAttempt is { } current && IsActive(current);
        var hasActiveRun = projection.CurrentRunAttempt?.State
            is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased;

        var latestReview = LatestSettledReview(projection);
        var deliverySha = ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
        var salvage = OperatorSweepTriggers.ReadSalvage(timeline);
        var triggers = new Dictionary<string, OperatorSweepTrigger?>(StringComparer.Ordinal)
        {
            [OperatorSweepKinds.FixRounds] = OperatorSweepTriggers.FixRound(
                job.State,
                latestReview is null
                    ? null
                    : new OperatorSweepReviewFacts(latestReview.AttemptId, latestReview.Outcome,
                        latestReview.Subject.ExpectedResultSha),
                deliverySha,
                timeline),
            [OperatorSweepKinds.GateTriage] = job.State is TaskStates.HumanReview or TaskStates.Escalated
                ? OperatorSweepTriggers.GateTriage(job.State, _gateFacts.Read(job), timeline)
                : null,
            [OperatorSweepKinds.Salvage] = OperatorSweepTriggers.Salvage(job.State, salvage, timeline),
        };

        var decisions = new List<OperatorSweepCardDecision>();
        var actedThisTick = false;
        foreach (var sweep in OperatorSweepKinds.All)
        {
            var trigger = triggers[sweep];
            var facts = new OperatorSweepGuardFacts(
                paused.Contains(sweep),
                settings.AutomaticFailureContinuationsEnabled,
                hasActiveReview,
                hasActiveRun,
                budget);
            var decision = OperatorSweepPolicy.Decide(sweep, trigger, facts);
            if (decision.Action == OperatorSweepAction.Ignore) continue;

            if (decision.Action == OperatorSweepAction.Act && actedThisTick)
                decision = new OperatorSweepDecision(OperatorSweepAction.Hold, OperatorSweepReasons.AnotherSweepActed);
            if (decision.Action == OperatorSweepAction.Act && string.IsNullOrWhiteSpace(workspace))
                decision = new OperatorSweepDecision(OperatorSweepAction.Hold, OperatorSweepReasons.JournalUnavailable);

            string? detail = null;
            if (decision.Action == OperatorSweepAction.Act)
            {
                var result = await ActAsync(sweep, job, trigger!, latestReview, salvage, budget, ct).ConfigureAwait(false);
                detail = result.Detail;
                if (result.Started)
                {
                    actedThisTick = true;
                    WriteReceipts(workspace!, job, sweep, trigger!.SubjectKey, decision.Reason, budget, result.Detail);
                    budget = budget with { Used = budget.Used + 1 };
                }
                else
                {
                    decision = new OperatorSweepDecision(OperatorSweepAction.Hold, OperatorSweepReasons.ActionFailed);
                }
            }

            _logger.LogInformation(
                "operator-sweep-decision project={Project} job={JobId} sweep={Sweep} action={Action} reason={Reason} subject={Subject} roundsUsed={RoundsUsed} roundsAllowed={RoundsAllowed}",
                job.ProjectName, job.Id, sweep, decision.Action, decision.Reason, trigger?.SubjectKey,
                budget.Used, budget.Allowed);
            decisions.Add(new OperatorSweepCardDecision(
                sweep,
                decision.Action.ToString(),
                decision.Reason,
                detail ?? OperatorSweepReasons.Explain(decision.Reason),
                trigger?.SubjectKey));
        }

        return new OperatorSweepCardState(
            job.Key ?? job.Id, job.Id, job.Title, job.State, budget.Used, budget.Allowed, decisions);
    }

    private async Task<OperatorSweepActionResult> ActAsync(
        string sweep,
        TaskInfo job,
        OperatorSweepTrigger trigger,
        ReviewAttemptDto? latestReview,
        OperatorSweepSalvageFacts? salvage,
        CardRoundBudgetState budget,
        CancellationToken ct)
    {
        switch (sweep)
        {
            case OperatorSweepKinds.FixRounds:
            {
                var report = RemoteReviewSettlementJournal.Read(job.FolderPath, latestReview!.AttemptId).Entry?.Report;
                var followUp = OperatorSweepActions.BuildFixRoundFollowUp(
                    latestReview.AttemptId, report, latestReview, budget.Used + 1, budget.Allowed);
                return await _actions.OpenFixRoundAsync(
                    job, new OperatorSweepFixRound(latestReview.AttemptId, followUp), ct).ConfigureAwait(false);
            }
            case OperatorSweepKinds.GateTriage:
                return await _actions.ContinueGateFailureAsync(job, trigger.SubjectKey, ct).ConfigureAwait(false);
            case OperatorSweepKinds.Salvage:
                return await _actions.ContinueSalvageAsync(job, salvage!, ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(sweep), sweep, "Unknown operator sweep.");
        }
    }

    /// <summary>
    /// Receipts after the round started, never before: a refused action must
    /// stay retryable and must not charge the budget. A crash between the
    /// action and the receipt cannot double-act either, because the card has
    /// left the lane the trigger requires.
    /// </summary>
    private void WriteReceipts(
        string workspace,
        TaskInfo job,
        string sweep,
        string subjectKey,
        string reason,
        CardRoundBudgetState budget,
        string detail)
    {
        var current = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        var now = Now();
        var round = budget.Used + 1;
        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: now,
            JobId: job.Id,
            Project: job.ProjectName,
            Kind: ReviewDecisionKind.Reissue,
            Reason: $"Operator sweep {sweep}: {OperatorSweepReasons.Explain(reason)}",
            Prompt: $"(operator sweep {sweep})",
            Response: subjectKey,
            FollowUp: detail)
        {
            AttemptEpoch = OperatorReviewRequeueService.ReadEpoch(current.FolderPath),
            FailureKind = $"operator-sweep:{sweep}",
        });
        _timeline.Append(
            current.FolderPath,
            TimelineEventKinds.OperatorSweepRoundStarted,
            TimelineActors.System,
            $"Operator sweep {sweep} opened round {round} of {budget.Allowed}: {detail}",
            details: new Dictionary<string, string>
            {
                [OperatorSweepTriggers.ReceiptSweepKey] = sweep,
                [OperatorSweepTriggers.ReceiptSubjectKey] = subjectKey,
                ["reason"] = reason,
                ["roundsUsed"] = round.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["roundsAllowed"] = budget.Allowed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Pause or resume one sweep for one project. Persisted in project settings.</summary>
    public void SetPaused(string project, string sweep, bool paused, string actor, string? reason)
    {
        _settings.SetOperatorSweepPaused(project, sweep, paused, actor, reason, Now());
        _logger.LogInformation(
            "operator-sweep-{Action} project={Project} sweep={Sweep} actor={Actor} reason={Reason}",
            paused ? "paused" : "resumed", project, sweep, actor, reason);
    }

    /// <summary>
    /// The health projection: per sweep when it last ran, what it did, whether
    /// it is paused or overdue; per card its rounds left and why each sweep did
    /// or did not act; and the cards that wait for a person.
    /// </summary>
    public OperatorSweepProjection Project(string project)
    {
        var options = Options;
        var now = Now();
        var pauses = (_settings.Get(project).OperatorSweepPauses ?? [])
            .ToDictionary(item => item.Sweep, StringComparer.OrdinalIgnoreCase);
        _projects.TryGetValue(project, out var state);
        var cards = state?.Cards ?? [];
        var sweeps = OperatorSweepKinds.All.Select(sweep =>
        {
            _runs.TryGetValue(sweep, out var run);
            run ??= new OperatorSweepRunState();
            pauses.TryGetValue(sweep, out var pause);
            var overdue = options.Enabled && OperatorSweepHealthPolicy.IsOverdue(
                run.LastFinishedAtUtc, _startedAtUtc, options, now);
            return new OperatorSweepStatus(
                sweep,
                pause is not null,
                pause?.PausedAtUtc,
                pause?.PausedBy,
                pause?.Reason,
                run.LastStartedAtUtc,
                run.LastFinishedAtUtc,
                run.LastError,
                overdue,
                run.LastActed,
                run.LastHeld,
                run.LastWaitingForPerson,
                state?.RecentActions(sweep) ?? []);
        }).ToList();
        var waiting = cards
            .SelectMany(card => card.Decisions
                .Where(decision => decision.Action == nameof(OperatorSweepAction.WaitForPerson))
                .Select(decision => new OperatorSweepWaitingCard(
                    card.TaskKey, card.Title, card.Lane, decision.Sweep, decision.Reason, decision.Detail ?? string.Empty)))
            .ToList();
        var status = OperatorSweepHealthPolicy.Status(options.Enabled, sweeps);
        return new OperatorSweepProjection(
            project,
            now,
            status,
            options.Enabled,
            (int)options.TickInterval.TotalSeconds,
            options.MaxRoundsPerCard,
            state?.CapturedAtUtc,
            sweeps,
            cards,
            waiting);
    }

    private static bool IsActive(ReviewAttemptDto attempt)
        => attempt.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased;

    private static ReviewAttemptDto? LatestSettledReview(AttemptAuthorityProjection projection)
    {
        if (projection.CurrentReviewAttempt is { Outcome: not null } current) return current;
        return projection.ReviewAttempts
            .Where(attempt => attempt.Outcome is not null)
            .OrderBy(attempt => attempt.TerminalAt ?? attempt.CreatedAt)
            .LastOrDefault();
    }

    private IReadOnlyList<ReviewDecisionRecord> ReadJournal(string? workspace, string project)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return [];
        return ReviewDecisionLog.ReadAll(workspace, project);
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private sealed class SweepCounters
    {
        public int Acted;
        public int Held;
        public int Waiting;
    }

    private sealed record OperatorSweepRunState
    {
        public DateTime? LastStartedAtUtc { get; init; }
        public DateTime? LastFinishedAtUtc { get; init; }
        public string? LastError { get; init; }
        public int LastActed { get; init; }
        public int LastHeld { get; init; }
        public int LastWaitingForPerson { get; init; }
    }

    private sealed class ProjectTickState
    {
        private readonly ConcurrentDictionary<string, List<OperatorSweepRecentAction>> _recent =
            new(StringComparer.Ordinal);

        public IReadOnlyList<OperatorSweepCardState> Cards { get; set; } = [];
        public DateTime? CapturedAtUtc { get; set; }

        public void RecordAction(string sweep, OperatorSweepRecentAction action, int limit)
        {
            var list = _recent.GetOrAdd(sweep, _ => []);
            lock (list)
            {
                list.Insert(0, action);
                if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
            }
        }

        public IReadOnlyList<OperatorSweepRecentAction> RecentActions(string sweep)
        {
            if (!_recent.TryGetValue(sweep, out var list)) return [];
            lock (list) return list.ToList();
        }
    }
}
