namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Stable decision reasons. A refused operator retry answers with the reason
/// the policy refused it, so these are constants the endpoint, the service, and
/// the tests share instead of inline strings that drift apart.
/// </summary>
public static class GateEnvironmentRetryReasons
{
    public const string Disabled = "gate-environment-retry-disabled";
    public const string OutsideLanes = "outside-retry-lanes";
    public const string NoCodeDelivery = "no-code-delivery";
    public const string AcceptanceIntegrationInFlight = "acceptance-integration-in-flight";
    public const string NotAGateEnvironmentFailure = "not-a-gate-environment-failure";
    public const string NoPassedReview = "no-passed-review-for-delivery-sha";
    public const string BudgetExhausted = "gate-environment-retry-budget-exhausted";
    public const string DueWithoutAnchor = "gate-environment-retry-due-without-anchor";
    public const string Backoff = "gate-environment-backoff";
    public const string Due = "gate-environment-retry-due";
    public const string AlreadyParked = "gate-environment-retry-already-parked";
    public const string HostQuarantined = "gate-host-quarantined";
    public const string HostRecovered = "gate-host-repair-recovered";
    public const string HumanHold = "gate-environment-human-hold";

    /// <summary>
    /// AGT-3792: a transport failure is replayable, but not by the timed
    /// ladder. The gate runner already retried the same exact subject; the
    /// outage probe returns the card once the gate host answers again.
    /// </summary>
    public const string TransportAwaitsGateHost = "gate-transport-awaits-gate-host";

    /// <summary>
    /// Not a policy verdict: the service refuses a second concurrent replay of
    /// the same card. It shares the vocabulary so callers switch on one set.
    /// </summary>
    public const string AlreadyRunning = "gate-environment-retry-already-running";

    /// <summary>
    /// Operator-facing sentence for a reason the policy refused a retry on.
    /// The slug travels next to it on the wire; this is what the card shows.
    /// </summary>
    public static string Explain(string reason) => reason switch
    {
        Disabled => "Gate-environment integration retries are disabled for this server.",
        OutsideLanes =>
            $"Retrying an integration requires a task in {TaskStates.AutoReview}, {TaskStates.HumanReview}, or {TaskStates.Escalated}.",
        NoCodeDelivery => "This task delivers no code, so there is no integration to retry.",
        AcceptanceIntegrationInFlight =>
            "An acceptance integration for this task is already running; wait for it to settle before retrying.",
        NotAGateEnvironmentFailure => "This task has no integration failure that can be replayed for the same delivery.",
        NoPassedReview => "The current delivery has no settled passed review to reuse.",
        _ => "This task is not in a gate-environment retry state.",
    };
}

public enum GateEnvironmentRetryAction
{
    /// <summary>The card is not in a gate-environment retry state at all.</summary>
    Ignore,

    /// <summary>Eligible, but the current rung has not elapsed yet.</summary>
    Wait,

    /// <summary>Replay the integration now, reusing the passed review.</summary>
    Retry,

    /// <summary>The ladder is spent; the card must show a parked reason.</summary>
    Park,
}

/// <param name="AttemptsSpent">Automatic rungs already spent on this delivery SHA.</param>
/// <param name="MaxAttempts">Rungs the ladder offers in total.</param>
/// <param name="Wait">Remaining wait before the next rung is due; zero when due now.</param>
/// <param name="DueAt">Instant the next rung becomes due, or null when there is no next rung.</param>
public sealed record GateEnvironmentRetryDecision(
    GateEnvironmentRetryAction Action,
    string Reason,
    int AttemptsSpent,
    int MaxAttempts,
    TimeSpan Wait,
    DateTimeOffset? DueAt);

/// <summary>
/// Frozen facts for one delivered card. The authority reads them once; the
/// evaluation instant and the durable receipts are values, never a clock.
/// </summary>
/// <param name="Enabled">The ladder is enabled on this server.</param>
/// <param name="InRetryLane">The card rests in Auto Review, Human Review, or Escalated.</param>
/// <param name="RequiresIntegration">The card delivers code that has to integrate.</param>
/// <param name="AcceptanceIntegrationInFlight">
/// A merge holds the in-flight gate journal, or an acceptance transaction
/// outside Auto Review is integrating the card.
/// </param>
/// <param name="IsGateEnvironmentFailure">
/// The latest integration failed on the gate environment, the gate transport
/// included, or on a missing server checkout that now exists (AGT-3810,
/// operator replay only).
/// </param>
/// <param name="ReviewPassed">The latest review for the delivery SHA ended in Pass.</param>
/// <param name="AttemptsSpent">Automatic rungs already spent on this delivery SHA.</param>
/// <param name="Backoff">The bounded ladder.</param>
/// <param name="LastAttemptAt">Instant of the last retry, or null.</param>
/// <param name="FailedAt">Instant the gate-environment failure was recorded, or null.</param>
/// <param name="Now">Evaluation instant.</param>
/// <param name="TransportFailure">
/// AGT-3792: the gate-environment failure is the gate transport (the gate host
/// did not answer). An operator or recovery replay may repeat it; the timed
/// ladder leaves it to the outage probe.
/// </param>
public sealed record GateEnvironmentRetryFacts(
    bool Enabled,
    bool InRetryLane,
    bool RequiresIntegration,
    bool AcceptanceIntegrationInFlight,
    bool IsGateEnvironmentFailure,
    bool ReviewPassed,
    int AttemptsSpent,
    IReadOnlyList<TimeSpan> Backoff,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset Now,
    bool TransportFailure = false,
    bool IntegrationBeforeFreshReview = false,
    int HostAttemptsSpent = 0,
    bool HostQuarantined = false,
    bool HostRepairSucceeded = false,
    bool HostRecoverySpent = false,
    bool HumanHold = false,
    bool OperatorRequest = false);

/// <summary>The AI Operator facts: the ladder plus the one continuation after parking.</summary>
public sealed record GateEnvironmentRetryOperatorFacts(
    GateEnvironmentRetryFacts Ladder,
    bool AlreadyParked,
    bool AutomaticContinuationsEnabled,
    int PriorContinuationRounds);

/// <summary>
/// Pure policy behind the automatic gate-environment integration retry
/// (AGT-2824, moved out of the monolith by AGT-3060).
/// <para>
/// A gate environment failure (CAC-18) says the build/test gate crashed before
/// it reached test discovery: a toolchain, bundler, or runtime-version problem
/// on the host, never a verdict on the reviewed change. The passed review for
/// the delivery SHA therefore stays valid, and the only thing worth repeating
/// is the integration itself. This policy decides when to repeat it, how often,
/// and when to stop.
/// </para>
/// </summary>
public static class GateEnvironmentRetryPolicy
{
    public static GateEnvironmentRetryDecision Decide(GateEnvironmentRetryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var maximum = facts.Backoff.Count;
        var spent = Math.Clamp(Math.Max(facts.AttemptsSpent, facts.HostAttemptsSpent), 0, maximum);
        if (OperatorReplayRefusal(facts) is { } refused)
            return Ignore(refused, spent, maximum);
        // The gate runner already retried the transport against the same
        // subject. Another timed rung would only meet the same unreachable host.
        if (facts.TransportFailure)
            return Ignore(GateEnvironmentRetryReasons.TransportAwaitsGateHost, spent, maximum);

        if (facts.HostQuarantined)
            return facts.HostRepairSucceeded && !facts.HostRecoverySpent
                ? new(GateEnvironmentRetryAction.Retry, GateEnvironmentRetryReasons.HostRecovered,
                    spent, maximum, TimeSpan.Zero, facts.Now)
                : new(GateEnvironmentRetryAction.Park, GateEnvironmentRetryReasons.HostQuarantined,
                    spent, maximum, TimeSpan.Zero, DueAt: null);

        if (spent >= maximum)
            return new(GateEnvironmentRetryAction.Park, GateEnvironmentRetryReasons.BudgetExhausted,
                spent, maximum, TimeSpan.Zero, DueAt: null);

        // The first rung measures from the failure itself, every later rung from
        // the retry that produced the current failure. A missing anchor (legacy
        // evidence without a timestamp) is treated as due now rather than as a
        // reason to never retry. A long gate can outlast its backoff, so wait
        // from its completed failure, not only the receipt written before it.
        var anchor = facts.FailedAt is { } failure && facts.LastAttemptAt is { } attempt
            ? (failure > attempt ? failure : attempt)
            : facts.FailedAt ?? facts.LastAttemptAt;
        if (anchor is not { } from)
            return new(GateEnvironmentRetryAction.Retry, GateEnvironmentRetryReasons.DueWithoutAnchor,
                spent, maximum, TimeSpan.Zero, facts.Now);

        var dueAt = from + facts.Backoff[spent];
        return dueAt > facts.Now
            ? new(GateEnvironmentRetryAction.Wait, GateEnvironmentRetryReasons.Backoff,
                spent, maximum, dueAt - facts.Now, dueAt)
            : new(GateEnvironmentRetryAction.Retry, GateEnvironmentRetryReasons.Due,
                spent, maximum, TimeSpan.Zero, dueAt);
    }

    /// <summary>
    /// Why the card has nothing to replay, or null when it does. The operator's
    /// "Retry integration" intent may override the ladder's timing but never
    /// this eligibility, so the authority validates the intent with it.
    /// </summary>
    public static string? OperatorReplayRefusal(GateEnvironmentRetryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Enabled) return GateEnvironmentRetryReasons.Disabled;
        if (facts.HumanHold) return GateEnvironmentRetryReasons.HumanHold;
        if (facts.OperatorRequest && facts.HostQuarantined
            && (!facts.HostRepairSucceeded || facts.HostRecoverySpent))
            return GateEnvironmentRetryReasons.HostQuarantined;
        if (!facts.InRetryLane) return GateEnvironmentRetryReasons.OutsideLanes;
        if (!facts.RequiresIntegration) return GateEnvironmentRetryReasons.NoCodeDelivery;
        if (facts.AcceptanceIntegrationInFlight) return GateEnvironmentRetryReasons.AcceptanceIntegrationInFlight;
        if (!facts.IsGateEnvironmentFailure) return GateEnvironmentRetryReasons.NotAGateEnvironmentFailure;
        if (!facts.ReviewPassed && !facts.IntegrationBeforeFreshReview)
            return GateEnvironmentRetryReasons.NoPassedReview;
        return null;
    }

    /// <summary>
    /// Operator-facing parked reason. Named after the environment failure it
    /// parks on, so the card answers "why is this stuck" without opening the
    /// pipeline evidence.
    /// </summary>
    public static string ParkedReason(int attempts, string integrationBranch, string? gateReason)
    {
        var detail = string.IsNullOrWhiteSpace(gateReason)
            ? "The gate host or run budget prevented verification from completing."
            : gateReason.Trim();
        return $"Parked after {attempts} automatic gate-environment retries: the merge into "
               + $"{integrationBranch} keeps failing on the gate environment, not on the reviewed change. "
               + $"{detail} The host fault is quarantined with a separate repair obligation. "
               + "A successful host repair wakes this exact delivery once, reusing its passed review.";
    }

    private static GateEnvironmentRetryDecision Ignore(string reason, int spent, int maximum)
        => new(GateEnvironmentRetryAction.Ignore, reason, spent, maximum, TimeSpan.Zero, DueAt: null);
}

/// <summary>Host faults never authorize a product continuation, including legacy engine receipts.</summary>
public static class GateEnvironmentContinuationPolicy
{
    public static bool ShouldStart(bool automaticEnabled, int priorRoundsForDelivery)
        => false;
}
