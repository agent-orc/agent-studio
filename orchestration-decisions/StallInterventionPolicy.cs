using System.Globalization;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Stall kinds of the AGT-3206 operational stall detector. The tokens match
/// the <c>operational:&lt;kind&gt;:&lt;subject&gt;</c> fingerprint keys.
/// </summary>
public static class StallKinds
{
    public const string HostOffline = "host-offline";
    public const string HostOverload = "host-overload";
    public const string MemoryPressure = "memory-pressure";
    public const string IdleCapacity = "idle-capacity";
    public const string ClaimRefusal = "claim-refusal";
    public const string ProviderAuthInvalid = "provider-auth-invalid";
    public const string ProviderRateLimited = "provider-rate-limited";
    public const string BatchGateStuck = "batch-gate-stuck";
    public const string LaneStall = "lane-stall";
    public const string DeployDrift = "deploy-drift";

    /// <summary>
    /// Kinds the AI Operator acts on or proposes for, in the order one pass
    /// looks at them. Every other kind (lane stalls) stays with the card
    /// deciders and is never submitted.
    /// </summary>
    private static readonly string[] Ordered =
    [
        HostOverload, MemoryPressure, ProviderAuthInvalid, HostOffline, IdleCapacity,
        BatchGateStuck, ProviderRateLimited, DeployDrift, ClaimRefusal,
    ];

    public static IReadOnlyList<string> Actionable => Ordered;

    /// <summary>Position of an actionable kind within one pass; -1 for an observe-only kind.</summary>
    public static int Priority(string? kind) => kind is null ? -1 : Array.IndexOf(Ordered, kind);
}

/// <summary>Subject keys of the stall detector.</summary>
public static class StallSubjects
{
    /// <summary><c>host:&lt;name&gt;[:role]</c> and <c>container:&lt;host&gt;:&lt;service&gt;</c> name their host.</summary>
    public static string HostOf(string subject)
    {
        var parts = subject.Split(':');
        return parts.Length >= 2 && parts[0] is "host" or "container" ? parts[1] : subject;
    }
}

/// <summary>Why the AI Operator looks at a stall subject.</summary>
public static class StallInterventionTriggers
{
    /// <summary>An open stall event without a pending intervention.</summary>
    public const string Stall = "stall";

    /// <summary>The follow-up check of an applied intervention.</summary>
    public const string FollowUp = "follow-up";

    /// <summary>
    /// A lowered admission that is still in force after its own follow-up
    /// settled (for example it was escalated): it is restored once the host is calm.
    /// </summary>
    public const string Restore = "restore";
}

/// <summary>
/// Stable intervention tokens. They are persisted in receipts and occurrence
/// payloads; never rename one.
/// </summary>
public static class StallInterventionActions
{
    public const string None = "none";
    public const string LowerAdmission = "lower-admission";
    public const string RestoreAdmission = "restore-admission";
    public const string RestartService = "restart-service";
    public const string RerouteProvider = "reroute-provider";
    public const string ReconcileBatchGate = "reconcile-batch-gate";
    public const string RequeueFromDelivery = "requeue-from-delivery";
    public const string RetryEngineStart = "retry-engine-start";
    public const string ConfirmCleared = "confirm-cleared";
    public const string ProposeProviderLogin = "propose-provider-login";
    public const string ProposeReviewToolchain = "propose-review-toolchain";
    public const string ProposeMoveWork = "propose-move-work";
    public const string ProposeRestartService = "propose-restart-service";
    public const string ProposeReleaseGateSlots = "propose-release-gate-slots";
    public const string ProposeDeployFix = "propose-deploy-fix";
    public const string Escalate = "escalate";

    /// <summary>Actions that change the system and therefore count against the rate limit.</summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        LowerAdmission, RestoreAdmission, RestartService, RerouteProvider,
        ReconcileBatchGate, RequeueFromDelivery, RetryEngineStart,
    };

    /// <summary>Actions that change cards; they also count against the per-card bound.</summary>
    public static readonly IReadOnlySet<string> CardTouching = new HashSet<string>(StringComparer.Ordinal)
    {
        ReconcileBatchGate, RequeueFromDelivery,
    };
}

/// <summary>Bounds of the automatic interventions. The host passes its configured values.</summary>
public sealed record StallInterventionLimits(
    int MaxPerHostPerHour = 3,
    int LoopWindowHours = 6,
    int MaxRepeatsPerSubject = 2,
    int MinAdmission = 1,
    int CalmMinutesBeforeRestore = 15,
    double RestoreBelowTargetFraction = 0.8,
    double GateMemberDeadlineHours = 6,
    int MaxCalmWaitHours = 4,
    int MaxActionsPerCard = 1);

/// <summary>
/// A coding host's load and admission at observation time.
/// <see cref="AiLoweringId"/> names an AI Operator reduction that is still in
/// force; <see cref="AiLoweredFrom"/> is the person's baseline it lowered.
/// </summary>
public sealed record StallHostFacts(
    string Host,
    int Cores,
    double? Load1,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double TargetPercent,
    int? Admission,
    int ActiveSlots,
    string? ClientId = null,
    int? AiLoweredFrom = null,
    string? AiLoweringId = null);

/// <summary>
/// The remote gate host's slots. <see cref="OwnBusy"/> slots are held by
/// this Studio process; <see cref="RemoteBusy"/> is what the gate host last
/// reported to a waiting gate run (null when not observed recently).
/// </summary>
public sealed record StallGateSlotFacts(int Total, int OwnBusy, int? RemoteBusy, bool BatchOrGateRunning);

/// <summary>
/// A review daemon, gate worker or engine container that stopped or idles.
/// <see cref="BusySlots"/> is the work it runs now (null when unknown),
/// <see cref="RunningWork"/> an active batch or per-task gate, and
/// <see cref="ClaimableWaiting"/> the waiting items this worker itself can claim.
/// </summary>
public sealed record StallServiceFacts(
    string Host,
    string Role,
    string Service,
    bool Restartable,
    bool StoppedByOperator,
    DateTime? LastSeenUtc,
    int WaitingCount,
    int FreeSlots,
    bool ProviderAuthStalled,
    int? BusySlots = null,
    bool RunningWork = false,
    int ClaimableWaiting = 0,
    StallGateSlotFacts? GateSlots = null);

/// <summary>
/// A provider failure on one host and the hosts that can take its attempts.
/// Only a review host's attempts are routed by provider; a coding host's login
/// is a person's renewal.
/// </summary>
public sealed record StallProviderFacts(
    string Host,
    string Provider,
    string Failure,
    IReadOnlyList<string> HealthyHosts,
    int AffectedAttempts,
    bool AlreadyRerouted,
    bool ReviewHost = false);

/// <summary>
/// One pending batch gate member, frozen with its review attempt and the
/// state of the card the authority key names (null when no card was found).
/// </summary>
public sealed record StallGateMember(string Card, string ReviewAttemptId, string? State);

/// <summary>Which pending batch gate members the AI Operator may drop; the adapter re-checks the same rule at apply time.</summary>
public static class StallGateMembers
{
    /// <summary>
    /// Completed, or positively out of review (Ready, Progress, Backlog,
    /// Archive and the like). Auto Review stays, Human Review and Escalated are
    /// the pilot's own resolution, and an unknown card (null) holds.
    /// </summary>
    public static bool Droppable(string? state)
        => state is not null and not (TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated);
}

/// <summary>
/// One project's batch gate queue. <see cref="OpenBatchId"/> names the batch
/// that would form from the members still in Auto Review and
/// <see cref="OpenBatchWaitHours"/> how long the oldest of them waits; both are
/// null while a batch already runs or nothing remains.
/// </summary>
public sealed record StallGateFacts(
    string Project,
    int PendingCount,
    IReadOnlyList<StallGateMember> Members,
    double OldestPendingHours,
    bool PlanIdle,
    string? OpenBatchId,
    double? OpenBatchWaitHours,
    StallGateSlotFacts? Slots = null);

/// <summary>What a card's next run would start from.</summary>
public static class StallContinuationBases
{
    public const string Missing = "missing";

    /// <summary>A pinned base whose commit is only the start of an earlier attempt: it carries no work.</summary>
    public const string EmptySalvage = "empty-salvage";

    public const string Delivery = "delivery";
    public const string Other = "other";
}

/// <summary>
/// A card of a rate-limited provider. <see cref="BaseKind"/> is one of
/// <see cref="StallContinuationBases"/>; the last delivery is the card's last
/// completed run whose result was reviewed.
/// </summary>
public sealed record StallRequeueCandidate(
    string Card,
    string State,
    string BaseKind,
    bool LastRunRateLimited,
    string? LastDeliveryRef,
    string? LastDeliverySha,
    string? BaseSha = null);

/// <summary>A container that does not run the deployed release.</summary>
public sealed record StallDeployFacts(
    string Host,
    string Service,
    string? ExpectedVersion,
    string? RunningVersion,
    string? StartError,
    IReadOnlyList<string> MissingVariables,
    bool StartRetryAvailable);

/// <summary>An earlier intervention, for the rate limit, the per-card bound and the loop protection.</summary>
public sealed record StallInterventionHistoryEntry(
    string InterventionId,
    string Subject,
    string Host,
    string Action,
    DateTime AppliedUtc,
    string Status,
    IReadOnlyList<string>? Members = null);

/// <summary>The measured effect of an applied intervention at its follow-up check.</summary>
public sealed record StallFollowUpFacts(
    string InterventionId,
    string Action,
    DateTime AppliedUtc,
    DateTime DueUtc,
    bool ConditionActive,
    bool EffectObserved,
    string? EffectEvidence,
    int ChainLength,
    int? AdmissionBefore = null,
    int? AdmissionNow = null,
    double? LoadPerCoreNow = null,
    double? TargetPercent = null,
    DateTime? CalmSinceUtc = null,
    int? ActiveSlotsNow = null);

/// <summary>
/// Frozen facts of one stall subject. Exactly the facts record of
/// <see cref="Kind"/> is set; <see cref="FollowUp"/> is set for the
/// follow-up and restore triggers. <see cref="ObservedAtUtc"/> is the host's
/// clock at collection; the policy never reads a clock.
/// </summary>
public sealed record StallInterventionFacts(
    string Trigger,
    string Kind,
    string Subject,
    DateTime ObservedAtUtc,
    DateTime? SinceUtc,
    IReadOnlyList<string> AffectedCards,
    StallInterventionLimits Limits,
    IReadOnlyList<StallInterventionHistoryEntry> History,
    StallHostFacts? Host = null,
    StallServiceFacts? Service = null,
    StallProviderFacts? Provider = null,
    StallGateFacts? Gate = null,
    IReadOnlyList<StallRequeueCandidate>? Requeue = null,
    StallDeployFacts? Deploy = null,
    string? ClaimReason = null,
    StallFollowUpFacts? FollowUp = null,
    string? CaseId = null);

/// <summary>
/// The plain-language receipt parts and the bounded parameters of one
/// intervention. The receipt says what was observed (with numbers), what was
/// done, why, what changed (before and after) and how to undo it.
/// </summary>
public sealed record StallInterventionPlan(
    string Action,
    AutonomyClass Autonomy,
    string ReasonCode,
    string HostName,
    string Observed,
    string Done,
    string Why,
    string Before,
    string After,
    string Undo,
    int FollowUpMinutes,
    int? TargetAdmission = null,
    int? PreviousAdmission = null,
    string? Service = null,
    string? Provider = null,
    IReadOnlyList<string>? Members = null,
    string? BatchId = null,
    string? Proposal = null,
    string? HostId = null,
    IReadOnlyList<string>? AttemptIds = null)
{
    /// <summary>One readable paragraph for feeds, timelines and the host view.</summary>
    public string Receipt => string.Join(" ", new[] { Observed, Done, Why, Change, Undo }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Change => Before.Length == 0 && After.Length == 0 ? string.Empty
        : $"Before: {Before}. After: {After}.";
}

/// <summary>
/// AGT-3207: what the AI Operator does about an operational stall. A0 and A1
/// interventions are bounded and reversible; A2 interventions are proposals
/// for a person; no A3 action is ever planned. The rate limit per host and
/// hour, the repeat limit per subject and the bound per card keep
/// interventions out of loops: a subject that needed the same action twice in
/// the loop window goes to a person with its receipt chain instead.
/// </summary>
public static class StallInterventionPolicy
{
    public const string AwaitingFollowUp = "awaiting-follow-up";
    public const string HostRateLimit = "host-rate-limit";
    public const string InterventionLoop = "intervention-loop";
    public const string CardBound = "card-bound";
    public const string ObserveOnly = "observe-only";
    public const string ConditionPersists = "condition-persists";
    public const string EffectMissing = "effect-missing";
    public const string ConditionCleared = "condition-cleared";
    public const string WaitingForCalm = "waiting-for-calm";
    public const string Draining = "draining";
    public const string FollowUpPending = "follow-up-pending";
    public const string WaitingForPerson = "waiting-for-person";
    public const string WaitingForLogin = "waiting-for-login";
    public const string FactsUnavailable = "facts-unavailable";
    public const string RestoreOverdue = "restore-overdue";
    public const string NothingToRestore = "nothing-to-restore";

    public static StallInterventionPlan Decide(StallInterventionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.Subject);
        return facts.Trigger switch
        {
            StallInterventionTriggers.FollowUp => FollowUp(facts,
                facts.FollowUp ?? throw new ArgumentException("The follow-up has no facts.")),
            StallInterventionTriggers.Restore => Restore(facts,
                facts.FollowUp ?? throw new ArgumentException("The restore check has no facts.")),
            _ => Guarded(facts, Candidate(facts)),
        };
    }

    /// <summary>Applies the per-subject pending check, the loop limit and the per-host hourly rate limit.</summary>
    private static StallInterventionPlan Guarded(StallInterventionFacts facts, StallInterventionPlan plan)
    {
        if (plan.Action == StallInterventionActions.None) return plan;
        var pending = facts.History.FirstOrDefault(item => item.Subject == facts.Subject
            && StallInterventionStatuses.Pending.Contains(item.Status));
        if (pending is not null)
            return Hold(facts, plan.HostName, AwaitingFollowUp,
                $"Intervention {pending.InterventionId} ({pending.Action}) on {facts.Subject} is {pending.Status} and waits for its check.");
        // A proposal or escalation of the current stall episode waits for a
        // person; asking again on every pass would only add noise.
        var episodeStart = facts.SinceUtc ?? DateTime.MinValue;
        var open = facts.History.LastOrDefault(item => item.Subject == facts.Subject
            && item.AppliedUtc >= episodeStart
            && item.Status is StallInterventionStatuses.Proposed or StallInterventionStatuses.Escalated
                or StallInterventionStatuses.Undone);
        if (open is not null)
            return Hold(facts, plan.HostName, WaitingForPerson,
                $"{open.InterventionId} ({open.Action}) on {facts.Subject} is {open.Status} and waits for a person.");
        if (!StallInterventionActions.Mutating.Contains(plan.Action)) return plan;

        var loopStart = facts.ObservedAtUtc.AddHours(-facts.Limits.LoopWindowHours);
        var repeats = facts.History.Where(item => item.Subject == facts.Subject && item.Action == plan.Action
            && item.AppliedUtc > loopStart && Acted(item)).ToArray();
        if (repeats.Length >= facts.Limits.MaxRepeatsPerSubject)
            return plan with
            {
                Action = StallInterventionActions.Escalate,
                Autonomy = AutonomyClass.A1,
                ReasonCode = InterventionLoop,
                Done = $"I did not {Verb(plan.Action)} again: it already ran {repeats.Length} times on this subject "
                    + $"in the last {facts.Limits.LoopWindowHours} hours ({string.Join(", ", repeats.Select(item => item.InterventionId))}) "
                    + "and the stall came back. A person decides now.",
                Why = "Repeating the same intervention would be a loop.",
                Before = string.Empty,
                After = string.Empty,
                Undo = "Nothing was changed by this step.",
                FollowUpMinutes = 0,
                Members = null,
                AttemptIds = null,
            };

        var hourStart = facts.ObservedAtUtc.AddHours(-1);
        var onHost = facts.History.Count(item => item.Host == plan.HostName && item.AppliedUtc > hourStart && Acted(item));
        return onHost >= facts.Limits.MaxPerHostPerHour
            ? Hold(facts, plan.HostName, HostRateLimit,
                $"{plan.HostName} already had {onHost} interventions in the last hour "
                + $"(limit {facts.Limits.MaxPerHostPerHour}); {Verb(plan.Action)} waits for the next hour.")
            : plan;
    }

    /// <summary>A mutating intervention that ran or may have run; observed and superseded ones changed nothing.</summary>
    private static bool Acted(StallInterventionHistoryEntry item)
        => StallInterventionActions.Mutating.Contains(item.Action) && StallInterventionStatuses.Acted.Contains(item.Status);

    /// <summary>True when the card already had as many card-changing interventions in the loop window as the bound allows.</summary>
    private static bool CardExhausted(StallInterventionFacts facts, string card)
    {
        var loopStart = facts.ObservedAtUtc.AddHours(-facts.Limits.LoopWindowHours);
        return facts.History.Count(item => StallInterventionActions.CardTouching.Contains(item.Action) && Acted(item)
            && item.AppliedUtc > loopStart && item.Members?.Contains(card, StringComparer.Ordinal) == true)
            >= facts.Limits.MaxActionsPerCard;
    }

    private static StallInterventionPlan Candidate(StallInterventionFacts facts) => facts.Kind switch
    {
        StallKinds.HostOverload or StallKinds.MemoryPressure when facts.Host is { } host => Overload(facts, host),
        StallKinds.HostOffline or StallKinds.IdleCapacity when facts.Service is { } service => Service(facts, service),
        StallKinds.ProviderAuthInvalid when facts.Provider is { } provider => ProviderAuth(facts, provider),
        StallKinds.ProviderRateLimited => RateLimited(facts, facts.Requeue ?? []),
        StallKinds.ClaimRefusal => ClaimRefusal(facts),
        StallKinds.BatchGateStuck when facts.Gate is { } gate => Gate(facts, gate),
        StallKinds.DeployDrift when facts.Deploy is { } deploy => Deploy(facts, deploy),
        StallKinds.HostOverload or StallKinds.MemoryPressure or StallKinds.HostOffline or StallKinds.IdleCapacity
            or StallKinds.ProviderAuthInvalid or StallKinds.BatchGateStuck or StallKinds.DeployDrift
            => Hold(facts, StallSubjects.HostOf(facts.Subject), FactsUnavailable,
                $"The {facts.Kind} stall on {facts.Subject} has no current facts (for example the queue already emptied); nothing to do."),
        _ => Hold(facts, StallSubjects.HostOf(facts.Subject), ObserveOnly,
            $"A {facts.Kind} stall stays with the card deciders; the AI Operator only reports it."),
    };

    private static StallInterventionPlan Overload(StallInterventionFacts facts, StallHostFacts host)
    {
        var observed = $"{host.Host} ran " + Load(host) + $" with {host.ActiveSlots} runs active.";
        // The person's admission is the baseline: an admission the AI Operator
        // already lowered is never lowered again without a person.
        if (host.AiLoweringId is { } lowering)
            return Propose(facts, host.Host, StallInterventionActions.ProposeMoveWork, "already-lowered", observed,
                $"I already lowered its admission from {host.AiLoweredFrom?.ToString(CultureInfo.InvariantCulture) ?? "its baseline"} "
                + $"to {host.Admission?.ToString(CultureInfo.InvariantCulture) ?? "a lower value"} ({lowering}) and do not lower it "
                + "further without a person. I propose to move work to a host with headroom or to lower the admission by hand "
                + "(Settings > Capacity).");
        if (host.Admission is not { } admission)
            return Propose(facts, host.Host, StallInterventionActions.ProposeMoveWork, "no-admission-control",
                observed, $"{host.Host} has no coding admission I can lower, so I propose to move work to a host with headroom for longer.");
        var step = Math.Max(1, (int)Math.Round(admission * 0.25, MidpointRounding.AwayFromZero));
        var target = Math.Max(facts.Limits.MinAdmission, admission - step);
        if (target >= admission)
            return Propose(facts, host.Host, StallInterventionActions.ProposeMoveWork, "admission-at-floor",
                observed, $"Admission is already at its floor of {admission}; I propose to move work to another host for longer.");
        return new StallInterventionPlan(StallInterventionActions.LowerAdmission, AutonomyClass.A1,
            facts.Kind, host.Host, observed,
            $"I lowered its admission from {admission} to {target} parallel runs.",
            "Fewer parallel builds bring the load back under the target; the gates and checks on this host stop timing out.",
            $"admission {admission}, {host.ActiveSlots} runs active",
            $"admission {target}; new work waits, running work continues",
            $"Undo restores {admission} (Settings > Capacity, or Undo on this receipt). "
            + "I raise it again myself once the load stays below the target.",
            15, TargetAdmission: target, PreviousAdmission: admission, HostId: host.ClientId);
    }

    private static StallInterventionPlan Service(StallInterventionFacts facts, StallServiceFacts service)
    {
        var name = $"{service.Service} on {service.Host}";
        var observed = facts.Kind == StallKinds.HostOffline
            ? $"{name} " + (service.StoppedByOperator ? "is stopped" : "stopped reporting")
                + (service.LastSeenUtc is { } seen ? $" (last seen {Minutes(facts.ObservedAtUtc - seen)} ago)" : string.Empty)
                + $"; {service.WaitingCount} items wait."
            : $"{name} has {service.FreeSlots} idle slots while {service.WaitingCount} items wait"
                + $" for {Minutes(facts.ObservedAtUtc - (facts.SinceUtc ?? facts.ObservedAtUtc))}.";
        if (service.ProviderAuthStalled)
            return Hold(facts, service.Host, "provider-auth-owns",
                observed + " Its provider login is invalid; the provider intervention handles this host.");
        if (service.GateSlots is { } slots && OrphanedGateSlots(slots))
            return OrphanedSlots(facts, service.Host, slots, observed);
        if (service.StoppedByOperator)
            return Propose(facts, service.Host, StallInterventionActions.ProposeRestartService, "stopped-by-operator", observed,
                $"A person stopped {name}; I do not restart it. I propose to start it again or move its work to another host.")
                with { Service = service.Service };
        if (!service.Restartable)
            return Propose(facts, service.Host, StallInterventionActions.ProposeMoveWork, "not-restartable", observed,
                $"The Operations Server cannot restart {name}; I propose to move its work to another host.");
        // docker compose up --force-recreate ends whatever the container runs:
        // restart only an idle worker that has claimable work waiting.
        if (service.BusySlots is not 0 || service.RunningWork)
            return Propose(facts, service.Host, StallInterventionActions.ProposeRestartService, "running-work",
                observed + (service.BusySlots is { } busy and > 0 ? $" It still runs {busy} items." : string.Empty)
                + (service.RunningWork ? " A batch or per-task gate is active." : string.Empty)
                + (service.BusySlots is null ? " Its running work is unknown." : string.Empty),
                $"A restart recreates {name} and would end its running work. I propose to restart it once that work "
                + "finished, or to check it now.") with { Service = service.Service };
        if (service.ClaimableWaiting <= 0)
            return Propose(facts, service.Host, StallInterventionActions.ProposeRestartService, "nothing-claimable", observed,
                $"None of the waiting items can be claimed by {name} (batch gate members wait for the batch plan, "
                + "review attempts for a host with their capabilities), so a restart would not move them. "
                + "I propose to check why they wait.") with { Service = service.Service };
        return new StallInterventionPlan(StallInterventionActions.RestartService, AutonomyClass.A1,
            facts.Kind, service.Host, observed,
            $"I restarted {name} through the Operations Server.",
            (facts.Kind == StallKinds.HostOffline
                ? "A stopped heartbeat with claimable waiting work is a stuck process"
                : "Free slots with claimable waiting work mean the worker no longer claims")
            + "; it ran no work, so a restart is bounded and keeps the configuration.",
            $"{service.FreeSlots} free slots, 0 busy, {service.ClaimableWaiting} claimable items waiting",
            "the restarted worker claims waiting work again",
            "Nothing to undo: a restart changes no configuration.",
            10, Service: service.Service);
    }

    /// <summary>
    /// After a Studio restart, remote gate workers of the old process can keep
    /// every gate slot (AGT-3237): the gate host reports all slots busy while
    /// this Studio holds none and runs no batch or per-task gate.
    /// </summary>
    private static bool OrphanedGateSlots(StallGateSlotFacts slots)
        => slots.Total > 0 && slots.RemoteBusy is { } busy && busy >= slots.Total
           && slots.OwnBusy == 0 && !slots.BatchOrGateRunning;

    private static StallInterventionPlan OrphanedSlots(StallInterventionFacts facts, string host,
        StallGateSlotFacts slots, string observed)
        => Propose(facts, host, StallInterventionActions.ProposeReleaseGateSlots, "orphaned-gate-workers",
            observed + $" The gate host reports {slots.RemoteBusy} of {slots.Total} gate slots busy (0 free) while this "
            + "Studio holds none and runs no batch or per-task gate.",
            $"Gate workers of an earlier Studio process still hold the slots on {host}. I propose to stop those orphaned "
            + "workers on the gate host; restarting the gate worker would not free them (AGT-3237 adds the automatic release).");

    private static StallInterventionPlan ProviderAuth(StallInterventionFacts facts, StallProviderFacts provider)
    {
        var observed = $"{provider.Provider} login is invalid on {provider.Host}; {provider.AffectedAttempts} attempts are affected.";
        var renew = $"Renew the {provider.Provider} login on {provider.Host} (see the CLI relogin runbook).";
        if (!provider.ReviewHost)
            return Propose(facts, provider.Host, StallInterventionActions.ProposeProviderLogin, "coding-host-login", observed,
                $"{provider.Host} is not a review host, so its attempts are not routed by provider. {renew}")
                with { Provider = provider.Provider };
        if (provider.AlreadyRerouted || provider.HealthyHosts.Count == 0)
            return Propose(facts, provider.Host, StallInterventionActions.ProposeProviderLogin,
                provider.AlreadyRerouted ? "rerouted-login-pending" : "no-healthy-host", observed,
                (provider.AlreadyRerouted ? "Its attempts already go to other hosts. "
                    : $"No other host has a valid {provider.Provider} login. ") + renew) with { Provider = provider.Provider };
        return new StallInterventionPlan(StallInterventionActions.RerouteProvider, AutonomyClass.A1,
            "provider-auth-invalid", provider.Host, observed,
            $"I stopped routing {provider.Provider} attempts to {provider.Host}; "
            + $"{string.Join(", ", provider.HealthyHosts)} take them.",
            "A host with an invalid login cannot finish an attempt; healthy hosts can.",
            $"{provider.Provider} attempts may go to {provider.Host}",
            $"{provider.Provider} attempts go to {string.Join(", ", provider.HealthyHosts)}",
            $"Undo routes {provider.Provider} to {provider.Host} again; it also happens by itself once the login is renewed. Proposal: {renew}",
            20, Provider: provider.Provider, Proposal: renew);
    }

    private static StallInterventionPlan RateLimited(StallInterventionFacts facts,
        IReadOnlyList<StallRequeueCandidate> candidates)
    {
        var host = StallSubjects.HostOf(facts.Subject);
        var eligible = candidates.Where(item => item.State == TaskStates.Ready && item.LastRunRateLimited
            && item.BaseKind is StallContinuationBases.Missing or StallContinuationBases.EmptySalvage
            && item.LastDeliverySha is not null && item.LastDeliveryRef is not null).ToArray();
        var requeue = eligible.Where(item => !CardExhausted(facts, item.Card)).ToArray();
        if (requeue.Length == 0)
            return eligible.Length > 0
                ? Hold(facts, host, CardBound,
                    $"{string.Join(", ", eligible.Select(item => item.Card))} already had a card intervention in the last "
                    + $"{facts.Limits.LoopWindowHours} hours; I do not pin a base on them again.")
                : Hold(facts, host, "wait-for-provider-reset",
                    $"The provider is rate limited on {host}; none of the {candidates.Count} affected cards waits in Ready "
                    + "after a rate-limited run with no base or an empty salvage and a reviewed delivery, so they wait for the provider reset.");
        string BaseText(StallRequeueCandidate item) => item.BaseKind == StallContinuationBases.Missing
            ? "no continuation base" : $"an empty salvage base at {Short(item.BaseSha)}";
        return new StallInterventionPlan(StallInterventionActions.RequeueFromDelivery, AutonomyClass.A1,
            "rate-limit-empty-salvage", host,
            $"{requeue.Length} of the {candidates.Count} affected cards ended their last run on a provider rate limit "
            + $"and wait in Ready: " + string.Join("; ", requeue.Select(item => $"{item.Card} with {BaseText(item)}")) + ".",
            "I pinned each card's last completed, reviewed delivery as its next run's base: "
            + string.Join(", ", requeue.Select(item => $"{item.Card} from {item.LastDeliveryRef} at {Short(item.LastDeliverySha)}")) + ".",
            "Starting without a base or from an empty salvage drops the reviewed work and escalates; the reviewed delivery keeps it.",
            string.Join("; ", requeue.Select(item => $"{item.Card}: {BaseText(item)}")),
            string.Join("; ", requeue.Select(item => $"{item.Card}: base {Short(item.LastDeliverySha)}")),
            "Undo removes the pinned bases again; the next runs then start without a continuation base.",
            60, Members: requeue.Select(item => item.Card).ToArray());
    }

    private static StallInterventionPlan ClaimRefusal(StallInterventionFacts facts)
    {
        var reason = facts.ClaimReason ?? string.Empty;
        var observed = $"{facts.AffectedCards.Count} attempts cannot be claimed: {reason}.";
        if (reason.StartsWith("toolchain:", StringComparison.Ordinal))
            return Propose(facts, "review-hosts", StallInterventionActions.ProposeReviewToolchain, "missing-toolchain",
                observed, $"No review host offers {reason}. I propose to add {reason["toolchain:".Length..]} to the review image.");
        if (reason.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("provider-auth:", StringComparison.Ordinal))
            return Propose(facts, "review-hosts", StallInterventionActions.ProposeProviderLogin, "credential-refusal",
                observed, $"Renew the login that {reason} names on the review hosts.");
        return Hold(facts, "review-hosts", ObserveOnly, observed + " No bounded intervention applies; the Watcher proposal covers it.");
    }

    private static StallInterventionPlan Gate(StallInterventionFacts facts, StallGateFacts gate)
    {
        // Stale is decided positively from the card the authority key names:
        // an unknown card holds, and Human Review or Escalated members stay
        // with the pilot's own resolution.
        var completed = gate.Members.Where(item => item.State == TaskStates.Completed).ToArray();
        var stale = gate.Members.Where(item => item.State != TaskStates.Completed
            && StallGateMembers.Droppable(item.State)).ToArray();
        var withPilot = gate.Members.Count(item => item.State is TaskStates.HumanReview or TaskStates.Escalated);
        var unknown = gate.Members.Count(item => item.State is null);
        var candidates = completed.Concat(stale).DistinctBy(item => item.ReviewAttemptId).ToArray();
        var drop = candidates.Where(item => !CardExhausted(facts, item.Card)).ToArray();
        var close = gate.OpenBatchId is not null && gate.OpenBatchWaitHours >= facts.Limits.GateMemberDeadlineHours;
        var observed = $"The {gate.Project} batch gate has {gate.PendingCount} pending members"
            + (gate.PlanIdle ? " while its plan is idle" : string.Empty)
            + $"; the oldest waits {gate.OldestPendingHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
            + (completed.Length > 0 ? $"; {completed.Length} are already completed" : string.Empty)
            + (stale.Length > 0 ? $"; {stale.Length} left Auto Review ({string.Join(", ", stale.Select(item => $"{item.Card} in {item.State}"))})" : string.Empty)
            + (withPilot > 0 ? $"; {withPilot} wait in Human Review or Escalated for the pilot" : string.Empty)
            + (unknown > 0 ? $"; {unknown} have no readable card and stay" : string.Empty) + ".";
        if (drop.Length == 0 && !close)
        {
            if (gate.Slots is { } slots && OrphanedGateSlots(slots))
                return OrphanedSlots(facts, $"gate:{gate.Project}", slots, observed);
            return candidates.Length > 0
                ? Hold(facts, $"gate:{gate.Project}", CardBound, observed
                    + $" {string.Join(", ", candidates.Select(item => item.Card))} already had a card intervention in the last "
                    + $"{facts.Limits.LoopWindowHours} hours.")
                : Hold(facts, $"gate:{gate.Project}", ObserveOnly,
                    observed + " No member is positively completed or stale and no batch is past its deadline.");
        }
        var done = new List<string>();
        if (drop.Length > 0) done.Add($"I dropped {string.Join(", ", drop.Select(item => item.Card))} from the gate queue");
        if (close) done.Add($"I closed batch {gate.OpenBatchId}, whose members waited past the "
            + $"{facts.Limits.GateMemberDeadlineHours.ToString("0.#", CultureInfo.InvariantCulture)} h deadline");
        return new StallInterventionPlan(StallInterventionActions.ReconcileBatchGate, AutonomyClass.A1,
            "batch-gate-reconcile", $"gate:{gate.Project}", observed, string.Join("; ", done) + ".",
            "Completed or stale members keep the plan from forming an eligible batch; the remaining members can run.",
            $"{gate.PendingCount} pending",
            $"{gate.PendingCount - drop.Length} pending" + (close ? ", the overdue batch closes now and its members run" : string.Empty),
            (drop.Length > 0
                ? "No automatic undo: a dropped member does not come back to the gate queue by itself. "
                : string.Empty)
            + (close ? "Closing the batch early only starts it sooner." : string.Empty),
            15, Members: drop.Select(item => item.Card).Distinct(StringComparer.Ordinal).ToArray(),
            BatchId: close ? gate.OpenBatchId : null, AttemptIds: drop.Select(item => item.ReviewAttemptId).ToArray());
    }

    private static StallInterventionPlan Deploy(StallInterventionFacts facts, StallDeployFacts deploy)
    {
        var name = $"{deploy.Service} on {deploy.Host}";
        var observed = $"{name} runs {deploy.RunningVersion ?? "an unknown version"} instead of {deploy.ExpectedVersion ?? "the deployed release"}"
            + (deploy.StartError is null ? "." : $"; the start failed: {deploy.StartError}.");
        // A known missing value fails every start the same way: that is a
        // person's configuration change, not a retry.
        if (deploy.MissingVariables.Count > 0)
            return Propose(facts, deploy.Host, StallInterventionActions.ProposeDeployFix, "missing-variables", observed,
                $"Set {string.Join(", ", deploy.MissingVariables)} in the host configuration of {deploy.Host}, then deploy again; "
                + "a start retry would fail the same way.") with { Service = deploy.Service };
        if (deploy.StartError is not null && deploy.StartRetryAvailable)
            return new StallInterventionPlan(StallInterventionActions.RetryEngineStart, AutonomyClass.A1,
                "start-failed-retry", deploy.Host, observed,
                $"I started {name} again through the Operations Server, which validates the configuration before it recreates the container.",
                "The old image kept running because the start failed; a validated start uses the current configuration "
                + "and changes nothing while a value is still missing.",
                $"running {deploy.RunningVersion ?? "unknown"}",
                $"expected {deploy.ExpectedVersion ?? "the deployed release"}",
                "Undo is a deploy of the previous release (Deploy > Releases).",
                10, Service: deploy.Service);
        return Propose(facts, deploy.Host, StallInterventionActions.ProposeDeployFix, "deploy-needs-person", observed,
            "Inspect the deployment failure and redeploy the release.") with { Service = deploy.Service };
    }

    private static StallInterventionPlan FollowUp(StallInterventionFacts facts, StallFollowUpFacts check)
    {
        var host = StallSubjects.HostOf(facts.Subject);
        if (!check.EffectObserved)
            return Escalation(facts, host, EffectMissing, check,
                $"The {Verb(check.Action)} of {check.InterventionId} shows no effect"
                + (check.EffectEvidence is null ? "." : $": {check.EffectEvidence}."));
        var lowered = check.Action == StallInterventionActions.LowerAdmission
            && check.AdmissionBefore is { } before && check.AdmissionNow is { } now && now < before;
        if (lowered && !check.ConditionActive)
            return RestoreWhenCalm(facts, host, check, escalateOverdue: true);
        // The reroute keeps attempts moving; the login itself is the person's
        // proposal, so a still invalid login is no reason to escalate again.
        if (check.Action == StallInterventionActions.RerouteProvider && check.ConditionActive)
            return Hold(facts, host, WaitingForLogin,
                $"Attempts avoid {host} since {check.AppliedUtc:HH:mm} UTC; the login renewal waits for a person.");
        if (!check.ConditionActive)
            return new StallInterventionPlan(StallInterventionActions.ConfirmCleared, AutonomyClass.A0,
                ConditionCleared, host,
                $"The stall on {facts.Subject} cleared after {Verb(check.Action)} ({check.InterventionId}).",
                check.Action == StallInterventionActions.RerouteProvider
                    ? $"The login works again, so I route attempts to {host} again and record {check.InterventionId} as effective."
                    : $"I recorded {check.InterventionId} as effective.",
                string.Empty, string.Empty, string.Empty, string.Empty, 0);
        if (facts.ObservedAtUtc < check.DueUtc)
            return Hold(facts, host, FollowUpPending,
                $"{check.InterventionId} is checked at {check.DueUtc:HH:mm} UTC.");
        // A lower admission only stops new work: while more runs are active
        // than it admits, the load cannot drop yet. That is no failure, up to
        // the calm wait limit.
        if (lowered && check.ActiveSlotsNow is { } active && active > check.AdmissionNow
            && facts.ObservedAtUtc - check.DueUtc < TimeSpan.FromHours(facts.Limits.MaxCalmWaitHours))
            return Hold(facts, host, Draining,
                $"{host} still runs {active} runs above the lowered admission of {check.AdmissionNow}; running work continues "
                + "and the load drops as it finishes.");
        return Escalation(facts, host, ConditionPersists, check,
            $"The stall on {facts.Subject} is still active "
            + $"{Minutes(facts.ObservedAtUtc - check.AppliedUtc)} after {Verb(check.Action)} ({check.InterventionId}).");
    }

    /// <summary>
    /// A lowered admission whose own follow-up already settled (escalated to a
    /// person) stays in force; it is restored once the host is calm, and the
    /// person may still undo it at any time.
    /// </summary>
    private static StallInterventionPlan Restore(StallInterventionFacts facts, StallFollowUpFacts check)
    {
        var host = StallSubjects.HostOf(facts.Subject);
        if (check.AdmissionBefore is not { } before || check.AdmissionNow is not { } now || now >= before)
            return Hold(facts, host, NothingToRestore,
                $"The admission of {host} reads {check.AdmissionNow?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; "
                + $"{check.InterventionId} has nothing left to restore.");
        if (check.ConditionActive)
            return Hold(facts, host, WaitingForCalm,
                $"{host} is still overloaded; its admission stays at {now} ({check.InterventionId}) until the load is back.");
        return RestoreWhenCalm(facts, host, check, escalateOverdue: false);
    }

    private static StallInterventionPlan RestoreWhenCalm(StallInterventionFacts facts, string host,
        StallFollowUpFacts check, bool escalateOverdue)
    {
        var before = check.AdmissionBefore!.Value;
        var now = check.AdmissionNow!.Value;
        var calmEnough = check.CalmSinceUtc is { } calm
            && facts.ObservedAtUtc - calm >= TimeSpan.FromMinutes(facts.Limits.CalmMinutesBeforeRestore)
            && check.LoadPerCoreNow is { } load && check.TargetPercent is { } target
            && load * 100 < target * facts.Limits.RestoreBelowTargetFraction;
        if (!calmEnough && escalateOverdue
            && facts.ObservedAtUtc - check.DueUtc >= TimeSpan.FromHours(facts.Limits.MaxCalmWaitHours))
            return Escalation(facts, host, RestoreOverdue, check,
                $"{host} has not reported a calm load for {facts.Limits.MaxCalmWaitHours} hours after {check.InterventionId}; "
                + $"its admission is still lowered to {now} instead of {before}.");
        if (!calmEnough)
            return Hold(facts, host, WaitingForCalm,
                $"{host} is below its overload threshold; admission stays at {now} until the load stays "
                + $"under {Percent(check.TargetPercent * facts.Limits.RestoreBelowTargetFraction)} for "
                + $"{facts.Limits.CalmMinutesBeforeRestore} minutes.");
        return new StallInterventionPlan(StallInterventionActions.RestoreAdmission, AutonomyClass.A1,
            "load-recovered", host,
            $"{host} runs at {Percent(check.LoadPerCoreNow * 100)} load per core, under its "
            + $"{Percent(check.TargetPercent)} target since {Minutes(facts.ObservedAtUtc - check.CalmSinceUtc!.Value)}.",
            $"I raised its admission from {now} back to {before} parallel runs.",
            $"The load is back; the reduction of {check.InterventionId} is no longer needed.",
            $"admission {now}", $"admission {before}",
            $"{before} is your own value again; to lower it, set it in Settings > Capacity.",
            10, TargetAdmission: before, PreviousAdmission: now);
    }

    private static StallInterventionPlan Escalation(StallInterventionFacts facts, string host, string reason,
        StallFollowUpFacts check, string observed)
        => new(StallInterventionActions.Escalate, AutonomyClass.A1, reason, host, observed,
            $"I handed the event to a person with the receipt chain ({check.ChainLength} receipts).",
            "The automatic intervention did not clear the condition in the expected time; repeating it would be a loop.",
            string.Empty, string.Empty, "Nothing was changed by this step.", 0);

    private static StallInterventionPlan Propose(StallInterventionFacts facts, string host, string action,
        string reason, string observed, string proposal)
        => new(action, AutonomyClass.A2, reason, host, observed,
            "I prepared a proposal for a person; nothing was changed.", proposal,
            string.Empty, string.Empty, "Nothing to undo.", 0, Proposal: proposal);

    private static StallInterventionPlan Hold(StallInterventionFacts facts, string host, string reason, string detail)
        => new(StallInterventionActions.None, AutonomyClass.A0, reason, host, detail,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0);

    private static string Load(StallHostFacts host)
    {
        var parts = new List<string>();
        if (host.Load1 is { } load && host.Cores > 0)
            parts.Add($"with load {load.ToString("0.#", CultureInfo.InvariantCulture)} on {host.Cores} cores");
        if (host.CpuPercent is { } cpu) parts.Add($"at {Percent(cpu)} CPU");
        if (host.MemoryUsedPercent is { } memory) parts.Add($"at {Percent(memory)} memory");
        parts.Add($"against a {Percent(host.TargetPercent)} target");
        return string.Join(", ", parts);
    }

    private static string Verb(string action) => action switch
    {
        StallInterventionActions.LowerAdmission => "lowering the admission",
        StallInterventionActions.RestoreAdmission => "restoring the admission",
        StallInterventionActions.RestartService => "the restart",
        StallInterventionActions.RerouteProvider => "the provider rerouting",
        StallInterventionActions.ReconcileBatchGate => "the batch gate reconciliation",
        StallInterventionActions.RequeueFromDelivery => "the requeue from the last delivery",
        StallInterventionActions.RetryEngineStart => "the start retry",
        _ => action,
    };

    private static string Percent(double? value)
        => value is { } number ? number.ToString("0", CultureInfo.InvariantCulture) + " %" : "unknown";

    private static string Minutes(TimeSpan span)
        => span.TotalMinutes >= 120
            ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : $"{Math.Max(0, (int)span.TotalMinutes)} min";

    private static string Short(string? sha) => sha is { Length: > 9 } ? sha[..9] : sha ?? string.Empty;

}

/// <summary>Lifecycle of one intervention record.</summary>
public static class StallInterventionStatuses
{
    /// <summary>
    /// Written before any side effect (write-ahead). A record that stays here
    /// was interrupted between its action and its settlement; it is never
    /// repeated, only measured and settled.
    /// </summary>
    public const string Applying = "applying";

    /// <summary>Applied; the follow-up check is pending.</summary>
    public const string Applied = "applied";
    public const string Verified = "verified";
    public const string Escalated = "escalated";
    public const string Proposed = "proposed";
    public const string Undone = "undone";
    public const string Failed = "failed";

    /// <summary>A person or another process changed the value first; nothing was done.</summary>
    public const string Superseded = "superseded";

    /// <summary>Observe mode or a switched-off action: the receipt says what would have been done.</summary>
    public const string Observed = "observed";

    /// <summary>Statuses that hold the subject until the check settles them.</summary>
    public static readonly IReadOnlySet<string> Pending = new HashSet<string>(StringComparer.Ordinal) { Applying, Applied };

    /// <summary>Statuses of an action that ran or may have run.</summary>
    public static readonly IReadOnlySet<string> Acted = new HashSet<string>(StringComparer.Ordinal)
    {
        Applying, Applied, Verified, Escalated, Undone,
    };
}
