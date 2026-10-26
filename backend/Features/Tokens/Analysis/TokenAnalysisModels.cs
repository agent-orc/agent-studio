namespace AgentStudio.Tokens;

/// <summary>
/// One cell of a card's usage cube: the tokens of one usage kind, model,
/// reasoning level, CLI, and host on one UTC day. A card total is the sum of
/// its slices, so every client-side filter keeps the sum invariant.
/// <see cref="Reasoning"/> is the reported reasoning subset of
/// <see cref="Output"/>, or null when no call in the slice reported it.
/// </summary>
public sealed record TokenAnalysisSlice(
    string Kind,
    string Model,
    string Level,
    string Cli,
    string Host,
    string Day,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? Reasoning,
    decimal CostUsd,
    bool Priced,
    int Calls);

public sealed record TokenAnalysisCard(
    string Project,
    string JobId,
    string? Key,
    string Title,
    string? State,
    string TaskType,
    IReadOnlyList<string> Tags,
    string Outcome,
    int Rounds,
    bool InFlight,
    DateTime? FirstActivity,
    DateTime? LastActivity,
    long TotalTokens,
    decimal CostUsd,
    bool CostComplete,
    IReadOnlyList<TokenAnalysisSlice> Slices);

/// <summary>Project usage in range that belongs to no card (project chat, orchestrator boot).</summary>
public sealed record TokenAnalysisProjectRemainder(
    string Project,
    long TotalTokens,
    decimal CostUsd,
    IReadOnlyList<TokenAnalysisSlice> Slices);

public sealed record TokenAnalysisQuotaPoint(DateTime At, double UsedPct);

/// <summary>
/// One quota window between two resets. <see cref="TokensPerPct"/> is the
/// workspace tokens of the CLI per quota percentage point; it is null when
/// the window moved less than one point. <see cref="Open"/> marks the window
/// still running, so its ratio is provisional.
/// </summary>
public sealed record TokenAnalysisQuotaSegment(
    DateTime From,
    DateTime To,
    double StartPct,
    double EndPct,
    long Tokens,
    long UncachedTokens,
    double? TokensPerPct,
    double? UncachedTokensPerPct,
    bool Open);

public sealed record TokenAnalysisQuotaWindow(
    string Cli,
    string Window,
    IReadOnlyList<TokenAnalysisQuotaPoint> Points,
    IReadOnlyList<TokenAnalysisQuotaSegment> Segments);

public sealed record TokenAnalysisResponse(
    DateTime GeneratedAt,
    DateTime From,
    DateTime To,
    int Days,
    IReadOnlyList<string> Projects,
    IReadOnlyList<TokenAnalysisCard> Cards,
    IReadOnlyList<TokenAnalysisProjectRemainder> Remainders,
    IReadOnlyList<TokenAnalysisQuotaWindow> Quota,
    ProjectTokenDataFreshness Freshness,
    string Disclaimer);

/// <summary>
/// One step of a card's drill-down timeline: a coding or fix-round run, a
/// review aspect, grade, gate, decision, chat turn, or enrichment call.
/// Ledger calls of one run are folded into the run's event per model.
/// </summary>
public sealed record TokenAnalysisEvent(
    DateTime At,
    DateTime? EndAt,
    string Kind,
    string Label,
    int Attempt,
    string Model,
    string Level,
    string Cli,
    string Host,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? Reasoning,
    long Total,
    decimal CostUsd,
    bool Priced,
    long? DurationMs,
    string? Result,
    int Calls,
    string Source);

public sealed record TokenAnalysisCardDetail(
    TokenAnalysisCard Card,
    IReadOnlyList<TokenAnalysisEvent> Events,
    ProjectTokenDataFreshness Freshness,
    string Disclaimer);
