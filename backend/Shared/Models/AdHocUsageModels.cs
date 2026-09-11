namespace AgentStudio.Shared;

/// <summary>
/// One ad-hoc Haiku CLI call recorded outside the main task-runner path.
/// These are the small "summarize the protocol", "extract the title",
/// "enhance the prompt", "generate a commit message" subprocess calls
/// that the orchestrator fires on demand. We log them to a workspace-wide
/// JSONL so the user can see how much ambient Haiku spend the orchestrator
/// is incurring on top of the main pipeline.
///
/// Schema is intentionally flat. New <see cref="Source"/> tags can be
/// added without breaking older readers; unknown sources just render
/// under their literal name.
/// </summary>
public sealed record AdHocUsageRecord
{
    /// <summary>UTC timestamp the call completed.</summary>
    public DateTime Ts { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Stable tag identifying which orchestrator code path made the call.
    /// One of <see cref="AdHocUsageSources"/>; new strings are tolerated.
    /// </summary>
    public string Source { get; init; } = AdHocUsageSources.Unknown;

    /// <summary>Model id reported by the CLI (e.g. "claude-haiku-4-5").</summary>
    public string Model { get; init; } = "";

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheCreationTokens { get; init; }

    /// <summary>Wall-clock duration of the subprocess in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>True when the underlying CLI invocation reported success.</summary>
    public bool Ok { get; init; } = true;

    /// <summary>Optional project name (when the call was tied to a watched project).</summary>
    public string? Project { get; init; }

    /// <summary>Optional job id (when the call was tied to a specific job folder).</summary>
    public string? JobId { get; init; }
}

/// <summary>
/// Stable string ids for the orchestrator code paths that fire ad-hoc
/// Haiku calls. Adding a new caller? Add a constant here so the
/// frontend's per-source breakdown stays consistent.
/// </summary>
public static class AdHocUsageSources
{
    public const string TitleGeneration   = "title-generation";
    public const string SummaryGeneration = "summary-generation";
    public const string PromptEnhancement = "prompt-enhancement";
    public const string CommitMessage     = "commit-message";
    public const string SoftReasoning     = "soft-reasoning";
    public const string ReviewDecision    = "review-decision";
    public const string DriftAnalysis     = "drift-analysis";
    public const string WikiGrading       = "wiki-grading";
    public const string TaskSpawner       = "task-spawner";
    public const string Watcher           = "watcher-analysis";
    public const string FailureIntervention = "failure-intervention";
    public const string Unknown           = "unknown";
}

/// <summary>
/// Aggregated rollup of <see cref="AdHocUsageRecord"/> entries. Same
/// shape conventions as <see cref="AgentStudio.Runner.TokenSummary"/>:
/// real token counts plus a theoretical USD estimate that does not
/// reflect the user's actual subscription bill.
/// </summary>
public sealed record AdHocUsageAggregate(
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    IReadOnlyList<AdHocUsageBySource> BySource,
    IReadOnlyList<AdHocUsageByDay> ByDay,
    IReadOnlyList<AdHocUsageByModel> ByModel,
    string LogPath,
    long LogSizeBytes,
    DateTime? LogModifiedAt,
    string Disclaimer);

public sealed record AdHocUsageBySource(
    string Source,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd);

/// <summary>
/// One per UTC date (YYYY-MM-DD). Days with zero calls do not appear.
/// </summary>
public sealed record AdHocUsageByDay(
    string Date,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd);

public sealed record AdHocUsageByModel(
    string Model,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool ModelPriced);
