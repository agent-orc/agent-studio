namespace AgentStudio.Shared;

/// <summary>Extracts a JSON container from a reply that may include a code fence or prose.</summary>
public static class JsonReplyExtractor
{
    public static string? ExtractJsonObject(string? reply) => Extract(reply, '{', '}');
    public static string? ExtractJsonArray(string? reply) => Extract(reply, '[', ']');

    private static string? Extract(string? reply, char open, char close)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var start = reply.IndexOf(open);
        if (start < 0) return null;

        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = start; i < reply.Length; i++)
        {
            var current = reply[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') quoted = false;
                continue;
            }
            if (current == '"') quoted = true;
            else if (current == open) depth++;
            else if (current == close && --depth == 0) return reply[start..(i + 1)];
        }
        return null;
    }
}
