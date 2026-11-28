using System.Text.Json;

namespace AgentRunner;

internal static class CodexReviewCheckoutFailure
{
    public static string? Read(ProcessResult raw)
    {
        foreach (var line in raw.StdOut.Split('\n'))
        {
            if (!line.Contains("bwrap:", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var frame = document.RootElement;
                if (frame.ValueKind != JsonValueKind.Object
                    || !frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "item.completed"
                    || !frame.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String || itemType.GetString() != "command_execution"
                    || !item.TryGetProperty("exit_code", out var exitCode)
                    || exitCode.ValueKind != JsonValueKind.Number || !exitCode.TryGetInt32(out var code) || code == 0
                    || !item.TryGetProperty("aggregated_output", out var output) || output.ValueKind != JsonValueKind.String)
                    continue;
                if (NamespaceError(output.GetString()!) is { } error) return error;
            }
            catch (JsonException)
            {
                // Non-protocol output cannot establish that a Codex command failed.
                continue;
            }
        }

        return raw.Success ? null : NamespaceError(raw.StdErr);
    }

    private static string? NamespaceError(string output)
        => output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line =>
            line.StartsWith("bwrap:", StringComparison.Ordinal)
            && (line.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No permissions to create a new namespace", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))) is { } error
            ? error[..Math.Min(error.Length, 400)]
            : null;
}
