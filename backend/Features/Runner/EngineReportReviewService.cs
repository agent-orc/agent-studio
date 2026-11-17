using System.Collections.Concurrent;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Whether a report review started, and the task that settles it.</summary>
public sealed record EngineReportReviewStart(bool Started, string Reason,
    Task<ReportReviewDecision>? Completion = null);

/// <summary>
/// AGT-3797: the owner of a settled planning or research delivery in Auto
/// Review while the engine owns Auto Review. Such a card gets no remote
/// ReviewAttempt, and the monolith's lightweight report pipeline is off, so
/// this service runs the requirement-fit review of the report
/// (<see cref="ReportContentReviewService"/>) and applies the
/// <see cref="ReportReviewPolicy"/> decision: Human Review with the verdict,
/// Auto Review while the review waits for a model, or Escalated with the
/// reason when no verdict could be produced. The engine applier starts it for
/// its <c>CreateReview</c> decision, and the stranding watchdog for a card
/// that lost its owner.
/// </summary>
public sealed class EngineReportReviewService
{
    public const string CheckName = "report-content-review";
    public const string ParkDetail = "report-review-failed";

    private readonly TaskScannerService _scanner;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly ReportContentReviewService _reports;
    private readonly HumanReviewEscalation _escalation;
    private readonly IConfiguration _configuration;
    private readonly TimelineLog? _timeline;
    private readonly ILogger<EngineReportReviewService> _logger;
    private readonly ConcurrentDictionary<string, Task<ReportReviewDecision>> _running =
        new(StringComparer.OrdinalIgnoreCase);

    public EngineReportReviewService(TaskScannerService scanner, TaskTransitionService transitions,
        TaskMutationService mutations, ReportContentReviewService reports, HumanReviewEscalation escalation,
        IConfiguration configuration, ILogger<EngineReportReviewService> logger, TimelineLog? timeline = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _mutations = mutations;
        _reports = reports;
        _escalation = escalation;
        _configuration = configuration;
        _logger = logger;
        _timeline = timeline;
    }

    /// <summary>True when this service, not the monolith pipeline, reviews the card's report.</summary>
    public bool Owns(TaskInfo card)
        => card.State == TaskStates.AutoReview && !card.Fixture
            && MonolithTransitionAdapter.ReportReviewHostRuns(TaskModes.IsReportOnly(card.Mode),
                MonolithTransitionSwitch.OwnsAutoReview(_configuration));

    /// <summary>True while a report review of this card runs; the card has an owner.</summary>
    public bool IsRunning(TaskInfo card) => _running.ContainsKey(Key(card));

    /// <summary>The report review still lacks a verdict for the current delivery and brief.</summary>
    public bool IsMissing(TaskInfo card) => _reports.IsMissing(card);

    /// <summary>
    /// Starts the report review of <paramref name="card"/> in the background.
    /// Refuses, and starts nothing, for a card this service does not own or
    /// whose review already runs.
    /// </summary>
    public EngineReportReviewStart Start(TaskInfo card, string source)
    {
        if (!Owns(card))
            return new(false, TaskModes.IsReportOnly(card.Mode)
                ? $"The report review runs in Auto Review while the engine owns it; the card is in {card.State}."
                : "Only planning and research cards have a report review.");
        var key = Key(card);
        var gate = new TaskCompletionSource<ReportReviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(key, gate.Task))
            return new(false, "A report review of this card is already running.");
        try
        {
            var now = DateTime.UtcNow;
            PostProcessingLifecycleStore.BeginPostProcessing(card.FolderPath, now, CheckName,
                "The requirement-fit review of the delivered report is running.", _logger, replaceChecks: true);
            _mutations.SetJobPhase(card.FolderPath, LifecyclePhases.PostProcessingRunning);
            _timeline?.Append(card.FolderPath, TimelineEventKinds.PostStepStarted, TimelineActors.System,
                $"Report review started ({source}): the requirement-fit review of the delivered report.",
                details: new Dictionary<string, string> { ["source"] = source });
            _scanner.InvalidateCache();
            _scanner.PublishCoreFromKnownFolder(card.FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _running.TryRemove(key, out _);
            return new(false, "The report review could not record its start: " + ex.Message);
        }
        _ = Task.Run(async () =>
        {
            ReportReviewDecision? decision = null;
            Exception? failure = null;
            try { decision = await RunAsync(card, source); }
            catch (Exception ex) { failure = ex; }
            // The card loses this owner before the outcome is observable.
            _running.TryRemove(key, out _);
            if (failure is null) gate.TrySetResult(decision!);
            else gate.TrySetException(failure);
        });
        _logger.LogInformation("engine-report-review-started project={Project} task={TaskId} source={Source}",
            card.ProjectName, card.Id, source);
        return new(true, "Started the report review of the delivered report.", gate.Task);
    }

    private async Task<ReportReviewDecision> RunAsync(TaskInfo card, string source)
    {
        ReportReviewDecision decision;
        try
        {
            // A verdict the current delivery already has is applied, not bought again.
            var result = _reports.CurrentVerdict(card)
                ?? await _reports.RunAsync(card, card.ProjectName, CancellationToken.None);
            decision = MonolithTransitionAdapter.ReportReviewSettle(new ReportReviewOutcomeFacts(
                result.Ran, result.Status, result.QuotaDeferred || result.HostWaiting,
                ReportDeliverable.MissingRequiredReport(card) is not null, result.Reason));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "engine-report-review-failed project={Project} task={TaskId}",
                card.ProjectName, card.Id);
            decision = MonolithTransitionAdapter.ReportReviewSettle(new ReportReviewOutcomeFacts(
                false, null, false, false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        await ApplyAsync(card, decision, source);
        return decision;
    }

    private async Task ApplyAsync(TaskInfo card, ReportReviewDecision decision, string source)
    {
        var current = _scanner.FindJob(card.Id, card.WatchPath);
        if (current is null || current.State != TaskStates.AutoReview)
        {
            _logger.LogInformation(
                "engine-report-review-card-moved project={Project} task={TaskId} state={State}",
                card.ProjectName, card.Id, current?.State ?? "missing");
            return;
        }
        _timeline?.Append(current.FolderPath, TimelineEventKinds.PostStepFinished, TimelineActors.System,
            decision.Reason, details: new Dictionary<string, string>
            {
                ["source"] = source,
                ["action"] = decision.Action.ToString(),
            });
        switch (decision.Action)
        {
            case ReportReviewAction.Wait:
                // The pending requirement-fit step names the capability it waits
                // for; the stranding watchdog restarts it when a host returns.
                PostProcessingLifecycleStore.Terminalize(current.FolderPath, DateTime.UtcNow, failed: false,
                    decision.Reason, _logger, onlyWhenActive: true);
                _mutations.SetJobPhase(current.FolderPath, LifecyclePhases.AwaitingReview);
                break;
            case ReportReviewAction.HumanReview:
                var moved = await _transitions.MoveAsync(current.Id, TaskStates.HumanReview, current.WatchPath,
                    CancellationToken.None, cause: TimelineActors.System, reason: decision.Reason,
                    expectedSourceState: TaskStates.AutoReview,
                    transitionCause: LaneChangeCauses.ReviewVerdict, transitionDetail: "report-review");
                if (moved.Status != MoveJobStatus.Success)
                {
                    _logger.LogWarning(
                        "engine-report-review-move-failed project={Project} task={TaskId} status={Status} message={Message}",
                        current.ProjectName, current.Id, moved.Status, moved.Message);
                    PostProcessingLifecycleStore.Terminalize(current.FolderPath, DateTime.UtcNow, failed: true,
                        $"The report has its verdict, but the move to Human Review failed: {moved.Status} {moved.Message}",
                        _logger, onlyWhenActive: true);
                    _mutations.SetJobPhase(current.FolderPath, LifecyclePhases.PostProcessingBlocked);
                }
                break;
            case ReportReviewAction.Escalate:
                await _escalation.EscalateAsync(current.Id, current.WatchPath, current.ProjectName,
                    HumanReviewEscalationCategories.AutoReviewEscalation,
                    HumanReviewEscalation.FormatReason(HumanReviewEscalationCategories.AutoReviewEscalation,
                        decision.Reason),
                    CancellationToken.None, expectedSourceState: TaskStates.AutoReview, parkDetail: ParkDetail);
                break;
        }
        _scanner.InvalidateCache();
        _scanner.PublishCoreFromKnownFolder((_scanner.FindJob(card.Id, card.WatchPath) ?? current).FolderPath);
    }

    private static string Key(TaskInfo card) => card.ProjectName + "\u001f" + card.Id;
}
