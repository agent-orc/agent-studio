using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// AGT-3281: an engine decision whose card has left the lane it was decided
/// for. The occurrence is settled as superseded and never retried. The message
/// keeps the wire error code of the source-state check.
/// </summary>
public sealed class OrchestrationOccurrenceSupersededException(string cause, string? observedState,
    string message = "orchestration-source-state-mismatch") : InvalidOperationException(message)
{
    public string Cause { get; } = cause;

    /// <summary>The card's lane when the mismatch was read; null when the card no longer exists.</summary>
    public string? ObservedState { get; } = observedState;
}

public static class EngineDecisionSupersessionCauses
{
    /// <summary>Prefix of <see cref="MonolithOrchestrationOccurrence.CancellationReason"/> for a superseded occurrence.</summary>
    public const string Prefix = "superseded:";
    public const string SourceStateMismatch = "source-state-mismatch";
    public const string OperatorMove = "operator-move";
    public const string CardAbsent = "card-absent";
}

public enum EngineDecisionStanding
{
    /// <summary>Completed or cancelled; the occurrence owes nothing.</summary>
    Settled,
    /// <summary>The occurrence can still apply to its card.</summary>
    Live,
    /// <summary>The card left the lane the occurrence expects; it can never apply.</summary>
    Superseded,
}

public sealed record EngineDecisionStandingFacts(
    OrchestrationStage Stage,
    string Status,
    bool Shadow,
    string ExpectedSourceState,
    string CardState,
    TimeSpan SinceUpdate);

/// <summary>
/// AGT-3281: whether an engine occurrence can still apply to its card. Only a
/// lane-bound stage (a review, gate, or bounded review-input decision) depends
/// on the card's lane; a sweep, batch plan, or AI Operator decision checks its
/// own facts when it applies. An applying occurrence already holds a decision,
/// and only its application can tell an owed lane move from one it already
/// made, so it stays live here and its applier settles it.
/// </summary>
public static class EngineDecisionStandingPolicy
{
    /// <summary>A card can still be moving into the lane an occurrence was just created for.</summary>
    public static readonly TimeSpan PreparationGrace = TimeSpan.FromSeconds(30);

    public static bool IsOpen(string status) => status is "pending" or "leased" or "applying";

    public static bool IsLaneBound(OrchestrationStage stage)
        => stage is OrchestrationStage.ReviewDecision or OrchestrationStage.GateDispatch
            or OrchestrationStage.ReviewInputDecision;

    /// <summary>An operator lane change supersedes every open, lane-bound, non-shadow decision of the card.</summary>
    public static bool HeldByOperatorMove(OrchestrationStage stage, string status, bool shadow)
        => !shadow && IsLaneBound(stage) && IsOpen(status);

    public static EngineDecisionStanding Decide(EngineDecisionStandingFacts facts)
    {
        if (!IsOpen(facts.Status)) return EngineDecisionStanding.Settled;
        if (facts.Shadow || !IsLaneBound(facts.Stage) || facts.Status == "applying"
            || facts.CardState == facts.ExpectedSourceState
            || facts.SinceUpdate < PreparationGrace)
            return EngineDecisionStanding.Live;
        return EngineDecisionStanding.Superseded;
    }
}

/// <summary>The card side of a superseded occurrence: its wait marker goes and its timeline says why.</summary>
public static class EngineDecisionSupersession
{
    public static void RecordOnCard(TimelineLog timeline, string folder,
        MonolithOrchestrationOccurrence occurrence, string cause, string? observedState, string actor)
    {
        EngineDecisionWaitMarker.Clear(folder, occurrence.Run.RunId);
        if (timeline.ReadAll(folder).Any(entry => entry.Kind == TimelineEventKinds.EngineDecisionSuperseded
                && entry.Details?.GetValueOrDefault("orchestrationRunId") == occurrence.Run.RunId))
            return;
        var subject = occurrence.Profile ?? occurrence.Run.CurrentStage.ToString();
        timeline.Append(folder, TimelineEventKinds.EngineDecisionSuperseded, actor,
            $"Engine decision {occurrence.Run.RunId} ({subject}) was superseded: it expected "
            + $"{occurrence.ExpectedSourceState}, and the card is in {observedState ?? "no lane"}. It will not apply.",
            occurrence.AuthorityAttemptId,
            details: new Dictionary<string, string>
            {
                ["orchestrationRunId"] = occurrence.Run.RunId,
                ["cause"] = cause,
                ["stage"] = occurrence.Run.CurrentStage.ToString(),
                ["profile"] = occurrence.Profile ?? string.Empty,
                ["expectedSourceState"] = occurrence.ExpectedSourceState,
                ["observedState"] = observedState ?? string.Empty,
            });
    }
}

/// <summary>
/// AGT-3281: an operator move or a queued <c>/continue</c> supersedes the
/// card's open lane decisions at the time of the move. The hold keeps their
/// application out while the card moves, so no decision applies to the new
/// lane. A decision whose application is already running is not held: it
/// either applies before the move, or its applier meets the moved card and
/// supersedes it.
/// </summary>
public sealed class OperatorMoveDecisionSupersession(
    MonolithOrchestrationStore store,
    TimelineLog timeline,
    TaskScannerService scanner,
    ILogger<OperatorMoveDecisionSupersession> logger)
{
    public MonolithOrchestrationStore.LaneDecisionHold Hold(TaskInfo card)
        => store.HoldOpenLaneDecisions(card.ProjectName, [card.Id, card.TaskKey, card.Key]);

    /// <summary>Supersedes the held decisions when the card left <paramref name="before"/>'s lane.</summary>
    public void Settle(MonolithOrchestrationStore.LaneDecisionHold hold, TaskInfo before, string actor)
    {
        if (hold.RunIds.Count == 0) return;
        scanner.InvalidateCache();
        var moved = scanner.FindJob(before.Id, before.WatchPath);
        if (moved is null || moved.State == before.State) return;
        foreach (var occurrence in hold.Supersede(EngineDecisionSupersessionCauses.OperatorMove, moved.State))
        {
            logger.LogInformation(
                "orchestration-occurrence-superseded run={RunId} task={TaskKey} profile={Profile} cause={Cause} expected={Expected} observed={Observed}",
                occurrence.Run.RunId, moved.Key ?? moved.Id, occurrence.Profile,
                EngineDecisionSupersessionCauses.OperatorMove, occurrence.ExpectedSourceState, moved.State);
            try
            {
                EngineDecisionSupersession.RecordOnCard(timeline, moved.FolderPath, occurrence,
                    EngineDecisionSupersessionCauses.OperatorMove, moved.State, actor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The occurrence is already cancelled, so the recovery pass
                // removes a marker left behind here.
                logger.LogWarning(exception, "orchestration-supersession-card-record-skipped run={RunId} task={TaskKey}",
                    occurrence.Run.RunId, moved.Key ?? moved.Id);
            }
        }
        scanner.InvalidateCache();
    }
}
