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

/// <summary>Wire values for a settled review outcome.</summary>
public static class ReviewOutcomes
{
    public const string Pass = "Pass";
    public const string PassWithConcerns = "PassWithConcerns";
    public const string ProductFailure = "ProductFailure";
    public const string ReviewInfra = "ReviewInfra";

    /// <summary>
    /// Whether the outcome clears the change for integration. Concerns are
    /// recorded and surfaced, but they do not block: they are review notes, not
    /// a refusal.
    /// </summary>
    public static bool IsAccepting(string? outcome)
        => string.Equals(outcome, Pass, StringComparison.OrdinalIgnoreCase)
           || string.Equals(outcome, PassWithConcerns, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Table-driven mapping from aspect verdict tokens to one review grade.
/// <para>
/// AGT-2706 settled as <c>ProductFailure</c> on 2026-09-06 with every aspect at
/// <c>pass</c> and a single <c>documentation-impact</c> aspect at
/// <c>concerns</c>. Concerns are the mildest verdict a reviewer can return that
/// is not a clean pass; folding them in with <c>block</c> made the card look
/// like broken product. The mapping below is the single place that decision is
/// made, and it is exercised directly by
/// <c>backend.Tests/ReviewGradingPolicyTests.cs</c>.
/// </para>
/// </summary>
public static class ReviewGradingPolicy
{
    /// <summary>Tokens that refuse the change.</summary>
    private static readonly string[] BlockingTokens = ["block", "blocked", "fail"];

    /// <summary>Tokens that accept the change while recording a reservation.</summary>
    private static readonly string[] ConcernTokens = ["concerns", "concern"];

    public static ReviewGrade Grade(
        IEnumerable<string?> aspectStatuses,
        bool hasBlockingCommandFailure = false)
    {
        var grade = hasBlockingCommandFailure ? ReviewGrade.ProductFailure : ReviewGrade.Pass;
        foreach (var status in aspectStatuses)
        {
            if (Matches(status, BlockingTokens)) return ReviewGrade.ProductFailure;
            if (Matches(status, ConcernTokens) && grade == ReviewGrade.Pass)
                grade = ReviewGrade.PassWithConcerns;
        }
        return grade;
    }

    public static string ToOutcome(ReviewGrade grade)
        => grade switch
        {
            ReviewGrade.ProductFailure => ReviewOutcomes.ProductFailure,
            ReviewGrade.PassWithConcerns => ReviewOutcomes.PassWithConcerns,
            _ => ReviewOutcomes.Pass,
        };

    /// <summary>
    /// Grades the aspect verdicts and returns the wire outcome in one step.
    /// </summary>
    public static string Outcome(
        IEnumerable<string?> aspectStatuses,
        bool hasBlockingCommandFailure = false)
        => ToOutcome(Grade(aspectStatuses, hasBlockingCommandFailure));

    public static bool IsBlockingToken(string? status) => Matches(status, BlockingTokens);

    public static bool IsConcernToken(string? status) => Matches(status, ConcernTokens);

    private static bool Matches(string? status, string[] tokens)
        => status is not null
           && tokens.Any(token => string.Equals(status.Trim(), token, StringComparison.OrdinalIgnoreCase));
}
