using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.CliHosting;

/// <summary>
/// Keeps an oversized JSONL object parseable by shortening payload strings
/// instead of cutting the serialized object at an arbitrary character.
/// </summary>
public static class JsonLineTruncator
{
    public const string TruncationNote =
        "… [payload cut at the 64 KiB log line cap; the full output is only in the run log]";

    private const int MaxPrefixRepairAttempts = 8;
    private static readonly HashSet<string> PayloadFieldNames =
        ["aggregated_output", "text", "output", "content"];
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Truncate a complete JSON object to <paramref name="maxChars"/>. Returns
    /// false for plain text, arrays, and malformed JSON.
    /// </summary>
    public static bool TryTruncateObject(string text, int maxChars, out string bounded)
    {
        bounded = text;
        if (text.Length == 0 || text[0] != '{') return false;

        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) return false;
            bounded = TruncateParsedObject(root, maxChars);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rebuild an oversized Codex frame from the bounded prefix retained by a
    /// streaming reader. The discarded suffix is never required or allocated.
    /// </summary>
    public static bool TryTruncateCodexFramePrefix(string prefix, int maxChars, out string bounded)
    {
        bounded = prefix;
        if (prefix.Length == 0 || prefix[0] != '{') return false;

        var body = prefix;
        for (var attempt = 0; attempt < MaxPrefixRepairAttempts; attempt++)
        {
            var scan = ScanPrefix(body);
            var candidate = ClosePrefix(body, scan);
            try
            {
                if (JsonNode.Parse(candidate) is not JsonObject root || !IsCodexFrame(root))
                    return false;

                bounded = TruncateParsedObject(root, maxChars);
                return true;
            }
            catch (JsonException)
            {
                if (scan.LastComma < 0) return false;
                body = body[..scan.LastComma];
            }
        }

        return false;
    }

    private static string TruncateParsedObject(JsonObject root, int maxChars)
    {
        if (maxChars < 2) return "{}";

        var markerOwner = root["item"] as JsonObject ?? root;
        markerOwner["truncated"] = true;
        EnsureNote(root, markerOwner);

        var serialized = root.ToJsonString(CompactJson);
        while (serialized.Length > maxChars)
        {
            var largest = CollectPayloadStrings(root)
                .Where(field => RemoveNote(field.Value).Length > 0)
                .MaxBy(field => field.Value.Length);
            if (largest is null) break;

            var valueWithoutNote = RemoveNote(largest.Value);
            var excess = serialized.Length - maxChars;
            var keep = Math.Max(0, valueWithoutNote.Length - excess - 16);
            var retained = valueWithoutNote[..keep].TrimEnd();
            largest.Owner[largest.Name] = retained.Length == 0
                ? TruncationNote
                : retained + "\n" + TruncationNote;
            serialized = root.ToJsonString(CompactJson);
        }

        if (serialized.Length <= maxChars) return serialized;

        // A frame can theoretically be oversized without any normal payload
        // field. Keep its routing metadata and produce a small valid frame.
        var minimal = BuildMinimalFrame(root);
        serialized = minimal.ToJsonString(CompactJson);
        if (serialized.Length <= maxChars) return serialized;

        if (minimal["item"] is JsonObject item)
        {
            item.Remove("command");
            serialized = minimal.ToJsonString(CompactJson);
            if (serialized.Length <= maxChars) return serialized;
            item.Remove("id");
            serialized = minimal.ToJsonString(CompactJson);
            if (serialized.Length <= maxChars) return serialized;
        }

        return "{\"truncated\":true}";
    }

    private static void EnsureNote(JsonObject root, JsonObject markerOwner)
    {
        var fields = CollectPayloadStrings(root);
        if (fields.Any(field => field.Value.Contains(TruncationNote, StringComparison.Ordinal)))
            return;

        var largest = fields.MaxBy(field => field.Value.Length);
        if (largest is not null)
        {
            largest.Owner[largest.Name] = largest.Value.Length == 0
                ? TruncationNote
                : largest.Value.TrimEnd() + "\n" + TruncationNote;
            return;
        }

        markerOwner["output"] = TruncationNote;
    }

    private static List<PayloadString> CollectPayloadStrings(JsonNode node)
    {
        var fields = new List<PayloadString>();
        Visit(node, fields);
        return fields;
    }

    private static void Visit(JsonNode? node, List<PayloadString> fields)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (PayloadFieldNames.Contains(property.Key)
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    fields.Add(new PayloadString(obj, property.Key, text));
                }
                else
                {
                    Visit(property.Value, fields);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) Visit(child, fields);
        }
    }

    private static string RemoveNote(string value)
    {
        var index = value.IndexOf(TruncationNote, StringComparison.Ordinal);
        return index < 0 ? value : value[..index].TrimEnd();
    }

    private static JsonObject BuildMinimalFrame(JsonObject root)
    {
        var minimal = new JsonObject();
        CopyString(root, minimal, "type");

        if (root["item"] is JsonObject sourceItem)
        {
            var item = new JsonObject();
            CopyString(sourceItem, item, "id");
            CopyString(sourceItem, item, "type");
            CopyString(sourceItem, item, "command");
            item["output"] = TruncationNote;
            item["truncated"] = true;
            minimal["item"] = item;
        }
        else
        {
            minimal["output"] = TruncationNote;
            minimal["truncated"] = true;
        }

        return minimal;
    }

    private static void CopyString(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is JsonValue value && value.TryGetValue<string>(out var text))
            destination[name] = text;
    }

    private static bool IsCodexFrame(JsonObject root)
    {
        if (root["type"] is not JsonValue value || !value.TryGetValue<string>(out var type))
            return false;
        return type.StartsWith("item.", StringComparison.Ordinal)
            || type.StartsWith("turn.", StringComparison.Ordinal)
            || type.StartsWith("thread.", StringComparison.Ordinal)
            || type.StartsWith("session.", StringComparison.Ordinal);
    }

    private static PrefixScan ScanPrefix(string body)
    {
        var inString = false;
        var escaping = false;
        var unicodeDigitsLeft = 0;
        var pendingEscapeStart = -1;
        var lastComma = -1;
        var stack = new List<char>();

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (inString)
            {
                if (unicodeDigitsLeft > 0)
                {
                    unicodeDigitsLeft--;
                    if (unicodeDigitsLeft == 0) pendingEscapeStart = -1;
                }
                else if (escaping)
                {
                    escaping = false;
                    if (c == 'u') unicodeDigitsLeft = 4;
                    else pendingEscapeStart = -1;
                }
                else if (c == '\\')
                {
                    escaping = true;
                    pendingEscapeStart = i;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"') inString = true;
            else if (c is '{' or '[') stack.Add(c);
            else if (c is '}' or ']')
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else if (c == ',') lastComma = i;
        }

        return new PrefixScan(
            inString,
            escaping || unicodeDigitsLeft > 0 ? pendingEscapeStart : -1,
            stack,
            lastComma);
    }

    private static string ClosePrefix(string body, PrefixScan scan)
    {
        var head = scan.PendingEscapeStart >= 0 ? body[..scan.PendingEscapeStart] : body;
        if (scan.InString)
            head += JsonSerializer.Serialize("\n" + TruncationNote)[1..^1] + '"';

        var closers = new char[scan.Stack.Count];
        for (var i = 0; i < scan.Stack.Count; i++)
            closers[i] = scan.Stack[scan.Stack.Count - 1 - i] == '{' ? '}' : ']';
        return head + new string(closers);
    }

    private sealed record PayloadString(JsonObject Owner, string Name, string Value);
    private sealed record PrefixScan(
        bool InString,
        int PendingEscapeStart,
        List<char> Stack,
        int LastComma);
}
