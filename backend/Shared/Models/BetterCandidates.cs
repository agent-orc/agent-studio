namespace AgentStudio.Shared;

/// <summary>
/// Server-computed, informational price-performance alternatives for a route.
/// The benchmark library recommends; Agent Studio never changes the route from
/// this note.
/// </summary>
public sealed record BetterCandidateNote
{
    public string CurrentModel { get; init; } = string.Empty;
    public string? CurrentThinkingLevel { get; init; }
    public string CapabilityClass { get; init; } = string.Empty;
    public string EvidenceSnapshot { get; init; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; init; }
    public string MatrixUrl { get; init; } = string.Empty;
    public IReadOnlyList<BetterCandidate> Candidates { get; init; } = [];
}

/// <summary>One benchmark-qualified alternative returned by TokenEconomy.</summary>
public sealed record BetterCandidate
{
    public string Model { get; init; } = string.Empty;
    public string? ThinkingLevel { get; init; }
    public string BenchmarkType { get; init; } = string.Empty;
    public string BenchmarkName { get; init; } = string.Empty;
    public decimal? ScoreDelta { get; init; }
    public decimal? CostDeltaUsd { get; init; }
    public int EvidenceAgeDays { get; init; }
    public bool EvidenceStale { get; init; }
}
