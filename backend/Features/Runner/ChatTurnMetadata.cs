namespace AgentStudio.Runner;

/// <summary>Provider and admission receipt for one orchestrator chat reply.</summary>
public sealed record ChatTurnMetadata(
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime FinishedAt,
    string? Host,
    string? ProviderSessionId,
    string? Model,
    string? Effort,
    int? ReasoningTokens = null,
    decimal? Cost = null,
    string? Currency = null,
    string? PriceCatalogueVersion = null,
    bool? IsHeavy = null,
    decimal? CpuShare = null)
{
    public long TotalLatencyMs => Math.Max(0, (long)(FinishedAt - QueuedAt).TotalMilliseconds);
    public long? QueueLatencyMs => StartedAt is { } started
        ? Math.Max(0, (long)(started - QueuedAt).TotalMilliseconds)
        : null;
}
