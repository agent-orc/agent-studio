using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// One record on the Agent Message Bus. Mirrors
/// <c>docs/app/schemas/agent-message.schema.json</c>. Append-only on disk, immutable
/// once written. The bus is observability and reference; it does not move jobs.
/// </summary>
/// <remarks>
/// Enum-like fields stay as plain <see cref="string"/> on the record so the
/// schema's mixed casing (PascalCase severity, lowercase role, kebab-case kind
/// and artifact kind) survives a round-trip without per-field converters.
/// Validation against the allowed value sets lives in
/// <see cref="AgentStudio.Bus.AgentMessageValidator"/>.
/// </remarks>
public sealed record AgentMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("participantId")]
    public required string ParticipantId { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    [JsonPropertyName("topic")]
    public string? Topic { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("replyToId")]
    public string? ReplyToId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("tokens")]
    public AgentMessageTokens? Tokens { get; init; }

    [JsonPropertyName("latency")]
    public AgentMessageLatency? Latency { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<AgentArtifactRef>? Artifacts { get; init; }

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record AgentMessageTokens(
    [property: JsonPropertyName("input")] long Input,
    [property: JsonPropertyName("output")] long Output,
    [property: JsonPropertyName("cacheRead")] long? CacheRead = null,
    [property: JsonPropertyName("cacheWrite")] long? CacheWrite = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("dollars")] double? Dollars = null,
    [property: JsonPropertyName("contextWindow")] AgentMessageContextWindow? ContextWindow = null,
    /// <summary>
    /// Effective reasoning / thinking level the call ran at, recorded at call
    /// time (AGT-2811). Model and level together are the call's identity: the
    /// same model at <c>low</c> and at <c>high</c> is neither the same cost nor
    /// the same quality. Null on rows emitted before this was recorded and for
    /// models that have no level dimension; consumers render that as
    /// "level unknown" and never guess a level.
    /// </summary>
    [property: JsonPropertyName("thinkingLevel")] string? ThinkingLevel = null);

/// <summary>
/// Snapshot of the model's context-window state at the moment one turn completed.
/// All fields optional - producers fill what they can derive from the CLI output.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TotalSize"/> comes from a model-registry lookup, not the CLI: Claude
/// does not echo "your context window is 200_000". The runner resolves the limit
/// for the model in use and stashes it here so the UI can compute pct without a
/// second lookup.
/// </para>
/// <para>
/// <see cref="Used"/> = input_tokens + cache_read_input_tokens (everything the
/// model loaded for this turn, cached or not). Cache hits still occupy context.
/// </para>
/// </remarks>
public sealed record AgentMessageContextWindow(
    [property: JsonPropertyName("totalSize")] long? TotalSize = null,
    [property: JsonPropertyName("used")] long? Used = null,
    [property: JsonPropertyName("remaining")] long? Remaining = null,
    [property: JsonPropertyName("systemPromptTokens")] long? SystemPromptTokens = null,
    [property: JsonPropertyName("conversationTokens")] long? ConversationTokens = null,
    [property: JsonPropertyName("filesLoadedCount")] int? FilesLoadedCount = null,
    [property: JsonPropertyName("largestFiles")] IReadOnlyList<string>? LargestFiles = null);

/// <summary>
/// Wall-clock latency for one model turn. The runner captures
/// <see cref="RequestedAt"/> when it dispatches the CLI input,
/// <see cref="FirstTokenAt"/> on the first stdout byte from the model,
/// and <see cref="CompletedAt"/> on CLI exit. <see cref="TtfbMs"/> /
/// <see cref="TotalMs"/> are convenience derivations the UI does not have
/// to recompute.
/// </summary>
public sealed record AgentMessageLatency(
    [property: JsonPropertyName("requestedAt")] DateTime? RequestedAt = null,
    [property: JsonPropertyName("firstTokenAt")] DateTime? FirstTokenAt = null,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt = null,
    [property: JsonPropertyName("ttfbMs")] long? TtfbMs = null,
    [property: JsonPropertyName("totalMs")] long? TotalMs = null);

public sealed record AgentArtifactRef
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("byteRange")]
    public AgentArtifactByteRange? ByteRange { get; init; }

    [JsonPropertyName("lineRange")]
    public AgentArtifactLineRange? LineRange { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }
}

public sealed record AgentArtifactByteRange(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

public sealed record AgentArtifactLineRange(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

public sealed record AgentParticipant
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("cli")]
    public string? Cli { get; init; }

    [JsonPropertyName("skill")]
    public string? Skill { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; init; }
}

/// <summary>
/// Filter passed to <c>AgentMessageBusStore.Query</c>. All fields are optional;
/// null means "do not filter on this dimension".
/// </summary>
public sealed record AgentMessageQuery(
    string? JobId = null,
    string? RunId = null,
    string? ParticipantId = null,
    string? Kind = null,
    string? Severity = null,
    string? Cli = null,
    string? Skill = null,
    string? Tag = null,
    string? CorrelationId = null,
    DateTime? Since = null,
    DateTime? Until = null,
    int? Limit = null);

/// <summary>
/// Aggregate counters for one project's bus, returned by the summary endpoint.
/// </summary>
public sealed record AgentMessageSummary(
    string Project,
    int TotalMessages,
    DateTime? FirstMessageAt,
    DateTime? LastMessageAt,
    IReadOnlyDictionary<string, int> CountsByKind,
    IReadOnlyDictionary<string, int> CountsByParticipant,
    IReadOnlyDictionary<string, int> CountsBySeverity);
