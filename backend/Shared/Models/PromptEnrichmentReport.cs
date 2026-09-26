namespace AgentStudio.Shared;

/// <summary>
/// Auditable decision written before a task prompt may be dispatched to a CLI.
/// The operator-facing Task tab reads the same shape from
/// <c>enrichment-report.json</c>.
/// </summary>
public sealed record PromptEnrichmentReport
{
    public string SchemaVersion { get; init; } = "1.1";
    public string EnrichmentId { get; init; } = "";
    public DateTime GeneratedAtUtc { get; init; }
    /// <summary>One of: enriched, unchanged, fallback-unenriched, blocked.</summary>
    public string Status { get; init; } = PromptEnrichmentStatuses.Unchanged;
    public string OriginalPromptSha256 { get; init; } = "";
    public string EnrichedPromptSha256 { get; init; } = "";
    public PromptEnrichmentPolicy Policy { get; init; } = new();
    public List<string> DetectedAreas { get; init; } = [];
    public List<PromptEnrichmentCandidate> Candidates { get; init; } = [];
    public List<PromptEnrichmentBlock> AppendedBlocks { get; init; } = [];
    public PromptEnrichmentTokens Tokens { get; init; } = new();
    public PromptEnrichmentCost Cost { get; init; } = new();
    public long TimingMs { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed record PromptEnrichmentPolicy
{
    public string Id { get; init; } = "prompt-enrichment";
    public string Version { get; init; } = "1";
    public bool ProjectEnabled { get; init; } = true;
    public string Selector { get; init; } = "";
    public string Tokenizer { get; init; } = "character-estimate-v1";
    public int TokenBudget { get; init; }
    public int OptionalBlockLimit { get; init; }
    public string? StyleGuideSnapshotId { get; init; }
}

public sealed record PromptEnrichmentCandidate
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public List<string> Signals { get; init; } = [];
    /// <summary>appended, rejected-budget, rejected-source-missing, rejected-project-catalogue, or rejected-project-disabled.</summary>
    public string Decision { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? MissingPath { get; init; }
    public int EstimatedTokens { get; init; }
}

public sealed record PromptEnrichmentBlock
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string SourceVerification { get; init; } = "repository";
    public string Revision { get; init; } = "";
    public string DigestSha256 { get; init; } = "";
    public string Tier { get; init; } = "";
    public int Order { get; init; }
    public int EstimatedTokens { get; init; }
    public string ExactContent { get; init; } = "";
}

/// <summary>
/// Token ledger attribution. Appended tokens are part of the final core prompt
/// and are therefore not added to billable step totals a second time.
/// Selector buckets describe only preprocessing work.
/// </summary>
public sealed record PromptEnrichmentTokens
{
    public string Tokenizer { get; init; } = "character-estimate-v1";
    public int Original { get; init; }
    public int Appended { get; init; }
    public int Final { get; init; }
    public long PreprocessingInput { get; init; }
    public long PreprocessingOutput { get; init; }
    public long PreprocessingCacheRead { get; init; }
    public long PreprocessingCacheCreation { get; init; }
}

public sealed record PromptEnrichmentCost
{
    public string Currency { get; init; } = "USD";
    public decimal SelectorUsd { get; init; }
    public decimal? AppendedInputUsd { get; init; }
    public string? EstimateModel { get; init; }
    public string? UnknownReason { get; init; }
}

public static class PromptEnrichmentStatuses
{
    public const string Enriched = "enriched";
    public const string Unchanged = "unchanged";
    public const string FallbackUnenriched = "fallback-unenriched";
    public const string Blocked = "blocked";
}
