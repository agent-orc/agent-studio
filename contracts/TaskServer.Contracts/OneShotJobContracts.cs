namespace AgentStudio.TaskServer.Contracts;

/// <summary>A bounded model turn. Selection and quota admission remain with Studio.</summary>
public sealed record OneShotJobRequest(string Cli, string Model, string Prompt,
    string? ThinkingLevel, int TimeoutSeconds, int MaxOutputBytes,
    IReadOnlyList<OneShotJobImage>? Images = null, int? ClaudeMaxOutputTokens = null);

public sealed record OneShotJobImage(string Base64, string MediaType);

public sealed record OneShotJobDto(string JobId, string Status, string Capability,
    string? Text = null, string? Error = null, OneShotJobUsage? Usage = null);

public sealed record OneShotJobUsage(long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheCreationTokens, string? Model, int? ReasoningTokens = null);

public sealed record OneShotJobClaimRequest(string RunnerId, string InstanceId);
public sealed record OneShotJobWork(string JobId, string ClaimToken, OneShotJobRequest Request);
public sealed record OneShotJobCompletion(string RunnerId, string InstanceId, string JobId, string ClaimToken,
    bool Success, string? Text, string? Error, OneShotJobUsage? Usage);

public static class OneShotJobErrors
{
    public const string TurnLimit = "One-shot reached the turn limit.";
    public const string OutputTokenLimit = "One-shot output reached the token limit.";
    public const string OutputByteLimit = "one-shot output exceeds the byte cap";
    public const string RawOutputByteLimit = "one-shot raw output exceeds the byte cap";
}
