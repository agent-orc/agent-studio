namespace AgentStudio.Review;

/// <summary>
/// Assembles the <see cref="ReviewProjection"/> for one card: read the canonical
/// records (backfilling legacy report Markdown once for cards that predate
/// them), observe the delivery and decision facts the records do not own, then
/// hand everything to <see cref="ReviewRoundProjectionPolicy"/>.
///
/// <para>
/// The flow is deliberately ordered boundary, coordination, pure decision,
/// bounded side effects: every branch that decides what the operator reads lives
/// in the policy, and this class only gathers inputs.
/// </para>
/// </summary>
public sealed class ReviewProjectionService
{
    private readonly TimelineLog _timeline;
    private readonly ILogger<ReviewProjectionService> _logger;

    public ReviewProjectionService(TimelineLog timeline, ILogger<ReviewProjectionService> logger)
    {
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// The card's review head. Always returns a projection; a card with no review
    /// round returns one whose <see cref="ReviewProjection.RoundCount"/> is zero
    /// and whose reasons say why nothing is recorded.
    /// </summary>
    public ReviewProjection Build(TaskInfo task)
    {
        var rounds = ReadRounds(task.FolderPath);
        var timeline = ReadTimeline(task);
        return ReviewRoundProjectionPolicy.Build(new ReviewProjectionInputs(
            task.Id,
            rounds,
            ObserveDelivery(task, timeline),
            ObserveDecision(task, timeline)));
    }

    /// <summary>
    /// Canonical records first; every legacy round the records do not already
    /// cover is backfilled from its report Markdown. Keyed by plane and attempt
    /// so a backfilled round can never double-count one the record already owns.
    /// </summary>
    public IReadOnlyList<ReviewRoundRecord> ReadRounds(string? jobFolder)
    {
        var records = ReviewRoundRecordStore.ReadAll(jobFolder, _logger);
        var known = records
            .Select(record => Key(record))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backfilled = ReviewRoundMarkdownBackfill.Read(jobFolder, _logger)
            .Where(round => !known.Contains(Key(round)));
        return ReviewRoundRecordStore.Order(records.Concat(backfilled));
    }

    private static string Key(ReviewRoundRecord record) =>
        $"{ReviewPlanes.Normalize(record.Plane)}:{record.AttemptId}";

    private IReadOnlyList<TimelineEvent> ReadTimeline(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath)) return [];
        try { return _timeline.ReadAll(task.FolderPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read timeline for review projection of {Job}", task.Id);
            return [];
        }
    }

    /// <summary>
    /// Proven membership wins over any recorded attempt: an out-of-band merge
    /// makes the card integrated even though no gate ever ran. Otherwise the
    /// newest <c>integration_failed</c> row carries the reason.
    /// </summary>
    private static ReviewDeliveryObservation ObserveDelivery(
        TaskInfo task,
        IReadOnlyList<TimelineEvent> timeline)
    {
        var integrated = string.Equals(
            task.Integration?.Status,
            IntegrationStatuses.Integrated,
            StringComparison.Ordinal);
        var failure = timeline
            .LastOrDefault(evt => evt.Kind == TimelineEventKinds.IntegrationFailed);
        return new ReviewDeliveryObservation(
            integrated,
            IntegratedDetail: task.Integration?.Detail,
            FailureReason: integrated ? null : DetailOf(failure),
            IntegrationBranch: task.Integration?.IntegrationBranch);
    }

    /// <summary>
    /// Who is waiting on a person, in the order the evidence is trustworthy: the
    /// durable park marker, then the escalation event, then the review lane
    /// change that put the card in front of a human. Null when the card is not in
    /// a human-decision lane, so a passing card never claims a pending decision.
    /// </summary>
    private static ReviewDecisionRequirement? ObserveDecision(
        TaskInfo task,
        IReadOnlyList<TimelineEvent> timeline)
    {
        if (task.ParkedBlocker is { } parked)
        {
            return new ReviewDecisionRequirement
            {
                Source = ReviewDecisionSources.ParkedBlocker,
                Reason = FirstNonEmpty(parked.Reason, parked.ConditionDescription, parked.Detail),
            };
        }

        var awaitsHuman = string.Equals(task.State, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(task.State, TaskStates.Escalated, StringComparison.OrdinalIgnoreCase);
        if (!awaitsHuman) return null;

        if (timeline.LastOrDefault(evt => evt.Kind == TimelineEventKinds.OrchestratorEscalated) is { } escalated)
        {
            return new ReviewDecisionRequirement
            {
                Source = ReviewDecisionSources.EscalationEvent,
                Reason = DetailOf(escalated) ?? escalated.Summary,
            };
        }

        var laneChange = timeline.LastOrDefault(evt =>
            evt.Kind == TimelineEventKinds.LaneChanged
            && evt.Actor.StartsWith("remote-review:", StringComparison.OrdinalIgnoreCase));
        return new ReviewDecisionRequirement
        {
            Source = ReviewDecisionSources.ReviewLaneChange,
            Reason = laneChange is null
                ? "The card is parked in a human-decision lane."
                : DetailOf(laneChange) ?? laneChange.Summary,
        };
    }

    private static string? DetailOf(TimelineEvent? evt)
    {
        if (evt is null) return null;
        var detail = FirstNonEmpty(
            evt.Details?.GetValueOrDefault("reason"),
            evt.Details?.GetValueOrDefault("detail"),
            evt.Summary);
        return string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
