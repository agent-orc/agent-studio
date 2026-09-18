namespace AgentStudio.Shared;

/// <summary>
/// Token usage for a single orchestrator / one-shot LLM call. Lives in
/// Shared (now <c>AgentStudio.Shared</c>)
/// so both the server-side orchestrator log and the executor-side
/// <c>ICliOneShot</c> result envelope can reference it without the executor
/// depending on the server's orchestrator-log types.
/// </summary>
public record OrchestratorTokenUsage
{
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheCreationTokens { get; init; }
    /// <summary>
    /// True when the provider's raw input counter included cached tokens,
    /// false when input and cache-read were reported separately, and null for
    /// legacy rows that predate provider-semantics provenance.
    /// </summary>
    public bool? InputIncludesCached { get; init; }
    public string? UsageNormalization { get; init; }
    /// <summary>
    /// Effective reasoning / thinking level of the call, when the caller knows
    /// it (AGT-2811). Null keeps legacy rows readable as "level unknown".
    /// </summary>
    public string? ThinkingLevel { get; init; }
}
