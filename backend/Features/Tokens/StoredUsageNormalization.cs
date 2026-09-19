using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

/// <summary>
/// Read compatibility for immutable legacy bus rows written before OpenAI
/// input semantics were recorded. Durable task and pipeline records are
/// rewritten by <see cref="OpenAiUsageHistoryRepair"/>; bus JSONL stays
/// append-only and is normalized as it enters every aggregate.
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
}
