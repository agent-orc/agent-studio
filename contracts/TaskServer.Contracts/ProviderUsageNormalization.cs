namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Canonical token dimensions derived from a provider usage frame.
/// <paramref name="InputTokens"/> is always uncached input after normalization,
/// while <paramref name="ContextInputTokens"/> is the complete input loaded into
/// the model context.
/// </summary>
public readonly record struct NormalizedProviderUsage(
    long InputTokens,
    long CacheReadTokens,
    long ContextInputTokens,
    bool InputIncludesCached);

/// <summary>
/// Shared provider-boundary normalization used by both Studio and the remote
/// runner. OpenAI reports cached input as a subset of <c>input_tokens</c>.
/// </summary>
public static class ProviderUsageNormalization
{
    public const string OpenAiInputIncludesCachedV1 = "openai-input-includes-cached-v1";

    public static NormalizedProviderUsage OpenAi(long inputTokens, long cachedInputTokens)
    {
        var input = Math.Max(0, inputTokens);
        var cached = Math.Max(0, cachedInputTokens);
        return new NormalizedProviderUsage(
            InputTokens: Math.Max(0, input - cached),
            CacheReadTokens: cached,
            ContextInputTokens: input,
            InputIncludesCached: true);
    }

    public static bool IsOpenAiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var value = model.Trim();
        return value.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("openai", StringComparison.OrdinalIgnoreCase);
    }
}
