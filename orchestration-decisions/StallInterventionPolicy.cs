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
    public const string Escalate = "escalate";

    /// <summary>Actions that change the system and therefore count against the rate limit.</summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        LowerAdmission, RestoreAdmission, RestartService, RerouteProvider,
        ReconcileBatchGate, RequeueFromDelivery, RetryEngineStart,
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
    int MaxCalmWaitHours = 4);

/// <summary>A coding host's load and admission at observation time.</summary>
public sealed record StallHostFacts(
    string Host,
    int Cores,
    double? Load1,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double TargetPercent,
    int? Admission,
    int ActiveSlots);

/// <summary>A review daemon, gate worker or engine container that stopped or idles.</summary>
public sealed record StallServiceFacts(
    string Host,
    string Role,
    string Service,
    bool Restartable,
    bool StoppedByOperator,
    DateTime? LastSeenUtc,
    int WaitingCount,
    int FreeSlots,
    bool ProviderAuthStalled);

/// <summary>A provider failure on one host and the hosts that can take its attempts.</summary>
public sealed record StallProviderFacts(
    string Host,
    string Provider,
    string Failure,
    IReadOnlyList<string> HealthyHosts,
    int AffectedAttempts,
    bool AlreadyRerouted);

/// <summary>
/// One project's batch gate queue. <see cref="OpenBatchId"/> names the batch
/// that would form from the remaining eligible members and
/// <see cref="OpenBatchWaitHours"/> how long its oldest member waits; both are
/// null while a batch already runs or nothing remains.
/// </summary>
public sealed record StallGateFacts(
    string Project,
    int PendingCount,
    IReadOnlyList<string> CompletedMembers,
    IReadOnlyList<string> StaleMembers,
    double OldestPendingHours,
    bool PlanIdle,
    string? OpenBatchId,
    double? OpenBatchWaitHours);

/// <summary>A card whose run ended on a provider rate limit.</summary>
public sealed record StallRequeueCandidate(
    string Card,
    string State,
    bool SalvageEmpty,
    string? LastDeliveryRef,
    string? LastDeliverySha);

/// <summary>A container that does not run the deployed release.</summary>
public sealed record StallDeployFacts(
    string Host,
    string Service,
    string? ExpectedVersion,
    string? RunningVersion,
    string? StartError,
    IReadOnlyList<string> MissingVariables,
    bool StartRetryAvailable);

/// <summary>An earlier intervention, for the rate limit and loop protection.</summary>
public sealed record StallInterventionHistoryEntry(
    string InterventionId,
    string Subject,
    string Host,
    string Action,
    DateTime AppliedUtc,
    string Status);

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
    DateTime? CalmSinceUtc = null);

/// <summary>
/// Frozen facts of one stall subject. Exactly the facts record of
/// <see cref="Kind"/> is set; <see cref="FollowUp"/> is set for the
/// follow-up trigger. <see cref="ObservedAtUtc"/> is the host's clock at
/// collection; the policy never reads a clock.
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
    StallFollowUpFacts? FollowUp = null);

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
    string? Proposal = null)
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
/// hour and the repeat limit per subject keep interventions out of loops: a
/// subject that needed the same action twice in the loop window goes to a
/// person with its receipt chain instead.
/// </summary>
public static class StallInterventionPolicy
{
    public const string AwaitingFollowUp = "awaiting-follow-up";
    public const string HostRateLimit = "host-rate-limit";
    public const string InterventionLoop = "intervention-loop";
    public const string ObserveOnly = "observe-only";
    public const string ConditionPersists = "condition-persists";
    public const string EffectMissing = "effect-missing";
    public const string ConditionCleared = "condition-cleared";
    public const string WaitingForCalm = "waiting-for-calm";
    public const string FollowUpPending = "follow-up-pending";
    public const string WaitingForPerson = "waiting-for-person";
    public const string WaitingForLogin = "waiting-for-login";
    public const string FactsUnavailable = "facts-unavailable";
    public const string RestoreOverdue = "restore-overdue";

    public static StallInterventionPlan Decide(StallInterventionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts.Subject);
        return facts.Trigger == StallInterventionTriggers.FollowUp
            ? FollowUp(facts, facts.FollowUp ?? throw new ArgumentException("The follow-up has no facts."))
            : Guarded(facts, Candidate(facts));
    }

    /// <summary>Applies the per-subject pending check, the loop limit and the per-host hourly rate limit.</summary>
    private static StallInterventionPlan Guarded(StallInterventionFacts facts, StallInterventionPlan plan)
    {
        if (plan.Action == StallInterventionActions.None) return plan;
        var pending = facts.History.FirstOrDefault(item => item.Subject == facts.Subject
            && item.Status == StallInterventionStatuses.Applied);
        if (pending is not null)
            return Hold(facts, plan.HostName, AwaitingFollowUp,
                $"Intervention {pending.InterventionId} ({pending.Action}) on {facts.Subject} waits for its follow-up check.");
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
            && item.AppliedUtc > loopStart && StallInterventionActions.Mutating.Contains(item.Action)).ToArray();
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
            };

        var hourStart = facts.ObservedAtUtc.AddHours(-1);
        var onHost = facts.History.Count(item => item.Host == plan.HostName && item.AppliedUtc > hourStart
            && StallInterventionActions.Mutating.Contains(item.Action));
        return onHost >= facts.Limits.MaxPerHostPerHour
            ? Hold(facts, plan.HostName, HostRateLimit,
                $"{plan.HostName} already had {onHost} interventions in the last hour "
                + $"(limit {facts.Limits.MaxPerHostPerHour}); {Verb(plan.Action)} waits for the next hour.")
            : plan;
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
        var observed = $"{host.Host} ran " + Load(host) + ".";
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
            15, TargetAdmission: target, PreviousAdmission: admission);
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
        if (service.StoppedByOperator || !service.Restartable)
            return Propose(facts, service.Host, StallInterventionActions.ProposeMoveWork,
                service.StoppedByOperator ? "stopped-by-operator" : "not-restartable", observed,
                service.StoppedByOperator
                    ? $"A person stopped {name}; I do not restart it. I propose to start it again or move its work to another host."
                    : $"The Operations Server cannot restart {name}; I propose to move its work to another host.");
        return new StallInterventionPlan(StallInterventionActions.RestartService, AutonomyClass.A1,
            facts.Kind, service.Host, observed,
            $"I restarted {name} through the Operations Server.",
            facts.Kind == StallKinds.HostOffline
                ? "A stopped heartbeat with waiting work is a stuck process; a restart is bounded and keeps the configuration."
                : "Free slots with eligible waiting work mean the worker no longer claims; a restart is bounded and keeps the configuration.",
            $"{service.FreeSlots} free slots, {service.WaitingCount} waiting",
            "the restarted worker claims waiting work again",
            "Nothing to undo: a restart changes no configuration.",
            10, Service: service.Service);
    }

    private static StallInterventionPlan ProviderAuth(StallInterventionFacts facts, StallProviderFacts provider)
    {
        var observed = $"{provider.Provider} login is invalid on {provider.Host}; {provider.AffectedAttempts} attempts are affected.";
        var renew = $"Renew the {provider.Provider} login on {provider.Host} (see the CLI relogin runbook).";
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
        var requeue = candidates.Where(item => item.State == TaskStates.Ready && item.SalvageEmpty
            && item.LastDeliverySha is not null && item.LastDeliveryRef is not null).ToArray();
        if (requeue.Length == 0)
            return Hold(facts, host, "wait-for-provider-reset",
                $"The provider is rate limited on {host}; none of the {candidates.Count} affected cards waits in Ready "
                + "with an empty salvage and an earlier delivery, so they wait for the provider reset.");
        var names = string.Join(", ", requeue.Select(item => $"{item.Card} from {item.LastDeliveryRef} at {Short(item.LastDeliverySha)}"));
        return new StallInterventionPlan(StallInterventionActions.RequeueFromDelivery, AutonomyClass.A1,
            "rate-limit-empty-salvage", host,
            $"A provider rate limit on {host} ended {requeue.Length} runs with an empty salvage.",
            $"I requeued {names}.",
            "Restarting from an empty salvage loses the delivered work and escalates; the last real delivery keeps it.",
            $"{requeue.Length} runs would start from an empty salvage",
            $"{requeue.Length} runs start from their last delivery once the provider resets",
            "Undo clears the continuation base; the next run then starts from the card's base branch.",
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
        var drop = gate.CompletedMembers.Concat(gate.StaleMembers).Distinct(StringComparer.Ordinal).ToArray();
        var close = gate.OpenBatchId is not null && gate.OpenBatchWaitHours >= facts.Limits.GateMemberDeadlineHours;
        var observed = $"The {gate.Project} batch gate has {gate.PendingCount} pending members"
            + (gate.PlanIdle ? " while its plan is idle" : string.Empty)
            + $"; the oldest waits {gate.OldestPendingHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
            + (gate.CompletedMembers.Count > 0 ? $"; {gate.CompletedMembers.Count} are already completed" : string.Empty)
            + (gate.StaleMembers.Count > 0 ? $"; {gate.StaleMembers.Count} left Auto Review" : string.Empty) + ".";
        if (drop.Length == 0 && !close)
            return Hold(facts, $"gate:{gate.Project}", ObserveOnly,
                observed + " No member is completed or stale and no batch is past its deadline.");
        var done = new List<string>();
        if (drop.Length > 0) done.Add($"I dropped {string.Join(", ", drop)} from the gate queue");
        if (close) done.Add($"I closed batch {gate.OpenBatchId}, whose members waited past the "
            + $"{facts.Limits.GateMemberDeadlineHours.ToString("0.#", CultureInfo.InvariantCulture)} h deadline");
        return new StallInterventionPlan(StallInterventionActions.ReconcileBatchGate, AutonomyClass.A1,
            "batch-gate-reconcile", $"gate:{gate.Project}", observed, string.Join("; ", done) + ".",
            "Completed or stale members keep the plan from forming an eligible batch; the remaining members can run.",
            $"{gate.PendingCount} pending",
            $"{gate.PendingCount - drop.Length} pending" + (close ? ", the overdue batch closed and its members queue again" : string.Empty),
            drop.Length > 0
                ? "Undo queues the dropped members again if their cards are still in Auto Review."
                : "Nothing to undo: a closed batch re-queues its members.",
            15, Members: drop, BatchId: close ? gate.OpenBatchId : null);
    }

    private static StallInterventionPlan Deploy(StallInterventionFacts facts, StallDeployFacts deploy)
    {
        var name = $"{deploy.Service} on {deploy.Host}";
        var observed = $"{name} runs {deploy.RunningVersion ?? "an unknown version"} instead of {deploy.ExpectedVersion ?? "the deployed release"}"
            + (deploy.StartError is null ? "." : $"; the start failed: {deploy.StartError}.");
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
        var missing = deploy.MissingVariables.Count > 0
            ? $"Set {string.Join(", ", deploy.MissingVariables)} in the host configuration, then retry the deploy."
            : "Inspect the deployment failure and redeploy the release.";
        return Propose(facts, deploy.Host, StallInterventionActions.ProposeMoveWork, "deploy-needs-person", observed, missing)
            with { Service = deploy.Service };
    }

    private static StallInterventionPlan FollowUp(StallInterventionFacts facts, StallFollowUpFacts check)
    {
        var host = StallSubjects.HostOf(facts.Subject);
        if (!check.EffectObserved)
            return Escalation(facts, host, EffectMissing, check,
                $"The {Verb(check.Action)} of {check.InterventionId} shows no effect"
                + (check.EffectEvidence is null ? "." : $": {check.EffectEvidence}."));
        if (check.Action == StallInterventionActions.LowerAdmission
            && check.AdmissionBefore is { } before && check.AdmissionNow is { } now && now < before
            && !check.ConditionActive)
        {
            var calmEnough = check.CalmSinceUtc is { } calm
                && facts.ObservedAtUtc - calm >= TimeSpan.FromMinutes(facts.Limits.CalmMinutesBeforeRestore)
                && check.LoadPerCoreNow is { } load && check.TargetPercent is { } target
                && load * 100 < target * facts.Limits.RestoreBelowTargetFraction;
            if (!calmEnough && facts.ObservedAtUtc - check.DueUtc >= TimeSpan.FromHours(facts.Limits.MaxCalmWaitHours))
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
                $"Undo sets {now} again (Settings > Capacity, or Undo on this receipt).",
                10, TargetAdmission: before, PreviousAdmission: now);
        }
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
        return Escalation(facts, host, ConditionPersists, check,
            $"The stall on {facts.Subject} is still active "
            + $"{Minutes(facts.ObservedAtUtc - check.AppliedUtc)} after {Verb(check.Action)} ({check.InterventionId}).");
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
    /// <summary>Applied; the follow-up check is pending.</summary>
    public const string Applied = "applied";
    public const string Verified = "verified";
    public const string Escalated = "escalated";
    public const string Proposed = "proposed";
    public const string Undone = "undone";
    public const string Failed = "failed";
    public const string Superseded = "superseded";
}
