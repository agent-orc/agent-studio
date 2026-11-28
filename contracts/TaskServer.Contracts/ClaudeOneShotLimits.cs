using System.Globalization;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Claude CLI launch and result fields for a single bounded turn.</summary>
public static class ClaudeOneShotLimits
{
    public static void Apply(IDictionary<string, string?> environment, IList<string> arguments, int? maxOutputTokens)
    {
        if (maxOutputTokens is not { } limit) return;
        environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = limit.ToString(CultureInfo.InvariantCulture);
        arguments.Add("--max-turns");
        arguments.Add("1");
    }

    public static string? GetLimitError(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (result.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String
            && reason.ValueEquals("max_tokens"))
            return OneShotJobErrors.OutputTokenLimit;
        if (result.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String
            && subtype.ValueEquals("error_max_turns"))
            return OneShotJobErrors.TurnLimit;
        return null;
    }

    public static int? ReadReasoningTokens(JsonElement usage) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens_details", out var details)
        && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("thinking_tokens", out var tokens)
        && tokens.ValueKind == JsonValueKind.Number && tokens.TryGetInt32(out var count) ? count : null;
}
