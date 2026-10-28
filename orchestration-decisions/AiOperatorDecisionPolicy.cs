namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Autonomy classes of AGT-W73 O1 A. The AI Operator acts on its own within
/// A0 and A1, prepares an A2 action as a proposal for a person and never takes
/// an A3 action; that one is asked of a person instead.
/// </summary>
public enum AutonomyClass
{
    /// <summary>Read-only: inspect, correlate, report.</summary>
    A0,

    /// <summary>Reversible and bounded: requeue, steer append, keyed lane move, bounded continuation.</summary>
    A1,

    /// <summary>Prepared: a non-trivial action that a person confirms.</summary>
    A2,

    /// <summary>Human-only: destructive, protected, cross-card, or waiving.</summary>
    A3,
}

/// <summary>How the authority treats one AI Operator decision.</summary>
public enum AiOperatorDisposition
{
    /// <summary>No effect; the next periodic occurrence looks again.</summary>
    Hold,

    /// <summary>Apply the effect (A0 or A1).</summary>
    Act,

    /// <summary>Record the effect as a proposal for a person (A2).</summary>
    Propose,

    /// <summary>Escalate to a person: the effect parks the card visibly.</summary>
    Ask,
}

/// <summary>
/// The background deciders that AGT-3060 (I18) moved out of the monolith.
/// The ids are persisted in occurrence payloads and receipts; never rename one.
/// </summary>
public static class AiOperatorDeciders
{
    public const string RunLiveness = "run-liveness";
    public const string StaleProgress = "stale-progress";
    public const string SteerTimeout = "steer-timeout";
    public const string TimeoutSalvage = "timeout-salvage";
    public const string GateEnvironmentRetry = "gate-environment-retry";
    public const string DeliveryChain = "delivery-chain";
    public const string CauseBreaker = "cause-breaker";
    public const string IntakeRouting = "intake-routing";
    public const string AcceptanceRail = "acceptance-rail";

    /// <summary>AGT-3207: interventions on operational stall events of hosts, containers, gates and queues.</summary>
    public const string StallIntervention = "stall-intervention";

    public static readonly IReadOnlyList<string> All =
    [
        RunLiveness, StaleProgress, SteerTimeout, TimeoutSalvage, GateEnvironmentRetry,
        DeliveryChain, CauseBreaker, IntakeRouting, AcceptanceRail, StallIntervention,
    ];
}

/// <summary>Stable effect tokens the authority applies through its keyed writers.</summary>
public static class AiOperatorEffects
{
    public const string None = "none";
    public const string DemoteToReady = "demote-to-ready";
    public const string RetriggerPostProcessing = "retrigger-post-processing";
    public const string RecoverToReview = "recover-to-review";
    public const string RequeueToReady = "requeue-to-ready";
    public const string AutoAnswer = "auto-answer";
    public const string Escalate = "escalate";
    public const string StartContinuation = "start-continuation";
    public const string ReplayIntegration = "replay-integration";
    public const string ParkIntegration = "park-integration";
    public const string ParkAndContinue = "park-and-continue";
    public const string PromoteToHumanReview = "promote-to-human-review";
    public const string ReturnToAutoReview = "return-to-auto-review";
    public const string CloseBreaker = "close-breaker";
    public const string AcceptDelivery = "accept-delivery";
    public const string RequeueRebase = "requeue-rebase";
    public const string RequeueInfrastructure = "requeue-infrastructure";
    public const string RecordHeldBounce = "record-held-bounce";
}

/// <summary>Frozen facts of a timeout completion. The salvage pair is null when none was transferred.</summary>
public sealed record TimeoutSalvageFacts(
    string? Outcome,
    RunSalvageReference? Salvage,
    int AutomaticContinuationRoundsUsed,
    string? ReportedReason);

/// <summary>
/// One occurrence payload. Exactly the facts record that matches
/// <see cref="Decider"/> is set.
/// </summary>
public sealed record AiOperatorDecisionPayload(
    string Decider,
    string SubjectKey,
    RunLivenessFacts? RunLiveness = null,
    StaleProgressFacts? StaleProgress = null,
    SteerTimeoutFacts? SteerTimeout = null,
    TimeoutSalvageFacts? TimeoutSalvage = null,
    GateEnvironmentRetryOperatorFacts? GateEnvironmentRetry = null,
    DeliveryChainOperatorFacts? DeliveryChain = null,
    CauseBreakerOperatorFacts? CauseBreaker = null,
    IntakeOperatorFacts? IntakeRouting = null,
    AcceptanceRailOperatorFacts? AcceptanceRail = null,
    StallInterventionFacts? StallIntervention = null);

/// <summary>The receipt-bearing answer of one AI Operator occurrence.</summary>
public sealed record AiOperatorDecision(
    string Decider,
    string SubjectKey,
    AiOperatorDisposition Disposition,
    AutonomyClass Autonomy,
    string Effect,
    string ReasonCode,
    string Detail,
    string? AnswerText = null,
    StallInterventionPlan? Intervention = null)
{
    /// <summary>The action recorded on the occurrence receipt.</summary>
    public string ReceiptAction => $"{Disposition}:{Effect}";
}

/// <summary>
/// The AI Operator role (AGT-W73 O1 A) for periodic and event-triggered
/// occurrences. Each decider's rule lives in its own pure policy; this policy
/// selects it, classifies the effect and applies the autonomy limit.
/// </summary>
public static class AiOperatorDecisionPolicy
{
    public static AiOperatorDecision Decide(AiOperatorDecisionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.SubjectKey);
        return payload.Decider switch
        {
            AiOperatorDeciders.RunLiveness => RunLiveness(payload,
                Required(payload.RunLiveness, payload.Decider)),
            AiOperatorDeciders.StaleProgress => StaleProgress(payload,
                Required(payload.StaleProgress, payload.Decider)),
            AiOperatorDeciders.SteerTimeout => SteerTimeout(payload,
                Required(payload.SteerTimeout, payload.Decider)),
            AiOperatorDeciders.TimeoutSalvage => TimeoutSalvage(payload,
                Required(payload.TimeoutSalvage, payload.Decider)),
            AiOperatorDeciders.GateEnvironmentRetry => GateEnvironmentRetry(payload,
                Required(payload.GateEnvironmentRetry, payload.Decider)),
            AiOperatorDeciders.DeliveryChain => DeliveryChain(payload,
                Required(payload.DeliveryChain, payload.Decider)),
            AiOperatorDeciders.CauseBreaker => CauseBreaker(payload,
                Required(payload.CauseBreaker, payload.Decider)),
            AiOperatorDeciders.IntakeRouting => IntakeRouting(payload,
                Required(payload.IntakeRouting, payload.Decider)),
            AiOperatorDeciders.AcceptanceRail => AcceptanceRail(payload,
                Required(payload.AcceptanceRail, payload.Decider)),
            AiOperatorDeciders.StallIntervention => StallIntervention(payload,
                Required(payload.StallIntervention, payload.Decider)),
            _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.Decider,
                "Unknown AI Operator decider."),
        };
    }

    /// <summary>A0 and A1 act, A2 becomes a proposal, A3 is asked of a person.</summary>
    public static AiOperatorDisposition Limit(AutonomyClass autonomy) => autonomy switch
    {
        AutonomyClass.A0 or AutonomyClass.A1 => AiOperatorDisposition.Act,
        AutonomyClass.A2 => AiOperatorDisposition.Propose,
        _ => AiOperatorDisposition.Ask,
    };

    private static AiOperatorDecision RunLiveness(AiOperatorDecisionPayload payload, RunLivenessFacts facts)
    {
        var verdict = RunLivenessPolicy.Decide(facts);
        return verdict.Action switch
        {
            RunLivenessAction.DemoteToReady => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.DemoteToReady, verdict.ReasonCode, verdict.Detail),
            RunLivenessAction.RetriggerPostProcessing => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.RetriggerPostProcessing, verdict.ReasonCode, verdict.Detail),
            _ => Hold(payload, verdict.ReasonCode, verdict.Detail),
        };
    }

    private static AiOperatorDecision StaleProgress(AiOperatorDecisionPayload payload, StaleProgressFacts facts)
    {
        var verdict = StaleProgressPolicy.Decide(facts);
        return verdict.Action switch
        {
            StaleProgressAction.RecoverToReview => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.RecoverToReview, verdict.ReasonCode, verdict.Detail),
            StaleProgressAction.RequeueToReady => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.RequeueToReady, verdict.ReasonCode, verdict.Detail),
            _ => Hold(payload, verdict.ReasonCode, verdict.Detail),
        };
    }

    private static AiOperatorDecision SteerTimeout(AiOperatorDecisionPayload payload, SteerTimeoutFacts facts)
    {
        var verdict = SteerTimeoutPolicy.Decide(facts);
        return verdict.Action switch
        {
            SteerTimeoutAction.AutoAnswer => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.AutoAnswer, verdict.ReasonCode, verdict.Detail, verdict.AnswerText),
            SteerTimeoutAction.RouteBlocked => Ask(payload, verdict.ReasonCode, verdict.Detail),
            _ => Hold(payload, verdict.ReasonCode, verdict.Detail),
        };
    }

    private static AiOperatorDecision TimeoutSalvage(AiOperatorDecisionPayload payload, TimeoutSalvageFacts facts)
    {
        var action = RunTimeoutSalvageContinuationPolicy.Decide(facts.Outcome, facts.Salvage is not null,
            facts.AutomaticContinuationRoundsUsed);
        return action switch
        {
            RunTimeoutSalvageAction.StartContinuation => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.StartContinuation, RunTimeoutSalvageContinuationPolicy.ContinuationReason,
                $"Automatic continuation round {facts.AutomaticContinuationRoundsUsed + 1} of "
                + $"{RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds} on salvage "
                + facts.Salvage!.Describe() + "."),
            RunTimeoutSalvageAction.Escalate => Ask(payload, "non-terminal-outcome",
                RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason(facts.ReportedReason,
                    facts.Salvage, facts.AutomaticContinuationRoundsUsed)),
            _ => Hold(payload, "not-a-timeout-completion",
                "The completion is not a non-terminal timeout outcome."),
        };
    }

    private static AiOperatorDecision GateEnvironmentRetry(AiOperatorDecisionPayload payload,
        GateEnvironmentRetryOperatorFacts facts)
    {
        var ladder = GateEnvironmentRetryPolicy.Decide(facts.Ladder);
        var detail = $"{ladder.Action} after {ladder.AttemptsSpent}/{ladder.MaxAttempts} gate-environment retries.";
        return ladder.Action switch
        {
            GateEnvironmentRetryAction.Retry => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.ReplayIntegration, ladder.Reason, detail),
            GateEnvironmentRetryAction.Park when facts.AlreadyParked => Hold(payload,
                GateEnvironmentRetryReasons.AlreadyParked, detail),
            GateEnvironmentRetryAction.Park => Effect(payload, AutonomyClass.A1,
                GateEnvironmentContinuationPolicy.ShouldStart(facts.AutomaticContinuationsEnabled,
                    facts.PriorContinuationRounds)
                    ? AiOperatorEffects.ParkAndContinue : AiOperatorEffects.ParkIntegration,
                ladder.Reason, detail),
            _ => Hold(payload, ladder.Reason, detail),
        };
    }

    private static AiOperatorDecision DeliveryChain(AiOperatorDecisionPayload payload, DeliveryChainOperatorFacts facts)
    {
        var verdict = DeliveryChainPolicy.Decide(facts);
        return verdict.Action switch
        {
            DeliveryChainAction.PromoteToHumanReview => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.PromoteToHumanReview, verdict.ReasonCode, verdict.Detail),
            DeliveryChainAction.ReturnToAutoReview => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.ReturnToAutoReview, verdict.ReasonCode, verdict.Detail),
            DeliveryChainAction.Escalate => Ask(payload, verdict.ReasonCode, verdict.Detail),
            _ => Hold(payload, verdict.ReasonCode, verdict.Detail),
        };
    }

    private static AiOperatorDecision CauseBreaker(AiOperatorDecisionPayload payload, CauseBreakerOperatorFacts facts)
        => CauseBreakerClosePolicy.Closes(facts)
            ? Effect(payload, AutonomyClass.A1, AiOperatorEffects.CloseBreaker, CauseBreakerClosePolicy.CauseIntegrated,
                $"The cause card of breaker {facts.Fingerprint} is delivered; its waiting cards are released.")
            : Hold(payload, CauseBreakerClosePolicy.CauseNotIntegrated,
                $"The cause card of breaker {facts.Fingerprint} is {facts.CauseCardState ?? "not found"}.");

    private static AiOperatorDecision IntakeRouting(AiOperatorDecisionPayload payload, IntakeOperatorFacts facts)
        => IntakeRoutingPolicy.RoutesToHumanReview(facts)
            ? Ask(payload, IntakeRoutingPolicy.AlreadyDoneReason,
                facts.Reason ?? "The prompt declares the card already finished.")
            : Hold(payload, IntakeRoutingPolicy.NoRouting, $"Intake outcome {facts.Outcome} needs no routing.");

    private static AiOperatorDecision AcceptanceRail(AiOperatorDecisionPayload payload,
        AcceptanceRailOperatorFacts facts)
    {
        var verdict = AcceptanceRailDecisionPolicy.Decide(facts);
        var detail = $"Acceptance rail: {verdict.Reason}.";
        return verdict.Action switch
        {
            AcceptanceRailVerdictAction.Accept => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.AcceptDelivery, verdict.Reason, detail),
            AcceptanceRailVerdictAction.Requeue => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.RequeueRebase, verdict.Reason, detail),
            AcceptanceRailVerdictAction.RequeueInfrastructure => Effect(payload, AutonomyClass.A1,
                AiOperatorEffects.RequeueInfrastructure, verdict.Reason, detail),
            AcceptanceRailVerdictAction.Escalate => Ask(payload, verdict.Reason, detail),
            // A held recoverable conflict keeps its bounce obligation visible; recording it is A0.
            _ when verdict.Reason == AcceptanceRailReasons.OperatorHold && facts.RebaseRecoveryAvailable
                => Effect(payload, AutonomyClass.A0, AiOperatorEffects.RecordHeldBounce, verdict.Reason, detail),
            _ => Hold(payload, verdict.Reason, detail),
        };
    }

    /// <summary>
    /// AGT-3207: A0 and A1 interventions act, A2 interventions become
    /// proposals, an escalation asks a person. The plan never names an A3 action.
    /// </summary>
    private static AiOperatorDecision StallIntervention(AiOperatorDecisionPayload payload,
        StallInterventionFacts facts)
    {
        var plan = StallInterventionPolicy.Decide(facts);
        var disposition = plan.Action switch
        {
            StallInterventionActions.None => AiOperatorDisposition.Hold,
            StallInterventionActions.Escalate => AiOperatorDisposition.Ask,
            _ => Limit(plan.Autonomy),
        };
        return new AiOperatorDecision(payload.Decider, payload.SubjectKey, disposition,
            disposition == AiOperatorDisposition.Hold ? AutonomyClass.A0 : plan.Autonomy,
            plan.Action == StallInterventionActions.None ? AiOperatorEffects.None : plan.Action,
            plan.ReasonCode, plan.Receipt, Intervention: plan);
    }

    private static AiOperatorDecision Effect(AiOperatorDecisionPayload payload, AutonomyClass autonomy,
        string effect, string reasonCode, string detail, string? answer = null)
        => new(payload.Decider, payload.SubjectKey, Limit(autonomy), autonomy, effect, reasonCode, detail, answer);

    private static AiOperatorDecision Ask(AiOperatorDecisionPayload payload, string reasonCode, string detail)
        => new(payload.Decider, payload.SubjectKey, AiOperatorDisposition.Ask, AutonomyClass.A1,
            AiOperatorEffects.Escalate, reasonCode, detail);

    private static AiOperatorDecision Hold(AiOperatorDecisionPayload payload, string reasonCode, string detail)
        => new(payload.Decider, payload.SubjectKey, AiOperatorDisposition.Hold, AutonomyClass.A0,
            AiOperatorEffects.None, reasonCode, detail);

    private static T Required<T>(T? facts, string decider) where T : class
        => facts ?? throw new ArgumentException($"The {decider} occurrence has no facts.");
}
