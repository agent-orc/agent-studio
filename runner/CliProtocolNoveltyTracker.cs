using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using CodingAgentRunner.Events;

namespace AgentRunner;

/// <summary>
/// Purely derives scrubbed, countable protocol-drift telemetry from CAR's
/// lossless <see cref="CliRunEvent.Unknown"/> event. The provider payload is
/// hashed but never copied into the marker that leaves the worker host.
/// </summary>
internal sealed class CliProtocolNoveltyTracker(string cliType)
{
    private static readonly HashSet<string> ClaudeFrameTypes = new(StringComparer.Ordinal)
    {
        "system",
        "assistant",
        "user",
        "result",
        "rate_limit_event",
    };

    private static readonly HashSet<string> CodexFrameTypes = new(StringComparer.Ordinal)
    {
        "thread.started",
        "turn.started",
        "turn.completed",
        "turn.failed",
        "error",
        "session_meta",
        "rate_limits",
        "item.started",
        "item.updated",
        "item.completed",
    };

    private static readonly HashSet<string> CodexItemTypes = new(StringComparer.Ordinal)
    {
        "agent_message",
        "reasoning",
        "command_execution",
        "command_call",
        "local_shell_call",
        "file_change",
        "web_search",
        "update_plan",
        "todo",
        "todo_list",
    };

    private readonly Dictionary<string, long> _byFrameType = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _total;

    public bool TryObserve(CliRunEvent evt, out ProtocolNoveltyTelemetry telemetry)
    {
        telemetry = null!;
        return evt is CliRunEvent.Unknown unknown
               && TryObserveRaw(unknown.RawDetail, out telemetry);
    }

    public bool TryObserveRaw(string? rawDetail, out ProtocolNoveltyTelemetry telemetry)
    {
        telemetry = null!;
        if (!TryFrameType(rawDetail, out var frameType)) return false;
        telemetry = Record(frameType, rawDetail);
        return true;
    }

    public bool TryObserveFrame(string? rawDetail, out ProtocolNoveltyTelemetry telemetry)
    {
        telemetry = null!;
        if (!TryFrameType(rawDetail, out var frameType) || IsKnown(frameType)) return false;
        telemetry = Record(frameType, rawDetail);
        return true;
    }

    private ProtocolNoveltyTelemetry Record(string frameType, string? rawDetail)
    {

        long occurrence;
        long total;
        lock (_gate)
        {
            _byFrameType.TryGetValue(frameType, out occurrence);
            occurrence++;
            _byFrameType[frameType] = occurrence;
            total = ++_total;
        }

        var payload = rawDetail ?? string.Empty;
        return new ProtocolNoveltyTelemetry(
            cliType,
            typeof(CodingAgentRunner.CliRunner).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            frameType,
            occurrence,
            total,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
    }

    private bool IsKnown(string frameType)
    {
        if (string.Equals(cliType, AgentCliProcess.ClaudeCli, StringComparison.Ordinal))
            return ClaudeFrameTypes.Contains(frameType);
        if (!string.Equals(cliType, AgentCliProcess.CodexCli, StringComparison.Ordinal))
            return false;
        if (CodexFrameTypes.Contains(frameType) && !frameType.StartsWith("item.", StringComparison.Ordinal)) return true;
        var separator = frameType.IndexOf('/');
        return separator > 0
               && frameType[..separator] is "item.started" or "item.updated" or "item.completed"
               && CodexItemTypes.Contains(frameType[(separator + 1)..]);
    }

    private static bool TryFrameType(string? rawDetail, out string frameType)
    {
        frameType = string.Empty;
        var text = rawDetail?.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || (text[0] != '{' && text[0] != '['))
            return false;
        if (text.StartsWith("[[", StringComparison.Ordinal)
            && text[2..].TrimStart().StartsWith("TASK", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                frameType = "<non-object-json>";
                return true;
            }

            var root = document.RootElement;
            frameType = Text(root, "type") ?? "<missing-type>";
            if (frameType is "item.started" or "item.updated" or "item.completed"
                && root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object
                && Text(item, "type") is { } itemType)
                frameType += "/" + itemType;
            frameType = ScrubIdentity(frameType);
            return true;
        }
        catch (JsonException)
        {
            frameType = "<malformed-json>";
            return true;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ScrubIdentity(string value)
    {
        var normalized = new string(value
            .Take(96)
            .Select(character => char.IsLetterOrDigit(character)
                                 || character is '.' or '-' or '_' or '/' or '<' or '>'
                ? character
                : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "<empty-type>" : normalized;
    }
}

/// <summary>
/// Compatibility bridge for Codex provider-error frames introduced after the
/// pinned CAR adapter. The runner treats the frame as a typed failed turn while
/// preserving unknown frames for protocol-drift telemetry.
/// </summary>
internal static class RunnerCodexEventAdapter
{
    public static CliRunEvent MapKnownError(CliRunEvent evt)
    {
        if (evt is not CliRunEvent.Unknown unknown)
            return evt;

        var raw = unknown.RawDetail ?? unknown.Sample;
        if (string.IsNullOrWhiteSpace(raw))
            return evt;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(Text(root, "type"), "error", StringComparison.Ordinal))
            {
                return evt;
            }

            var message = Text(root, "message")
                          ?? NestedText(root, "error", "message")
                          ?? "Codex provider error";
            return new CliRunEvent.TurnFailed(message) { RunId = evt.RunId };
        }
        catch (JsonException)
        {
            return evt;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NestedText(JsonElement element, string parent, string property)
        => element.TryGetProperty(parent, out var nested)
           && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, property)
            : null;
}
