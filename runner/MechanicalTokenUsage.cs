using System.Text.Json;

namespace AgentRunner;

/// <summary>Counts provider-reported tokens for one CLI generation without double-counting cache reads.</summary>
internal sealed class MechanicalTokenUsage
{
    public long Input { get; private set; }
    public long Output { get; private set; }
    public long CacheRead { get; private set; }
    public bool HasSummary { get; private set; }
    private long _claudeMessageInput;
    private long _claudeMessageOutput;
    private long _claudeMessageCache;
    private long _codexCumulative;
    private bool _codexSeen;

    public long Total => _codexSeen ? Input + Output : Input + Output + CacheRead;

    public void Observe(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = String(root, "type");
            if (type == "turn.completed" && TryObject(root, "usage", out var codex))
            {
                _codexSeen = true;
                // Codex turn.completed reports this turn, not the whole session.
                Input += Long(codex, "input_tokens");
                Output += Long(codex, "output_tokens");
                CacheRead += Long(codex, "cached_input_tokens");
                HasSummary = true;
            }
            else if (type == "event_msg" && TryObject(root, "payload", out var payload)
                     && String(payload, "type") == "token_count"
                     && TryObject(payload, "info", out var info)
                     && TryObject(info, "total_token_usage", out var cumulative))
            {
                _codexSeen = true;
                var total = Long(cumulative, "input_tokens") + Long(cumulative, "output_tokens");
                if (total > _codexCumulative) _codexCumulative = total;
            }
            else if (type == "assistant" && TryObject(root, "message", out var message)
                     && TryObject(message, "usage", out var messageUsage))
            {
                _claudeMessageInput += Long(messageUsage, "input_tokens");
                _claudeMessageOutput += Long(messageUsage, "output_tokens");
                _claudeMessageCache += Long(messageUsage, "cache_read_input_tokens")
                                       + Long(messageUsage, "cache_creation_input_tokens");
            }
            else if (type == "result" && TryObject(root, "usage", out var claude))
            {
                Input = Math.Max(Input, Long(claude, "input_tokens"));
                Output = Math.Max(Output, Long(claude, "output_tokens"));
                CacheRead = Math.Max(CacheRead, Long(claude, "cache_read_input_tokens")
                                                 + Long(claude, "cache_creation_input_tokens"));
                HasSummary = true;
            }
        }
    }

    public (long? Input, long? Output, long? CacheRead, long? Total) Snapshot()
    {
        if (!HasSummary && _claudeMessageInput + _claudeMessageOutput + _claudeMessageCache == 0
            && _codexCumulative == 0) return (null, null, null, null);
        var input = Math.Max(Input, _claudeMessageInput);
        var output = Math.Max(Output, _claudeMessageOutput);
        var cache = Math.Max(CacheRead, _claudeMessageCache);
        var total = Math.Max(_codexSeen ? input + output : input + output + cache, _codexCumulative);
        return (input, output, cache, total);
    }

    public static MechanicalTokenUsage Parse(string stdout)
    {
        var usage = new MechanicalTokenUsage();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            usage.Observe(line);
        return usage;
    }

    private static bool TryObject(JsonElement parent, string key, out JsonElement value)
        => parent.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? String(JsonElement parent, string key)
        => parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long Long(JsonElement parent, string key)
        => parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? Math.Max(0, number) : 0;
}
