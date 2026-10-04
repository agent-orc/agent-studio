namespace AgentStudio.Tokens;

/// <summary>Identity of a captured usage frame, independent of completion generation.</summary>
internal readonly record struct TokenUsageIdentity(
    string JobId, string ParticipantKind, long TimestampTicks, string Model, long Input, long Output,
    long CacheRead, long CacheWrite)
{
    public static TokenUsageIdentity From(string jobId, TaskTokenCall call) => new(
        jobId, Category(call.ParticipantId), call.Ts.ToUniversalTime().Ticks,
        ModelMetadataRegistry.NormalizeId(call.Model ?? string.Empty),
        call.InputTokens, call.OutputTokens, call.CacheReadTokens, call.CacheCreationTokens);

    public static TokenUsageIdentity From(OrchestratorLogEntry entry)
    {
        var usage = entry.TokenUsage!;
        return new TokenUsageIdentity(entry.JobId ?? string.Empty,
            Category(entry.ParticipantId),
            entry.Ts.ToUniversalTime().Ticks,
            ModelMetadataRegistry.NormalizeId(usage.Model ?? string.Empty),
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens,
            usage.CacheCreationTokens);
    }

    public static IReadOnlyList<OrchestratorLogEntry> Distinct(
        IEnumerable<OrchestratorLogEntry> entries)
    {
        var seen = new HashSet<TokenUsageIdentity>();
        return entries.Where(entry => entry.TokenUsage is null || seen.Add(From(entry))).ToList();
    }

    private static string Category(string? participant)
    {
        if (string.IsNullOrWhiteSpace(participant)) return string.Empty;
        var separator = participant.IndexOf(':');
        return separator < 0 ? participant : participant[..separator];
    }
}
