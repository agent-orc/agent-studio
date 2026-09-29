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
    /// <summary>CLI that made the call, when the writer knows it (AGT-2986).</summary>
    public string? CliType { get; init; }
    /// <summary>
    /// Host that executed the call: a remote runner id or <c>local</c>.
    /// Null keeps legacy rows readable; readers derive a host from the
    /// participant id (see <c>TokenUsageHost.Resolve</c>).
    /// </summary>
    public string? Host { get; init; }
    /// <summary>The model pinned for the run when it differs from observed usage.</summary>
    public string? PinnedModel { get; init; }
    /// <summary>True when provider usage identified a model other than the pin.</summary>
    public bool ModelMismatch { get; init; }
}
