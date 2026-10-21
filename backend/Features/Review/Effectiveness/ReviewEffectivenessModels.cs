using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>Which review produced an observation (AGT-3269).</summary>
public static class ReviewLevels
{
    /// <summary>One aspect pass of the review plan (code-quality, requirement-fit, ...).</summary>
    public const string Aspect = "aspect";
    /// <summary>The post-review code-review quality grade (A-D).</summary>
    public const string Grade = "grade";
    /// <summary>A deeper review run outside the standard plan, recorded through the API.</summary>
    public const string Deep = "deep";

    public static readonly IReadOnlyList<string> All = [Aspect, Grade, Deep];

    public static bool IsKnown(string? level) => level is not null && All.Contains(level, StringComparer.Ordinal);
}

/// <summary>The semantic outcome of one review, independent of the level's verdict vocabulary.</summary>
public static class ReviewOutcomes
{
    public const string Pass = "pass";
    public const string Concerns = "concerns";
    public const string Block = "block";

    /// <summary>
    /// Maps an aspect token (pass, concerns, block) or a grade token (A-D) to
    /// an outcome. Unknown tokens, environmental aspect failures and a missing
    /// grade ("?") have no outcome and are not review observations.
    /// </summary>
    public static string? From(string? verdict) => verdict?.Trim().ToLowerInvariant() switch
    {
        "pass" or "a" or "b" or "grade-a" or "grade-b" => Pass,
        "concerns" or "c" or "grade-c" => Concerns,
        "block" or "fail" or "d" or "grade-d" => Block,
        _ => null,
    };

    /// <summary>An approval is every outcome that let the delivery continue.</summary>
    public static bool Approves(string outcome) => outcome is Pass or Concerns;
}

/// <summary>Who or what later found a defect in a delivery.</summary>
public static class ReviewDefectSources
{
    /// <summary>A blocking review of a different level on the same delivery.</summary>
    public const string LaterReview = "later-review";
    /// <summary>A failed build/test gate on the same delivery.</summary>
    public const string Gate = "gate";
    /// <summary>An operator sent the delivery back or recorded a defect.</summary>
    public const string Operator = "operator";
    /// <summary>A production health incident traced to the delivery.</summary>
    public const string ProductionIncident = "production-incident";
    /// <summary>A production rollback of the delivery.</summary>
    public const string Rollback = "rollback";

    public static readonly IReadOnlyList<string> All = [LaterReview, Gate, Operator, ProductionIncident, Rollback];

    /// <summary>Sources a caller may record through the API; reviews and gates are recorded by the pipeline.</summary>
    public static readonly IReadOnlyList<string> External = [Operator, ProductionIncident, Rollback];
}

public static class ReviewFindingSeverities
{
    public const string Blocker = "blocker";
    public const string Major = "major";
    public const string Minor = "minor";
    public const string Nit = "nit";
    public const string Unclassified = "unclassified";
}

/// <summary>Findings of one review by severity. Real findings are blocker, major and minor; nits and unclassified text are not.</summary>
public sealed record ReviewFindingCounts(int Blocker = 0, int Major = 0, int Minor = 0, int Nit = 0, int Unclassified = 0)
{
    public static readonly ReviewFindingCounts None = new();

    public int Total => Blocker + Major + Minor + Nit + Unclassified;
    public int Real => Blocker + Major + Minor;

    public ReviewFindingCounts Add(ReviewFindingCounts other) => new(
        Blocker + other.Blocker, Major + other.Major, Minor + other.Minor,
        Nit + other.Nit, Unclassified + other.Unclassified);

    public bool HasNegative => Blocker < 0 || Major < 0 || Minor < 0 || Nit < 0 || Unclassified < 0;

    /// <summary>One finding per non-pass aspect verdict: a block is a blocker, concerns are minor.</summary>
    public static ReviewFindingCounts ForAspect(string outcome) => outcome switch
    {
        ReviewOutcomes.Block => new(Blocker: 1),
        ReviewOutcomes.Concerns => new(Minor: 1),
        _ => None,
    };

    /// <summary>Counts finding texts by their leading severity tag, for example "[major] ..." or "Minor: ...".</summary>
    public static ReviewFindingCounts Classify(IEnumerable<string> findings)
    {
        var counts = None;
        foreach (var finding in findings)
        {
            counts = ReviewFindingSeverity.Of(finding) switch
            {
                ReviewFindingSeverities.Blocker => counts with { Blocker = counts.Blocker + 1 },
                ReviewFindingSeverities.Major => counts with { Major = counts.Major + 1 },
                ReviewFindingSeverities.Minor => counts with { Minor = counts.Minor + 1 },
                ReviewFindingSeverities.Nit => counts with { Nit = counts.Nit + 1 },
                _ => counts with { Unclassified = counts.Unclassified + 1 },
            };
        }
        return counts;
    }
}

public static partial class ReviewFindingSeverity
{
    [GeneratedRegex(@"^\W*(?<tag>blocker|critical|p0|major|high|p1|minor|medium|low|p2|nit|nitpick|trivial)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTag();

    public static string Of(string? finding)
    {
        var match = LeadingTag().Match(finding ?? string.Empty);
        if (!match.Success) return ReviewFindingSeverities.Unclassified;
        return match.Groups["tag"].Value.ToLowerInvariant() switch
        {
            "blocker" or "critical" or "p0" => ReviewFindingSeverities.Blocker,
            "major" or "high" or "p1" => ReviewFindingSeverities.Major,
            "minor" or "medium" or "low" or "p2" => ReviewFindingSeverities.Minor,
            _ => ReviewFindingSeverities.Nit,
        };
    }
}

/// <summary>
/// One reviewed delivery: a pipeline attempt of a task and the coding round
/// inside it. A bounded fix round inside the same attempt is a new delivery.
/// </summary>
public sealed record ReviewDelivery(string Project, string JobId, int PipelineAttempt, int DeliveryRound)
{
    public string Key => $"{Project}/{JobId}#{PipelineAttempt}.{DeliveryRound}";
}

/// <summary>One completed review: route, cost, duration, verdict and findings.</summary>
public sealed record ReviewObservation(
    string Id,
    ReviewDelivery Delivery,
    string Level,
    string Subject,
    string? Model,
    string? ReasoningLevel,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long DurationMs,
    decimal? CostUsd,
    string Verdict,
    string Outcome,
    ReviewFindingCounts Findings,
    DateTime ObservedAtUtc,
    string Origin,
    string? EvidenceRef = null)
{
    public bool Approved => ReviewOutcomes.Approves(Outcome);

    /// <summary>A review that blocks, or names a blocker, is defect evidence against approvals of other levels.</summary>
    public bool FindsDefect => Outcome == ReviewOutcomes.Block || Findings.Blocker > 0;
}

/// <summary>Later evidence that a delivery carried a defect.</summary>
public sealed record ReviewDefectEvidence(
    string Id,
    ReviewDelivery Delivery,
    string Source,
    string Summary,
    DateTime ObservedAtUtc,
    string RecordedBy,
    string? FinderLevel = null,
    string? FinderObservationId = null,
    string? Severity = null,
    string? Reference = null);

/// <summary>A review approval contradicted by later evidence, attributed to the review's level and route.</summary>
public sealed record ReviewMiss(
    string ObservationId,
    string EvidenceId,
    string Level,
    string? Model,
    string? ReasoningLevel,
    string Source,
    DateTime RecordedAtUtc);

/// <summary>Origins of an observation.</summary>
public static class ReviewObservationOrigins
{
    public const string Pipeline = "pipeline";
    public const string Api = "api";
}
