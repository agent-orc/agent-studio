namespace AgentStudio.Review.Observations;

public sealed record ReviewFindingCounts(int Blocker = 0, int Major = 0, int Minor = 0)
{
    public int Total => Blocker + Major + Minor;
}

public sealed record ReviewObservation(
    string Id, string JobId, string Level, string? ResultSha, DateTime ReviewedAt,
    string? Model, string? ReasoningLevel, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheCreationTokens, long DurationMs,
    string Verdict, ReviewFindingCounts Findings, decimal? CostUsd,
    string EvidenceRef, int? ConfirmedFindings = null,
    string? Cli = null, string? Aspect = null, int? FilesReviewed = null,
    int? DismissedFindings = null, string? ReviewRoundId = null);

public sealed record ReviewLaterEvidence(
    string Id, string ReviewId, string ResultSha, DateTime FoundAt,
    string Source, string EvidenceRef, string Severity, string Summary);

public sealed record ReviewFindingDisposition(
    string ReviewId, int Confirmed, int Dismissed, DateTime AssessedAt,
    string EvidenceRef);

public sealed record ReviewObservationAggregate(
    string Model, string ReasoningLevel, int Reviews, int Blockers, int Approved,
    int MissEligibleApprovals,
    int Misses, int? RealFindings, decimal? CostUsd, decimal? CostPerReview,
    double BlockerRate, double? MissRate, decimal? CostPerRealFinding);

public sealed record ReviewObservationReport(
    string Project, DateTime GeneratedAt, string Method,
    IReadOnlyList<ReviewObservation> Reviews,
    IReadOnlyList<ReviewLaterEvidence> LaterEvidence,
    IReadOnlyList<ReviewObservationAggregate> ByModelAndReasoning);
