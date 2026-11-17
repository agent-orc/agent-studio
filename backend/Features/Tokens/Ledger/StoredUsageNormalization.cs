using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

/// <summary>
/// Read compatibility for immutable legacy bus rows written before OpenAI
/// input semantics were recorded. Durable task and pipeline records are
/// normally rewritten by the completed one-time migration; bus JSONL stays
/// append-only and is normalized as it enters every aggregate. Legacy receipt
/// calls get the same treatment in <see cref="ProjectTokenReceiptReader"/>.
/// Pipeline step records that remain unnormalized are handled by
/// <see cref="AgentStudio.Pipeline.PipelineStoredUsageNormalization"/>.
/// </summary>
internal static class StoredUsageNormalization
{
    public static AgentMessageTokens Normalize(AgentMessageTokens tokens)
    {
        if (tokens.InputIncludesCached is not null
            || !ProviderUsageNormalization.IsOpenAiModel(tokens.Model)
            || tokens.CacheRead is not > 0
            || tokens.Input < tokens.CacheRead.Value)
        {
            return tokens;
        }

        var normalized = ProviderUsageNormalization.OpenAi(tokens.Input, tokens.CacheRead.Value);
        return tokens with
        {
            Input = normalized.InputTokens,
            CacheRead = normalized.CacheReadTokens,
            InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
        };
    }

    /// <summary>
    /// Same rule for a durable legacy receipt. Repaired and freshly parsed
    /// calls carry <see cref="TaskTokenCall.InputIncludesCached"/>
    /// and pass through unchanged.
    /// </summary>
    public static TaskTokenCall Normalize(TaskTokenCall call)
    {
        if (call.InputIncludesCached is not null
            || string.Equals(call.UsageNormalization,
                ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
                StringComparison.Ordinal)
            || !ProviderUsageNormalization.IsOpenAiModel(call.Model)
            || call.CacheReadTokens <= 0
            || call.InputTokens < call.CacheReadTokens)
        {
            return call;
        }

        var normalized = ProviderUsageNormalization.OpenAi(call.InputTokens, call.CacheReadTokens);
        return call with
        {
            InputTokens = normalized.InputTokens,
            CacheReadTokens = normalized.CacheReadTokens,
            InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
        };
    }
}
