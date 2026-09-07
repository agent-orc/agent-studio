namespace AgentStudio.Review;

/// <summary>
/// The one review head every surface reads: the escalation banner, the Evidence
/// tab, the Result header and the board card chip. Served on the task detail and
/// as <c>GET /api/tasks/{id}/review-projection</c>.
///
/// <para>
/// It answers three questions in this order, and only from
/// <see cref="ReviewRoundRecord"/> data: how many rounds and when
/// (<see cref="RoundCount"/>, <see cref="LatestAt"/>), what blocked
/// (<see cref="BlockingAspects"/> or <see cref="BuildTests"/>), and what to do
/// (<see cref="Recommendation"/>). "Not recorded", "Not proven" and "0" appear
/// only when the records say so, and then with a reason.
/// </para>
/// </summary>
public sealed record ReviewProjection
{
    public string JobId { get; init; } = "";

    /// <summary>How many review rounds exist, across both planes.</summary>
    public int RoundCount { get; init; }

    /// <summary>One of <see cref="ReviewProjectionPlanes"/>.</summary>
    public string Plane { get; init; } = ReviewProjectionPlanes.None;

    /// <summary>Every round, oldest first, with its full evidence.</summary>
    public List<ReviewRoundRecord> Rounds { get; init; } = [];

    /// <summary>Newest round, or null when none exists.</summary>
    public ReviewRoundRecord? Latest { get; init; }

    /// <summary>Plane-native outcome of <see cref="Latest"/>; null when no round exists.</summary>
    public string? LatestOutcome { get; init; }

    /// <summary>When <see cref="Latest"/> was recorded; null when no round exists.</summary>
    public DateTime? LatestAt { get; init; }

    /// <summary>
    /// Newest recorded quality grade. Null only when no round carried one, which
    /// is the normal case for a purely remote-reviewed card.
    /// </summary>
    public string? Grade { get; init; }

    /// <summary>
    /// Blocking semantic verdicts on the newest round, each with the reviewer's
    /// own reason. Empty when nothing semantic blocks.
    /// </summary>
    public List<ReviewRoundAspect> BlockingAspects { get; init; } = [];

    /// <summary>Collapsed build and test proof, with the reason behind it.</summary>
    public ReviewBuildTestsView BuildTests { get; init; } = new();

    /// <summary>Where the reviewed delivery ended up, with the reason when it did not land.</summary>
    public ReviewDeliveryView Delivery { get; init; } = new();

    /// <summary>Why a human is on the hook, or null when nobody is waiting on a decision.</summary>
    public ReviewDecisionRequirement? DecisionRequired { get; init; }

    /// <summary>One of <see cref="ReviewRecommendations"/>.</summary>
    public string Recommendation { get; init; } = ReviewRecommendations.None;

    /// <summary>
    /// The concrete next step, naming the gap: the aspect and its quoted reason,
    /// the failing command, or the gate result to override.
    /// </summary>
    public string RecommendationReason { get; init; } = "";
}

/// <summary>Collapsed build-tests proof across the card's review rounds.</summary>
public sealed record ReviewBuildTestsView
{
    /// <summary>One of <see cref="ReviewBuildTestsResults"/>.</summary>
    public string Result { get; init; } = ReviewBuildTestsResults.NotProven;

    /// <summary>One sentence naming the commands, or why nothing is proven.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Plan steps the verdict covers, e.g. <c>verify-1</c>, <c>verify-2</c>.</summary>
    public List<string> Steps { get; init; } = [];
}

/// <summary>Where the reviewed delivery ended up.</summary>
public sealed record ReviewDeliveryView
{
    /// <summary>One of <see cref="ReviewDeliveryStates"/>.</summary>
    public string State { get; init; } = ReviewDeliveryStates.NotAttempted;

    /// <summary>Why the delivery is in that state; empty when it needs no reason.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Branch the delivery targeted, when one was resolved.</summary>
    public string? IntegrationBranch { get; init; }
}

/// <summary>Why the card is waiting on a person, and what recorded that.</summary>
public sealed record ReviewDecisionRequirement
{
    /// <summary>One of <see cref="ReviewDecisionSources"/>.</summary>
    public string Source { get; init; } = ReviewDecisionSources.ReviewLaneChange;

    /// <summary>The reason as its source recorded it.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Which planes contributed the card's review rounds.</summary>
public static class ReviewProjectionPlanes
{
    public const string None = "none";
    public const string Local = "local";
    public const string Remote = "remote";
    public const string Mixed = "mixed";
}

/// <summary>What recorded the human-decision requirement.</summary>
public static class ReviewDecisionSources
{
    public const string ParkedBlocker = "parked-blocker";
    public const string EscalationEvent = "escalation-event";
    public const string ReviewLaneChange = "review-lane-change";
}

/// <summary>The operator's next step, derived from the blocking evidence.</summary>
public static class ReviewRecommendations
{
    /// <summary>Send the card back naming the concrete gap.</summary>
    public const string Reissue = "reissue";
    /// <summary>The work is sound; a human overrides the gate that held it.</summary>
    public const string AcceptWithOverride = "accept-with-override";
    /// <summary>A review is still in flight; nothing to decide yet.</summary>
    public const string Wait = "wait";
    /// <summary>Nothing is open.</summary>
    public const string None = "none";
}
