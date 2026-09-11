namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Grade of one completed review, derived from its aspect verdicts. Distinct
/// from <see cref="RunFailureClass"/>: this describes what the reviewers said
/// about a change that was actually reviewed, not why a review could not reach
/// a verdict.
/// </summary>
public enum ReviewGrade
{
    Pass,
    PassWithConcerns,
    ProductFailure,
}

/// <summary>
/// Pure, table-driven mapping from per-aspect verdict tokens to one review
/// grade.
/// <para>
/// AGT-2706 settled as <c>ProductFailure</c> on 2026-09-06 with every aspect at
/// <c>pass</c> and a single <c>documentation-impact</c> aspect at
/// <c>concerns</c>. Concerns is the mildest verdict a reviewer can return that
/// is not a clean pass; the remote executor folded it in with <c>block</c> and
/// <c>fail</c> (<c>runner/RemoteReviewWorkspace.cs</c>), so a card with only
/// reservations looked like broken product. This is the single place that
/// decision is made now.
/// </para>
/// </summary>
public static class ReviewGradingPolicy
{
    /// <summary>Tokens that refuse the change.</summary>
    private static readonly string[] BlockingTokens = ["block", "blocked", "fail"];

    /// <summary>Tokens that accept the change while recording a reservation.</summary>
    private static readonly string[] ConcernTokens = ["concerns", "concern"];

    /// <summary>
    /// Grades a set of per-aspect verdict tokens (<c>"pass"</c> / <c>"concerns"</c>
    /// / <c>"block"</c> / <c>"fail"</c>, case-insensitive). A single blocking token
    /// outranks any number of concerns; concerns alone never escalate to
    /// <see cref="ReviewGrade.ProductFailure"/>.
    /// </summary>
    public static ReviewGrade Grade(IEnumerable<string?> aspectStatuses)
    {
        var grade = ReviewGrade.Pass;
        foreach (var status in aspectStatuses)
        {
            if (IsBlockingToken(status)) return ReviewGrade.ProductFailure;
            if (IsConcernToken(status) && grade == ReviewGrade.Pass)
                grade = ReviewGrade.PassWithConcerns;
        }
        return grade;
    }

    public static bool IsBlockingToken(string? status) => Matches(status, BlockingTokens);

    public static bool IsConcernToken(string? status) => Matches(status, ConcernTokens);

    private static bool Matches(string? status, string[] tokens)
        => status is not null
           && tokens.Any(token => string.Equals(status.Trim(), token, StringComparison.OrdinalIgnoreCase));
}
