using System.Globalization;
using System.Text;

namespace AgentStudio.ProjectChat;

/// <summary>
/// One per-turn markdown document on disk. <c>Body</c> is verbatim
/// markdown (the chat surface renders it client-side). The frontmatter
/// fields are the authoritative metadata — see
/// <see cref="ProjectChatTurnAuthors"/> and
/// <see cref="ProjectChatTurnKinds"/> for the closed enums Slice D
/// promises consumers.
/// </summary>
public sealed record ProjectChatTurn
{
    public string TurnId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Author { get; init; } = ProjectChatTurnAuthors.User;
    public string Kind { get; init; } = ProjectChatTurnKinds.Turn;
    public DateTime Ts { get; init; } = DateTime.UtcNow;
    public DateTime? QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public IReadOnlyList<string>? Refs { get; init; }
    public string Body { get; init; } = "";
}

public static class ProjectChatTurnAuthors
{
    public const string User = "user";
    public const string Orchestrator = "orchestrator";
    public const string Agent = "agent";
    public const string Supervisor = "supervisor";
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        User, Orchestrator, Agent, Supervisor, Claude, Codex, Gemini
    };

    public static bool IsKnown(string? value) => value != null && All.Contains(value);
}

public static class ProjectChatTurnKinds
{
    public const string Turn = "turn";
    public const string EventToolCall = "event-tool-call";
    public const string EventWatchdog = "event-watchdog";
    public const string EventRateLimit = "event-rate-limit";
    public const string EventUpdate = "event-update";
    public const string EventTask = "event-task";
    public const string EventDecision = "event-decision";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Turn, EventToolCall, EventWatchdog, EventRateLimit, EventUpdate, EventTask, EventDecision
    };

    public static bool IsKnown(string? value) => value != null && All.Contains(value);
}

/// <summary>
/// Minimal, dependency-free YAML-frontmatter reader/writer for chat
/// turn files. The format is intentionally a tiny subset of YAML:
/// <c>key: scalar</c> for the four required scalar fields and
/// <c>refs: [a, b, c]</c> for the optional ID-list. Adding YamlDotNet
/// for one document shape would be overkill and would pull in a fair
/// bit of weight for little payoff (see csproj — kept dependency-light
/// per the task's hard rules).
/// </summary>
public static class ProjectChatTurnSerializer
{
    public const string Delimiter = "---";

    public static string Serialize(ProjectChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.TurnId)) throw new ArgumentException("TurnId is required");
        if (!ProjectChatTurnAuthors.IsKnown(turn.Author)) throw new ArgumentException($"Unknown author '{turn.Author}'");
        if (!ProjectChatTurnKinds.IsKnown(turn.Kind)) throw new ArgumentException($"Unknown kind '{turn.Kind}'");

        var sb = new StringBuilder();
        sb.Append(Delimiter).Append('\n');
        sb.Append("turnId: ").Append(EscapeScalar(turn.TurnId)).Append('\n');
        sb.Append("author: ").Append(EscapeScalar(turn.Author)).Append('\n');
        sb.Append("kind: ").Append(EscapeScalar(turn.Kind)).Append('\n');
        sb.Append("ts: ").Append(turn.Ts.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
        AppendTimestamp(sb, "queuedAt", turn.QueuedAt);
        AppendTimestamp(sb, "startedAt", turn.StartedAt);
        AppendTimestamp(sb, "finishedAt", turn.FinishedAt);
        if (turn.Refs is { Count: > 0 })
        {
            sb.Append("refs: [");
            for (int i = 0; i < turn.Refs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(EscapeScalar(turn.Refs[i]));
            }
            sb.Append("]\n");
        }
        sb.Append(Delimiter).Append('\n');
        sb.Append(turn.Body ?? "");
        if (!(turn.Body ?? "").EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    private static void AppendTimestamp(StringBuilder sb, string key, DateTime? value)
    {
        if (value is { } at)
            sb.Append(key).Append(": ").Append(at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
    }

    /// <summary>
    /// Parse a chat-turn markdown file. Returns null when the document is
    /// missing the leading <c>---</c> frontmatter or the required scalar
    /// fields. Tolerates trailing CRLF and arbitrary body content.
    /// </summary>
    public static ProjectChatTurn? Parse(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        // Normalise line endings without allocating a second copy when not needed.
        var firstNewline = content.IndexOf('\n');
        if (firstNewline < 0) return null;
        var firstLine = content[..firstNewline].TrimEnd('\r').Trim();
        if (firstLine != Delimiter) return null;

        var rest = content[(firstNewline + 1)..];
        var endIdx = rest.IndexOf("\n" + Delimiter, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            // Try start-of-string match (frontmatter immediately closes with ---).
            if (rest.StartsWith(Delimiter)) endIdx = -1;
            else return null;
        }

        string fmBlock;
        string body;
        if (endIdx < 0)
        {
            // closing delimiter is the very first line of `rest`
            var afterDelim = rest.IndexOf('\n', Delimiter.Length);
            if (afterDelim < 0) { fmBlock = ""; body = ""; }
            else { fmBlock = ""; body = rest[(afterDelim + 1)..]; }
        }
        else
        {
            fmBlock = rest[..endIdx];
            var bodyStart = endIdx + 1 + Delimiter.Length;
            // skip closing delimiter line's newline
            if (bodyStart < rest.Length && rest[bodyStart] == '\r') bodyStart++;
            if (bodyStart < rest.Length && rest[bodyStart] == '\n') bodyStart++;
            body = rest[bodyStart..];
        }

        string? turnId = null, author = null, kind = null;
        DateTime? ts = null, queuedAt = null, startedAt = null, finishedAt = null;
        List<string>? refs = null;

        foreach (var rawLine in fmBlock.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "turnId": turnId = UnescapeScalar(value); break;
                case "author": author = UnescapeScalar(value); break;
                case "kind": kind = UnescapeScalar(value); break;
                case "ts":
                    if (DateTime.TryParse(UnescapeScalar(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTs))
                        ts = DateTime.SpecifyKind(parsedTs, DateTimeKind.Utc);
                    break;
                case "queuedAt": queuedAt = ParseTimestamp(value); break;
                case "startedAt": startedAt = ParseTimestamp(value); break;
                case "finishedAt": finishedAt = ParseTimestamp(value); break;
                case "refs":
                    refs = ParseRefsList(value);
                    break;
            }
        }

        if (turnId == null || author == null || kind == null || ts == null) return null;
        return new ProjectChatTurn
        {
            TurnId = turnId,
            Author = author,
            Kind = kind,
            Ts = ts.Value,
            QueuedAt = queuedAt,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Refs = refs,
            Body = body
        };
    }

    private static DateTime? ParseTimestamp(string value)
        => DateTime.TryParse(UnescapeScalar(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : null;

    private static List<string>? ParseRefsList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.StartsWith('[') || !raw.EndsWith(']')) return null;
        var inner = raw[1..^1];
        var items = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var t = UnescapeScalar(part.Trim());
            if (t.Length > 0) items.Add(t);
        }
        return items.Count > 0 ? items : null;
    }

    private static string EscapeScalar(string value)
    {
        // Quote when the scalar contains any character that would confuse
        // the line-based parser; otherwise leave bare for readability.
        if (value.Length == 0) return "\"\"";
        bool needsQuote = false;
        foreach (var c in value)
        {
            if (c == ':' || c == '#' || c == ',' || c == '[' || c == ']' || c == '\'' || c == '"' || c == '\n' || c == '\r')
            {
                needsQuote = true;
                break;
            }
        }
        if (!needsQuote) return value;
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private static string UnescapeScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'");
        }
        return value;
    }
}
