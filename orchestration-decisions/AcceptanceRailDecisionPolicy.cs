namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Facts of one Human Review or Escalated card for the platform acceptance
/// rail. The authority derives them from the card, its Git-derived
/// integration status and the rail's receipts.
/// </summary>
/// <param name="State">The card's lane.</param>
/// <param name="ExplicitlyHeld">The card id, key or a tag is on the rail's hold list.</param>
/// <param name="DecisionHeld">The card is explicitly held or waits for a person's decision.</param>
/// <param name="IntegrationRequired">The card's acceptance requires an integrated code delivery.</param>
/// <param name="Merged">The delivery is merged into the target (pushed or merged locally).</param>
/// <param name="VerificationPermitsCompletion">The merge carries gate evidence that permits completion.</param>
/// <param name="RebaseRecoveryAvailable">The integration was skipped on a conflict that a rebase can recover.</param>
/// <param name="ConflictFailureClass">The failure class of a conflict-skipped integration, or null.</param>
/// <param name="ConflictRequeues">Rebase recoveries already spent on this card.</param>
/// <param name="MaxRequeues">The rebase recovery budget.</param>
/// <param name="InfrastructureRequeues">Infrastructure or quota replays already spent on this card.</param>
/// <param name="MaxInfrastructureRequeues">The infrastructure replay budget.</param>
/// <param name="InfrastructureReplayDue">The backoff of the next replay (<see cref="AcceptanceRailBackoff"/>) has run out.</param>
/// <param name="DeliveryChainOwns">
/// A Human Review card of the guarded delivery chain whose delivery is not
/// integrated: the delivery reconciler returns it to integration, so it is not
/// an operator review decision for the rail to consume.
/// </param>
public sealed record AcceptanceRailOperatorFacts(
    string State,
    bool ExplicitlyHeld,
    bool DecisionHeld,
    bool IntegrationRequired,
    bool Merged,
    bool VerificationPermitsCompletion,
    bool RebaseRecoveryAvailable,
    RunFailureClass? ConflictFailureClass,
    int ConflictRequeues,
    int MaxRequeues,
    int InfrastructureRequeues,
    int MaxInfrastructureRequeues,
    bool InfrastructureReplayDue,
    bool DeliveryChainOwns = false);

public enum AcceptanceRailVerdictAction
{
    Ignore,
    Accept,

    /// <summary>Rebase recovery: the delivery itself must change before it can integrate.</summary>
    Requeue,

    /// <summary>Replay after a host or account fault; the delivery is unchanged (AGT-2749).</summary>
    RequeueInfrastructure,

    Escalate,
}

public sealed record AcceptanceRailVerdict(AcceptanceRailVerdictAction Action, string Reason);

public static class AcceptanceRailReasons
{
    public const string DeliveryChainOwned = "delivery-chain-owned";
    public const string OutsideRailLanes = "outside-rail-lanes";
    public const string OperatorHold = "operator-hold";
    public const string NoCodeAcceptance = "no-code-acceptance";
    public const string GitDerivedIntegrated = "git-derived-integrated";
    public const string IntegratedUnverified = "integrated-unverified";
    public const string RecoverableConflict = "recoverable-integration-conflict";
    public const string RequeueBudgetExhausted = "integration-requeue-budget-exhausted";
    public const string InfrastructureBudgetExhausted = "infrastructure-requeue-budget-exhausted";
    public const string InfrastructureBackoff = "infrastructure-backoff";
    public const string NotRecoverable = "not-recoverable";
}

/// <summary>
/// Pure rule of the platform-owned acceptance rail (formerly
/// <c>AcceptanceRailPolicy.Decide</c> in the monolith). It accepts only
/// Git-derived integrated coding deliveries, requeues typed rebase-recoverable
/// integration failures, and replays failures the shared taxonomy attributes to
/// the host or the provider account instead of parking them (AGT-2749).
/// </summary>
public static class AcceptanceRailDecisionPolicy
{
    public static AcceptanceRailVerdict Decide(AcceptanceRailOperatorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.DeliveryChainOwns)
            return Ignore(AcceptanceRailReasons.DeliveryChainOwned);
        if (facts.State is not (TaskStates.HumanReview or TaskStates.Escalated))
            return Ignore(AcceptanceRailReasons.OutsideRailLanes);
        if (facts.ExplicitlyHeld)
            return Ignore(AcceptanceRailReasons.OperatorHold);
        if (!facts.IntegrationRequired)
            return Ignore(AcceptanceRailReasons.NoCodeAcceptance);

        // AGT-2849: merged-locally counts; the push is the push backstop's work.
        // AGT-3002: containment without gate evidence is not a reason to accept.
        if (facts.State == TaskStates.HumanReview && facts.Merged)
            return facts.VerificationPermitsCompletion
                ? new(AcceptanceRailVerdictAction.Accept, AcceptanceRailReasons.GitDerivedIntegrated)
                : Ignore(AcceptanceRailReasons.IntegratedUnverified);

        if (facts.RebaseRecoveryAvailable)
        {
            if (facts.DecisionHeld)
                return Ignore(AcceptanceRailReasons.OperatorHold);
            return Math.Max(0, facts.ConflictRequeues) < facts.MaxRequeues
                ? new(AcceptanceRailVerdictAction.Requeue, AcceptanceRailReasons.RecoverableConflict)
                : new(AcceptanceRailVerdictAction.Escalate, AcceptanceRailReasons.RequeueBudgetExhausted);
        }

        // A generic Human Review decision marker must not suppress recovery of
        // a delivery whose review already passed; genuine decision blockers
        // still hold every non-conflict path.
        if (facts.DecisionHeld)
            return Ignore(AcceptanceRailReasons.OperatorHold);

        // AGT-2749: a host or account fault says nothing about the reviewed
        // change. Replay it, bounded and backed off, and escalate only once the
        // replay budget is gone.
        if (facts.ConflictFailureClass is { } failureClass && AcceptanceRailBackoff.Requeueable(failureClass))
        {
            if (Math.Max(0, facts.InfrastructureRequeues) >= facts.MaxInfrastructureRequeues)
                return new(AcceptanceRailVerdictAction.Escalate, AcceptanceRailReasons.InfrastructureBudgetExhausted);
            if (!facts.InfrastructureReplayDue)
                return Ignore(AcceptanceRailReasons.InfrastructureBackoff);
            return new(AcceptanceRailVerdictAction.RequeueInfrastructure,
                $"requeueable-{failureClass.ToString().ToLowerInvariant()}-failure");
        }

        return Ignore(AcceptanceRailReasons.NotRecoverable);
    }

    private static AcceptanceRailVerdict Ignore(string reason) => new(AcceptanceRailVerdictAction.Ignore, reason);
}

/// <summary>
/// Replay schedule of the acceptance rail. The authority evaluates it to
/// derive the <see cref="AcceptanceRailOperatorFacts.InfrastructureReplayDue"/>
/// fact; a deterministic fact keeps one occurrence per unchanged card.
/// </summary>
public static class AcceptanceRailBackoff
{
    /// <summary>First infrastructure backoff step, doubled per further retry.</summary>
    public const int BaseSeconds = 60;

    /// <summary>Upper bound of the doubled backoff, so a long budget cannot park a card for hours.</summary>
    public const int CeilingSeconds = 1800;

    public static bool Requeueable(RunFailureClass failureClass)
        => failureClass is RunFailureClass.Infrastructure or RunFailureClass.Quota;

    /// <summary>
    /// Remaining wait before the rail may replay this card, measured from
    /// <paramref name="now"/>. Zero means "replay now".
    /// <list type="bullet">
    /// <item>A known quota reset is an absolute instant, so it also holds the
    /// very first replay: retrying before the account resets only burns the
    /// budget.</item>
    /// <item>Everything else backs off from the previous replay, doubling per
    /// attempt up to a ceiling, so a host that stays broken is not hammered.</item>
    /// </list>
    /// Pure: same inputs, same wait.
    /// </summary>
    public static TimeSpan Remaining(
        int attempt,
        RunFailureClass failureClass,
        DateTimeOffset? lastRequeueAt,
        DateTimeOffset? quotaResetAt,
        DateTimeOffset now)
    {
        if (failureClass == RunFailureClass.Quota && quotaResetAt is { } resetAt)
            return resetAt > now ? resetAt - now : TimeSpan.Zero;

        var spent = Math.Max(0, attempt);
        if (spent == 0 || lastRequeueAt is not { } last) return TimeSpan.Zero;

        var seconds = Math.Min(BaseSeconds * Math.Pow(2, Math.Min(spent - 1, 16)), CeilingSeconds);
        var due = last + TimeSpan.FromSeconds(seconds);
        return due > now ? due - now : TimeSpan.Zero;
    }
}
