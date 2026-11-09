namespace AgentStudio.TaskServer.Contracts;

/// <summary>A bounded model turn. Selection and quota admission remain with Studio.</summary>
public sealed record OneShotJobRequest(string Cli, string Model, string Prompt,
    string? ThinkingLevel, int TimeoutSeconds, int MaxOutputBytes,
    IReadOnlyList<OneShotJobImage>? Images = null);

public sealed record OneShotJobImage(string Base64, string MediaType);

public sealed record OneShotJobDto(string JobId, string Status, string Capability,
    string? Text = null, string? Error = null, OneShotJobUsage? Usage = null);

public sealed record OneShotJobUsage(long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheCreationTokens, string? Model);

public sealed record OneShotJobClaimRequest(string RunnerId, string InstanceId);
public sealed record OneShotJobWork(string JobId, string ClaimToken, OneShotJobRequest Request);
public sealed record OneShotJobCompletion(string RunnerId, string InstanceId, string JobId, string ClaimToken,
    bool Success, string? Text, string? Error, OneShotJobUsage? Usage);
