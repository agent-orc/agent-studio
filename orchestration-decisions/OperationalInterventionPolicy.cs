namespace AgentStudio.OrchestrationDecisions;

/// <summary>Frozen evidence for one operational stall. Values come from the watcher,
/// not from a model response. An action is allowed only when its required proof is present.</summary>
public sealed record OperationalStallFacts(
    string EventId,
    string Fingerprint,
    string Kind,
    string HostId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> AffectedCards,
    double? Load1 = null,
    int? CpuCores = null,
    int? CurrentAdmission = null,
    int? PreviousAdmission = null,
    bool HasHealthyAlternative = false,
    int CompletedGateMembers = 0,
    int PendingGateMembers = 0,
    bool GateDeadlinePassed = false,
    bool HasLastRealDelivery = false,
    bool CorrectedConfigurationVerified = false,
    bool ConditionCleared = false);

public sealed record OperationalInterventionReceipt(
    string EventId,
    string Fingerprint,
    string HostId,
    string Action,
    AutonomyClass Autonomy,
    DateTimeOffset At,
    DateTimeOffset CheckAt,
    string Observation,
    string Reason,
    string Before,
    string After,
    string Undo,
    string Outcome);

public sealed record OperationalInterventionDecision(
    string Action,
    AutonomyClass Autonomy,
    AiOperatorDisposition Disposition,
    string Reason,
    string Before,
    string After,
    string Undo,
    TimeSpan CheckAfter);

/// <summary>AI Operator guardrail for event-triggered host intervention.
/// Hosts supply facts; this policy alone chooses the action and autonomy class.</summary>
public static class OperationalInterventionPolicy
{
    public const int MaxActionsPerHostHour = 2;
    public static readonly TimeSpan FollowUpDelay = TimeSpan.FromMinutes(10);

    public static OperationalInterventionDecision Decide(
        OperationalStallFacts facts,
        IReadOnlyList<OperationalInterventionReceipt> history,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.Fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.HostId);
        var prior = history.Where(item => item.HostId == facts.HostId && item.Fingerprint == facts.Fingerprint)
            .OrderByDescending(item => item.At).FirstOrDefault();
        if (facts.ConditionCleared)
        {
            if (facts.Kind == "host-overload" && prior?.Action == "lower-host-admission"
                && facts.PreviousAdmission is > 0 && facts.CurrentAdmission is > 0
                && facts.CurrentAdmission < facts.PreviousAdmission
                && facts.Load1 is { } recoveredLoad && facts.CpuCores is > 0
                && recoveredLoad / facts.CpuCores <= 0.7)
                return Act("restore-host-admission", "Host load returned below 70% per core.",
                    $"admission {facts.CurrentAdmission}", $"admission {facts.PreviousAdmission}",
                    "Settings > Capacity: restore the previous reduced limit.");
            return Hold("Condition cleared; no intervention is needed.");
        }

        if (prior is not null)
        {
            if (prior.Action == "escalate" || prior.Autonomy == AutonomyClass.A2)
                return Hold("The event is already awaiting a human decision.");
            if (prior.Outcome.StartsWith("failed:", StringComparison.Ordinal))
                return Ask("The prior intervention failed or its outcome is unknown; inspect the receipt before retrying.");
            if (now < prior.CheckAt) return Hold("The prior intervention is awaiting its follow-up check.");
            return Ask("The condition remained after the intervention's follow-up deadline; inspect the receipt chain.");
        }
        var recent = history.Count(item => item.HostId == facts.HostId && item.Autonomy == AutonomyClass.A1
            && item.At > now.AddHours(-1) && item.At <= now);
        if (recent >= MaxActionsPerHostHour)
            return Ask($"Host intervention limit reached: {recent} automatic actions in the last hour.");

        return facts.Kind switch
        {
            "host-overload" or "memory-pressure" when facts.CurrentAdmission is > 1
                && facts.CpuCores is > 0 && facts.Load1 is > 0 =>
                Act("lower-host-admission",
                    $"Load {facts.Load1:0.0} on {facts.CpuCores} cores; new work must wait while running work continues.",
                    $"admission {facts.CurrentAdmission}",
                    $"admission {Math.Max(1, facts.CurrentAdmission.Value - Math.Max(1, facts.CurrentAdmission.Value / 4))}",
                    "Settings > Capacity: restore the previous admission limit."),
            "host-overload" or "memory-pressure" => Ask("The host load or current admission lacks safe adjustment evidence."),
            "review-daemon-stuck" or "gate-worker-stuck" =>
                Act("restart-stuck-worker", "The worker stopped making progress; restart its named service through the Operations Server.",
                    "worker stalled", "restart requested; current claims remain fenced",
                    "Operations > Host: stop the restarted service and restore its prior deployment."),
            "provider-auth-invalid" when facts.HasHealthyAlternative =>
                Act("reroute-attempts", "Provider login is invalid on this host; another host passed the same capability check.",
                    "attempts wait on invalid-auth host", "new attempts claim on a healthy host",
                    "Host routing: restore this host after renewing and checking its login."),
            "provider-auth-invalid" => Propose("renew-provider-login", "No proven healthy host can take these attempts."),
            "batch-gate-stuck" when facts.CompletedGateMembers > 0
                && facts.PendingGateMembers >= facts.CompletedGateMembers =>
                Act("drop-completed-gate-members", $"{facts.CompletedGateMembers} of {facts.PendingGateMembers} pending members already completed.",
                    $"{facts.PendingGateMembers} pending", $"{Math.Max(0, facts.PendingGateMembers - facts.CompletedGateMembers)} pending",
                    "Batch Gate: restore the member only with its original generation and evidence."),
            "batch-gate-stuck" when facts.GateDeadlinePassed && facts.PendingGateMembers > 0 =>
                Act("close-expired-batch", "The batch deadline passed while members still waited.",
                    $"{facts.PendingGateMembers} pending in open batch", "batch closed; remaining cards return to normal routing",
                    "Batch Gate: create a new batch from the remaining eligible members."),
            "batch-gate-stuck" => Ask("Batch has no completed member and has not passed its deadline."),
            "provider-rate-limited" when facts.HasLastRealDelivery =>
                Act("requeue-from-delivery", "The provider rate limit ended the run; a real delivery exists.",
                    "run stopped at rate limit", "run requeued from the last real delivery",
                    "Task > Run: cancel the queued continuation before it is claimed."),
            "provider-rate-limited" => Ask("No real delivery is available; an empty salvage ref is not a safe base."),
            "deploy-drift" when facts.CorrectedConfigurationVerified =>
                Act("retry-engine-start", "The engine start failed and corrected configuration has been verified.",
                    "old engine image running", "start retried with corrected configuration",
                    "Operations > Deploy: roll back to the recorded previous image and configuration."),
            "deploy-drift" => Ask("The corrected engine configuration has not been verified."),
            "toolchain-missing" => Propose("add-review-toolchain", "Changing the review image requires a human decision."),
            "claim-refusal" => Propose("repair-claim-requirements", "Claim requirements need review before changing a host image or routing policy."),
            "idle-capacity" => Ask("Free slots and waiting cards need a claim diagnosis before a worker can be restarted."),
            "lane-stall" => Ask("The stale lane needs its current gate and card generation checked before a transition."),
            "long-host-move" => Propose("move-work-to-host", "A long-lived host move requires a human decision."),
            _ => Ask($"No bounded intervention is defined for {facts.Kind}."),
        };
    }

    private static OperationalInterventionDecision Act(string action, string reason, string before, string after, string undo)
        => new(action, AutonomyClass.A1, AiOperatorDisposition.Act, reason, before, after, undo, FollowUpDelay);

    private static OperationalInterventionDecision Propose(string action, string reason)
        => new(action, AutonomyClass.A2, AiOperatorDisposition.Propose, reason, "unchanged", "proposal only",
            "Withdraw the proposal in the operator inbox.", TimeSpan.Zero);

    private static OperationalInterventionDecision Ask(string reason)
        => new("escalate", AutonomyClass.A0, AiOperatorDisposition.Ask, reason, "unchanged", "human review requested",
            "Resolve the event after the cause is fixed.", TimeSpan.Zero);

    private static OperationalInterventionDecision Hold(string reason)
        => new("hold", AutonomyClass.A0, AiOperatorDisposition.Hold, reason, "unchanged", "unchanged",
            "No action to undo.", TimeSpan.Zero);
}
