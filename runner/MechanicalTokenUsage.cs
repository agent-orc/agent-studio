using System.Text.Json;

namespace AgentRunner;

internal static class MechanicalTokenUsage
{
    public static bool TryRead(string line, out long tokens, out bool cumulative)
    {
        tokens = 0;
        cumulative = false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return false;
            var type = typeElement.GetString();
            if (type == "token_count")
            {
                var info = root.TryGetProperty("info", out var infoNode) ? infoNode : root;
                var hasLast = info.TryGetProperty("last_token_usage", out var lastNode);
                cumulative = !hasLast;
                var usage = hasLast ? lastNode
                    : info.TryGetProperty("total_token_usage", out var totalNode) ? totalNode : info;
                tokens = Count(usage);
                return tokens > 0;
            }
            if (type is not ("turn.completed" or "result" or "response.completed")) return false;
            if (!root.TryGetProperty("usage", out var perTurn)) return false;
            tokens = Count(perTurn);
            return tokens > 0;
        }
        catch (JsonException) { return false; }
    }

    private static long Count(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return 0;
        static long Read(JsonElement item, string key)
            => item.TryGetProperty(key, out var value) && value.TryGetInt64(out var parsed)
                ? Math.Max(0, parsed) : 0;
        var input = Read(usage, "input_tokens");
        var output = Read(usage, "output_tokens");
        var cacheRead = Read(usage, "cache_read_input_tokens");
        var cacheWrite = Read(usage, "cache_creation_input_tokens");
        // Codex input already includes its cached input. Claude reports cache
        // dimensions separately; counting them is the processed-token measure.
        return input + output + cacheRead + cacheWrite;
    }
}
