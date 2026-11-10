namespace AgentStudio.Operator;

/// <summary>
/// The typed events of the operator decision stream (operator agent level
/// Dossier, OA-D2 option B). Each one publishes a fact Studio already holds;
/// none of them adds a detection or transition rule.
/// </summary>
public static class OperatorDecisionTypes
{
    public const string EscalationOpened = "escalation.opened";
    public const string HumanReviewReady = "human-review.ready";
    public const string DecisionCardOpened = "decision-card.opened";
    public const string BackendStalled = "backend.stalled";
    public const string BatchGateStuck = "batch-gate.stuck";
    public const string ProgressIdleWithReady = "progress.idle-with-ready";
    public const string BatchRed = "batch.red";
    public const string BatchIsolating = "batch.isolating";
    public const string PushPendingOverdue = "push.pending-overdue";
    public const string QuotaExhausted = "quota.exhausted";
    public const string QuotaFallbackActive = "quota.fallback-active";
    public const string WatcherHeartbeat = "watcher.heartbeat";

    public static readonly IReadOnlyList<string> All =
    [
        EscalationOpened, HumanReviewReady, DecisionCardOpened, BackendStalled, BatchGateStuck, ProgressIdleWithReady,
        BatchRed, BatchIsolating, PushPendingOverdue, QuotaExhausted, QuotaFallbackActive, WatcherHeartbeat,
    ];

    /// <summary>The type a fact key names: every key starts with its type and a <c>|</c>.</summary>
    public static string OfKey(string factKey)
    {
        var separator = factKey.IndexOf('|', StringComparison.Ordinal);
        return separator < 0 ? factKey : factKey[..separator];
    }
}

/// <summary>
/// Names of the operator operations an event suggests. They are the
/// operation names of the operator surface (Dossier "Operator surface"); the
/// stream only names them, it never runs them.
/// </summary>
public static class OperatorOperations
{
    public const string CardInspect = "card.inspect";
    public const string CardContinue = "card.continue";
    public const string CardMove = "card.move";
    public const string CardAccept = "card.accept";
    public const string OwnerDecide = "owner.decide";
    public const string StallsInspect = "stalls.inspect";
    public const string CapacityRead = "capacity.read";
    public const string RoutingChange = "routing.change";
    public const string BatchTriage = "batch.triage";
    public const string BatchEject = "batch.eject";
    public const string PushInspect = "push.inspect";
    public const string WatcherStatus = "watcher.status";

    public static IReadOnlyList<string> For(string type) => type switch
    {
        OperatorDecisionTypes.EscalationOpened => [CardInspect, CardContinue, CardMove],
        OperatorDecisionTypes.HumanReviewReady => [CardInspect, CardAccept, CardContinue],
        // Decision cards stay with the owner (OA-D5).
        OperatorDecisionTypes.DecisionCardOpened => [CardInspect, OwnerDecide],
        OperatorDecisionTypes.BackendStalled => [StallsInspect, CapacityRead],
        OperatorDecisionTypes.BatchGateStuck => [BatchTriage],
        OperatorDecisionTypes.ProgressIdleWithReady => [StallsInspect, CapacityRead],
        OperatorDecisionTypes.BatchRed => [BatchTriage, BatchEject],
        // Eject is the only member action while a batch isolates (Guardrails).
        OperatorDecisionTypes.BatchIsolating => [BatchTriage, BatchEject],
        OperatorDecisionTypes.PushPendingOverdue => [PushInspect, BatchTriage],
        OperatorDecisionTypes.QuotaExhausted => [CapacityRead, RoutingChange],
        OperatorDecisionTypes.QuotaFallbackActive => [CapacityRead, RoutingChange],
        OperatorDecisionTypes.WatcherHeartbeat => [WatcherStatus],
        _ => [],
    };
}

public sealed record OperatorEvidenceLink(string Label, string Href);

/// <summary>
/// One existing fact as a fact source reads it. <see cref="Key"/> identifies
/// the occurrence: it starts with the type and includes the time the fact
/// began where the source records one, so a card that escalates again is a
/// new occurrence.
/// </summary>
public sealed record OperatorDecisionFact(
    string Type,
    string Key,
    string? Project,
    string? CardKey,
    string Reason,
    DateTime ObservedAtUtc,
    IReadOnlyList<OperatorEvidenceLink> Evidence,
    IReadOnlyList<string>? Cards = null);

/// <summary>
/// One published event. <see cref="Cursor"/> is the decimal sequence of the
/// event in the installation's decision log; a client resumes with the last
/// cursor it processed. <see cref="Project"/> is null for an installation-wide
/// fact.
/// </summary>
public sealed record OperatorDecisionEvent(
    string Id,
    string Cursor,
    string Type,
    DateTime Time,
    string? Project,
    string? CardKey,
    IReadOnlyList<string> Cards,
    string Reason,
    IReadOnlyList<string> SuggestedOperations,
    IReadOnlyList<OperatorEvidenceLink> Evidence,
    string Fact,
    DateTime ObservedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Sequence => long.TryParse(Cursor, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>Answer of <c>GET /api/operator/decisions</c>.</summary>
public sealed record OperatorDecisionsResponse(
    IReadOnlyList<OperatorDecisionEvent> Events,
    string Cursor,
    string HeadCursor,
    string? OldestCursor,
    bool HasMore,
    bool Gap,
    int RetentionHours);
