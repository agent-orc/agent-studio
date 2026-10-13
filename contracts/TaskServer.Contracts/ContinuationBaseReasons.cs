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

    public static bool IsReviewFixRound(string? reason)
        => string.Equals((reason ?? string.Empty).Trim(), ReviewFixRound, StringComparison.Ordinal);
}
