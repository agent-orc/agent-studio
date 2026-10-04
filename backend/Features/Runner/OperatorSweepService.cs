using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Pipeline;

namespace AgentStudio.Runner;

public static class OperatorSweepNames
{
    public const string AutoFix = "auto-fix";
    public const string GateTriage = "gate-triage";
    public const string Salvage = "salvage";
    public static readonly IReadOnlyList<string> Ordered = [AutoFix, GateTriage, Salvage];
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AutoFix, GateTriage, Salvage,
    };
}

public static class OperatorSweepPolicy
{
    public static bool HasActiveReview(IEnumerable<ReviewAttemptDto> attempts)
        => attempts.Any(review => review.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased);
}

public sealed record OperatorSweepCardStatus(
    string TaskKey, string JobId, string Sweep, string Reason, string? AttemptId,
    DateTime AtUtc, int RoundsUsed, int RoundsLeft, bool WaitingForPerson);

public sealed record OperatorSweepStatus(
    string Sweep, bool Paused, DateTime? LastRunAtUtc, int LastActions,
    string? LastError);

public sealed record OperatorSweepHealth(
    IReadOnlyList<OperatorSweepStatus> Sweeps,
    IReadOnlyList<OperatorSweepCardStatus> Cards);

/// <summary>Task-folder decision evidence. An unchanged refusal is not appended every tick.</summary>
public static class OperatorSweepJournal
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string FileName = "operator-sweep-decisions.jsonl";

    public static IReadOnlyList<OperatorSweepCardStatus> Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return [];
        var rows = new List<OperatorSweepCardStatus>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<OperatorSweepCardStatus>(line, Json) is { } row)
                        rows.Add(row);
                }
                catch (JsonException ex)
                {
                    SilentCatch.Note(ex, "OperatorSweepJournal: partial decision line ignored");
                }
            }
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "OperatorSweepJournal: decision read unavailable");
        }
        return rows;
    }

    public static bool Record(string folder, OperatorSweepCardStatus status)
    {
        var path = Path.Combine(folder, FileName);
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            var last = Read(folder).LastOrDefault(row => row.Sweep == status.Sweep);
            if (last is not null && last.Reason == status.Reason
                && last.AttemptId == status.AttemptId
                && last.RoundsUsed == status.RoundsUsed
                && last.WaitingForPerson == status.WaitingForPerson)
                return false;
            File.AppendAllText(path, JsonSerializer.Serialize(status, Json) + Environment.NewLine);
            return true;
        }
    }
}

/// <summary>
/// One supervised tick for the three operator recovery sweeps. The gate retry
/// delegates to the existing typed policy; coding continuations use the same
/// TaskRunnerService as operator and orchestrator follow-ups. Every action is
/// checked against current ReviewAttempt authority and the shared decision budget.
/// </summary>
public sealed class OperatorSweepService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly ProjectSettingsService _settings;
    private readonly TaskRunnerService _runner;
    private readonly TaskIntegrationStatusService _integration;
    private readonly PipelineExecutionLog _pipeline;
    private readonly GateEnvironmentRetryService _gateRetries;
    private readonly RunTimeoutContinuationService _timeoutContinuations;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatorSweepService> _logger;
    private readonly ConcurrentDictionary<string, OperatorSweepStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public OperatorSweepService(
        TaskScannerService scanner, AttemptAuthorityService authority,
        ProjectSettingsService settings, TaskRunnerService runner,
        TaskIntegrationStatusService integration, PipelineExecutionLog pipeline,
        GateEnvironmentRetryService gateRetries, RunTimeoutContinuationService timeoutContinuations,
        TimelineLog timeline,
        IConfiguration configuration, ILogger<OperatorSweepService> logger)
    {
        _scanner = scanner;
        _authority = authority;
        _settings = settings;
        _runner = runner;
        _integration = integration;
        _pipeline = pipeline;
        _gateRetries = gateRetries;
        _timeoutContinuations = timeoutContinuations;
        _timeline = timeline;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var seconds = Math.Clamp(_configuration.GetValue("OperatorSweeps:IntervalSeconds", 60), 10, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunTickSafelyAsync(() => TickAsync(stoppingToken),
                ex => _logger.LogWarning(ex, "operator-sweeps-tick-failed")).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal static async Task RunTickSafelyAsync(Func<Task> run, Action<Exception> onError)
    {
        try { await run().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { onError(ex); }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sweep in OperatorSweepNames.Ordered)
            {
                var paused = IsPaused(entry.Name, sweep);
                if (paused)
                {
                    _status[Key(entry.Name, sweep)] = new OperatorSweepStatus(sweep, true,
                        _status.GetValueOrDefault(Key(entry.Name, sweep))?.LastRunAtUtc, 0, null);
                    continue;
                }
                try
                {
                    var actions = await RunSweepAsync(entry.Name, sweep, ct).ConfigureAwait(false);
                    _status[Key(entry.Name, sweep)] = new OperatorSweepStatus(
                        sweep, false, DateTime.UtcNow, actions, null);
                    _logger.LogInformation("operator-sweep-finished project={Project} sweep={Sweep} actions={Actions}",
                        entry.Name, sweep, actions);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _status[Key(entry.Name, sweep)] = new OperatorSweepStatus(
                        sweep, false, DateTime.UtcNow, 0, ex.Message);
                    _logger.LogWarning(ex, "operator-sweep-failed project={Project} sweep={Sweep}",
                        entry.Name, sweep);
                }
            }
        }
    }

    public OperatorSweepHealth Snapshot(string project)
    {
        var maximum = MaximumRounds();
        var workspace = _configuration["TaskRepository"];
        var decisions = string.IsNullOrWhiteSpace(workspace)
            ? [] : ReviewDecisionLog.ReadAll(workspace, project);
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => card.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .SelectMany(card => OperatorSweepJournal.Read(card.FolderPath)
                .GroupBy(row => row.Sweep, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Select(row => row with
                {
                    RoundsUsed = ReviewDecisionOrchestrator.CountReissuesInCurrentChain(decisions, card.Id),
                    RoundsLeft = Math.Max(0, maximum - ReviewDecisionOrchestrator.CountReissuesInCurrentChain(decisions, card.Id)),
                }))
            .OrderByDescending(row => row.AtUtc)
            .ToArray();
        var sweeps = OperatorSweepNames.Ordered.Select(sweep =>
        {
            var previous = _status.GetValueOrDefault(Key(project, sweep));
            return new OperatorSweepStatus(sweep, IsPaused(project, sweep),
                previous?.LastRunAtUtc, previous?.LastActions ?? 0, previous?.LastError);
        }).ToArray();
        return new OperatorSweepHealth(sweeps, cards);
    }

    private async Task<int> RunSweepAsync(string project, string sweep, CancellationToken ct)
    {
        var cards = _scanner.ScanAllAutomationJobs()
            .Where(card => !card.Fixture && card.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .OrderBy(card => card.EnteredLaneAt).ToArray();
        var actions = 0;
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (IsPaused(project, sweep)) return actions;
            try
            {
                if (await RunCardAsync(card, sweep, ct).ConfigureAwait(false)) actions++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Note(card, sweep, "card-error: " + ex.Message, null, true);
                _logger.LogWarning(ex, "operator-sweep-card-failed project={Project} sweep={Sweep} job={JobId}",
                    project, sweep, card.Id);
            }
        }
        if (sweep == OperatorSweepNames.GateTriage && !IsPaused(project, sweep))
        {
            var retry = await _gateRetries.RunOnceAsync(ct, project,
                (card, reason, waiting) => Note(
                    _scanner.FindJob(card.Id, card.WatchPath) ?? card,
                    sweep, reason, null, waiting)).ConfigureAwait(false);
            actions += retry.Retried;
        }
        return actions;
    }

    private async Task<bool> RunCardAsync(TaskInfo card, string sweep, CancellationToken ct)
    {
        if (sweep == OperatorSweepNames.AutoFix && card.State != TaskStates.HumanReview
            || sweep == OperatorSweepNames.GateTriage && card.State != TaskStates.HumanReview
            || sweep == OperatorSweepNames.Salvage && card.State != TaskStates.Escalated)
            return false;

        var authority = _authority.GetTaskProjection(card.TaskKey);
        if (OperatorSweepPolicy.HasActiveReview(authority.ReviewAttempts))
        {
            Note(card, sweep, "active-review-attempt", authority.CurrentReviewAttempt?.AttemptId, true);
            return false;
        }

        if (sweep == OperatorSweepNames.AutoFix)
        {
            var review = authority.CurrentReviewAttempt;
            if (review?.Outcome != ReviewTerminalOutcome.ProductFailure)
            {
                Note(card, sweep, review?.Outcome == ReviewTerminalOutcome.Pass
                    ? "human-acceptance" : "no-current-product-failure", review?.AttemptId, true);
                return false;
            }
            if (AlreadyActed(card, sweep, review.AttemptId)) return false;
            var report = RemoteReviewSettlementJournal.Read(card.FolderPath, review.AttemptId).Entry?.Report;
            if (report is null)
            {
                Note(card, sweep, "review-report-unavailable", review.AttemptId, true);
                return false;
            }
            return await QueueFixAsync(card, sweep, review.AttemptId,
                BuildProductFixPrompt(card, review, report), ct).ConfigureAwait(false);
        }

        if (sweep == OperatorSweepNames.GateTriage)
        {
            var pipelineSteps = _pipeline.Read(card.FolderPath)?.Steps;
            if (pipelineSteps is null || !pipelineSteps.Any(step =>
                    step.Status == PipelineStepStatus.Failed || step.Verdict is "concerns" or "block"))
            {
                Note(card, sweep, "no-failed-gate-step", null, true);
                return false;
            }
            var status = _integration.BuildLookup([card]).GetValueOrDefault(card.TaskKey);
            if (status?.Failure?.Code is AcceptedIntegrationFailureCodes.GateEnvironmentFailure
                or AcceptedIntegrationFailureCodes.GateInterrupted)
            {
                Note(card, sweep, "gate-environment-retry-policy", authority.CurrentReviewAttempt?.AttemptId, false);
                return false; // The typed retry service decides the due rung below.
            }
            var failed = TaskFailureContinuationEndpoints.CurrentFailureStep(
                pipelineSteps, status, ReviewSubjectStore.Read(card.FolderPath));
            if (failed?.FailureCode is AcceptedIntegrationFailureCodes.GateEnvironmentFailure
                or AcceptedIntegrationFailureCodes.GateInterrupted)
            {
                Note(card, sweep, "gate-environment-retry-policy", authority.CurrentReviewAttempt?.AttemptId, false);
                return false;
            }
            if (failed is null || status?.Status == IntegrationStatuses.Integrated)
            {
                Note(card, sweep, "no-current-product-gate-failure", null, true);
                return false;
            }
            var id = failed.StepId + ":" + (failed.CompletedAt?.ToString("O") ?? "unknown");
            if (AlreadyActed(card, sweep, id)) return false;
            var prompt = IntegrationContinuationPrompt.Build(card.Key ?? card.Id,
                ReviewSubjectStore.Read(card.FolderPath)?.ResultRef ?? status?.DeliveryRef,
                ReviewSubjectStore.Read(card.FolderPath)?.ResultSha,
                status?.IntegrationBranch ?? "develop", failed.StepId,
                failed.Reason ?? status?.Detail ?? "The gate failed.",
                failed.ConflictReport, failed.VerdictSummary, evidenceRef: failed.EvidenceRef);
            return await QueueFixAsync(card, sweep, id, prompt, ct).ConfigureAwait(false);
        }

        var finished = _timeline.ReadAll(card.FolderPath)
            .LastOrDefault(row => row.Kind == TimelineEventKinds.AgentRunFinished);
        if (finished?.Details?.GetValueOrDefault("status") != "unknown")
        {
            Note(card, sweep, "latest-run-not-timeout", finished?.RunId, true);
            return false;
        }
        var salvage = RunSalvageReference.From(
            finished?.Details?.GetValueOrDefault("salvageBranch"),
            finished?.Details?.GetValueOrDefault("salvageCommitSha"),
            finished?.Details?.GetValueOrDefault("salvageRecoveryBranch"),
            finished?.Details?.GetValueOrDefault("salvageRecoveryCommitSha"));
        if (salvage is null)
        {
            Note(card, sweep, "no-salvage-reference", null, true);
            return false;
        }
        var attemptId = finished!.Details!.GetValueOrDefault("runAttemptId") ?? finished.RunId ?? "unknown";
        if (AlreadyActed(card, sweep, attemptId)) return false;
        if (_timeoutContinuations.CountAutomaticRounds(card) > 0
            || OperatorSweepJournal.Read(card.FolderPath).Any(row =>
                row.Sweep == OperatorSweepNames.Salvage && row.Reason == "queued"))
        {
            Note(card, sweep, "salvage-round-budget-exhausted", attemptId, true);
            return false;
        }
        if (!HasBudget(card, sweep, attemptId)) return false;
        var escalated = _timeline.ReadAll(card.FolderPath).LastOrDefault(row =>
            row.Kind == TimelineEventKinds.OrchestratorEscalated);
        if (escalated?.Details?.GetValueOrDefault("category") == HumanReviewEscalationCategories.UnverifiedDelivery)
        {
            Note(card, sweep, "unverified-delivery", attemptId, true);
            return false;
        }
        if (!long.TryParse(finished.Details.GetValueOrDefault("fence"), out var fence)
            || !long.TryParse(finished.Details.GetValueOrDefault("authorityEpoch"), out var epoch)
            || !ReviewSubjectStore.IsValidResultSha(salvage.CommitSha))
        {
            Note(card, sweep, "salvage-authority-evidence-incomplete", attemptId, true);
            return false;
        }
        var latest = _scanner.FindJob(card.Id, card.WatchPath);
        if (latest?.State != TaskStates.Escalated
            || OperatorSweepPolicy.HasActiveReview(_authority.GetTaskProjection(card.TaskKey).ReviewAttempts))
        {
            Note(card, sweep, "lane-or-review-authority-changed", attemptId, true);
            return false;
        }
        var continuation = await _timeoutContinuations.StartAsync(card, salvage,
            "The prior run timed out.", attemptId,
            new AttemptWriteReference(attemptId, fence, epoch,
                "operator-sweep-salvage:" + attemptId), ct,
            RunContinuationCause.Timeout("The prior run timed out.") with
            { StartsFromSalvage = true }).ConfigureAwait(false);
        if (!continuation.Started)
        {
            Note(card, sweep, "salvage-continuation-refused: " + continuation.Reason, attemptId, true);
            return false;
        }
        Charge(card, sweep, attemptId, continuation.Reason);
        Note(_scanner.FindJob(card.Id, card.WatchPath) ?? card, sweep, "queued", attemptId, false);
        return true;
    }

    private Task<bool> QueueFixAsync(TaskInfo card, string sweep, string id, string prompt, CancellationToken ct)
        => !HasBudget(card, sweep, id)
            ? Task.FromResult(false)
            : ContinueAndChargeAsync(card, sweep, id, prompt, ct);

    private async Task<bool> ContinueAndChargeAsync(TaskInfo card, string sweep, string id, string prompt, CancellationToken ct)
    {
        // Re-check immediately before the continuation. Human Review and Escalated
        // cannot admit a new review claim, but legacy leases may still be live.
        if (OperatorSweepPolicy.HasActiveReview(_authority.GetTaskProjection(card.TaskKey).ReviewAttempts))
        {
            Note(card, sweep, "active-review-attempt", id, true);
            return false;
        }
        var current = _scanner.FindJob(card.Id, card.WatchPath);
        if (current is null || current.State != card.State)
        {
            Note(card, sweep, "lane-changed-before-continuation", id, true);
            return false;
        }
        await _runner.ContinueJobAsync(card.Id, prompt, card.WatchPath,
            mode: ContinueModes.Extend, ct: ct,
            reason: "operator-sweep:" + sweep, triggeredBy: "operator-sweep").ConfigureAwait(false);
        Charge(card, sweep, id, prompt);
        Note(_scanner.FindJob(card.Id, card.WatchPath) ?? card, sweep, "queued", id, false);
        return true;
    }

    private void Charge(TaskInfo card, string sweep, string id, string prompt)
    {
        var workspace = _configuration["TaskRepository"]!;
        var current = _scanner.FindJob(card.Id, card.WatchPath) ?? card;
        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            DateTime.UtcNow, card.Id, card.ProjectName, ReviewDecisionKind.Reissue,
            "operator-sweep:" + sweep + ":" + id, prompt, string.Empty, prompt)
        { AttemptEpoch = OperatorReviewRequeueService.ReadEpoch(current.FolderPath) });
    }

    private bool HasBudget(TaskInfo card, string sweep, string id)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            Note(card, sweep, "decision-journal-unavailable", id, true);
            return false;
        }
        if (ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
                ReviewDecisionLog.ReadAll(workspace, card.ProjectName), card.Id) < MaximumRounds()) return true;
        Note(card, sweep, "round-budget-exhausted", id, true);
        return false;
    }

    private bool AlreadyActed(TaskInfo card, string sweep, string id)
        => OperatorSweepJournal.Read(card.FolderPath).Any(row =>
            row.Sweep == sweep && row.AttemptId == id && row.Reason == "queued");

    private void Note(TaskInfo card, string sweep, string reason, string? attemptId, bool waiting)
    {
        var workspace = _configuration["TaskRepository"];
        var used = string.IsNullOrWhiteSpace(workspace) ? 0 :
            ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
                ReviewDecisionLog.ReadAll(workspace, card.ProjectName), card.Id);
        var appended = OperatorSweepJournal.Record(card.FolderPath, new OperatorSweepCardStatus(
            card.TaskKey, card.Id, sweep, reason, attemptId, DateTime.UtcNow,
            used, Math.Max(0, MaximumRounds() - used), waiting));
        if (appended)
            _logger.LogInformation("operator-sweep-card project={Project} sweep={Sweep} job={JobId} reason={Reason} roundsUsed={RoundsUsed} roundsLeft={RoundsLeft}",
                card.ProjectName, sweep, card.Id, reason, used, Math.Max(0, MaximumRounds() - used));
    }

    internal static string BuildProductFixPrompt(
        TaskInfo card, ReviewAttemptDto review,
        AgentStudio.TaskServer.Contracts.ReviewReportRequest report)
    {
        var findings = report.Verdicts
            .Where(verdict => !string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .Select(verdict => $"- {verdict.Aspect} ({verdict.Status}): {verdict.Summary}; missing: {verdict.Missing ?? "not recorded"}");
        var commands = report.Commands
            .Where(command => command.ExitCode != 0 || command.NewFailures?.Count > 0)
            .Take(12)
            .Select(command => $"- {command.FileName} {string.Join(" ", command.Arguments)} (step {command.StepId}, exit {command.ExitCode?.ToString() ?? command.Signal ?? "unknown"})");
        return "## REVIEW FIX ROUND\n\n"
            + $"Continue task {card.Key ?? card.Id} after ProductFailure review {review.AttemptId}.\n"
            + $"Review summary: {report.Summary ?? review.TerminalReason ?? "Product failure."}\n\n"
            + "Findings:\n" + string.Join("\n", findings) + "\n\n"
            + "Failed test commands:\n" + string.Join("\n", commands) + "\n\n"
            + "Fix the concrete findings, rerun the relevant commands, and cite the result evidence. "
            + "Preserve the task's model and CLI pins. Finish with the normal task terminal sentinel.";
    }

    private int MaximumRounds() => Math.Max(0, _configuration.GetValue(
        "ReviewDecisionOrchestrator:MaxAutoReissueAttempts", ReviewDecisionOrchestrator.MaxAutoReissueAttempts));
    private bool IsPaused(string project, string sweep)
        => _settings.Get(project).OperatorSweepPauses?.GetValueOrDefault(sweep) == true;
    private static string Key(string project, string sweep) => project + "\0" + sweep;
}
