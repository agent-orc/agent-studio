namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Reasons a claim's continuation base is sent with, where the runner's
/// behaviour depends on the reason (AGT-3056).
/// </summary>
public static class ContinuationBaseReasons
{
    /// <summary>
    /// The delivery a review graded ProductFailure; the fix round continues on
    /// it. Unlike a salvage base, the runner never replaces it with the
    /// integration branch: an unusable base fails the preparation.
    /// </summary>
    public const string ReviewFixRound = "review-fix-round";

    /// <summary>
    /// The card's recorded result ref, or the ref the operator named, that an
    /// operator continue, steer or extend round starts from (AGT-3483). The
    /// runner treats it like a salvage base: required, with the integration
    /// branch merged on top.
    /// </summary>
    public const string ContinueRoundResult = "continue-round-result";

    public const string Missing = "continuation-base-missing";
    public const string Moved = "continuation-base-moved";
    public const string Unreadable = "continuation-base-unreadable";
    public const string Incomplete = "continuation-base-incomplete";

    /// <summary>An unusable required base needs operator repair before another claim.</summary>
    public static bool IsUnavailable(string? reason)
        => reason is Missing or Moved or Unreadable or Incomplete;

    public static bool IsReviewFixRound(string? reason)
        => string.Equals((reason ?? string.Empty).Trim(), ReviewFixRound, StringComparison.Ordinal);
}
