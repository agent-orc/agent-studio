using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>A side effect that <see cref="MonolithOrchestrationApplier"/> owes an engine decision.</summary>
public enum EngineApplierEffectKind
{
    /// <summary>The card reached the decided lane.</summary>
    LaneMove,

    /// <summary>The decision selects no lane; the card keeps the lane its occurrence found.</summary>
    LaneKept,

    /// <summary>
    /// The batch gate owns the settled review (ownership and a pending record),
    /// the per-task emergency gate settled the card, or the operator feed records
    /// why no admission was owed.
    /// </summary>
    BatchAdmission,

    /// <summary>The attempt authority holds a scheduled review infrastructure retry.</summary>
    ReviewRetryScheduled,

    /// <summary>A linked follow-up card exists in Ready, and both cards reference each other.</summary>
    LinkedFollowUp,

    /// <summary>The delivery settlement journal names the review and has settled the integration.</summary>
    IntegrationJournal,

    /// <summary>The card prompt carries the keyed engine continuation note.</summary>
    ContinuationPrompt,

    /// <summary>The card timeline records the reopened quality loop with the authority write key.</summary>
    QualityLoopReceipt,

    /// <summary>A coding completion with a review request created the review attempt and its subject.</summary>
    ReviewAttempt,

    /// <summary>A review occurrence parked in Human Review recorded its keyed review verdict.</summary>
    ParkVerdict,

    /// <summary>
    /// A passed gate keeps its lane and records a Continue stage result; the
    /// monolith gate handler reads it on its next pass, dispatches the aspect
    /// review and records <c>engine_gate_dispatched</c> on the card timeline.
    /// That receipt is written by a later pass, so it may take longer than the
    /// effect window.
    /// </summary>
    GateDispatch,

    /// <summary>
    /// The engine batch plan is stored as the durable intent of its scope, or
    /// the batch tick recorded what it did with it (applied, expired, rejected
    /// or superseded).
    /// </summary>
    BatchPlanIntent,

    /// <summary>The card's engine wait marker no longer names the occurrence.</summary>
    WaitMarkerCleared,

    /// <summary>The occurrence completed with the engine receipt and its stage result.</summary>
    DecisionReceipt,

    /// <summary>
    /// The integration raised a review-input decision, whose occurrence now
    /// holds the card; the applier stops before the lane move and the park
    /// verdict, which that decision owns.
    /// </summary>
    ReviewInputHandoff,

    /// <summary>
    /// The integration ended in Conflict or AgentRoundRequired and started the
    /// automatic agent round, which moved the card to Ready; the applier
    /// settles the journal and owes no lane move and no park verdict.
    /// </summary>
    AgentRoundHandoff,
}

/// <summary>What an effect check verified; only <see cref="Row"/> checks count toward a canary.</summary>
public static class EngineEffectBasis
{
    /// <summary>The decision's row of <see cref="EngineApplierContract"/>.</summary>
    public const string Row = "row";

    /// <summary>The AI Operator holds the decision as an ask.</summary>
    public const string Ask = "ask";

    /// <summary>An active choice wrote the verified-tree checkpoint.</summary>
    public const string Checkpoint = "checkpoint";

    /// <summary>The applier handed the card to a review-input decision.</summary>
    public const string ReviewInputHandoff = "review-input-handoff";

    /// <summary>The integration handed the card to an automatic agent round.</summary>
    public const string AgentRoundHandoff = "agent-round-handoff";

    /// <summary>
    /// The card left the source lane by a move this decision's write key did
    /// not write (a person, a runner, an integration round) before the applier
    /// completed; the applier refuses the decision on purpose, so this is no
    /// applier defect.
    /// </summary>
    public const string SourceExit = "source-exit";

    /// <summary>
    /// A passed gate's card left Auto Review, got a pending intent or a new
    /// attempt before the gate handler's next pass, which therefore never
    /// dispatches this decision's aspect review.
    /// </summary>
    public const string GateSuperseded = "gate-superseded";

    /// <summary>The effect probe failed on the decision; the canary alerts and does not count it.</summary>
    public const string ProbeFailed = "probe-failed";

    /// <summary>The bases that never count toward a canary, whatever the effects show.</summary>
    public static bool Refused(string basis) => basis is SourceExit or GateSuperseded or ProbeFailed;
}

/// <summary>Why a check waits on something that is not an applier defect.</summary>
public static class EngineEffectWait
{
    /// <summary>The card of the occurrence is not in the scanner snapshot (a rescan, a move, a restart).</summary>
    public const string CardNotFound = "card-not-found";

    /// <summary>The integration lane shows this delivery enqueued or dequeued without a terminal event.</summary>
    public const string IntegrationInFlight = "integration-in-flight";
}

/// <summary>The effect the applier owes one engine decision action.</summary>
/// <param name="Action">A <see cref="TransitionAction"/> name, or <see cref="EngineApplierContract.BatchPlanAction"/>.</param>
/// <param name="Lane">
/// The decided lane, <see cref="EngineApplierContract.StageLane"/> when the
/// stage payload or stage action selects it, or null when the card keeps its lane.
/// </param>
public sealed record EngineApplierEffect(string Action, string? Lane,
    IReadOnlyList<EngineApplierEffectKind> Effects, string Summary);

/// <summary>
/// AGT-3218: the action to effect table of the engine applier. On 16.10.2026
/// ReviewReport switched to Engine after a perfect shadow streak, and for 16
/// hours every DeferToBatch decision completed without its batch admission:
/// shadow mode compares receipts and never runs the applier. The contract
/// tests drive the real applier through every row, and the applier canary
/// checks the same rows on the first engine decisions after a switch.
/// <see cref="For"/> has no default arm and the backend treats CS8509 as an
/// error, so a new <see cref="TransitionAction"/> without an effect fails the build.
/// </summary>
public static class EngineApplierContract
{
    /// <summary>The action name of a BatchPlan decision, which carries a plan instead of a transition.</summary>
    public const string BatchPlanAction = "BatchPlan";

    /// <summary>The lane is the stage's: its payload target, or Escalated for an escalating stage action.</summary>
    public const string StageLane = "stage-lane";

    private static readonly EngineApplierEffectKind[] Settled =
        [EngineApplierEffectKind.WaitMarkerCleared, EngineApplierEffectKind.DecisionReceipt];

    /// <summary>Every action each rollout profile can complete, in the order the docs list them.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProfileActions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["CodingCompletion"] =
            [
                nameof(TransitionAction.CreateReview), nameof(TransitionAction.ReturnToReady),
                nameof(TransitionAction.SalvageContinuation), nameof(TransitionAction.Escalate),
                nameof(TransitionAction.HumanReview), nameof(TransitionAction.Continue),
            ],
            ["ReviewReport"] =
            [
                nameof(TransitionAction.FindingRound), nameof(TransitionAction.ConcernRound),
                nameof(TransitionAction.AcceptAndIntegrate), nameof(TransitionAction.DeferToBatch),
                nameof(TransitionAction.HumanReview), nameof(TransitionAction.Escalate),
                nameof(TransitionAction.ReviewInfrastructureRetry),
                nameof(TransitionAction.DegradeWithFollowUp), nameof(TransitionAction.Halt),
            ],
            ["GateResult"] =
            [
                nameof(TransitionAction.Continue), nameof(TransitionAction.ReturnToReady),
                nameof(TransitionAction.Escalate),
            ],
            ["BatchPlan"] = [BatchPlanAction],
        };

    /// <summary>Every row: each transition action, then the batch plan.</summary>
    public static IReadOnlyList<EngineApplierEffect> All
        => [.. Enum.GetValues<TransitionAction>().Select(For), BatchPlan];

    public static EngineApplierEffect BatchPlan { get; } = new(BatchPlanAction, null,
        [EngineApplierEffectKind.BatchPlanIntent, EngineApplierEffectKind.DecisionReceipt],
        "Stores the plan as the durable intent of its scope; the batch tick takes it, checks the authority floors again and records what it did.");

    /// <summary>The effect of a named action, or null for an action outside the table.</summary>
    public static EngineApplierEffect? Find(string action)
        => action == BatchPlanAction ? BatchPlan
            : Enum.TryParse<TransitionAction>(action, out var parsed)
              && Enum.IsDefined(parsed) && parsed.ToString() == action ? For(parsed) : null;

    /// <summary>Effects that a later pass of another loop writes, after the applier completed.</summary>
    public static bool Downstream(EngineApplierEffectKind effect) => effect == EngineApplierEffectKind.GateDispatch;

    /// <summary>
    /// The effects one decision owes: its row narrowed by the lane the stage
    /// selects (<paramref name="lane"/>, null when none) and by the occurrence
    /// payload. With <paramref name="reviewInputHandoff"/> the applier stopped
    /// at the review-input decision that holds the card: it owes what it did
    /// before (follow-up, integration journal) and the handoff, and the lane
    /// move, park verdict and prompts belong to that decision. With
    /// <paramref name="agentRoundHandoff"/> the integration started an agent
    /// round, which owns the card in the same way. The effect probe and the
    /// contract tests both read this.
    /// </summary>
    public static IReadOnlyList<EngineApplierEffectKind> Owed(EngineApplierEffect row, string? lane,
        bool gateStage, bool reviewPayload, bool reviewRequest, bool reviewInputHandoff = false,
        bool agentRoundHandoff = false)
        => row.Effects
            .Select(effect => effect == EngineApplierEffectKind.LaneMove && lane is null
                ? EngineApplierEffectKind.LaneKept : effect)
            .Where(effect => reviewInputHandoff || agentRoundHandoff
                ? effect is EngineApplierEffectKind.LinkedFollowUp or EngineApplierEffectKind.IntegrationJournal
                    or EngineApplierEffectKind.ReviewRetryScheduled or EngineApplierEffectKind.WaitMarkerCleared
                    or EngineApplierEffectKind.DecisionReceipt
                : effect switch
                {
                    EngineApplierEffectKind.GateDispatch => lane is null && gateStage,
                    EngineApplierEffectKind.ParkVerdict => lane == TaskStates.HumanReview && reviewPayload,
                    EngineApplierEffectKind.ReviewAttempt => lane == TaskStates.AutoReview && reviewRequest,
                    _ => true,
                })
            .Concat(reviewInputHandoff ? [EngineApplierEffectKind.ReviewInputHandoff]
                : agentRoundHandoff ? [EngineApplierEffectKind.AgentRoundHandoff] : [])
            .Distinct().ToArray();

    // CS8524 asks for an arm for unnamed enum values; those cannot come from a
    // typed decision, and a discard arm would hide a new named action.
#pragma warning disable CS8524
    public static EngineApplierEffect For(TransitionAction action) => action switch
    {
        TransitionAction.CreateReview => Effect(action, StageLane,
            "Moves the card to its payload lane, Auto Review; a review request creates the review attempt and subject.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.ReviewAttempt),
        TransitionAction.FindingRound or TransitionAction.ConcernRound => Effect(action, TaskStates.Ready,
            "Saves the reviewed continuation base, moves the card to Ready, appends the finding prompt and records the quality-loop receipt.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.ContinuationPrompt,
            EngineApplierEffectKind.QualityLoopReceipt),
        TransitionAction.AcceptAndIntegrate => Effect(action, TaskStates.HumanReview,
            "Writes the delivery settlement journal, settles the integration, moves the card to Human Review and records the park verdict.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.IntegrationJournal,
            EngineApplierEffectKind.ParkVerdict),
        TransitionAction.DeferToBatch => Effect(action, null,
            "Admits the settled review to the batch gate (ownership and pending record), runs the per-task gate when the queue refuses it, or records why no admission was owed.",
            EngineApplierEffectKind.BatchAdmission),
        TransitionAction.HumanReview => Effect(action, TaskStates.HumanReview,
            "Moves the card to Human Review; a review occurrence records its park verdict.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.ParkVerdict),
        TransitionAction.Escalate => Effect(action, TaskStates.Escalated,
            "Moves the card to Escalated.", EngineApplierEffectKind.LaneMove),
        TransitionAction.ReviewInfrastructureRetry => Effect(action, null,
            "Schedules the bounded review infrastructure retry; the card stays in its lane.",
            EngineApplierEffectKind.LaneKept, EngineApplierEffectKind.ReviewRetryScheduled),
        TransitionAction.DegradeWithFollowUp => Effect(action, TaskStates.HumanReview,
            "Creates the linked follow-up in Ready, settles the integration, moves the card to Human Review and records the park verdict.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.LinkedFollowUp,
            EngineApplierEffectKind.IntegrationJournal, EngineApplierEffectKind.ParkVerdict),
        TransitionAction.SweepIgnore or TransitionAction.SweepHold => Effect(action, null,
            "Holds: the card stays in its lane until the next sweep.", EngineApplierEffectKind.LaneKept),
        TransitionAction.SweepRound or TransitionAction.SweepWaitForPerson => Effect(action, StageLane,
            "An operator sweep receipt; through a transition profile it moves the card to the stage lane.",
            EngineApplierEffectKind.LaneMove),
        TransitionAction.SalvageContinuation => Effect(action, TaskStates.Ready,
            "Stages the salvage continuation base and moves the card to Ready.",
            EngineApplierEffectKind.LaneMove),
        TransitionAction.ReturnToReady => Effect(action, TaskStates.Ready,
            "Moves the card to Ready and appends the gate failure prompt.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.ContinuationPrompt),
        TransitionAction.Continue => Effect(action, StageLane,
            "Moves the card to its payload lane; a passed gate without one keeps the lane and dispatches the aspect review.",
            EngineApplierEffectKind.LaneMove, EngineApplierEffectKind.GateDispatch),
        TransitionAction.Halt => Effect(action, null,
            "Applies nothing: the review is already applied or the card left Auto Review.",
            EngineApplierEffectKind.LaneKept),
    };
#pragma warning restore CS8524

    private static EngineApplierEffect Effect(TransitionAction action, string? lane, string summary,
        params EngineApplierEffectKind[] effects)
        => new(action.ToString(), lane, [.. effects, .. Settled], summary);
}
