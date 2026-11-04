using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// What the pre-launch quota check decided for one candidate card.
/// </summary>
public enum QuotaAdmissionOutcome
{
    /// <summary>Launch on the requested/primary model - quota is healthy.</summary>
    LaunchPrimary,
    /// <summary>Launch on the AGT-2040 fallback because the primary is (or is about to be) capped.</summary>
    LaunchFallback,
    /// <summary>Do not launch: every viable model is exhausted. Wait quietly for the next reset.</summary>
    Wait,
    /// <summary>Only reduce parallelism: a projected breach with no fallback while a slot is already busy.</summary>
    Throttle,
    /// <summary>The claiming host did not prove the named model; no quota reset applies.</summary>
    HostCapabilityMissing,
}

/// <summary>
/// The pre-launch quota decision for a card, plus the numbers behind it.
/// </summary>
public sealed record QuotaAdmissionPlan(
    QuotaAdmissionOutcome Outcome,
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string Reason,
    DateTime? NextResetAt,
    QuotaProjection? Projection,
    QuotaProjectionWarning? ProjectionWarning = null,
    bool NearbyResetWait = false)
{
    /// <summary>
    /// Informational benchmark alternatives for the effective route. This is
    /// attached by <see cref="QuotaAdmissionService"/> after the pure quota
    /// decision and never changes <see cref="Outcome"/>.
    /// </summary>
    public BetterCandidateNote? BetterCandidates { get; init; }

    /// <summary>Shared run/one-shot fallback receipt, also used by provider rejection continuations.</summary>
    public ModelFallbackInfo? ModelFallback { get; init; }

    /// <summary>The durable planner receipt, reused by legacy admission emitters.</summary>
    public string? SelectionEventId { get; init; }

    /// <summary>The legacy admission view of a selection result; the outcome and receipt carry over unchanged.</summary>
    public static QuotaAdmissionPlan From(SelectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var outcome = result.Outcome switch
        {
            SelectionOutcome.Primary => QuotaAdmissionOutcome.LaunchPrimary,
            SelectionOutcome.Fallback => QuotaAdmissionOutcome.LaunchFallback,
            SelectionOutcome.Wait => QuotaAdmissionOutcome.Wait,
            SelectionOutcome.Throttle => QuotaAdmissionOutcome.Throttle,
            _ => QuotaAdmissionOutcome.HostCapabilityMissing,
        };
        return new QuotaAdmissionPlan(
            outcome,
            result.Route.CliType,
            result.Route.Model,
            result.Route.ThinkingLevel,
            result.IsFallback,
            result.Reason,
            result.IsFallback ? result.ReturnToPrimaryAt : result.RetryAt,
            result.Quota?.Projection,
            result.Quota?.ProjectionWarning,
            result.NearbyResetWait)
        {
            ModelFallback = result.Fallback,
            SelectionEventId = result.LedgerId,
        };
    }

    /// <summary>True when the runner should proceed to a launch (primary or fallback).</summary>
    public bool ShouldLaunch => Outcome is QuotaAdmissionOutcome.LaunchPrimary or QuotaAdmissionOutcome.LaunchFallback;

    /// <summary>True when the card should be held back this tick (wait or throttle).</summary>
    public bool IsDeferred => Outcome is QuotaAdmissionOutcome.Wait or QuotaAdmissionOutcome.Throttle
        or QuotaAdmissionOutcome.HostCapabilityMissing;
}

/// <summary>
/// The algorithmic pre-launch admission check the operator asked for (AGT-2055):
/// "Die Last-Steuerung soll ein ALGORITHMUS sein, nicht ein CLI-Call." Before a
/// card is admitted, the scheduler evaluates the cached quota snapshots for its
/// target CLI and decides - purely from data, without spawning anything -
/// whether to launch on primary, switch to the AGT-2040 fallback, throttle, or
/// wait for the next reset.
///
/// <para>
/// Since MS-5 the rules live in <see cref="ModelSelectionPlanner"/> in the
/// contracts package; this type is the legacy adapter until MS-6.
/// </para>
/// <para>
/// This planner deliberately <b>reuses</b> the merged AGT-2040 routing map
/// (<see cref="CliQuotaFallbackService"/>) rather than duplicating "which model
/// replaces which": that map IS the configuration. The planner's own job is
/// (a) to feed the router a <i>projection-aware</i> quota view so a primary
/// that is about to breach switches early, and (b) to turn a "no viable model"
/// situation into a quiet, reasoned wait instead of a burned launch.
/// </para>
/// </summary>
public static class QuotaAdmissionPlanner
{
    /// <summary>
    /// Thin adapter over <see cref="ModelSelectionPlanner.Plan"/> (MS-5). The
    /// admission rules moved unchanged into the shared planner; this keeps the
    /// legacy signature for callers that have not moved to
    /// <see cref="ModelSelectionService"/> yet (MS-6).
    /// </summary>
    public static QuotaAdmissionPlan Plan(
        string? requestedCli,
        string? requestedModel,
        string? requestedThinking,
        CliQuotaFallbackService? fallback,
        CliQuotaCapsService caps,
        Func<string?, QuotaSnapshot?> snapshotFor,
        DateTime nowUtc,
        int occupiedSlots,
        ResolvedCliQuotaWaitPolicy? waitPolicy = null,
        CliFallbackPreference? preference = null,
        Func<string, string?, bool>? modelAccepted = null,
        string? claimingHost = null,
        Func<string, string?, bool>? fallbackModelAccepted = null,
        bool sameClassOnly = false,
        Func<string, bool>? fallbackProviderAllowed = null)
    {
        var request = new SelectionRequest
        {
            Pins = new SelectionPins(requestedCli, requestedModel, requestedThinking),
            Required = new SelectionRequirements(SameClassOnly: sameClassOnly),
            Host = claimingHost is null ? null : new SelectionHost(claimingHost),
        };
        var facts = new SelectionFacts
        {
            NowUtc = nowUtc,
            Caps = caps,
            SnapshotFor = snapshotFor,
            Routing = fallback?.RoutingFacts(),
            OccupiedSlots = occupiedSlots,
            WaitPolicy = waitPolicy,
            OperatorSwitch = preference,
            FallbackProviderAllowed = fallbackProviderAllowed,
            HostAccepts = modelAccepted,
            HostAcceptsFallback = fallbackModelAccepted,
        };
        return QuotaAdmissionPlan.From(ModelSelectionPlanner.Plan(request, facts));
    }

    /// <summary>
    /// One-line "with the numbers" description of an admission decision for the
    /// load-distribution feed (AGT-2055, requirement 7): burn rate, remaining
    /// budget and remaining time behind the umschichten / drosseln / normal
    /// call. Falls back to the outcome + next reset when no window could be
    /// projected (e.g. a snapshot with no reset time to extrapolate from).
    /// </summary>
    public static string DescribeLoadNumbers(QuotaAdmissionPlan plan)
    {
        var p = plan?.Projection;
        var warning = plan?.ProjectionWarning;
        if (warning is not null)
            return $"projection ignored: {warning.Reason}; used {warning.CurrentUsedPct:0.#}% -> suspect {warning.ProjectedUsedPct:0.#}%; " +
                   $"resetAt {warning.ResetAt:o}, assumed start {warning.AssumedStartAt:o}, elapsed fraction {warning.ElapsedFraction:0.###}";
        if (p is null)
        {
            var outcome = plan?.Outcome.ToString() ?? "unknown";
            return plan?.NextResetAt is { } reset
                ? $"{outcome}; next reset {reset:HH:mm} UTC"
                : outcome;
        }

        var budgetLeft = Math.Max(0d, p.CapPct - p.CurrentUsedPct);
        return
            $"burn {p.BurnRatePctPerHour:0.#}%/h, used {p.CurrentUsedPct:0.#}% -> projected {p.ProjectedUsedPct:0.#}% " +
            $"(cap {p.CapPct}%), {budgetLeft:0.#}% budget left, {p.HoursRemaining:0.#}h to reset";
    }
}
