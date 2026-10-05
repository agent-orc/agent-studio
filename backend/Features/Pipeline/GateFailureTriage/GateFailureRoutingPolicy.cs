namespace AgentStudio.Pipeline;

/// <summary>Park categories a gate failure can carry (<c>[category]</c> in the park reason).</summary>
public static class GateFailureParkCategories
{
    /// <summary>The bounded environment replay ladder is spent; the gate host needs repair.</summary>
    public const string EnvironmentExhausted = "gate-environment-exhausted";

    /// <summary>A product failure whose bounded fix round is spent or disabled.</summary>
    public const string Product = "gate-product";

    /// <summary>The card waits on a shared cause card instead of paying for its own rounds.</summary>
    public const string SharedCause = "gate-shared-cause";

    /// <summary>The only category that asks a person to judge the failure.</summary>
    public const string Undecidable = "gate-undecidable";
}

public enum GateFailureRouteAction
{
    /// <summary>Leave the card where it is; the bounded gate-environment ladder replays the integration.</summary>
    ReplayGate,

    /// <summary>Start one agent fix round carrying the failing items and the reason line.</summary>
    FixRound,

    /// <summary>Open or join the cause card for this fingerprint and wait on it.</summary>
    AttachToCause,

    /// <summary>Cause creation failed; leave the card in place and retry on the next sweep.</summary>
    WaitForCause,

    /// <summary>Park for a person, stating the class and the missing evidence.</summary>
    ParkUndecidable,

    /// <summary>A bounded automatic route is spent; park with the typed category that names it.</summary>
    ParkExhausted,
}

/// <param name="Category">Park category when the route parks or waits; null for <see cref="GateFailureRouteAction.ReplayGate"/> and <see cref="GateFailureRouteAction.FixRound"/>.</param>
/// <param name="Reason">Stable slug recorded on the route receipt.</param>
public sealed record GateFailureRoute(GateFailureRouteAction Action, string? Category, string Reason)
{
    public bool Parks => Action is GateFailureRouteAction.ParkUndecidable
        or GateFailureRouteAction.ParkExhausted
        or GateFailureRouteAction.AttachToCause;
}

/// <param name="ReplayBudgetLeft">The gate-environment ladder still has a rung for this delivery SHA.</param>
/// <param name="OtherCardsWithFingerprint">
/// Distinct other cards that failed with the same fingerprint in the counting
/// window, or null when no counter could be read. An unreadable counter never
/// invents a shared cause.
/// </param>
/// <param name="FixRoundsForDelivery">Gate fix rounds already started for this delivery SHA.</param>
/// <param name="AutomaticRoundsEnabled">The project allows automatic failure continuations.</param>
public sealed record GateFailureRoutingFacts(
    bool ReplayBudgetLeft,
    int? OtherCardsWithFingerprint,
    int FixRoundsForDelivery,
    bool AutomaticRoundsEnabled);

/// <summary>
/// Pure routing of a classified gate failure (AGT-3009). The human park is the
/// explicit fallback of every bounded route, never the default: environment
/// replays, product fixes, shared causes wait on one cause card, and only an
/// undecidable failure asks a person to judge it.
/// </summary>
public static class GateFailureRoutingPolicy
{
    /// <summary>One fix round per delivery SHA. A round that changes nothing leaves the same SHA red.</summary>
    public const int MaxFixRoundsPerDelivery = 1;

    /// <summary>
    /// The fingerprint becomes one cause at its second card: the first card
    /// pays for a fix round, every later card waits on the cause card.
    /// </summary>
    public const int SharedCauseOtherCards = 1;

    public static GateFailureRoute Decide(GateFailureTriage triage, GateFailureRoutingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(facts);

        switch (triage.Class)
        {
            case GateFailureClasses.Environment:
                if (triage.Kind == GateEnvironmentKinds.Transport)
                    return new(GateFailureRouteAction.ParkExhausted,
                        GateFailureParkCategories.EnvironmentExhausted, "transport-gate-retry-spent");
                return facts.ReplayBudgetLeft
                    ? new(GateFailureRouteAction.ReplayGate, null, "environment-replay")
                    : new(GateFailureRouteAction.ParkExhausted,
                        GateFailureParkCategories.EnvironmentExhausted, "environment-replay-budget-spent");

            case GateFailureClasses.IntegrationBranch:
                return new(GateFailureRouteAction.AttachToCause,
                    GateFailureParkCategories.SharedCause, "integration-branch-cause");

            case GateFailureClasses.Product:
                if (facts.OtherCardsWithFingerprint >= SharedCauseOtherCards)
                    return new(GateFailureRouteAction.AttachToCause,
                        GateFailureParkCategories.SharedCause, "fingerprint-seen-on-other-cards");
                if (!facts.AutomaticRoundsEnabled)
                    return new(GateFailureRouteAction.ParkExhausted,
                        GateFailureParkCategories.Product, "automatic-fix-rounds-disabled");
                return facts.FixRoundsForDelivery < MaxFixRoundsPerDelivery
                    ? new(GateFailureRouteAction.FixRound, null, "product-fix-round")
                    : new(GateFailureRouteAction.ParkExhausted,
                        GateFailureParkCategories.Product, "fix-round-budget-spent");

            default:
                return new(GateFailureRouteAction.ParkUndecidable,
                    GateFailureParkCategories.Undecidable, "undecidable");
        }
    }

    /// <summary>
    /// The park reason the card shows. It names the class, the fingerprint, and
    /// for an undecidable failure the evidence that would have decided it, so
    /// the person it reaches does not need to open the gate log to learn why.
    /// </summary>
    public static string ParkReason(GateFailureTriage triage, GateFailureRoute route, string? causeKey = null)
    {
        var detail = route.Action switch
        {
            GateFailureRouteAction.AttachToCause => causeKey is null
                ? "Waiting on the shared cause of this fingerprint."
                : $"Waiting on {causeKey}, the shared cause of this fingerprint.",
            GateFailureRouteAction.ParkUndecidable =>
                $"Missing evidence: {triage.MissingEvidence ?? "unknown"}. Decide whether the delivery or the environment caused the red gate.",
            _ when route.Reason == "environment-replay-budget-spent" =>
                "The bounded environment replay ladder is spent or disabled for this project; repair the gate host, then use Retry integration.",
            _ when route.Reason == "transport-gate-retry-spent" =>
                "The same exact-subject gate was retried after a transport failure and remains unavailable; repair the gate transport, then retry the gate.",
            _ when route.Reason == "automatic-fix-rounds-disabled" =>
                "Automatic fix rounds are disabled for this project; start a steer round with the failing items.",
            _ => "The bounded fix round is spent and the gate is still red on the same delivery.",
        };
        var kind = triage.Kind is null ? string.Empty : $"/{triage.Kind}";
        return HumanReviewEscalation.FormatReason(
            route.Category ?? GateFailureParkCategories.Undecidable,
            $"Merge gate class {triage.Class}{kind} ({triage.Fingerprint}). {triage.ReasonLine} {detail}");
    }
}
